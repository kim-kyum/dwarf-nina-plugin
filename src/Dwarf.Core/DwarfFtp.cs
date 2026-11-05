// Anonymous FTP access to the DWARF's astronomy folders.
// Folder layout, stale-file guard and timestamp parsing follow dwarfAlp's ftp_client.py (GPL-3.0).
using System.Globalization;
using System.Text.RegularExpressions;
using FluentFTP;

namespace Dwarf.Core;

public sealed record FtpFitsEntry(string Path, DateTime Timestamp);

public sealed class DwarfFtp {
    private static readonly string[] AstroRoots = { "/Astronomy", "/DWARF_mini/Astronomy", "/DWARF_II/Astronomy" };
    private const int MaxDirectoriesPerScan = 8;
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(10);
    private readonly string host;

    public DwarfFtp(string host) { this.host = host; }

    private AsyncFtpClient NewClient() {
        var client = new AsyncFtpClient(host, "Anonymous", "", 21);
        client.Config.ConnectTimeout = 10000;
        client.Config.ReadTimeout = 15000;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        return client;
    }

    /// <summary>Newest raw (non-stacked) FITS among the most recent DWARF_RAW* capture folders.</summary>
    public async Task<FtpFitsEntry?> GetLatestFitsAsync(CancellationToken token) {
        await using var client = NewClient();
        await client.Connect(token);
        var found = new List<FtpFitsEntry>();
        foreach (var root in AstroRoots) {
            FtpListItem[] dirs;
            try { dirs = await client.GetListing(root, token); } catch { continue; }
            var captureDirs = dirs
                .Where(d => d.Type == FtpObjectType.Directory && d.Name.StartsWith("DWARF_RAW", StringComparison.Ordinal))
                .OrderByDescending(d => DirectorySortKey(d.Name))
                .Take(MaxDirectoriesPerScan);
            foreach (var dir in captureDirs) {
                FtpListItem[] items;
                try { items = await client.GetListing(dir.FullName, token); } catch { continue; }
                foreach (var f in items) {
                    if (f.Type != FtpObjectType.File) continue;
                    var lower = f.Name.ToLowerInvariant();
                    if (!(lower.EndsWith(".fits") || lower.EndsWith(".fit"))) continue;
                    // Stacked FITS are cumulative firmware products, not the single raw exposure.
                    if (lower.StartsWith("stacked")) continue;
                    var ts = EmbeddedTimestamp(f.FullName) ?? (f.Modified == DateTime.MinValue ? DateTime.Now : f.Modified);
                    found.Add(new FtpFitsEntry(f.FullName, ts));
                }
            }
        }
        return found.OrderBy(e => e.Timestamp).ThenBy(e => e.Path, StringComparer.Ordinal).LastOrDefault();
    }

    public async Task<byte[]> DownloadAsync(string path, CancellationToken token) {
        await using var client = NewClient();
        await client.Connect(token);
        var bytes = await client.DownloadBytes(path, token);
        return bytes ?? throw new IOException($"FTP download returned nothing for {path}");
    }

    /// <summary>Poll until a FITS newer than the baseline and not older than notBefore appears.</summary>
    public async Task<(FtpFitsEntry entry, byte[] content)?> WaitForNewFitsAsync(
        FtpFitsEntry? baseline, DateTime notBefore, TimeSpan timeout, CancellationToken token) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested) {
            try {
                var latest = await GetLatestFitsAsync(token);
                if (latest != null
                    && (baseline == null || latest.Path != baseline.Path || latest.Timestamp > baseline.Timestamp)
                    && latest.Timestamp >= notBefore - ClockTolerance) {
                    var content = await DownloadAsync(latest.Path, token);
                    return (latest, content);
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                DwarfLog.Debug($"FTP poll failed: {ex.Message}");
            }
            await Task.Delay(1000, token);
        }
        return null;
    }

    private static long DirectorySortKey(string name) {
        var matches = Regex.Matches(name, @"20\d{2}(?:[-_]?\d{2}){5}(?:[-_]?\d{3})?");
        if (matches.Count == 0) return 0;
        var digits = Regex.Replace(matches[^1].Value, @"\D", "");
        return long.Parse(digits.PadRight(17, '0')[..17], CultureInfo.InvariantCulture);
    }

    /// <summary>DWARF astronomy paths embed the local capture time; MDTM is not reliable on all firmware.</summary>
    internal static DateTime? EmbeddedTimestamp(string path) {
        var found = new List<DateTime>();
        foreach (Match m in Regex.Matches(path, @"(?<!\d)(20\d{2})[-_](\d{2})[-_](\d{2})[-_](\d{2})[-_](\d{2})[-_](\d{2})(?:[-_](\d{3}))?(?!\d)")) {
            try {
                var g = m.Groups;
                found.Add(new DateTime(int.Parse(g[1].Value), int.Parse(g[2].Value), int.Parse(g[3].Value),
                    int.Parse(g[4].Value), int.Parse(g[5].Value), int.Parse(g[6].Value),
                    g[7].Success ? int.Parse(g[7].Value) : 0, DateTimeKind.Local));
            } catch { }
        }
        foreach (Match m in Regex.Matches(path, @"(?<!\d)(20\d{6})[-_](\d{6})(\d{3})?(?!\d)")) {
            if (DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)) {
                found.Add(dt.AddMilliseconds(m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0));
            }
        }
        return found.Count == 0 ? null : found.Max();
    }
}
