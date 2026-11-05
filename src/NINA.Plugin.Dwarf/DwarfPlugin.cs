using System.ComponentModel.Composition;
using Dwarf.Core;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace DwarfNina.Plugin;

/// <summary>Plugin manifest. Metadata comes from Properties/AssemblyInfo.cs.</summary>
[Export(typeof(IPluginManifest))]
public class DwarfPlugin : PluginBase {
    [ImportingConstructor]
    public DwarfPlugin() {
        LogBridge.Install();
    }
}

/// <summary>Forwards the core library's log lines to NINA's log file.</summary>
internal static class LogBridge {
    private static int installed;

    public static void Install() {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        DwarfLog.Message += (level, text) => {
            var line = "[DWARF] " + text;
            switch (level) {
                case "WARN": Logger.Warning(line); break;
                case "DEBUG": Logger.Debug(line); break;
                default: Logger.Info(line); break;
            }
        };
    }
}
