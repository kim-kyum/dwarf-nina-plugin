// Device capability profiles.
// Values are taken from dwarfAlp's device_profile.py (GPL-3.0, https://github.com/acocalypso/dwarfAlp),
// which records them from DWARFLAB app analysis and hardware tests.
namespace Dwarf.Core;

public sealed record CameraSpec(
    string SensorName,
    int Width,
    int Height,
    int AdcBits,
    double PixelSizeUm,
    int MinGain,
    int MaxGain,
    double MinExposureS,
    double MaxExposureS,
    double ElectronsPerAdu,
    string BayerPattern);

public sealed record DeviceProfile(
    string ModelId,
    string DisplayName,
    string WsClientId,
    int WsMajorVersion,
    int WsMinorVersion,
    int WsDeviceId,
    CameraSpec Camera,
    IReadOnlyList<string> FilterLabels,
    IReadOnlyList<int> FilterFirmwareIndices) {

    public bool HasFilterWheel => FilterLabels.Count > 0;

    public static readonly DeviceProfile Dwarf3 = new(
        ModelId: "dwarf3",
        DisplayName: "DWARF 3",
        WsClientId: "0000DAF3-0000-1000-8000-00805F9B34FB",
        WsMajorVersion: 1,
        WsMinorVersion: 20,
        WsDeviceId: 4,
        Camera: new CameraSpec(
            SensorName: "Sony IMX678 STARVIS 2",
            // Astronomy FITS use the full raw array (not the 3840x2160 photo crop).
            Width: 3856,
            Height: 2180,
            AdcBits: 12,
            PixelSizeUm: 2.0,
            MinGain: 0,
            MaxGain: 200,
            MinExposureS: 0.00001,
            MaxExposureS: 120.0,
            ElectronsPerAdu: 2.75,
            BayerPattern: "RGGB"),
        FilterLabels: new[] { "VIS Filter", "Astro Filter", "Duo-Band Filter" },
        FilterFirmwareIndices: new[] { 0, 1, 2 });

    public static readonly DeviceProfile DwarfMini = new(
        ModelId: "dwarfmini",
        DisplayName: "DWARF mini",
        WsClientId: "0000DAF4-0000-1000-8000-00805F9B34FB",
        WsMajorVersion: 1,
        WsMinorVersion: 20,
        WsDeviceId: 4,
        Camera: new CameraSpec("Sony IMX662", 1920, 1080, 12, 2.9, 40, 100, 1.0, 180.0, 2.75, "RGGB"),
        FilterLabels: new[] { "Astro", "Duo-Band" },
        FilterFirmwareIndices: new[] { 1, 2 });

    public static IReadOnlyList<DeviceProfile> All { get; } = new[] { Dwarf3, DwarfMini };

    public static DeviceProfile FromId(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.ModelId, id, StringComparison.OrdinalIgnoreCase)) ?? Dwarf3;
}
