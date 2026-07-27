using System.IO;

namespace XIVLauncher.Common.Unix.Compatibility.Wine.Releases;

/// <summary>
/// The CrossOver-based Wine runtime used by XIV on Mac.
/// </summary>
public sealed class WineMacOSRelease : IWineRelease
{
    public string Name { get; } =
        Path.Combine("XIV on Mac.app", "Contents", "Resources", "wine");

    public string DownloadUrl { get; } =
        "https://softwareupdate.xivmac.com/sites/default/files/update_data/XIV%20on%20Mac5.4.2.tar.xz";

    public string[] Checksums { get; } =
    [
        "48a04b9dca4204b6c9345bd46d263be647be2e5df63dec86e7f810167365f83005a32f5bceed79859f28b12258ab7f03e657e40cd14651396d89188ca30211b9",
    ];
}
