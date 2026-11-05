using System.Text.Json;
using Dwarf.Core;

namespace DwarfNina.Plugin;

/// <summary>Small JSON settings file shared by all NINA profiles:
/// %LOCALAPPDATA%\DwarfNinaPlugin\settings.json</summary>
public sealed class DwarfSettings {
    public string ModelId { get; set; } = DeviceProfile.Dwarf3.ModelId;
    /// <summary>Last address that worked, or one the user typed. Empty = search automatically.</summary>
    public string Host { get; set; } = "";
    /// <summary>Filter used while no DWARF filter wheel is connected in NINA (index into the model's filter list).</summary>
    public int DefaultFilter { get; set; }
    public int DefaultGain { get; set; } = 80;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwarfNinaPlugin", "settings.json");

    public static DwarfSettings Load() {
        try {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<DwarfSettings>(File.ReadAllText(FilePath)) ?? new();
        } catch { }
        return new();
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        } catch { }
    }

    public DeviceProfile Profile => DeviceProfile.FromId(ModelId);
}
