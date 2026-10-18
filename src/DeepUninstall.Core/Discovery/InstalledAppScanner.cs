using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Uninstall;
using Microsoft.Win32;

namespace DeepUninstall.Core.Discovery;

/// <summary>Lists installed programs the same way "Apps &amp; features" does, from the Uninstall registry keys.</summary>
public sealed partial class InstalledAppScanner
{
    public const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly HashSet<string> SkippedReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "Hotfix", "Security Update", "Service Pack",
    };

    public IReadOnlyList<InstalledApp> Scan()
    {
        var apps = new List<InstalledApp>();
        foreach (var (hive, view) in Sources())
        {
            var root = new RegistryLocation(hive, view, UninstallRoot);
            using var rootKey = root.Open();
            if (rootKey is null) continue;

            foreach (var name in rootKey.GetSubKeyNames())
            {
                try
                {
                    using var key = rootKey.OpenSubKey(name);
                    if (key is null) continue;
                    var app = Read(key, root.Child(name));
                    if (app is not null) apps.Add(app);
                }
                catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
                {
                    // Unreadable entry; skip it.
                }
            }
        }

        apps.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return apps;
    }

    /// <summary>Re-reads a single entry, or returns null when it no longer exists.</summary>
    public static InstalledApp? ReadEntry(RegistryLocation location)
    {
        using var key = location.Open();
        return key is null ? null : Read(key, location);
    }

    private static IEnumerable<(RegistryHive, RegistryView)> Sources()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
        if (Environment.Is64BitOperatingSystem)
            yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
        yield return (RegistryHive.CurrentUser, RegistryView.Default);
    }

    internal static InstalledApp? Read(RegistryKey key, RegistryLocation location)
    {
        var displayName = GetString(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        if (GetInt(key, "SystemComponent") == 1) return null;
        if (!string.IsNullOrWhiteSpace(GetString(key, "ParentKeyName"))) return null;
        var releaseType = GetString(key, "ReleaseType");
        if (releaseType is not null && SkippedReleaseTypes.Contains(releaseType)) return null;

        var uninstallString = GetString(key, "UninstallString");
        var isMsi = GetInt(key, "WindowsInstaller") == 1;
        if (string.IsNullOrWhiteSpace(uninstallString) && !isMsi) return null;

        var keyName = location.Name;
        var valueNames = key.GetValueNames();
        var type = InstallerDetector.Detect(keyName, isMsi, uninstallString, valueNames);
        var productCode = type == InstallerType.Msi ? FindProductCode(keyName, uninstallString) : null;

        var installLocation = CleanPath(GetString(key, "InstallLocation"))
                              ?? CleanPath(GetString(key, "Inno Setup: App Path"));

        var sizeKb = GetInt(key, "EstimatedSize");

        return new InstalledApp
        {
            DisplayName = displayName.Trim(),
            DisplayVersion = GetString(key, "DisplayVersion"),
            Publisher = GetString(key, "Publisher"),
            InstallLocation = installLocation,
            UninstallString = uninstallString,
            QuietUninstallString = GetString(key, "QuietUninstallString"),
            DisplayIcon = GetString(key, "DisplayIcon"),
            InstallDate = ParseDate(key.GetValue("InstallDate")),
            EstimatedSizeBytes = sizeKb is > 0 ? sizeKb.Value * 1024L : null,
            IsWindowsInstaller = isMsi,
            MsiProductCode = productCode,
            InstallerType = type,
            UninstallKey = location,
            UninstallerMissing = IsUninstallerMissing(type, productCode, uninstallString),
        };
    }

    private static bool IsUninstallerMissing(InstallerType type, string? productCode, string? uninstallString)
    {
        if (type == InstallerType.Msi && productCode is not null) return false;
        var command = CommandLineParser.Parse(uninstallString);
        if (command is null) return true;
        return Path.IsPathRooted(command.FileName) && !File.Exists(command.FileName);
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidPattern();

    internal static string? FindProductCode(string keyName, string? uninstallString)
    {
        var fromKey = GuidPattern().Match(keyName);
        if (fromKey.Success && fromKey.Length == keyName.Length) return fromKey.Value.ToUpperInvariant();
        var fromCommand = uninstallString is null ? null : GuidPattern().Match(uninstallString);
        return fromCommand is { Success: true } ? fromCommand.Value.ToUpperInvariant() : null;
    }

    internal static bool IsGuid(string s) => GuidPattern().Match(s) is { Success: true } m && m.Length == s.Length;

    private static string? GetString(RegistryKey key, string name)
    {
        var value = key.GetValue(name) switch
        {
            string s => s,
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        value = value?.Trim().Trim('\0');
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? GetInt(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static DateTime? ParseDate(object? value)
    {
        var s = value switch
        {
            string str => str.Trim(),
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return exact;
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var loose)) return loose;
        return null;
    }

    internal static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"').Trim();
        if (p.Length == 0) return null;
        var root = Path.GetPathRoot(p) ?? "";
        if (p.Length > root.Length) p = p.TrimEnd('\\', '/');
        return p;
    }
}
