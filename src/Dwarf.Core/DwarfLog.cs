namespace Dwarf.Core;

/// <summary>Very small logging hook so the core library stays host-agnostic.
/// The NINA plugin forwards these lines to NINA's log; the probe tool prints them.</summary>
public static class DwarfLog {
    public static event Action<string, string>? Message;   // (level, text)

    public static void Info(string text) => Emit("INFO", text);
    public static void Warn(string text) => Emit("WARN", text);
    public static void Debug(string text) => Emit("DEBUG", text);

    private static void Emit(string level, string text) {
        try { Message?.Invoke(level, text); } catch { /* never let logging break the driver */ }
    }
}

public class DwarfCommandException : Exception {
    public int ModuleId { get; }
    public int CommandId { get; }
    public int Code { get; }

    public DwarfCommandException(int moduleId, int commandId, int code)
        : base($"DWARF command {moduleId}:{commandId} failed with code {code}") {
        ModuleId = moduleId; CommandId = commandId; Code = code;
    }
}

public class DwarfCaptureException : Exception {
    public DwarfCaptureException(string message, Exception? inner = null) : base(message, inner) { }
}
