using XIVLauncher.Common;

namespace XIVLauncher.Core.Configuration;

public enum GameRegion
{
    [SettingsDescription("Global", "Square Enix global service")]
    Global,

    [SettingsDescription("Korea", "Actoz Korean service")]
    Korea,
}
