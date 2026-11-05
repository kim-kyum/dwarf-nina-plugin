// DWARF session: connection, master lock and the V3 "Deep Sky" single-frame capture workflow.
//
// The command sequence is a C# port of the hardware-verified workflow in dwarfAlp
// (src/dwarf_alpaca/dwarf/session.py, GPL-3.0, https://github.com/acocalypso/dwarfAlp):
//   connect ws -> 13004 master lock -> 16405 device state -> 13000/13001 time
//   16402 mode 8 -> 16404 enter camera -> 16403 tech 2 -> 10050 preview quality
//   HTTP mode-2 catalogue -> 16700/16701 exposure+gain -> 11041 quick set -> 16700/16701 again
//   -> 16703 frame count -> 11005 start (11050 continue on dark warning)
//   -> runtime re-apply in the capture namespace -> wait 15209 progress / FTP FITS
//   -> 11006 stop -> download FITS (FTP, or album fitsList over HTTP).
using System.Globalization;
using System.Text.Json.Nodes;
using Dwarf.Core.Proto;
using Google.Protobuf;
using Notify = Dwarf.Core.Proto.Notify;
using Param = Dwarf.Core.Proto.Param;

namespace Dwarf.Core;

public enum CapturePhase { Idle, Configuring, Starting, Exposing, Transferring, Ready, Failed, Aborting }

public sealed class DwarfSession : IAsyncDisposable {
    // ---- protocol constants (see dwarfAlp session.py) ----
    private const int ModSystem = (int)ModuleId.ModuleSystem;
    private const int ModNotify = (int)ModuleId.ModuleNotify;
    private const int ModAstro = (int)ModuleId.ModuleAstro;
    private const int ModTele = (int)ModuleId.ModuleCameraTele;
    private const int ModParams = (int)ModuleId.ModuleCameraParams;
    private const int ModDeviceConfig = (int)ModuleId.ModuleDeviceConfig;

    private const int CmdSetMaster = (int)DwarfCMD.CmdSystemSetMaster;
    private const int CmdSetTime = (int)DwarfCMD.CmdSystemSetTime;
    private const int CmdSetTimeZone = (int)DwarfCMD.CmdSystemSetTimeZone;
    private const int CmdHostSlave = (int)DwarfCMD.CmdNotifyWsHostSlaveMode;
    private const int CmdSwitchMode = (int)DwarfCMD.CmdGlobalTaskManagerSwitchShootingMode;
    private const int CmdSwitchTech = (int)DwarfCMD.CmdGlobalTaskManagerSwitchShootingTech;
    private const int CmdEnterCamera = (int)DwarfCMD.CmdGlobalTaskManagerEnterCamera;
    private const int CmdDeviceState = (int)DwarfCMD.CmdGlobalTaskGetDeviceStateInfo;
    private const int CmdPreviewQuality = (int)DwarfCMD.CmdCameraTeleSetPreviewQuality;
    private const int CmdGoLive = (int)DwarfCMD.CmdAstroGoLive;
    private const int CmdCheckDark = (int)DwarfCMD.CmdAstroCheckGotDark;
    private const int CmdQuickSetList = (int)DwarfCMD.CmdAstroGetQuickSetList;
    private const int CmdQuickSet = (int)DwarfCMD.CmdAstroSetQuickSet;
    private const int CmdSetExposure = (int)DwarfCMD.CmdParamSetExposure;
    private const int CmdSetGain = (int)DwarfCMD.CmdParamSetGain;
    private const int CmdSetGeneralInt = (int)DwarfCMD.CmdParamSetGeneralIntParam;
    private const int CmdGeneralIntNotify = (int)DwarfCMD.CmdNotifyGeneralIntParam;
    private const int CmdStartCapture = (int)DwarfCMD.CmdAstroStartCaptureRawLiveStacking;
    private const int CmdStopCapture = (int)DwarfCMD.CmdAstroStopCaptureRawLiveStacking;
    private const int CmdContinueShooting = (int)DwarfCMD.CmdAstroContinueShooting;
    private const int CmdCaptureState = (int)DwarfCMD.CmdNotifyStateCaptureRawLiveStacking;
    private const int CmdCaptureProgress = (int)DwarfCMD.CmdNotifyPrograssCaptureRawLiveStacking;
    private const int CmdWaitShootingProgress = (int)DwarfCMD.CmdNotifyWaitShootingProgress;
    private const int CmdCmosTemperature = (int)DwarfCMD.CmdNotifyCmosTemperature;

    private const int CodeOk = 0;
    private const int CodeDarkNotFound = (int)DwarfErrorCode.CodeAstroDarkNotFound;
    private const int CodeDarkTempMismatch = (int)DwarfErrorCode.CodeAstroDarkTempMismatch;
    private const int CodeNeedGoto = (int)DwarfErrorCode.CodeAstroNeedGoto;
    private const int CodeNeedAdjust = (int)DwarfErrorCode.CodeAstroNeedAdjustShootParam;
    private const int CodeBusy = (int)DwarfErrorCode.CodeAstroFunctionBusy;

    private const int AstroCatalogueModeId = 2;
    private const int AstroCaptureModeId = 11;
    private const int ParamModeShift = 56;
    private const ulong ParamValueMask = (1UL << ParamModeShift) - 1;
    private const ulong AstroExposureParamId = 0x0201000000000001;
    private const ulong AstroGainParamId = 0x0201000000000002;
    private const ulong AstroFrameCountParamId = 0x202000000000010;

    private static readonly (int, int)[] GeneralIntAlternates = { (ModParams, CmdGeneralIntNotify), (ModNotify, CmdGeneralIntNotify) };
    private static readonly (int, int)[] HostSlaveAlternates = { (ModSystem, CmdHostSlave), (ModNotify, CmdHostSlave) };

    public DeviceProfile Profile { get; }
    public string Host { get; }

    private readonly DwarfWsClient ws;
    private readonly DwarfHttpApi http;
    private readonly DwarfFtp ftp;
    private readonly SemaphoreSlim commandLock = new(1, 1);
    private readonly SemaphoreSlim captureLock = new(1, 1);

    public bool HasMasterLock { get; private set; }
    public bool IsConnected => ws.IsConnected && HasMasterLock;
    public double? TemperatureC { get; private set; }
    public event Action? ConnectionLost;

    // ---- capture state ----
    private int? operationState;                 // notify.OperationState: 0 idle, 1 running, 2 stopping, 3 stopped
    private TaskCompletionSource readyTcs = NewSignal(set: true);
    private TaskCompletionSource frameCompleteTcs = NewSignal(set: false);
    private string? captureId;
    private int requestedFrames = 1;
    private int progressCount;
    private int? captureParamModeId;
    private bool? liveParametersSupported;
    private string? runtimeReapplyCaptureId;
    private CaptureRequest? currentRequest;
    private JsonNode? paramCatalogue;
    private List<string[]>? quickSetPresets;
    private FtpFitsEntry? lastFtpEntry;

    public CapturePhase Phase { get; private set; } = CapturePhase.Idle;
    public FitsImage? LastImage { get; private set; }
    public string? LastError { get; private set; }
    public double? AppliedExposure { get; private set; }
    public int? AppliedGain { get; private set; }
    private Task? captureTask;
    private CancellationTokenSource? captureCts;

    public sealed record CaptureRequest(double DurationS, int Gain, int FilterPosition, bool Light);

    public DwarfSession(string host, DeviceProfile profile) {
        Host = host;
        Profile = profile;
        ws = new DwarfWsClient(host, profile);
        ws.Notification += OnNotification;
        ws.Disconnected += _ => { HasMasterLock = false; ConnectionLost?.Invoke(); };
        http = new DwarfHttpApi(host);
        ftp = new DwarfFtp(host);
    }

    private static TaskCompletionSource NewSignal(bool set) {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (set) t.TrySetResult();
        return t;
    }

    // =====================================================================================
    // Connection
    // =====================================================================================

    public async Task ConnectAsync(CancellationToken token) {
        await ws.ConnectAsync(token);
        if (!HasMasterLock) await AcquireMasterLockAsync(token);
        if (!HasMasterLock) throw new DwarfCaptureException(
            "DWARF did not grant control (master lock). Close the DWARFLAB app on your phone and try again.");
        await QueryDeviceStateAsync(token);
        await SyncClockAsync(token);
    }

    public async Task DisconnectAsync() {
        try {
            if (Phase is CapturePhase.Configuring or CapturePhase.Starting or CapturePhase.Exposing or CapturePhase.Transferring)
                await AbortExposureAsync(CancellationToken.None);
        } catch { }
        try {
            if (ws.IsConnected && HasMasterLock) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws.RequestAsync(ModSystem, CmdSetMaster, new ReqsetMasterLock { Lock = false },
                    TimeSpan.FromSeconds(5), HostSlaveAlternates, cts.Token);
                DwarfLog.Info("Master lock released");
            }
        } catch (Exception ex) {
            DwarfLog.Debug($"Master lock release failed: {ex.Message}");
        }
        HasMasterLock = false;
        await ws.CloseAsync();
    }

    public async ValueTask DisposeAsync() {
        await DisconnectAsync();
        http.Dispose();
    }

    private async Task AcquireMasterLockAsync(CancellationToken token) {
        var packet = await ws.RequestAsync(ModSystem, CmdSetMaster, new ReqsetMasterLock { Lock = true },
            TimeSpan.FromSeconds(15), HostSlaveAlternates, token);
        if (packet.Cmd == CmdHostSlave) {
            var mode = Notify.HostSlaveMode.Parser.ParseFrom(packet.Data);
            HasMasterLock = mode.Mode == 0 && mode.Lock;
            DwarfLog.Info($"Master lock notification: mode={mode.Mode} lock={mode.Lock}");
        } else {
            var res = ComResponse.Parser.ParseFrom(packet.Data);
            if (res.Code != CodeOk) throw new DwarfCommandException(ModSystem, CmdSetMaster, res.Code);
            HasMasterLock = true;
        }
        if (HasMasterLock) DwarfLog.Info($"Master lock acquired on {Host}");
    }

    private async Task SyncClockAsync(CancellationToken token) {
        try {
            var offsetHours = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours;
            offsetHours = Math.Round(offsetHours * 4.0) / 4.0;
            await SendAndCheckAsync(ModSystem, CmdSetTime,
                new ReqSetTime { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), TimezoneOffset = offsetHours },
                TimeSpan.FromSeconds(5), null, token);
            var iana = TimeZoneInfo.Local.Id;
            if (!iana.Contains('/') && TimeZoneInfo.TryConvertWindowsIdToIanaId(iana, out var converted)) iana = converted;
            if (iana.Contains('/')) {
                await SendAndCheckAsync(ModSystem, CmdSetTimeZone, new ReqSetTimezone { Timezone = iana },
                    TimeSpan.FromSeconds(5), null, token);
            }
            DwarfLog.Info($"Clock synced (UTC offset {offsetHours:+0.##;-0.##}h, {iana})");
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            DwarfLog.Warn($"Clock sync failed (continuing): {ex.Message}");
        }
    }

    private async Task<int?> QueryDeviceStateAsync(CancellationToken token) {
        try {
            var packet = await RequestAsync(ModDeviceConfig, CmdDeviceState, new ReqGetDeviceStateInfo(),
                TimeSpan.FromSeconds(5), null, token);
            var res = ResGetDeviceStateInfo.Parser.ParseFrom(packet.Data);
            var tele = res.TeleCameraStateInfo;
            if (tele != null) {
                if (tele.CmosTemperature is { HasTemperature: true } t) TemperatureC = t.Temperature;
                if (tele.ExclusiveState?.CurrentStateCase == ExclusiveCameraState.CurrentStateOneofCase.CaptureRawState) {
                    SetOperationState((int)tele.ExclusiveState.CaptureRawState.State, "device_state_16405");
                    return operationState;
                }
            }
        } catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) {
            DwarfLog.Debug($"Device state query failed: {ex.Message}");
        }
        return null;
    }

    // =====================================================================================
    // Command helpers
    // =====================================================================================

    private async Task<WsPacket> RequestAsync(int module, int cmd, IMessage request, TimeSpan timeout,
        IEnumerable<(int, int)>? alternates, CancellationToken token) {
        await commandLock.WaitAsync(token);
        try {
            DwarfLog.Debug($"-> {module}:{cmd} {request}");
            var packet = await ws.RequestAsync(module, cmd, request, timeout, alternates, token);
            DwarfLog.Debug($"<- {packet.ModuleId}:{packet.Cmd} ({packet.Data.Length} bytes)");
            return packet;
        } finally {
            commandLock.Release();
        }
    }

    private async Task<Task<WsPacket>> BeginRequestAsync(int module, int cmd, IMessage request, CancellationToken token) {
        await commandLock.WaitAsync(token);
        try {
            DwarfLog.Debug($"=> {module}:{cmd} {request} (async)");
            return await ws.BeginRequestAsync(module, cmd, request, null, token);
        } finally {
            commandLock.Release();
        }
    }

    /// <summary>Send and require ComResponse.code == 0 (an alternate notification counts as success).</summary>
    private async Task SendAndCheckAsync(int module, int cmd, IMessage request, TimeSpan timeout,
        IEnumerable<(int, int)>? alternates, CancellationToken token) {
        var packet = await RequestAsync(module, cmd, request, timeout, alternates, token);
        if ((int)packet.ModuleId == module && (int)packet.Cmd == cmd) {
            var code = ComResponse.Parser.ParseFrom(packet.Data).Code;
            if (code != CodeOk) throw new DwarfCommandException(module, cmd, code);
        }
    }

    // =====================================================================================
    // Notifications
    // =====================================================================================

    private void OnNotification(WsPacket packet) {
        var module = (int)packet.ModuleId;
        var cmd = (int)packet.Cmd;
        try {
            if (cmd == CmdGeneralIntNotify && (module == ModParams || module == ModNotify)) {
                var p = Notify.GeneralIntParam.Parser.ParseFrom(packet.Data);
                var modeId = (int)((p.ParamId >> ParamModeShift) & 0xFF);
                var key = p.ParamId & ParamValueMask;
                if (captureId != null && Phase is CapturePhase.Starting or CapturePhase.Exposing
                    && modeId != AstroCatalogueModeId
                    && (key == (AstroExposureParamId & ParamValueMask) || key == (AstroGainParamId & ParamValueMask))
                    && captureParamModeId != modeId) {
                    captureParamModeId = modeId;
                    DwarfLog.Info($"Capture parameter namespace detected: mode {modeId}");
                }
                return;
            }
            if (module != ModNotify) return;
            switch (cmd) {
                case CmdCaptureState: {
                    var state = packet.Data.Length == 0 ? 0 : (int)Notify.CaptureRawState.Parser.ParseFrom(packet.Data).State;
                    SetOperationState(state, "notification_15208");
                    break;
                }
                case CmdCaptureProgress: {
                    var m = Notify.ProgressCaptureRawLiveStacking.Parser.ParseFrom(packet.Data);
                    if (m.CurrentCount > 0) progressCount = Math.Max(progressCount, m.CurrentCount);
                    DwarfLog.Info($"Capture progress: {progressCount}/{requestedFrames} (total {m.TotalCount}, stacked {m.StackedCount})");
                    if (captureId != null && progressCount >= requestedFrames) frameCompleteTcs.TrySetResult();
                    break;
                }
                case CmdWaitShootingProgress: {
                    var (elapsed, total) = ReadTwoVarints(packet.Data);
                    var id = captureId;
                    // DWARF 3 can reload its stored preset during preparation; elapsed>=7 precedes
                    // the sensor exposure and is the last point to re-apply exact values.
                    if (elapsed >= 7 && id != null && liveParametersSupported != false && runtimeReapplyCaptureId != id
                        && Phase is CapturePhase.Starting or CapturePhase.Exposing && currentRequest is { } req) {
                        runtimeReapplyCaptureId = id;
                        _ = Task.Run(async () => {
                            try {
                                await ApplyRuntimeParametersAsync(req, captureParamModeId ?? AstroCaptureModeId, CancellationToken.None);
                                DwarfLog.Info($"Runtime parameters re-applied (elapsed {elapsed}/{total})");
                            } catch (Exception ex) {
                                DwarfLog.Warn($"Runtime parameter re-apply failed: {ex.Message}");
                            }
                        });
                    }
                    break;
                }
                case CmdCmosTemperature: {
                    var t = Notify.CmosTemperature.Parser.ParseFrom(packet.Data);
                    if (t.HasTemperature) TemperatureC = t.Temperature;
                    break;
                }
            }
        } catch (Exception ex) {
            DwarfLog.Debug($"Notification {module}:{cmd} decode failed: {ex.Message}");
        }
    }

    private void SetOperationState(int value, string source) {
        operationState = value;
        var ready = value is 0 or 3;
        if (ready) readyTcs.TrySetResult();
        else if (readyTcs.Task.IsCompleted) readyTcs = NewSignal(set: false);
        DwarfLog.Info($"Capture state {value} ({(ready ? "ready" : "busy")}) from {source}");
    }

    private static (int, int) ReadTwoVarints(ByteString data) {
        int f1 = 0, f2 = 0;
        try {
            var input = new CodedInputStream(data.ToByteArray());
            uint tag;
            while ((tag = input.ReadTag()) != 0) {
                var field = WireFormat.GetTagFieldNumber(tag);
                if (WireFormat.GetTagWireType(tag) != WireFormat.WireType.Varint) { input.SkipLastField(); continue; }
                var v = input.ReadInt32();
                if (field == 1) f1 = v; else if (field == 2) f2 = v;
            }
        } catch { }
        return (f1, f2);
    }

    // =====================================================================================
    // Capture
    // =====================================================================================

    public bool IsImageReady => Phase == CapturePhase.Ready && LastImage != null;

    /// <summary>Configure and start a single astronomy exposure. Returns once the DWARF accepted the start;
    /// the frame is retrieved in the background (see <see cref="WaitForImageAsync"/>).</summary>
    public async Task StartExposureAsync(CaptureRequest request, CancellationToken token) {
        await captureLock.WaitAsync(token);
        try {
            if (Phase is CapturePhase.Configuring or CapturePhase.Starting or CapturePhase.Exposing or CapturePhase.Transferring)
                throw new DwarfCaptureException("An exposure is already in progress");
            await WaitForCaptureReadyAsync(TimeSpan.FromSeconds(75), token);

            captureId = Guid.NewGuid().ToString("N");
            currentRequest = request;
            requestedFrames = 1;
            progressCount = 0;
            captureParamModeId = null;
            runtimeReapplyCaptureId = null;
            frameCompleteTcs = NewSignal(set: false);
            LastImage = null;
            LastError = null;
            Phase = CapturePhase.Configuring;

            try {
                await EnterAstroModeAsync(token);
                var controls = await ResolveAstroControlsAsync(request, token);
                AppliedExposure = controls.ExposureSeconds;
                AppliedGain = controls.Gain;
                await GoLiveAsync(token);
                await CheckDarkLibraryAsync(token);
                await ConfigureAstroCaptureAsync(controls, token);

                Phase = CapturePhase.Starting;
                var albumBaseline = await LatestAlbumModTimeAsync(token);
                var startedAt = DateTime.Now;
                await StartAstroCaptureAsync(request, token);

                if (liveParametersSupported == true) {
                    try {
                        await ApplyRuntimeParametersAsync(request, AstroCaptureModeId, token);
                    } catch (Exception ex) when (ex is not OperationCanceledException) {
                        // 11005 is already running; never fail the exposure for this best-effort correction.
                        DwarfLog.Warn($"Runtime parameter correction failed: {ex.Message}");
                    }
                }
                Phase = CapturePhase.Exposing;
                DwarfLog.Info($"Exposure started: {controls.ExposureSeconds:g}s gain {controls.Gain} filter #{request.FilterPosition}");

                captureCts = new CancellationTokenSource();
                var id = captureId;
                captureTask = Task.Run(() => RetrieveFrameAsync(id!, request, startedAt, albumBaseline, captureCts.Token));
            } catch (Exception ex) {
                Phase = CapturePhase.Failed;
                LastError = ex.Message;
                captureId = null;
                throw;
            }
        } finally {
            captureLock.Release();
        }
    }

    public async Task<FitsImage> WaitForImageAsync(CancellationToken token) {
        var task = captureTask ?? throw new DwarfCaptureException("No exposure has been started");
        await task.WaitAsync(token);
        if (LastImage == null) throw new DwarfCaptureException(LastError ?? "The DWARF did not deliver an image");
        return LastImage;
    }

    public async Task AbortExposureAsync(CancellationToken token) {
        Phase = CapturePhase.Aborting;
        captureId = null;
        captureCts?.Cancel();
        try { await StopAstroCaptureAsync(token); } catch (Exception ex) { DwarfLog.Warn($"Abort: stop failed: {ex.Message}"); }
        LastError = "aborted";
        Phase = CapturePhase.Idle;
    }

    private async Task WaitForCaptureReadyAsync(TimeSpan timeout, CancellationToken token) {
        if (operationState is null or 0 or 3) return;
        DwarfLog.Info($"Waiting for the previous capture to finish (state {operationState})");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            try {
                await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                return;
            } catch (TimeoutException) {
                await QueryDeviceStateAsync(token);
                if (operationState is 0 or 3) return;
            }
        }
        throw new DwarfCommandException(ModAstro, CmdStartCapture, CodeBusy);
    }

    private async Task EnterAstroModeAsync(CancellationToken token) {
        var mode = ResSwitchShootingMode.Parser.ParseFrom((await RequestAsync(ModDeviceConfig, CmdSwitchMode,
            new ReqSwitchShootingMode { Mode = 8 }, TimeSpan.FromSeconds(8), null, token)).Data);
        if (mode.Code != CodeOk || mode.ShootingModeId != 8)
            throw new DwarfCaptureException($"DWARF did not select astronomy mode (code {mode.Code}, mode {mode.ShootingModeId})");

        try {
            var enter = ResEnterCamera.Parser.ParseFrom((await RequestAsync(ModDeviceConfig, CmdEnterCamera,
                new ReqEnterCamera { ClientParam = new ClientParams { EncodeType = 1 } }, TimeSpan.FromSeconds(8), null, token)).Data);
            if (enter.Code != CodeOk || (enter.ShootingModeId != 2 && enter.ShootingModeId != 8))
                throw new DwarfCaptureException($"DWARF did not enter the astronomy camera (code {enter.Code}, mode {enter.ShootingModeId})");
        } catch (TimeoutException) {
            // Tested DWARF 3 firmware can leave 16404 unacknowledged and still accept the camera commands.
            DwarfLog.Warn("Enter-camera (16404) not acknowledged; continuing");
        }

        var tech = ResSwitchShootingTech.Parser.ParseFrom((await RequestAsync(ModDeviceConfig, CmdSwitchTech,
            new ReqSwitchShootingTech { Tech = 2 }, TimeSpan.FromSeconds(8), null, token)).Data);
        if (tech.Code != CodeOk || tech.ShootingTechId != 2)
            throw new DwarfCaptureException($"DWARF did not select Deep Sky stacking (code {tech.Code}, technique {tech.ShootingTechId})");

        await SendAndCheckAsync(ModTele, CmdPreviewQuality, new ReqSetPreviewQuality { Level = 1 }, TimeSpan.FromSeconds(8), null, token);
    }

    private sealed record AstroControls(ulong ExposureParamId, int ExposureIndex, double ExposureSeconds, ulong GainParamId, int Gain);

    private async Task<AstroControls> ResolveAstroControlsAsync(CaptureRequest req, CancellationToken token) {
        paramCatalogue ??= await http.GetParamAndSettingAsync(AstroCatalogueModeId, token);
        var tele = (paramCatalogue["cameraParams"] as JsonArray)?.OfType<JsonObject>()
                       .FirstOrDefault(o => DwarfHttpApi.Long(o["cameraId"]) == 0)
                   ?? throw new DwarfCaptureException("Astronomy camera parameters are unavailable");
        var exp = tele["specialParams"]?["exp"] as JsonObject;
        var gain = tele["specialParams"]?["gain"] as JsonObject;
        if (exp == null || gain == null) throw new DwarfCaptureException("Astronomy exposure/gain controls are unavailable");

        var choices = new List<(double seconds, int index, string label)>();
        foreach (var item in (exp["values"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()) {
            var label = DwarfHttpApi.Str(item["name"])?.Trim() ?? "";
            var idx = DwarfHttpApi.Long(item["value"]);
            var secs = ParseExposureLabel(label);
            if (idx != null && secs is > 0) choices.Add((secs.Value, (int)idx.Value, label));
        }
        if (choices.Count == 0) throw new DwarfCaptureException("The DWARF reported no exposure values");
        var best = choices.MinBy(c => Math.Abs(Math.Log(c.seconds / Math.Max(req.DurationS, 1e-6))));
        if (Math.Abs(best.seconds - req.DurationS) > Math.Max(1e-6, req.DurationS * 1e-6))
            DwarfLog.Warn($"Exposure {req.DurationS:g}s is not a DWARF value; using {best.label} ({best.seconds:g}s)");

        var gains = (gain["values"] as JsonArray)?.Select(v => DwarfHttpApi.Long(v)).Where(v => v != null).Select(v => (int)v!.Value).ToList() ?? new();
        var g = req.Gain;
        if (gains.Count > 0 && !gains.Contains(g)) {
            var nearest = gains.MinBy(v => Math.Abs(v - g));
            DwarfLog.Warn($"Gain {g} is not supported; using {nearest}");
            g = nearest;
        }
        return new AstroControls(
            (ulong)(DwarfHttpApi.Long(exp["paramId"]) ?? (long)AstroExposureParamId), best.index, best.seconds,
            (ulong)(DwarfHttpApi.Long(gain["paramId"]) ?? (long)AstroGainParamId), g);
    }

    /// <summary>Parse firmware exposure labels such as "1/30", "5", "0.5s", "100ms".</summary>
    public static double? ParseExposureLabel(string label) {
        var s = label.Trim().ToLowerInvariant();
        var mult = 1.0;
        if (s.EndsWith("ms")) { mult = 0.001; s = s[..^2]; }
        else if (s.EndsWith("s")) s = s[..^1];
        s = s.Replace("seconds", "").Replace("second", "").Replace("sec", "").Replace("″", "").Replace("\"", "").Trim();
        if (s.Length == 0 || s.Any(char.IsLetter)) return null;
        var slash = s.IndexOf('/');
        if (slash > 0) {
            if (double.TryParse(s[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && double.TryParse(s[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
                return n / d * mult;
            return null;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v * mult : null;
    }

    private async Task GoLiveAsync(CancellationToken token) {
        try {
            await SendAndCheckAsync(ModAstro, CmdGoLive, new ReqGoLive(), TimeSpan.FromSeconds(5), null, token);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            DwarfLog.Warn($"Go-live failed (continuing): {ex.Message}");
        }
    }

    private async Task CheckDarkLibraryAsync(CancellationToken token) {
        try {
            var res = ResCheckDarkFrame.Parser.ParseFrom((await RequestAsync(ModAstro, CmdCheckDark, new ReqCheckDarkFrame(),
                TimeSpan.FromSeconds(5), null, token)).Data);
            if (res.Code == CodeOk) DwarfLog.Info("Dark library: ready");
            else DwarfLog.Warn($"Dark library not ready (code {res.Code}); continuing like the app's 'Continue'");
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            DwarfLog.Warn($"Dark library check failed (continuing): {ex.Message}");
        }
    }

    private async Task ApplyExposureGainAsync(ulong expParamId, int expIndex, ulong gainParamId, int gain, CancellationToken token) {
        await SendAndCheckAsync(ModParams, CmdSetExposure, new Param.ReqSetExposure { ParamId = expParamId, Mode = 1, Value = expIndex },
            TimeSpan.FromSeconds(10), GeneralIntAlternates, token);
        await SendAndCheckAsync(ModParams, CmdSetGain, new Param.ReqSetExposure { ParamId = gainParamId, Mode = 1, Value = gain },
            TimeSpan.FromSeconds(10), GeneralIntAlternates, token);
    }

    private async Task ConfigureAstroCaptureAsync(AstroControls c, CancellationToken token) {
        // Probe live exposure/gain writes first; firmware rejecting them (-1) falls back to the quick-set tuple.
        var live = true;
        try {
            await ApplyExposureGainAsync(c.ExposureParamId, c.ExposureIndex, c.GainParamId, c.Gain, token);
        } catch (DwarfCommandException ex) when (ex.Code == -1) {
            live = false;
            DwarfLog.Warn("Live exposure/gain parameters unsupported; using quick-set only");
        }
        liveParametersSupported = live;

        await ApplyQuickSetAsync(c, token);
        if (live) {
            // DWARF 3 otherwise reloads a stale stored preset when 11005 is dispatched.
            await ApplyExposureGainAsync(c.ExposureParamId, c.ExposureIndex, c.GainParamId, c.Gain, token);
            try {
                await SendAndCheckAsync(ModParams, CmdSetGeneralInt,
                    new Param.ReqSetGeneralIntParam { ParamId = AstroFrameCountParamId, Value = requestedFrames },
                    TimeSpan.FromSeconds(3), GeneralIntAlternates, token);
            } catch (DwarfCommandException ex) when (ex.Code == -1) {
                DwarfLog.Warn("Frame-count command unsupported");
            }
        }
    }

    private async Task ApplyQuickSetAsync(AstroControls c, CancellationToken token) {
        if (quickSetPresets == null) {
            var res = ResGetQuickSetList.Parser.ParseFrom((await RequestAsync(ModAstro, CmdQuickSetList,
                new ReqGetQuickSetList { CameraType = 0 }, TimeSpan.FromSeconds(8), null, token)).Data);
            if (res.Code != CodeOk) throw new DwarfCaptureException($"Astronomy preset discovery failed (code {res.Code})");
            quickSetPresets = res.QuickSetList.Select(q => q.InfoId.Split('|')).Where(p => p.Length >= 6).ToList();
            if (quickSetPresets.Count == 0) throw new DwarfCaptureException("The DWARF returned no astronomy presets");
        }
        double Exp(string[] p) => double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.MaxValue;
        int Gain(string[] p) => int.TryParse(p[3], out var v) ? v : int.MaxValue;
        var template = quickSetPresets
            .OrderBy(p => Math.Abs(Exp(p) - c.ExposureSeconds))
            .ThenBy(p => Math.Abs((long)Gain(p) - c.Gain))
            .First();
        var parts = (string[])template.Clone();
        // Field 2 = exposure, field 3 = gain. Field 4 is resolution - never the frame count.
        parts[2] = c.ExposureSeconds.ToString("G", CultureInfo.InvariantCulture);
        parts[3] = c.Gain.ToString(CultureInfo.InvariantCulture);
        var info = string.Join("|", parts);
        var set = ResSetQuickSet.Parser.ParseFrom((await RequestAsync(ModAstro, CmdQuickSet,
            new ReqSetQuickSet { InfoId = info }, TimeSpan.FromSeconds(8), null, token)).Data);
        if (set.Code != CodeOk) throw new DwarfCaptureException($"DWARF rejected the astronomy preset (code {set.Code})");
        DwarfLog.Info($"Quick set applied: {info}");
    }

    private async Task ApplyRuntimeParametersAsync(CaptureRequest req, int modeId, CancellationToken token) {
        var c = await ResolveAstroControlsAsync(req, token);
        ulong InMode(ulong id) => ((ulong)modeId << ParamModeShift) | (id & ParamValueMask);
        await ApplyExposureGainAsync(InMode(c.ExposureParamId), c.ExposureIndex, InMode(c.GainParamId), c.Gain, token);
        await SendAndCheckAsync(ModParams, CmdSetGeneralInt,
            new Param.ReqSetGeneralIntParam { ParamId = InMode(AstroFrameCountParamId), Value = requestedFrames },
            TimeSpan.FromSeconds(3), GeneralIntAlternates, token);
    }

    private async Task StartAstroCaptureAsync(CaptureRequest req, CancellationToken token) {
        var irIndex = Profile.HasFilterWheel
            ? Profile.FilterFirmwareIndices[Math.Clamp(req.FilterPosition, 0, Profile.FilterFirmwareIndices.Count - 1)]
            : -1;
        var responseTask = await BeginRequestAsync(ModAstro, CmdStartCapture,
            new ReqCaptureRawLiveStacking { IrIndex = irIndex, ForceStart = false }, token);
        SetOperationState(1, "start_11005_dispatch");

        // An immediate rejection arrives quickly; the final 11005 response can come much later.
        var first = await Task.WhenAny(responseTask, Task.Delay(1500, token));
        if (first == responseTask) {
            var code = ResAstroShooting.Parser.ParseFrom(responseTask.Result.Data).Code;
            if (code is CodeDarkNotFound or CodeDarkTempMismatch) {
                await ContinueAfterDarkWarningAsync(irIndex, code, token);
            } else if (code != CodeOk && code != CodeNeedGoto && code != CodeNeedAdjust) {
                SetOperationState(0, "start_rejected");
                throw new DwarfCommandException(ModAstro, CmdStartCapture, code);
            }
            return;
        }
        var monitorTimeout = TimeSpan.FromSeconds(Math.Max(20, req.DurationS + 60));
        var id = captureId;
        _ = Task.Run(async () => {
            try {
                var packet = await responseTask.WaitAsync(monitorTimeout);
                var code = ResAstroShooting.Parser.ParseFrom(packet.Data).Code;
                if (code is CodeDarkNotFound or CodeDarkTempMismatch) {
                    await ContinueAfterDarkWarningAsync(irIndex, code, CancellationToken.None);
                } else if (code != CodeOk && code != CodeNeedGoto && code != CodeNeedAdjust && captureId == id && LastImage == null) {
                    LastError = $"DWARF rejected the exposure (code {code})";
                    DwarfLog.Warn(LastError);
                    captureCts?.Cancel();
                } else {
                    DwarfLog.Info($"Capture start response: code {code}");
                }
            } catch (Exception ex) {
                ws.CancelPending(ModAstro, CmdStartCapture);
                DwarfLog.Debug($"Capture start response not received: {ex.Message}");
            }
        });
    }

    private async Task ContinueAfterDarkWarningAsync(int irIndex, int code, CancellationToken token) {
        DwarfLog.Warn($"Dark frames missing/mismatched (code {code}); continuing without darks");
        var packet = await RequestAsync(ModAstro, CmdContinueShooting, new ReqContinueShooting(), TimeSpan.FromSeconds(30), null, token);
        var c = ComResponse.Parser.ParseFrom(packet.Data).Code;
        if (c is -1 or -3) {
            // Older protocol: retry the start with force_start.
            await SendAndCheckAsync(ModAstro, CmdStartCapture, new ReqCaptureRawLiveStacking { IrIndex = irIndex, ForceStart = true },
                TimeSpan.FromSeconds(30), null, token);
        } else if (c != CodeOk) {
            throw new DwarfCommandException(ModAstro, CmdContinueShooting, c);
        }
    }

    private async Task StopAstroCaptureAsync(CancellationToken token) {
        ws.CancelPending(ModAstro, CmdStartCapture);
        if (readyTcs.Task.IsCompleted) readyTcs = NewSignal(set: false);
        operationState = 2;
        var response = await BeginRequestAsync(ModAstro, CmdStopCapture, new ReqStopCaptureRawLiveStacking(), token);
        var done = await Task.WhenAny(response, readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(75), token));
        if (done == response && response.IsCompletedSuccessfully) {
            var code = ComResponse.Parser.ParseFrom(response.Result.Data).Code;
            DwarfLog.Info($"Stop response: code {code}");
            if (code == CodeOk) SetOperationState(3, "stop_response");
        } else if (done == readyTcs.Task) {
            ws.CancelPending(ModAstro, CmdStopCapture);
            DwarfLog.Info("Capture stopped (state notification)");
        } else {
            ws.CancelPending(ModAstro, CmdStopCapture);
            DwarfLog.Warn("Stop not confirmed within 75 s");
        }
    }

    private async Task<long?> LatestAlbumModTimeAsync(CancellationToken token) {
        var entries = await http.ListAlbumMediaInfosAsync(4, 1, token);
        return entries.Count > 0 ? DwarfHttpApi.Long(entries[0]["modificationTime"]) : null;
    }

    private async Task RetrieveFrameAsync(string id, CaptureRequest req, DateTime startedAt, long? albumBaseline, CancellationToken token) {
        try {
            Phase = CapturePhase.Exposing;
            var budget = TimeSpan.FromSeconds(Math.Max(req.DurationS + 55, 58));
            using var ftpCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var ftpTask = ftp.WaitForNewFitsAsync(lastFtpEntry, startedAt.AddSeconds(-5), budget, ftpCts.Token);
            var progressTask = frameCompleteTcs.Task;

            var first = await Task.WhenAny(ftpTask, progressTask, Task.Delay(budget, token));
            DwarfLog.Info(first == progressTask ? "Frame complete (progress notification)"
                : first == ftpTask ? "New FITS found on the DWARF" : "Capture timed out waiting for completion");
            Phase = CapturePhase.Transferring;
            await StopAstroCaptureAsync(token);

            FitsImage? image = null;
            // Give FTP a short grace period after the stop, then fall back to the HTTP album route.
            if (await Task.WhenAny(ftpTask, Task.Delay(TimeSpan.FromSeconds(20), token)) != ftpTask) {
                ftpCts.Cancel();
                DwarfLog.Info("FTP did not deliver the FITS; trying the album (HTTP) route");
            }
            (FtpFitsEntry entry, byte[] content)? ftpResult = null;
            try { ftpResult = await ftpTask; } catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            if (ftpResult is { } r) {
                lastFtpEntry = r.entry;
                image = FitsImage.Parse(r.content);
                DwarfLog.Info($"FITS downloaded via FTP: {r.entry.Path} ({r.content.Length} bytes)");
            } else {
                image = await TryAlbumFitsAsync(albumBaseline, token);
            }
            if (captureId != id) return;    // aborted meanwhile
            if (image == null) {
                LastError ??= "The DWARF did not deliver a FITS file";
                Phase = CapturePhase.Failed;
                return;
            }
            LastImage = image;
            Phase = CapturePhase.Ready;
            DwarfLog.Info($"Image ready: {image.Width}x{image.Height}, max {image.MaxValue}");
            await GoLiveAsync(CancellationToken.None);
        } catch (OperationCanceledException) {
            if (Phase != CapturePhase.Idle) { LastError ??= "cancelled"; Phase = CapturePhase.Failed; }
        } catch (Exception ex) {
            LastError = ex.Message;
            Phase = CapturePhase.Failed;
            DwarfLog.Warn($"Frame retrieval failed: {ex.Message}");
        } finally {
            if (captureId == id) captureId = null;
        }
    }

    private async Task<FitsImage?> TryAlbumFitsAsync(long? baseline, CancellationToken token) {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested) {
            var entries = await http.ListAlbumMediaInfosAsync(4, 1, token);
            var entry = entries.FirstOrDefault();
            var mod = entry == null ? null : DwarfHttpApi.Long(entry["modificationTime"]);
            if (entry != null && (baseline == null || mod > baseline)) {
                var srcDir = DwarfHttpApi.Str(entry["astroImageDetails"]?["srcDir"]) ?? DwarfHttpApi.Str(entry["srcDir"]);
                if (!string.IsNullOrWhiteSpace(srcDir)) {
                    var fits = (await http.ListAstroFitsAsync(srcDir.Trim(), token))
                        .Where(f => f["isFailed"]?.GetValue<bool>() != true)
                        .Select(f => DwarfHttpApi.Str(f["filePath"]) ?? DwarfHttpApi.Str(f["url"]))
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    if (fits.Count > 0) {
                        var bytes = await http.FetchMediaFileAsync(fits[^1]!, token);
                        DwarfLog.Info($"FITS downloaded via album: {fits[^1]} ({bytes.Length} bytes)");
                        return FitsImage.Parse(bytes);
                    }
                }
            }
            await Task.Delay(1000, token);
        }
        return null;
    }
}
