using System.Reflection;
using System.Runtime.InteropServices;

// Unique plugin identifier - never change it after the first release.
[assembly: Guid("6b3f9c1e-4d2a-4f7e-9a51-2c8d0e7b4a63")]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

[assembly: AssemblyTitle("DWARF")]
[assembly: AssemblyDescription("Connect a DWARF smart telescope (DWARF 3) to N.I.N.A. directly - no extra bridge program.")]
[assembly: AssemblyCompany("kim-kyum")]
[assembly: AssemblyProduct("DWARF")]
[assembly: AssemblyCopyright("Copyright © 2026 kim-kyum. GPL-3.0. Protocol knowledge ported from dwarfAlp (acocalypso).")]

[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.2017")]
[assembly: AssemblyMetadata("License", "GPL-3.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.gnu.org/licenses/gpl-3.0.html")]
[assembly: AssemblyMetadata("Repository", "https://github.com/kim-kyum/dwarf-nina-plugin")]
[assembly: AssemblyMetadata("Homepage", "https://github.com/kim-kyum/dwarf-nina-plugin")]
[assembly: AssemblyMetadata("Tags", "DWARF,DWARFLAB,Smart Telescope,Camera")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/kim-kyum/dwarf-nina-plugin/blob/main/CHANGELOG.md")]
[assembly: AssemblyMetadata("FeaturedImageURL", "")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"Unofficial, experimental driver that talks to a DWARF smart telescope over Wi-Fi.

Stage 1: camera (connect, single exposure, FITS download into NINA).
Not affiliated with DWARFLAB. Keep the telescope attended while testing.")]

[assembly: ComVisible(false)]
