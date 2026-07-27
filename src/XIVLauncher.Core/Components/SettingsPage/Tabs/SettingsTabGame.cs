using Hexa.NET.ImGui;

using Serilog;

using XIVLauncher.Common;
using XIVLauncher.Core.Configuration;
using XIVLauncher.Core.Resources.Localization;
using XIVLauncher.Core.Support;

namespace XIVLauncher.Core.Components.SettingsPage.Tabs;

public class SettingsTabGame : SettingsTab
{
    private volatile bool configBackupBusy;
    private volatile string? configBackupStatus;
    private volatile bool configBackupStatusIsError;
    private bool preserveNewerConfigFiles = true;

    public override SettingsEntry[] Entries { get; } =
    {
        new SettingsEntry<GameRegion>("Game region", "Select the service region used for login, patching, and game launch.", () => Program.Config.GameRegion ?? GameRegion.Global, x => Program.Config.GameRegion = x),

        new SettingsEntry<DirectoryInfo>(Strings.GamePathSetting, Strings.GamePathSettingDescription, () => Program.Config.GamePath, x => Program.Config.GamePath = x)
        {
            CheckValidity = x =>
            {
                if (string.IsNullOrWhiteSpace(x?.FullName))
                    return Strings.GamePathSettingNotSetValidation;

                if (x.Name is "game" or "boot")
                    return Strings.GamePathSettingInvalidValidationj;

                return null;
            }
        },

        new SettingsEntry<DirectoryInfo>(Strings.GameConfigurationPathSetting, Strings.GameConfigurationPathSettingDescription, () => Program.Config.GameConfigPath, x => Program.Config.GameConfigPath = x)
        {
            CheckValidity = x => string.IsNullOrWhiteSpace(x?.FullName) ? Strings.GameConfigurationPathNotSetValidation : null,

            // TODO: We should also support this on Windows
            CheckVisibility = () => Environment.OSVersion.Platform == PlatformID.Unix,
        },

        new SettingsEntry<string>(Strings.AdditionalGameArgsSetting, Strings.AdditionalGameArgsSettingDescription, () => Program.Config.AdditionalArgs, x => Program.Config.AdditionalArgs = x),
        new SettingsEntry<ClientLanguage>(Strings.GameLanguageSetting, Strings.GameLanguageSettingDescription, () => Program.Config.ClientLanguage ?? ClientLanguage.English, x => Program.Config.ClientLanguage = x),
        new SettingsEntry<DpiAwareness>(Strings.GameDPIAwarenessSetting, Strings.GameDPIAwarenessSettingDescription, () => Program.Config.DpiAwareness ?? DpiAwareness.Unaware, x => Program.Config.DpiAwareness = x),
        new SettingsEntry<bool>(Strings.UseXLAuthMacrosSetting, Strings.UseXLAuthMacrosSettingDescription, () => Program.Config.IsOtpServer ?? false, x => Program.Config.IsOtpServer = x),
        new SettingsEntry<bool>(Strings.IgnoreSteamSetting, Strings.IgnoreSteamSettingDescription, () => Program.Config.IsIgnoringSteam ?? false, x => Program.Config.IsIgnoringSteam = x)
        {
            CheckVisibility = () => !CoreEnvironmentSettings.IsSteamCompatTool,
        },
        new SettingsEntry<bool>(Strings.UseUIDCacheSetting, Strings.UseUIDCacheSettingDescription, () => Program.Config.IsUidCacheEnabled ?? false, x => Program.Config.IsUidCacheEnabled = x),
    };

    public override string Title => Strings.GameTitle;

    public override void Draw()
    {
        base.Draw();

        if (Program.Config.IsUidCacheEnabled == true)
        {
            ImGui.Text(Strings.ResetUIDCacheInfo);
            if (ImGui.Button(Strings.ResetUIDCacheButton))
            {
                Program.ResetUIDCache();
            }
        }

        ImGui.Dummy(new System.Numerics.Vector2(10) * ImGuiHelpers.GlobalScale);
        ImGui.Separator();
        ImGui.Dummy(new System.Numerics.Vector2(5) * ImGuiHelpers.GlobalScale);
        ImGui.Text(Strings.GameConfigBackupTitle);
        ImGui.TextWrapped(Strings.GameConfigBackupDescription);
        ImGui.Checkbox(Strings.GameConfigBackupPreserveNewer, ref this.preserveNewerConfigFiles);

        if (this.configBackupBusy)
            ImGui.BeginDisabled();

        if (ImGui.Button(Strings.GameConfigBackupExport))
            this.StartConfigExport();

        ImGui.SameLine();
        if (ImGui.Button(Strings.GameConfigBackupImport))
            this.StartConfigImport();

        if (this.configBackupBusy)
            ImGui.EndDisabled();

        var status = this.configBackupStatus;
        if (!string.IsNullOrWhiteSpace(status))
        {
            ImGui.TextColored(
                this.configBackupStatusIsError ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen,
                status);
        }
    }

    private void StartConfigExport()
    {
        var configDirectory = Program.Config.GameConfigPath;
        if (configDirectory is null)
        {
            this.SetConfigBackupStatus(Strings.GameConfigBackupPathMissing, true);
            return;
        }

        this.configBackupBusy = true;
        var defaultPath = Path.Combine(
            GetDefaultBackupDirectory(configDirectory),
            FfxivConfigBackup.OfficialFileName);
        NativeFileDialog.ShowSave(defaultPath, (selectedPath, error) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    this.SetConfigBackupStatus(
                        string.Format(Strings.GameConfigBackupFailed, error),
                        true);
                    return;
                }

                if (string.IsNullOrWhiteSpace(selectedPath))
                    return;

                var result = FfxivConfigBackup.Export(configDirectory, selectedPath);
                this.SetConfigBackupStatus(
                    string.Format(
                        Strings.GameConfigBackupExported,
                        result.CharacterCount,
                        result.FileCount,
                        result.Path),
                    false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not export an official-compatible FFXIV settings backup");
                this.SetConfigBackupStatus(
                    string.Format(Strings.GameConfigBackupFailed, ex.Message),
                    true);
            }
            finally
            {
                this.configBackupBusy = false;
            }
        });
    }

    private void StartConfigImport()
    {
        var configDirectory = Program.Config.GameConfigPath;
        if (configDirectory is null)
        {
            this.SetConfigBackupStatus(Strings.GameConfigBackupPathMissing, true);
            return;
        }

        this.configBackupBusy = true;
        NativeFileDialog.ShowOpen(
            GetDefaultBackupDirectory(configDirectory),
            (selectedPath, error) =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        this.SetConfigBackupStatus(
                            string.Format(Strings.GameConfigBackupFailed, error),
                            true);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(selectedPath))
                        return;

                    var result = FfxivConfigBackup.Import(
                        configDirectory,
                        selectedPath,
                        Program.storage.GetFolder("config-backups"),
                        this.preserveNewerConfigFiles);
                    this.SetConfigBackupStatus(
                        string.Format(
                            Strings.GameConfigBackupImported,
                            result.RestoredFileCount,
                            result.SkippedFileCount,
                            result.SafetyBackupPath),
                        false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not import an official-compatible FFXIV settings backup");
                    this.SetConfigBackupStatus(
                        string.Format(Strings.GameConfigBackupFailed, ex.Message),
                        true);
                }
                finally
                {
                    this.configBackupBusy = false;
                }
            });
    }

    private void SetConfigBackupStatus(string status, bool isError)
    {
        this.configBackupStatusIsError = isError;
        this.configBackupStatus = status;
    }

    private static string GetDefaultBackupDirectory(DirectoryInfo configDirectory)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents)
            ? configDirectory.Parent?.FullName ?? configDirectory.FullName
            : documents;
    }
}
