namespace XIVLauncher.Common.Unix.Compatibility.Wine.Releases;

/// <summary>
/// The CrossOver-based Wine runtime used by XIV on Mac.
/// </summary>
public sealed class WineMacOSRelease : IWineRelease
{
    public string Name { get; } = "wine";

    public string DownloadUrl { get; } =
        "https://github.com/marzent/winecx/releases/download/ff-wine-9.12.1/wine.tar.gz";

    public string[] Checksums { get; } =
    [
        "41835ab42b526bd1fd6f4670fa9df4267213b83550b9438f17970558ee44a37e54dbab7cc1cf25ccf0fe54fea431d9044b7e557a9aab21206ad6bfea6fa910a0",
    ];
}
