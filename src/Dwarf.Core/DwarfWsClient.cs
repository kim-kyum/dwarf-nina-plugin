// DWARF control-plane WebSocket client.
// Protocol behaviour (WsPacket envelope, (module, cmd) keyed request matching, alternate
// response keys, notification fan-out) follows dwarfAlp's ws_client.py (GPL-3.0).
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Dwarf.Core.Proto;
using Google.Protobuf;

namespace Dwarf.Core;

public sealed class DwarfWsClient : IAsyncDisposable {
    public const int TypeRequest = 0;
    public const int TypeResponse = 1;
    public const int TypeNotification = 2;
    public const int TypeNotificationResponse = 3;

    private readonly Uri uri;
    private readonly DeviceProfile profile;
    private ClientWebSocket? socket;
    private CancellationTokenSource? readerCts;
    private Task? readerTask;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly SemaphoreSlim connectLock = new(1, 1);

    private sealed class Pending {
        public required TaskCompletionSource<WsPacket> Tcs { get; init; }
        public required (int, int)[] Aliases { get; init; }
    }

    private readonly ConcurrentDictionary<(int, int), Pending> pending = new();
    private readonly ConcurrentDictionary<(int, int), (int, int)> aliases = new();

    public event Action<WsPacket>? Notification;
    public event Action<Exception?>? Disconnected;

    public DwarfWsClient(string host, DeviceProfile profile, int port = 9900) {
        uri = new Uri($"ws://{host}:{port}/");
        this.profile = profile;
    }

    public bool IsConnected => socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken token) {
        if (IsConnected) return;
        await connectLock.WaitAsync(token);
        try {
            if (IsConnected) return;
            socket?.Dispose();
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(uri, timeout.Token);
            socket = ws;
            readerCts = new CancellationTokenSource();
            readerTask = Task.Run(() => ReaderLoop(ws, readerCts.Token));
            DwarfLog.Info($"WebSocket connected to {uri}");
        } finally {
            connectLock.Release();
        }
    }

    public async Task CloseAsync() {
        readerCts?.Cancel();
        var ws = socket;
        socket = null;
        if (ws != null) {
            try {
                if (ws.State == WebSocketState.Open) {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
                }
            } catch { }
            ws.Dispose();
        }
        FailAllPending(new OperationCanceledException("WebSocket closed"));
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    /// <summary>Send a request and return a task that completes with the first matching packet.
    /// A response matches when it arrives as (response | notification-response) for the same
    /// (module, cmd), or as any packet on one of the alternate keys.</summary>
    public async Task<Task<WsPacket>> BeginRequestAsync(int module, int cmd, IMessage request,
        IEnumerable<(int module, int cmd)>? alternates, CancellationToken token) {
        var ws = socket;
        if (ws == null || ws.State != WebSocketState.Open) throw new InvalidOperationException("DWARF WebSocket is not connected");
        var key = (module, cmd);
        var alts = (alternates ?? Array.Empty<(int, int)>()).Select(a => (a.Item1, a.Item2)).ToArray();
        var p = new Pending { Tcs = new TaskCompletionSource<WsPacket>(TaskCreationOptions.RunContinuationsAsynchronously), Aliases = alts };
        if (!pending.TryAdd(key, p)) throw new InvalidOperationException($"Another request for {module}:{cmd} is already pending");
        foreach (var a in alts) aliases[a] = key;

        var packet = new WsPacket {
            MajorVersion = (uint)profile.WsMajorVersion,
            MinorVersion = (uint)profile.WsMinorVersion,
            DeviceId = (uint)profile.WsDeviceId,
            ModuleId = (uint)module,
            Cmd = (uint)cmd,
            Type = TypeRequest,
            Data = request.ToByteString(),
            ClientId = profile.WsClientId,
        };
        await sendLock.WaitAsync(token);
        try {
            await ws.SendAsync(packet.ToByteArray(), WebSocketMessageType.Binary, true, token);
        } catch {
            RemovePending(key);
            throw;
        } finally {
            sendLock.Release();
        }
        return p.Tcs.Task;
    }

    public async Task<WsPacket> RequestAsync(int module, int cmd, IMessage request, TimeSpan timeout,
        IEnumerable<(int module, int cmd)>? alternates, CancellationToken token) {
        var task = await BeginRequestAsync(module, cmd, request, alternates, token);
        try {
            return await task.WaitAsync(timeout, token);
        } catch {
            RemovePending((module, cmd));
            throw;
        }
    }

    public void CancelPending(int module, int cmd) {
        var p = RemovePending((module, cmd));
        p?.Tcs.TrySetCanceled();
    }

    private Pending? RemovePending((int, int) key) {
        if (!pending.TryRemove(key, out var p)) return null;
        foreach (var a in p.Aliases) {
            if (aliases.TryGetValue(a, out var owner) && owner == key) aliases.TryRemove(a, out _);
        }
        return p;
    }

    private void FailAllPending(Exception ex) {
        foreach (var key in pending.Keys.ToArray()) RemovePending(key)?.Tcs.TrySetException(ex);
    }

    private async Task ReaderLoop(ClientWebSocket ws, CancellationToken token) {
        var buffer = new byte[64 * 1024];
        Exception? error = null;
        try {
            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open) {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do {
                    result = await ws.ReceiveAsync(buffer, token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Binary) continue;
                WsPacket packet;
                try {
                    packet = WsPacket.Parser.ParseFrom(ms.ToArray());
                } catch (Exception ex) {
                    DwarfLog.Debug($"Undecodable packet: {ex.Message}");
                    continue;
                }
                Dispatch(packet);
            }
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            error = ex;
            DwarfLog.Warn($"WebSocket reader stopped: {ex.Message}");
        } finally {
            FailAllPending(error ?? new OperationCanceledException("WebSocket closed"));
            if (!token.IsCancellationRequested) Disconnected?.Invoke(error);
        }
    }

    private void Dispatch(WsPacket packet) {
        var key = ((int)packet.ModuleId, (int)packet.Cmd);
        Pending? p = null;
        if (aliases.TryRemove(key, out var owner)) {
            p = RemovePending(owner);
        } else if (packet.Type == TypeResponse || packet.Type == TypeNotificationResponse) {
            p = RemovePending(key);
        }
        p?.Tcs.TrySetResult(packet);

        if (packet.Type == TypeNotification) {
            try { Notification?.Invoke(packet); } catch (Exception ex) { DwarfLog.Warn($"Notification handler failed: {ex.Message}"); }
        }
    }
}
