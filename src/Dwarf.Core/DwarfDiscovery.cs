using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Dwarf.Core;

/// <summary>Finds a DWARF on the local network: an explicit/saved address first, then the DWARF's own
/// Wi-Fi address (192.168.88.1), then every host of the PC's /24 networks answering on 9900 and 8082.</summary>
public static class DwarfDiscovery {
    public const string ApModeAddress = "192.168.88.1";

    public static async Task<string?> FindAsync(string? preferred, CancellationToken token) {
        var quick = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred)) quick.Add(preferred.Trim());
        quick.Add(ApModeAddress);
        var hit = await ProbeAsync(quick.Distinct().ToList(), TimeSpan.FromMilliseconds(1500), token);
        if (hit != null) return hit;

        var candidates = LocalSubnetHosts().Except(quick).ToList();
        DwarfLog.Info($"Searching {candidates.Count} addresses on the local network for a DWARF");
        return await ProbeAsync(candidates, TimeSpan.FromMilliseconds(2500), token);
    }

    private static async Task<string?> ProbeAsync(IReadOnlyList<string> hosts, TimeSpan timeout, CancellationToken token) {
        var ws = await OpenPortsAsync(hosts, 9900, timeout, token);
        if (ws.Count == 0) return null;
        var api = await OpenPortsAsync(ws, 8082, timeout, token);
        return hosts.FirstOrDefault(api.Contains);
    }

    private static async Task<List<string>> OpenPortsAsync(IReadOnlyList<string> hosts, int port, TimeSpan timeout, CancellationToken token) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        var tasks = hosts.Select(async h => {
            using var client = new TcpClient();
            try {
                await client.ConnectAsync(h, port, cts.Token);
                return client.Connected ? h : null;
            } catch {
                return null;
            }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        token.ThrowIfCancellationRequested();
        return results.Where(r => r != null).Select(r => r!).ToList();
    }

    private static IEnumerable<string> LocalSubnetHosts() {
        var own = new HashSet<string>();
        var prefixes = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses) {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = ua.Address.ToString();
                if (s.StartsWith("169.254.") || IPAddress.IsLoopback(ua.Address)) continue;
                own.Add(s);
                prefixes.Add(s[..s.LastIndexOf('.')]);
            }
        }
        foreach (var p in prefixes)
            for (var i = 1; i < 255; i++) {
                var h = $"{p}.{i}";
                if (!own.Contains(h)) yield return h;
            }
    }
}
