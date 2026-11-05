// Local DWARF HTTP API (port 8082) and static file server (port 80).
// Endpoints and payload shapes follow dwarfAlp's http_client.py (GPL-3.0).
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dwarf.Core;

public sealed class DwarfHttpApi : IDisposable {
    private readonly HttpClient api;
    private readonly HttpClient files;
    private readonly HttpClient filesAlt;

    public DwarfHttpApi(string host) {
        api = new HttpClient { BaseAddress = new Uri($"http://{host}:8082"), Timeout = TimeSpan.FromSeconds(8) };
        files = new HttpClient { BaseAddress = new Uri($"http://{host}:80"), Timeout = TimeSpan.FromSeconds(60) };
        filesAlt = new HttpClient { BaseAddress = new Uri($"http://{host}:8092"), Timeout = TimeSpan.FromSeconds(60) };
    }

    public void Dispose() { api.Dispose(); files.Dispose(); filesAlt.Dispose(); }

    private async Task<JsonNode?> PostAsync(string path, object payload, CancellationToken token) {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++) {
            try {
                using var resp = await api.PostAsJsonAsync(path, payload, token);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(token);
                return JsonNode.Parse(text);
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested) {
                last = ex;
                await Task.Delay(500 * attempt, token);
            }
        }
        throw last!;
    }

    /// <summary>POST /shootingMode/getParamAndSetting {modeId} - live parameter catalogue (astronomy = 2).</summary>
    public async Task<JsonNode> GetParamAndSettingAsync(int modeId, CancellationToken token) {
        var node = await PostAsync("/shootingMode/getParamAndSetting", new { modeId }, token)
                   ?? throw new DwarfCaptureException("Empty parameter catalogue response");
        if ((int?)node["code"] != 0 || node["data"] is not JsonObject data)
            throw new DwarfCaptureException($"Parameter catalogue request failed: {node.ToJsonString()}");
        return data;
    }

    /// <summary>POST /album/list/mediaInfos - newest album entries of a media type (astronomy = 4).</summary>
    public async Task<IReadOnlyList<JsonObject>> ListAlbumMediaInfosAsync(int mediaType, int pageSize, CancellationToken token) {
        try {
            var node = await PostAsync("/album/list/mediaInfos", new { mediaType, pageIndex = 0, pageSize }, token);
            return ExtractList(node, "mediaInfos", "list", "items", "records", "mediaList");
        } catch (Exception ex) when (!token.IsCancellationRequested) {
            DwarfLog.Warn($"Album list failed: {ex.Message}");
            return Array.Empty<JsonObject>();
        }
    }

    /// <summary>POST /album/astro/fitsList {srcDir} - FITS products of an astronomy album entry.</summary>
    public async Task<IReadOnlyList<JsonObject>> ListAstroFitsAsync(string srcDir, CancellationToken token) {
        var node = await PostAsync("/album/astro/fitsList", new { srcDir }, token);
        return ExtractList(node, "fitsInfo", "items", "list");
    }

    /// <summary>GET a media file from the static file server (port 80, falling back to 8092).</summary>
    public async Task<byte[]> FetchMediaFileAsync(string filePath, CancellationToken token) {
        var path = filePath.Trim();
        if (path.StartsWith("/sdcard/")) path = path["/sdcard/".Length..];
        path = "/" + path.TrimStart('/');
        try {
            return await files.GetByteArrayAsync(path, token);
        } catch (HttpRequestException) {
            return await filesAlt.GetByteArrayAsync(path, token);
        }
    }

    private static IReadOnlyList<JsonObject> ExtractList(JsonNode? response, params string[] keys) {
        if (response is not JsonObject root) return Array.Empty<JsonObject>();
        JsonNode? data = root["data"];
        if (data == null) {
            foreach (var alt in new[] { "result", "obj", "payload", "value" }) {
                if (root[alt] != null) { data = root[alt]; break; }
            }
        }
        if (data is JsonObject obj) {
            foreach (var k in keys) {
                if (obj[k] is JsonArray arr) { data = arr; break; }
            }
        }
        return data is JsonArray list ? list.OfType<JsonObject>().ToList() : Array.Empty<JsonObject>();
    }

    internal static string? Str(JsonNode? node) {
        try { return node?.GetValue<string>(); } catch { return node?.ToJsonString(); }
    }

    internal static long? Long(JsonNode? node) {
        if (node == null) return null;
        try { return node.GetValue<long>(); } catch { }
        try { return (long)node.GetValue<double>(); } catch { }
        return long.TryParse(Str(node), out var v) ? v : null;
    }
}
