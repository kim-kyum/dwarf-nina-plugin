// DwarfProbe - test the DWARF connection and one exposure without NINA.
// Usage: DwarfProbe [--ip 192.168.0.23] [--model dwarf3] [--exp 1] [--gain 80] [--filter 0] [--connect-only]
using System.Globalization;
using System.Text;
using Dwarf.Core;

var opts = ParseArgs(args);
var logPath = Path.Combine(AppContext.BaseDirectory, $"dwarfprobe-{DateTime.Now:yyyyMMdd-HHmmss}.log");
var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
var verbose = opts.ContainsKey("verbose");
DwarfLog.Message += (level, text) => {
    var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {text}";
    log.WriteLine(line);
    if (level != "DEBUG" || verbose) Console.WriteLine(line);
};

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("DWARF probe - connection and single-exposure test");
Console.WriteLine($"Log file: {logPath}");
Console.WriteLine("Close the DWARFLAB phone app first (only one controller can hold the telescope).");
Console.WriteLine();

var profile = DeviceProfile.FromId(opts.GetValueOrDefault("model"));
var exposure = double.Parse(opts.GetValueOrDefault("exp", "1"), CultureInfo.InvariantCulture);
var gain = int.Parse(opts.GetValueOrDefault("gain", "80"), CultureInfo.InvariantCulture);
var filter = int.Parse(opts.GetValueOrDefault("filter", "0"), CultureInfo.InvariantCulture);
var exitCode = 0;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

DwarfSession? session = null;
try {
    var host = await DwarfDiscovery.FindAsync(opts.GetValueOrDefault("ip"), cts.Token)
               ?? throw new Exception("No DWARF found. Connect the PC to the DWARF Wi-Fi or the same router, or pass --ip.");
    Console.WriteLine($"DWARF found at {host}");
    session = new DwarfSession(host, profile);
    await session.ConnectAsync(cts.Token);
    Console.WriteLine($"Connected and in control. Sensor temperature: {session.TemperatureC?.ToString("0") ?? "?"} C");

    if (!opts.ContainsKey("connect-only")) {
        Console.WriteLine($"Starting {exposure:g}s exposure, gain {gain}, filter #{filter} ({profile.FilterLabels.ElementAtOrDefault(filter) ?? "-"})");
        var started = DateTime.Now;
        await session.StartExposureAsync(new DwarfSession.CaptureRequest(exposure, gain, filter, true), cts.Token);
        var image = await session.WaitForImageAsync(cts.Token);
        var outPath = Path.Combine(AppContext.BaseDirectory, $"dwarf-{DateTime.Now:yyyyMMdd-HHmmss}.fits");
        WriteFits(outPath, image);
        Console.WriteLine();
        Console.WriteLine($"SUCCESS: {image.Width}x{image.Height}, pixel max {image.MaxValue}, took {(DateTime.Now - started).TotalSeconds:0.0}s");
        Console.WriteLine($"Saved: {outPath}");
        if (image.MaxValue >= 4095 && image.Pixels.Min() >= 4095) Console.WriteLine("Note: every pixel is 4095 = saturated. Use a shorter exposure / lower gain in daylight.");
    }
} catch (Exception ex) {
    exitCode = 1;
    Console.WriteLine();
    Console.WriteLine($"FAILED: {ex.Message}");
    log.WriteLine(ex.ToString());
} finally {
    if (session != null) await session.DisposeAsync();
    log.Flush();
}

Console.WriteLine();
Console.WriteLine("Please send the log file above if something went wrong. Press Enter to exit.");
if (!Console.IsInputRedirected) Console.ReadLine();
return exitCode;

static Dictionary<string, string> ParseArgs(string[] a) {
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length; i++) {
        if (!a[i].StartsWith("--")) continue;
        var key = a[i][2..];
        if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) d[key] = a[++i]; else d[key] = "true";
    }
    return d;
}

static void WriteFits(string path, FitsImage img) {
    var cards = new List<string> {
        "SIMPLE  =                    T", "BITPIX  =                   16", "NAXIS   =                    2",
        $"NAXIS1  = {img.Width,20}", $"NAXIS2  = {img.Height,20}",
        "BZERO   =                32768", "BSCALE  =                    1",
        "BAYERPAT= 'RGGB    '", "INSTRUME= 'DWARF   '", "END",
    };
    using var fs = File.Create(path);
    var header = new StringBuilder();
    foreach (var c in cards) header.Append(c.PadRight(80));
    while (header.Length % 2880 != 0) header.Append(' ');
    fs.Write(Encoding.ASCII.GetBytes(header.ToString()));
    var data = new byte[img.Pixels.Length * 2];
    for (var i = 0; i < img.Pixels.Length; i++) {
        var v = (short)(img.Pixels[i] - 32768);
        data[2 * i] = (byte)(v >> 8);
        data[2 * i + 1] = (byte)v;
    }
    fs.Write(data);
    var pad = (2880 - data.Length % 2880) % 2880;
    fs.Write(new byte[pad]);
}
