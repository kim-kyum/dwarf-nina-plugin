// MockDwarf - a fake DWARF 3 for local end-to-end testing of Dwarf.Core (development only).
// Implements just enough of the control WebSocket (9900), HTTP API (8082) and file server (80)
// to run: master lock -> astro mode -> parameters -> 11005 (with dark warning) -> progress -> 11006 -> album FITS.
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dwarf.Core.Proto;
using Google.Protobuf;
using N = Dwarf.Core.Proto.Notify;

var bind = args.Length > 0 ? args[0] : "127.0.0.1";
long albumMod = 1_700_000_000;
var fits = MakeFits(64, 48);
var exposureSeconds = 1.0;

var http = new HttpListener();
http.Prefixes.Add($"http://{bind}:9900/");
http.Prefixes.Add($"http://{bind}:8082/");
http.Prefixes.Add($"http://{bind}:80/");
http.Start();
Console.WriteLine("MockDwarf listening on 9900 (ws), 8082 (api), 80 (files)");

while (true) {
    var ctx = await http.GetContextAsync();
    _ = Task.Run(() => Handle(ctx));
}

async Task Handle(HttpListenerContext ctx) {
    try {
        var port = ctx.Request.LocalEndPoint.Port;
        if (port == 9900 && ctx.Request.IsWebSocketRequest) { await HandleWs(ctx); return; }
        var path = ctx.Request.Url!.AbsolutePath;
        var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
        Console.WriteLine($"HTTP {port} {ctx.Request.HttpMethod} {path} {body}");
        object? reply = null;
        if (port == 8082 && path == "/shootingMode/getParamAndSetting") {
            reply = new { code = 0, data = new { cameraParams = new[] { new {
                cameraId = 0,
                specialParams = new {
                    exp = new { paramId = 0x0201000000000001L, values = new object[] {
                        new { name = "1/1000", value = 10 }, new { name = "1", value = 120 }, new { name = "5", value = 141 }, new { name = "15", value = 156 } } },
                    gain = new { paramId = 0x0201000000000002L, values = new[] { 0, 10, 20, 40, 60, 80, 100, 120, 150, 200 } },
                } } } } };
        } else if (port == 8082 && path == "/album/list/mediaInfos") {
            reply = new { code = 0, data = new { mediaInfos = new[] { new { modificationTime = albumMod, astroImageDetails = new { srcDir = $"/Astronomy/DWARF_RAW_TELE_M42_EXP_1_GAIN_80_{albumMod}" } } } } };
        } else if (port == 8082 && path == "/album/astro/fitsList") {
            reply = new { code = 0, data = new { fitsInfo = new[] { new { filePath = "/sdcard/Astronomy/raw_0001.fits", isFailed = false } } } };
        } else if (port == 80 && path.EndsWith(".fits")) {
            ctx.Response.OutputStream.Write(fits);
            ctx.Response.Close();
            return;
        }
        if (reply == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reply);
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    } catch (Exception ex) {
        Console.WriteLine("handler error: " + ex);
    }
}

async Task HandleWs(HttpListenerContext ctx) {
    var wsCtx = await ctx.AcceptWebSocketAsync(null);
    var ws = wsCtx.WebSocket;
    var sendLock = new SemaphoreSlim(1, 1);
    Console.WriteLine("WS client connected");

    async Task Send(int module, int cmd, int type, IMessage msg) {
        var p = new WsPacket { MajorVersion = 1, MinorVersion = 20, DeviceId = 4, ModuleId = (uint)module, Cmd = (uint)cmd, Type = (uint)type, Data = msg.ToByteString() };
        await sendLock.WaitAsync();
        try { await ws.SendAsync(p.ToByteArray(), WebSocketMessageType.Binary, true, CancellationToken.None); }
        finally { sendLock.Release(); }
    }
    Task Respond(WsPacket req, IMessage msg) => Send((int)req.ModuleId, (int)req.Cmd, 1, msg);
    Task Ok(WsPacket req) => Respond(req, new ComResponse { Code = 0 });
    Task Notify(int cmd, IMessage msg, int module = 9) => Send(module, cmd, 2, msg);

    var buf = new byte[65536];
    while (ws.State == WebSocketState.Open) {
        var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do {
            r = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return;
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        var req = WsPacket.Parser.ParseFrom(ms.ToArray());
        Console.WriteLine($"WS <- {req.ModuleId}:{req.Cmd} client={req.ClientId} v{req.MajorVersion}.{req.MinorVersion} dev{req.DeviceId}");
        switch ((int)req.Cmd) {
            case 13004: await Notify(15223, new N.HostSlaveMode { Mode = 0, Lock = true }); break; // lock via notification path
            case 16405: await Respond(req, new ResGetDeviceStateInfo { TeleCameraStateInfo = new TeleCameraStateInfo {
                    CmosTemperature = new N.CmosTemperature { Temperature = 21 },
                    ExclusiveState = new ExclusiveCameraState { CaptureRawState = new N.CaptureRawState { State = N.OperationState.Idle } } } }); break;
            case 16402: await Respond(req, new ResSwitchShootingMode { Code = 0, ShootingModeId = 8 }); break;
            case 16404: break;   // DWARF 3 firmware silently ignores enter-camera: exercise the timeout path
            case 16403: await Respond(req, new ResSwitchShootingTech { Code = 0, ShootingTechId = 2 }); break;
            case 11009: await Respond(req, new ResCheckDarkFrame { Code = -11503 }); break;
            case 16700:
            case 16701:
            case 16703: {
                // Real firmware answers parameter writes with a 15264 notification from module 15.
                var p = Dwarf.Core.Proto.Param.ReqSetExposure.Parser.ParseFrom(req.Data);
                await Notify(15264, new N.GeneralIntParam { ParamId = p.ParamId, Mode = 1, Value = p.Value }, module: 15);
                break;
            }
            case 11040: await Respond(req, new ResGetQuickSetList { Code = 0, QuickSetList = {
                    new QuikSetInfo { InfoId = "0|2|15|80|0|0" }, new QuikSetInfo { InfoId = "0|2|5|60|0|0" } } }); break;
            case 11041: await Respond(req, new ResSetQuickSet { Code = 0 }); break;
            case 11005: {
                var start = ReqCaptureRawLiveStacking.Parser.ParseFrom(req.Data);
                Console.WriteLine($"   start capture ir_index={start.IrIndex} force={start.ForceStart}");
                await Task.Delay(200);
                await Respond(req, new ResAstroShooting { Code = -11503 });   // dark missing -> client must send 11050
                break;
            }
            case 11050: {
                await Ok(req);
                await Notify(15208, new N.CaptureRawState { State = N.OperationState.Running });
                _ = Task.Run(async () => {
                    await Task.Delay(TimeSpan.FromSeconds(exposureSeconds + 1));
                    albumMod += 10;
                    await Notify(15209, new N.ProgressCaptureRawLiveStacking { TotalCount = 999, CurrentCount = 1, StackedCount = 0 });
                });
                break;
            }
            case 11006:
                await Notify(15208, new N.CaptureRawState { State = N.OperationState.Stopping });
                await Task.Delay(300);
                await Notify(15208, new N.CaptureRawState { State = N.OperationState.Stopped });
                await Ok(req);
                break;
            default: await Ok(req); break;   // 13000, 13001, 13004 legacy, 10050, 11010 ...
        }
    }
}

static byte[] MakeFits(int w, int h) {
    var hdr = new StringBuilder();
    foreach (var c in new[] { "SIMPLE  =                    T", "BITPIX  =                   16", "NAXIS   =                    2",
                 $"NAXIS1  = {w,20}", $"NAXIS2  = {h,20}", "BZERO   =                32768", "END" })
        hdr.Append(c.PadRight(80));
    while (hdr.Length % 2880 != 0) hdr.Append(' ');
    var data = new byte[w * h * 2];
    for (var i = 0; i < w * h; i++) {
        var v = (short)((i % 4096) - 32768);
        data[2 * i] = (byte)(v >> 8); data[2 * i + 1] = (byte)v;
    }
    var all = Encoding.ASCII.GetBytes(hdr.ToString()).Concat(data).ToList();
    while (all.Count % 2880 != 0) all.Add(0);
    return all.ToArray();
}
