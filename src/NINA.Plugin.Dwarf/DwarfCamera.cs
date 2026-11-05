using Dwarf.Core;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace DwarfNina.Plugin;

/// <summary>NINA camera driver that talks to the DWARF directly over Wi-Fi.</summary>
public class DwarfCamera : BaseINPC, ICamera {
    private readonly DwarfSettings settings;
    private readonly IExposureDataFactory exposureDataFactory;
    private readonly DeviceProfile profile;
    private DwarfSession? session;
    private Task? startTask;
    private CameraStates state = CameraStates.NoState;
    private int gain;

    public DwarfCamera(DwarfSettings settings, IExposureDataFactory exposureDataFactory) {
        this.settings = settings;
        this.exposureDataFactory = exposureDataFactory;
        profile = settings.Profile;
        gain = Math.Clamp(settings.DefaultGain, profile.Camera.MinGain, profile.Camera.MaxGain);
        BinningModes = new AsyncObservableCollection<BinningMode> { new BinningMode(1, 1) };
    }

    // ------------------------------------------------------------------ IDevice
    public string Id => $"DWARF-{profile.ModelId}-camera";
    public string Name => $"{profile.DisplayName} Camera";
    public string DisplayName => Name;
    public string Category => "DWARFLAB";
    public string Description => $"{profile.DisplayName} over Wi-Fi (unofficial DWARF plugin)";
    public string DriverInfo => "DWARF NINA plugin - protocol ported from dwarfAlp";
    public string DriverVersion => typeof(DwarfCamera).Assembly.GetName().Version?.ToString() ?? "0.1";
    public IList<string> SupportedActions => new List<string>();
    public bool HasSetupDialog => true;
    public bool Connected => session?.IsConnected == true;

    public async Task<bool> Connect(CancellationToken token) {
        try {
            CameraState = CameraStates.Waiting;
            var host = await DwarfDiscovery.FindAsync(settings.Host, token);
            if (host == null) {
                Notification.ShowError($"{profile.DisplayName}를 찾지 못했습니다. PC가 DWARF Wi-Fi 또는 같은 공유기에 연결돼 있는지 확인하세요. " +
                                       "장비 옆 설정(톱니) 버튼에서 IP를 직접 입력할 수도 있습니다.");
                CameraState = CameraStates.NoState;
                return false;
            }
            var s = new DwarfSession(host, profile);
            s.ConnectionLost += OnConnectionLost;
            await s.ConnectAsync(token);
            session = s;
            if (settings.Host != host) { settings.Host = host; settings.Save(); }
            CameraState = CameraStates.Idle;
            RaiseAllPropertiesChanged();
            Notification.ShowSuccess($"{profile.DisplayName} 연결됨 ({host})");
            return true;
        } catch (OperationCanceledException) {
            CameraState = CameraStates.NoState;
            return false;
        } catch (Exception ex) {
            Logger.Error("[DWARF] Connect failed", ex);
            Notification.ShowError($"{profile.DisplayName} 연결 실패: {ex.Message}");
            CameraState = CameraStates.NoState;
            return false;
        }
    }

    public void Disconnect() {
        var s = session;
        session = null;
        if (s != null) {
            s.ConnectionLost -= OnConnectionLost;
            _ = s.DisposeAsync().AsTask();
        }
        CameraState = CameraStates.NoState;
        RaiseAllPropertiesChanged();
    }

    private void OnConnectionLost() {
        Logger.Warning("[DWARF] Connection lost");
        CameraState = CameraStates.Error;
        RaisePropertyChanged(nameof(Connected));
    }

    public void SetupDialog() {
        if (SetupWindow.Show(settings)) settings.Save();
    }

    public string Action(string actionName, string actionParameters) => throw new NotSupportedException();
    public string SendCommandString(string command, bool raw) => throw new NotSupportedException();
    public bool SendCommandBool(string command, bool raw) => throw new NotSupportedException();
    public void SendCommandBlind(string command, bool raw) => throw new NotSupportedException();

    // ------------------------------------------------------------------ sensor description
    public string SensorName => profile.Camera.SensorName;
    public SensorType SensorType => SensorType.RGGB;
    public short BayerOffsetX => 0;
    public short BayerOffsetY => 0;
    public int CameraXSize => profile.Camera.Width;
    public int CameraYSize => profile.Camera.Height;
    public double PixelSizeX => profile.Camera.PixelSizeUm;
    public double PixelSizeY => profile.Camera.PixelSizeUm;
    public double ExposureMin => profile.Camera.MinExposureS;
    public double ExposureMax => profile.Camera.MaxExposureS;
    public int BitDepth => profile.Camera.AdcBits;
    public double ElectronsPerADU => profile.Camera.ElectronsPerAdu;
    public bool HasShutter => false;

    public CameraStates CameraState {
        get => state;
        private set { state = value; RaisePropertyChanged(); }
    }

    // ------------------------------------------------------------------ gain / offset / readout
    public bool CanGetGain => true;
    public bool CanSetGain => true;
    public int GainMin => profile.Camera.MinGain;
    public int GainMax => profile.Camera.MaxGain;
    public int Gain {
        get => gain;
        set { gain = Math.Clamp(value, GainMin, GainMax); RaisePropertyChanged(); }
    }
    public IList<int> Gains => new List<int>();

    public bool CanSetOffset => false;
    public int Offset { get => 0; set { } }
    public int OffsetMin => 0;
    public int OffsetMax => 0;

    public bool CanSetUSBLimit => false;
    public int USBLimit { get => 0; set { } }
    public int USBLimitMin => 0;
    public int USBLimitMax => 0;
    public int USBLimitStep => 1;

    public IList<string> ReadoutModes => new List<string> { "Default" };
    public short ReadoutMode { get => 0; set { } }
    public short ReadoutModeForSnapImages { get => 0; set { } }
    public short ReadoutModeForNormalImages { get => 0; set { } }

    // ------------------------------------------------------------------ binning / subsample
    public short BinX { get => 1; set { } }
    public short BinY { get => 1; set { } }
    public short MaxBinX => 1;
    public short MaxBinY => 1;
    public AsyncObservableCollection<BinningMode> BinningModes { get; }
    public void SetBinning(short x, short y) { }

    public bool CanSubSample => false;
    public bool EnableSubSample { get => false; set { } }
    public int SubSampleX { get => 0; set { } }
    public int SubSampleY { get => 0; set { } }
    public int SubSampleWidth { get => CameraXSize; set { } }
    public int SubSampleHeight { get => CameraYSize; set { } }
    public void UpdateSubSampleArea() { }

    // ------------------------------------------------------------------ cooling / power
    public double Temperature => session?.TemperatureC ?? double.NaN;
    public double TemperatureSetPoint { get => double.NaN; set { } }
    public bool CanSetTemperature => false;
    public bool CoolerOn { get => false; set { } }
    public double CoolerPower => double.NaN;
    public bool HasDewHeater => false;
    public bool DewHeaterOn { get => false; set { } }
    public bool HasBattery => false;
    public int BatteryLevel => -1;

    // ------------------------------------------------------------------ live view (not supported yet)
    public bool CanShowLiveView => false;
    public bool LiveViewEnabled => false;
    public void StartLiveView(CaptureSequence sequence) => throw new NotSupportedException();
    public Task<IExposureData> DownloadLiveView(CancellationToken token) => throw new NotSupportedException();
    public void StopLiveView() { }

    // ------------------------------------------------------------------ exposure
    public void StartExposure(CaptureSequence sequence) {
        var s = session ?? throw new InvalidOperationException("DWARF camera is not connected");
        var light = sequence.ImageType == CaptureSequence.ImageTypes.LIGHT || sequence.ImageType == CaptureSequence.ImageTypes.SNAPSHOT;
        if (!light) {
            Logger.Warning($"[DWARF] {sequence.ImageType} frames are captured with the normal astronomy workflow (the DWARF has no shutter)");
        }
        var requestGain = sequence.Gain >= 0 ? sequence.Gain : Gain;
        var request = new DwarfSession.CaptureRequest(sequence.ExposureTime, requestGain, settings.DefaultFilter, light);
        CameraState = CameraStates.Exposing;
        startTask = Task.Run(() => s.StartExposureAsync(request, CancellationToken.None));
    }

    public async Task WaitUntilExposureIsReady(CancellationToken token) {
        var s = session ?? throw new InvalidOperationException("DWARF camera is not connected");
        try {
            if (startTask != null) await startTask.WaitAsync(token);
            CameraState = CameraStates.Exposing;
            await s.WaitForImageAsync(token);
            CameraState = CameraStates.Download;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            CameraState = CameraStates.Error;
            Notification.ShowError($"DWARF 촬영 실패: {ex.Message}");
            throw;
        }
    }

    public Task<IExposureData> DownloadExposure(CancellationToken token) {
        var s = session ?? throw new InvalidOperationException("DWARF camera is not connected");
        var img = s.LastImage ?? throw new InvalidOperationException("No DWARF image is available");
        // DWARF 3 FITS hold 12-bit values; treat anything larger as full 16-bit data.
        var depth = img.MaxValue > 4095 ? 16 : BitDepth;
        IExposureData data = exposureDataFactory.CreateImageArrayExposureData(
            img.Pixels, img.Width, img.Height, depth, isBayered: true, metaData: new ImageMetaData());
        CameraState = CameraStates.Idle;
        return Task.FromResult(data);
    }

    public void StopExposure() => AbortExposure();

    public void AbortExposure() {
        var s = session;
        if (s == null) return;
        _ = Task.Run(async () => {
            try { await s.AbortExposureAsync(CancellationToken.None); } catch (Exception ex) { Logger.Warning($"[DWARF] Abort failed: {ex.Message}"); }
        });
        CameraState = CameraStates.Idle;
    }

}
