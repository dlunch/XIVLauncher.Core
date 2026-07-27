using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;

using Serilog;

using XIVLauncher.Common.Unix.Compatibility.Dxvk;
using XIVLauncher.Common.Unix.Compatibility.Wine;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Unix.Compatibility;

public class CompatibilityTools
{
    private const string WINEDLLOVERRIDES = "msquic=,mscoree=n,b;d3d9,d3d11,d3d10core,dxgi=";
    private const uint DXVK_CLEANUP_THRESHHOLD = 5;
    private const uint WINE_CLEANUP_THRESHHOLD = 5;

    private readonly DirectoryInfo wineDirectory;
    private readonly DirectoryInfo dxvkDirectory;
    private readonly StreamWriter logWriter;

    private string WineBinPath => Settings.StartupType == WineStartupType.Managed ?
                                    Path.Combine(wineDirectory.FullName, Settings.Release.Name, "bin") :
                                    Settings.CustomBinPath;
    private string WineExecutablePath
    {
        get
        {
            var wine64Path = Path.Combine(WineBinPath, "wine64");
            return File.Exists(wine64Path) ? wine64Path : Path.Combine(WineBinPath, "wine");
        }
    }
    private string WineServerPath => Path.Combine(WineBinPath, "wineserver");

    private readonly DxvkVersion dxvkVersion;
    private readonly DxvkHudType hudType;
    private readonly bool gamemodeOn;
    private readonly string dxvkAsyncOn;
    private string? macOSBundledRendererRootPath;
    private string? macOSBundledRendererWindowsPath;
    private bool macOSBundledDxmt;

    public bool IsToolReady { get; private set; }
    public WineSettings Settings { get; private set; }
    public bool IsToolDownloaded => File.Exists(WineExecutablePath) && Settings.Prefix.Exists;

    public CompatibilityTools(WineSettings wineSettings, DxvkVersion dxvkVersion, DxvkHudType hudType, bool gamemodeOn, bool dxvkAsyncOn, DirectoryInfo toolsFolder)
    {
        this.Settings = wineSettings;
        this.dxvkVersion = dxvkVersion;
        this.hudType = hudType;
        this.gamemodeOn = gamemodeOn;
        this.dxvkAsyncOn = dxvkAsyncOn ? "1" : "0";

        this.wineDirectory = new DirectoryInfo(Path.Combine(toolsFolder.FullName, "wine"));
        this.dxvkDirectory = new DirectoryInfo(Path.Combine(toolsFolder.FullName, "dxvk"));

        // TODO: Replace these with a nicer way of preventing a pileup of compat tools,
        // This implementation is just a hack.
        if (Directory.GetFiles(dxvkDirectory.FullName).Length >= DXVK_CLEANUP_THRESHHOLD)
        {
            Directory.Delete(dxvkDirectory.FullName, true);
            Directory.CreateDirectory(dxvkDirectory.FullName);
        }
        if (Directory.GetFiles(wineDirectory.FullName).Length >= WINE_CLEANUP_THRESHHOLD)
        {
            Directory.Delete(wineDirectory.FullName, true);
            Directory.CreateDirectory(wineDirectory.FullName);
        }

        this.logWriter = new StreamWriter(wineSettings.LogFile.FullName);

        if (wineSettings.StartupType == WineStartupType.Managed)
        {
            if (!this.wineDirectory.Exists)
                this.wineDirectory.Create();
            if (!this.dxvkDirectory.Exists)
                this.dxvkDirectory.Create();
        }

        if (!wineSettings.Prefix.Exists)
            wineSettings.Prefix.Create();
    }

    public async Task EnsureTool(HttpClient httpClient, DirectoryInfo tempPath)
    {
        if (!File.Exists(WineExecutablePath))
        {
            Log.Information($"Compatibility tool does not exist, downloading {Settings.Release.DownloadUrl}");
            await DownloadTool(httpClient, tempPath).ConfigureAwait(false);
        }

        EnsurePrefix();
        if (!TryUseMacOSBundledRenderer())
            await Dxvk.Dxvk.InstallDxvk(httpClient, Settings.Prefix, dxvkDirectory, dxvkVersion).ConfigureAwait(false);

        IsToolReady = true;
    }

    private bool TryUseMacOSBundledRenderer()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            || dxvkVersion == DxvkVersion.Disabled)
        {
            return false;
        }

        // Managed macOS Wine packages keep their DXMT/Winemetal modules in the
        // regular Wine library tree instead of inside an application wrapper.
        var managedRendererRoot = Path.GetFullPath(
            Path.Combine(WineBinPath, "..", "lib", "wine"));
        var managedRendererWindowsPath =
            Path.Combine(managedRendererRoot, "x86_64-windows");
        var managedRendererUnixPath =
            Path.Combine(managedRendererRoot, "x86_64-unix");
        if (Settings.StartupType == WineStartupType.Managed
            && File.Exists(Path.Combine(managedRendererWindowsPath, "d3d11.dll"))
            && File.Exists(Path.Combine(managedRendererWindowsPath, "dxgi.dll"))
            && File.Exists(Path.Combine(managedRendererUnixPath, "winemetal.so")))
        {
            macOSBundledRendererRootPath = managedRendererRoot;
            macOSBundledRendererWindowsPath = managedRendererWindowsPath;
            macOSBundledDxmt = true;
            Log.Information(
                "Using the DXMT renderer bundled with managed macOS Wine: {Path}",
                macOSBundledRendererWindowsPath);
            return true;
        }

        if (Settings.StartupType != WineStartupType.Custom)
            return false;

        var contentsDirectory = FindMacOSBundleContents();
        if (contentsDirectory == null)
            return false;

        var rendererRoot = Path.Combine(contentsDirectory.FullName, "Frameworks", "renderer");
        var bundledDxmtWindowsPath =
            Path.Combine(rendererRoot, "dxmt", "wine", "x86_64-windows");
        var bundledDxmtUnixPath =
            Path.Combine(rendererRoot, "dxmt", "wine", "x86_64-unix");
        var bundledDxvkPath =
            Path.Combine(rendererRoot, "dxvk", "wine", "x86_64-windows");

        // XIV on Mac uses DXMT directly rather than translating D3D11 through Vulkan
        // and MoltenVK. Prefer the same backend when the selected Wine bundle provides
        // a complete DXMT pair. This also avoids the very large Metal allocations seen
        // with Kegworks DXVK after FFXIV enters a fully loaded scene.
        if (File.Exists(Path.Combine(bundledDxmtWindowsPath, "d3d11.dll"))
            && File.Exists(Path.Combine(bundledDxmtWindowsPath, "dxgi.dll"))
            && File.Exists(Path.Combine(bundledDxmtUnixPath, "winemetal.so")))
        {
            macOSBundledRendererRootPath = Path.Combine(rendererRoot, "dxmt", "wine");
            macOSBundledRendererWindowsPath = bundledDxmtWindowsPath;
            macOSBundledDxmt = true;
        }
        else if (Directory.Exists(bundledDxvkPath))
        {
            // Some bundles only ship a MoltenVK-compatible DXVK build. Linux DXVK
            // releases can load on macOS but then hang while creating the D3D device.
            macOSBundledRendererRootPath = bundledDxvkPath;
            macOSBundledRendererWindowsPath = bundledDxvkPath;
        }
        else
        {
            return false;
        }

        var wineBuiltinPath = Path.GetFullPath(
            Path.Combine(WineBinPath, "..", "lib", "wine", "x86_64-windows"));
        var rendererDlls = new[] { "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll" };

        // Sikarugir packages its DXVK DLLs as Wine builtin modules. They must be
        // selected through WINEDLLPATH; copying them into system32 makes Wine try
        // (and reject) them as native Windows DLLs before falling back to WineD3D.
        // Restore any files copied there by older launcher builds.
        var system32Path = Path.Combine(Settings.Prefix.FullName, "drive_c", "windows", "system32");
        foreach (var dll in rendererDlls)
        {
            var builtinDll = Path.Combine(wineBuiltinPath, dll);
            if (File.Exists(builtinDll))
                File.Copy(builtinDll, Path.Combine(system32Path, dll), true);
        }

        Log.Information(
            "Using the {Renderer} renderer bundled with the custom macOS Wine app: {Path}",
            macOSBundledDxmt ? "DXMT" : "DXVK",
            macOSBundledRendererWindowsPath);
        return true;
    }

    private async Task DownloadTool(HttpClient httpClient, DirectoryInfo tempPath)
    {
        var tempFilePath = Path.Combine(tempPath.FullName, $"{Guid.NewGuid()}");
        await File.WriteAllBytesAsync(tempFilePath, await httpClient.GetByteArrayAsync(Settings.Release.DownloadUrl).ConfigureAwait(false)).ConfigureAwait(false);
        if (!CompatUtil.EnsureChecksumMatch(tempFilePath, Settings.Release.Checksums))
        {
            throw new InvalidDataException("SHA512 checksum verification failed");
        }
        PlatformHelpers.Untar(tempFilePath, this.wineDirectory.FullName);
        Log.Information("Compatibility tool successfully extracted to {Path}", this.wineDirectory.FullName);
        File.Delete(tempFilePath);
    }

    public void EnsurePrefix()
    {
        RunInPrefix("cmd /c dir %userprofile%/Documents > nul").WaitForExit();
    }

    public Process RunInPrefix(string command, string workingDirectory = "", IDictionary<string, string> environment = null, bool redirectOutput = false, bool writeLog = false, bool wineD3D = false)
    {
        var psi = new ProcessStartInfo(WineExecutablePath);
        psi.Arguments = command;

        Log.Verbose("Running in prefix: {FileName} {Arguments}", psi.FileName, command);
        return RunInPrefix(psi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
    }

    public Process RunInPrefix(string[] args, string workingDirectory = "", IDictionary<string, string> environment = null, bool redirectOutput = false, bool writeLog = false, bool wineD3D = false)
    {
        var psi = new ProcessStartInfo(WineExecutablePath);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Log.Verbose("Running in prefix: {FileName} {Arguments}", psi.FileName, psi.ArgumentList.Aggregate(string.Empty, (a, b) => a + " " + b));
        return RunInPrefix(psi, workingDirectory, environment, redirectOutput, writeLog, wineD3D);
    }

    private void MergeDictionaries(StringDictionary a, IDictionary<string, string> b)
    {
        if (b is null)
            return;

        foreach (var keyValuePair in b)
        {
            if (a.ContainsKey(keyValuePair.Key))
                a[keyValuePair.Key] = keyValuePair.Value;
            else
                a.Add(keyValuePair.Key, keyValuePair.Value);
        }
    }

    private Process RunInPrefix(ProcessStartInfo psi, string workingDirectory, IDictionary<string, string> environment, bool redirectOutput, bool writeLog, bool wineD3D)
    {
        psi.RedirectStandardOutput = redirectOutput;
        psi.RedirectStandardError = writeLog;
        psi.UseShellExecute = false;
        psi.WorkingDirectory = workingDirectory;

        var ogl = wineD3D || this.dxvkVersion == DxvkVersion.Disabled;

        var rendererOverride = this.macOSBundledRendererWindowsPath != null
            ? "b"
            : (ogl ? "b" : "n,b");
        var wineEnviromentVariables = new Dictionary<string, string>
        {
            { "WINEPREFIX", Settings.Prefix.FullName },
            { "WINEDLLOVERRIDES", $"{WINEDLLOVERRIDES}{rendererOverride}" }
        };

        if (!string.IsNullOrEmpty(Settings.DebugVars))
        {
            wineEnviromentVariables.Add("WINEDEBUG", Settings.DebugVars);
        }

        wineEnviromentVariables.Add("XL_WINEONLINUX", "true");
        string ldPreload = Environment.GetEnvironmentVariable("LD_PRELOAD") ?? "";

        string dxvkHud = hudType switch
        {
            DxvkHudType.None => "0",
            DxvkHudType.Fps => "fps",
            DxvkHudType.Full => "full",
            _ => throw new ArgumentOutOfRangeException()
        };

        if (this.gamemodeOn == true && !ldPreload.Contains("libgamemodeauto.so.0"))
        {
            ldPreload = ldPreload.Equals("", StringComparison.OrdinalIgnoreCase) ? "libgamemodeauto.so.0" : ldPreload + ":libgamemodeauto.so.0";
        }

        wineEnviromentVariables.Add("DXVK_HUD", dxvkHud);
        // Kegworks' asynchronous compiler can continuously allocate Metal
        // resources on macOS. In practice this can consume tens of gigabytes
        // before FFXIV reaches the lobby, so keep the bundle's stable path
        // synchronous even if the cross-platform setting is enabled.
        var effectiveDxvkAsync = this.macOSBundledRendererWindowsPath != null ? "0" : dxvkAsyncOn;
        wineEnviromentVariables.Add("DXVK_ASYNC", effectiveDxvkAsync);
        AddMacOSBundleEnvironment(wineEnviromentVariables);
        if (this.macOSBundledDxmt)
        {
            // The current XIV on Mac runtime uses macOS-native msync by default.
            // Do not enable esync/fsync at the same time.
            wineEnviromentVariables.Add("WINEMSYNC", "1");
        }
        else switch (Settings.SyncType)
        {
            case WineSyncType.ESync:
                wineEnviromentVariables.Add("WINEESYNC", "1");
                break;
            case WineSyncType.FSync:
                wineEnviromentVariables.Add("WINEFSYNC", "1");
                break;
        }
        wineEnviromentVariables.Add("LD_PRELOAD", ldPreload);

        MergeDictionaries(psi.EnvironmentVariables, wineEnviromentVariables);
        MergeDictionaries(psi.EnvironmentVariables, environment);

        Process helperProcess = new();
        helperProcess.StartInfo = psi;
        helperProcess.ErrorDataReceived += new DataReceivedEventHandler((_, errLine) =>
        {
            if (string.IsNullOrEmpty(errLine.Data))
                return;

            try
            {
                logWriter.WriteLine(errLine.Data);
                Console.Error.WriteLine(errLine.Data);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException ||
                                       ex is OverflowException ||
                                       ex is IndexOutOfRangeException)
            {
                // very long wine log lines get chopped off after a (seemingly) arbitrary limit resulting in strings that are not null terminated
                //logWriter.WriteLine("Error writing Wine log line:");
                //logWriter.WriteLine(ex.Message);
            }
        });

        helperProcess.Start();
        if (writeLog)
            helperProcess.BeginErrorReadLine();

        return helperProcess;
    }

    private void AddMacOSBundleEnvironment(IDictionary<string, string> environment)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        var libraryPaths = new List<string>();
        var wineLibraryPath = Path.GetFullPath(Path.Combine(WineBinPath, "..", "lib"));
        if (Directory.Exists(wineLibraryPath))
            libraryPaths.Add(wineLibraryPath);

        var currentDirectory = FindMacOSBundleContents();

        if (currentDirectory != null)
        {
            var frameworksPath = Path.Combine(currentDirectory.FullName, "Frameworks");
            var moltenVkCxPath = Path.Combine(frameworksPath, "moltenvkcx");
            // Keep the renderer paired with the MoltenVK variant selected by
            // its containing Wine bundle. Sikarugir's Kegworks DXVK build can
            // allocate tens of gigabytes when paired with its newer generic
            // MoltenVK library instead of moltenvkcx.
            if (IsMacOSBundleOptionEnabled(currentDirectory, "MOLTENVKCX")
                && Directory.Exists(moltenVkCxPath))
            {
                libraryPaths.Add(moltenVkCxPath);
            }

            if (Directory.Exists(frameworksPath))
                libraryPaths.Add(frameworksPath);
        }

        if (libraryPaths.Count == 0)
            return;

        var existingPath = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(existingPath))
            libraryPaths.Add(existingPath);

        environment["DYLD_FALLBACK_LIBRARY_PATH"] =
            string.Join(Path.PathSeparator, libraryPaths.Distinct(StringComparer.Ordinal));

        if (this.macOSBundledRendererWindowsPath != null)
        {
            // Wine builtin PE modules and their Unix companions must be discovered
            // from one architecture-containing root. Passing the x86_64-windows and
            // x86_64-unix children separately makes winemetal fail its Unix-function
            // ABI handshake (c0000142) even though all three DXMT DLLs are found.
            var wineDllPaths = new List<string>
            {
                this.macOSBundledRendererRootPath ?? this.macOSBundledRendererWindowsPath
            };
            var existingWineDllPath = Environment.GetEnvironmentVariable("WINEDLLPATH");
            if (!string.IsNullOrEmpty(existingWineDllPath))
                wineDllPaths.Add(existingWineDllPath);

            environment["WINEDLLPATH"] =
                string.Join(Path.PathSeparator, wineDllPaths.Distinct(StringComparer.Ordinal));
            // CrossOver-derived Wine engines, including Sikarugir's, use this
            // companion variable to put renderer modules ahead of their bundled
            // WineD3D modules.
            environment["WINEDLLPATH_PREPEND"] =
                this.macOSBundledRendererRootPath ?? this.macOSBundledRendererWindowsPath;
        }

        if (this.macOSBundledDxmt)
        {
            // Match XIV on Mac's conservative DXMT defaults. Upscaling is disabled;
            // users retain control of resolution and the game's own frame limiter.
            environment["DXMT_CONFIG"] =
                "d3d11.metalSpatialUpscaleFactor=1.0;d3d11.preferredMaxFrameRate=0;";
            environment["DXMT_ENABLE_NVEXT"] = "1";
            environment["DXMT_METALFX_SPATIAL_SWAPCHAIN"] = "0";
            environment["MVK_CONFIG_FAST_MATH_ENABLED"] = "0";
            environment["MVK_CONFIG_RESUME_LOST_DEVICE"] = "1";
        }
    }

    private DirectoryInfo FindMacOSBundleContents()
    {
        var currentDirectory = new DirectoryInfo(WineBinPath);
        while (currentDirectory != null
               && !string.Equals(
                   currentDirectory.Name,
                   "Contents",
                   StringComparison.OrdinalIgnoreCase))
        {
            currentDirectory = currentDirectory.Parent;
        }

        return currentDirectory;
    }

    private static bool IsMacOSBundleOptionEnabled(DirectoryInfo contentsDirectory, string option)
    {
        var infoPlist = Path.Combine(contentsDirectory.FullName, "Info.plist");
        if (!File.Exists(infoPlist))
            return false;

        try
        {
            var values = XDocument.Load(infoPlist)
                .Descendants("dict")
                .FirstOrDefault()?
                .Elements()
                .ToList();
            if (values == null)
                return false;

            for (var i = 0; i + 1 < values.Count; i++)
            {
                if (values[i].Name == "key"
                    && values[i].Value == option)
                {
                    return values[i + 1].Name == "true"
                           || (values[i + 1].Name == "integer" && values[i + 1].Value == "1");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read macOS Wine bundle option {Option}", option);
        }

        return false;
    }

    public void EnsureKoreanFontFallback()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        const string fontSubstitutes =
            @"HKLM\Software\Microsoft\Windows NT\CurrentVersion\FontSubstitutes";
        var replacements = new Dictionary<string, string>
        {
            { "MS Shell Dlg", "NanumGothic" },
            { "MS Shell Dlg 2", "NanumGothic" },
            { "Gulim", "NanumGothic" },
            { "GulimChe", "NanumGothic" },
            { "Malgun Gothic", "NanumGothic" },
            { "Malgun Gothic Semilight", "NanumGothic" },
        };

        foreach (var replacement in replacements)
            AddRegistryKey(fontSubstitutes, replacement.Key, replacement.Value);
    }

    public int[] GetProcessIds(string executableName)
    {
        var wineDbg = RunInPrefix("winedbg --command \"info proc\"", redirectOutput: true);
        var output = wineDbg.StandardOutput.ReadToEnd();
        var matchingLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.Contains(executableName));
        return matchingLines.Select(l => int.Parse(l.Substring(1, 8), System.Globalization.NumberStyles.HexNumber)).ToArray();
    }

    public int GetProcessId(string executableName)
    {
        return GetProcessIds(executableName).FirstOrDefault();
    }

    public int GetUnixProcessId(int winePid)
    {
        var wineDbg = RunInPrefix("winedbg --command \"info procmap\"", redirectOutput: true);
        var output = wineDbg.StandardOutput.ReadToEnd();
        if (output.Contains("syntax error\n"))
            return 0;
        var matchingLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Where(
            l => int.Parse(l.Substring(1, 8), System.Globalization.NumberStyles.HexNumber) == winePid);
        var unixPids = matchingLines.Select(l => int.Parse(l.Substring(10, 8), System.Globalization.NumberStyles.HexNumber)).ToArray();
        return unixPids.FirstOrDefault();
    }

    public string UnixToWinePath(string unixPath)
    {
        var launchArguments = new string[] { "winepath", "--windows", unixPath };
        var winePath = RunInPrefix(launchArguments, redirectOutput: true);
        var output = winePath.StandardOutput.ReadToEnd();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    public void AddRegistryKey(string key, string value, string data)
    {
        var args = new string[] { "reg", "add", key, "/v", value, "/d", data, "/f" };
        var wineProcess = RunInPrefix(args);
        wineProcess.WaitForExit();
    }

    public void Kill()
    {
        var psi = new ProcessStartInfo(WineServerPath)
        {
            Arguments = "-k"
        };
        psi.EnvironmentVariables.Add("WINEPREFIX", Settings.Prefix.FullName);
        var environment = new Dictionary<string, string>();
        AddMacOSBundleEnvironment(environment);
        MergeDictionaries(psi.EnvironmentVariables, environment);

        Process.Start(psi);
    }
}
