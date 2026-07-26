using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace XIVLauncher.Common.Unix.Compatibility.Wine;

public enum WineReleaseDistro
{
    ubuntu,
    fedora,
    arch,
    macOS
}

public static class CompatUtil
{
    private static readonly string[] MacOSWineBundleBinPaths =
    [
        Path.Combine("Contents", "SharedSupport", "wine", "bin"),
        Path.Combine("Contents", "Resources", "wine", "bin"),
    ];

    public static string FindMacOSWineBinPath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return string.Empty;

        var candidates = new List<string>();
        var environmentPath = Environment.GetEnvironmentVariable("XL_WINE_BINARY_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
            candidates.Add(environmentPath);

        var searchPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(searchPath))
            candidates.AddRange(
                searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddWineBundleCandidates(candidates, Path.Combine(homeDirectory, "Applications"));
        AddWineBundleCandidates(candidates, "/Applications");

        return candidates
               .Distinct(StringComparer.Ordinal)
               .FirstOrDefault(IsWineBinPath)
               ?? string.Empty;
    }

    public static bool IsWineBinPath(string path)
    {
        return Directory.Exists(path)
               && (File.Exists(Path.Combine(path, "wine64"))
                   || File.Exists(Path.Combine(path, "wine")))
               && File.Exists(Path.Combine(path, "wineserver"));
    }

    private static void AddWineBundleCandidates(ICollection<string> candidates, string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
            return;

        foreach (var entryPath in GetDirectoriesOrEmpty(rootDirectory))
        {
            if (string.Equals(
                    Path.GetExtension(entryPath),
                    ".app",
                    StringComparison.OrdinalIgnoreCase))
            {
                AddWineBundleLayoutCandidates(candidates, entryPath);
                continue;
            }

            // Package managers commonly group generated app wrappers one level
            // below ~/Applications or /Applications.
            foreach (var bundlePath in GetDirectoriesOrEmpty(entryPath, "*.app"))
                AddWineBundleLayoutCandidates(candidates, bundlePath);
        }
    }

    private static IEnumerable<string> GetDirectoriesOrEmpty(
        string rootDirectory,
        string searchPattern = "*")
    {
        try
        {
            return Directory.GetDirectories(rootDirectory, searchPattern);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddWineBundleLayoutCandidates(
        ICollection<string> candidates,
        string bundlePath)
    {
        foreach (var relativeBinPath in MacOSWineBundleBinPaths)
            candidates.Add(Path.Combine(bundlePath, relativeBinPath));
    }

    public static WineReleaseDistro GetWineIdForDistro()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return WineReleaseDistro.macOS;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new InvalidOperationException("GetWineIdForDistro called on unsupported platform");
        }

        try
        {
            const string OS_RELEASE_PATH = "/etc/os-release";

            if (!File.Exists(OS_RELEASE_PATH))
            {
                return WineReleaseDistro.ubuntu;
            }
            var osRelease = File.ReadAllLines(OS_RELEASE_PATH);
            var osInfo = new Dictionary<string, string>();
            foreach (var line in osRelease)
            {
                var keyValue = line.Split('=', 2);
                if (keyValue.Length == 1)
                    osInfo.Add(keyValue[0], "");
                else
                    osInfo.Add(keyValue[0], keyValue[1]);
            }

            // Check for flatpak or snap
            if (osInfo.ContainsKey("ID"))
            {
                if (osInfo["ID"] == "org.freedesktop.platform")
                {
                    return WineReleaseDistro.ubuntu;
                }
                else if (osInfo.ContainsKey("HOME_URL"))
                {
                    if (osInfo["ID"] == "ubuntu-core" && osInfo["HOME_URL"] == "https://snapcraft.io")
                    {
                        return WineReleaseDistro.ubuntu;
                    }
                }
            }

            // Check for values in osInfo.
            foreach (var kvp in osInfo)
            {
                if (kvp.Value.ToLower().Contains("fedora") || kvp.Value.ToLower().Contains("tumbleweed"))
                {
                    return WineReleaseDistro.fedora;
                }

                if (kvp.Value.ToLower().Contains("arch"))
                {
                    return WineReleaseDistro.arch;
                }

                if (kvp.Value.ToLower().Contains("ubuntu") || kvp.Value.ToLower().Contains("debian"))
                {
                    return WineReleaseDistro.ubuntu;
                }
            }

            return WineReleaseDistro.ubuntu;
        }
        catch
        {
            return WineReleaseDistro.ubuntu;
        }
    }

    public static bool EnsureChecksumMatch(string filePath, string[] checksums)
    {
        if (checksums.Length == 0)
        {
            return false;
        }
        using var sha512 = SHA512.Create();
        using var stream = File.OpenRead(filePath);
        var computedHash = Convert.ToHexString(sha512.ComputeHash(stream)).ToLowerInvariant();
        return checksums.Any(checksum => string.Equals(checksum, computedHash, StringComparison.OrdinalIgnoreCase));
    }
}
