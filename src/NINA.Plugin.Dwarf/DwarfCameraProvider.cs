using System.ComponentModel.Composition;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

namespace DwarfNina.Plugin;

/// <summary>Adds "DWARF 3 Camera" to NINA's camera list. Finding the telescope on the network
/// happens on Connect, so refreshing the equipment list stays instant.</summary>
[Export(typeof(IEquipmentProvider))]
public class DwarfCameraProvider : IEquipmentProvider<ICamera> {
    private readonly IProfileService profileService;
    private readonly IExposureDataFactory exposureDataFactory;

    [ImportingConstructor]
    public DwarfCameraProvider(IProfileService profileService, IExposureDataFactory exposureDataFactory) {
        this.profileService = profileService;
        this.exposureDataFactory = exposureDataFactory;
        LogBridge.Install();
    }

    public string Name => "DWARF";

    public IList<ICamera> GetEquipment() {
        var settings = DwarfSettings.Load();
        return new List<ICamera> { new DwarfCamera(settings, exposureDataFactory) };
    }
}
