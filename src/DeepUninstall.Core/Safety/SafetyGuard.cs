using DeepUninstall.Core.Models;
using Microsoft.Win32;

namespace DeepUninstall.Core.Safety;

/// <summary>
/// The last line of defence. Every delete goes through this class, both when leftovers are
/// collected and again right before they are removed. It refuses to touch system locations,
/// well-known user folders, and anything that contains them.
/// </summary>
public sealed class SafetyGuard
{
    private readonly List<string> _protectedDirectories;
    private readonly List<string> _blockedTrees;

    /// <param name="protectedDirectories">
    /// Folders that must never be deleted, and whose ancestors must never be deleted either.
    /// Their contents may be deleted (e.g. a single shortcut on the Desktop).
    /// </param>
    /// <param name="blockedTrees">Folders whose whole subtree is off limits (e.g. C:\Windows).</param>
    public SafetyGuard(IEnumerable<string> protectedDirectories, IEnumerable<string> blockedTrees)
    {
        _protectedDirectories = NormalizeAll(protectedDirectories);
        _blockedTrees = NormalizeAll(blockedTrees);
    }

    public IReadOnlyList<string> ProtectedDirectories => _protectedDirectories;
    public IReadOnlyList<string> BlockedTrees => _blockedTrees;

    public static SafetyGuard CreateDefault()
    {
        var protectedDirs = new List<string>();
        var blocked = new List<string>();

        // Every shell "special folder" (Program Files, AppData, Desktop, Start Menu, Fonts...).
        foreach (var folder in Enum.GetValues<Environment.SpecialFolder>())
        {
            var path = TryGetFolder(folder);
            if (path is not null) protectedDirs.Add(path);
        }

        var profile = TryGetFolder(Environment.SpecialFolder.UserProfile);
        var appData = TryGetFolder(Environment.SpecialFolder.ApplicationData);
        var localAppData = TryGetFolder(Environment.SpecialFolder.LocalApplicationData);
        var programData = TryGetFolder(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = TryGetFolder(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = TryGetFolder(Environment.SpecialFolder.ProgramFilesX86);
        var windows = TryGetFolder(Environment.SpecialFolder.Windows);

        if (profile is not null)
        {
            protectedDirs.Add(Path.Combine(profile, "AppData"));
            protectedDirs.Add(Path.Combine(profile, "AppData", "LocalLow"));
            protectedDirs.Add(Path.Combine(profile, "Downloads"));
            protectedDirs.Add(Path.Combine(profile, "Saved Games"));
            protectedDirs.Add(Path.Combine(profile, "Contacts"));
            protectedDirs.Add(Path.Combine(profile, "Links"));
            protectedDirs.Add(Path.Combine(profile, "Searches"));
            var users = Path.GetDirectoryName(profile);
            if (users is not null)
            {
                protectedDirs.Add(users);
                protectedDirs.Add(Path.Combine(users, "Default"));
                protectedDirs.Add(Path.Combine(users, "All Users"));
            }
        }

        foreach (var env in new[] { "PUBLIC", "OneDrive", "OneDriveConsumer", "OneDriveCommercial", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value)) protectedDirs.Add(value);
        }
        protectedDirs.Add(Path.GetTempPath());

        foreach (var root in new[] { appData, localAppData, programData })
        {
            if (root is null) continue;
            protectedDirs.Add(Path.Combine(root, "Microsoft"));
            protectedDirs.Add(Path.Combine(root, "Microsoft", "Windows"));
            protectedDirs.Add(Path.Combine(root, "Microsoft", "Windows", "Start Menu"));
            protectedDirs.Add(Path.Combine(root, "Microsoft", "Windows", "Start Menu", "Programs"));
            protectedDirs.Add(Path.Combine(root, "Microsoft", "Windows", "Start Menu", "Programs", "Startup"));
            protectedDirs.Add(Path.Combine(root, "Microsoft", "Internet Explorer", "Quick Launch"));
            protectedDirs.Add(Path.Combine(root, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned"));
            protectedDirs.Add(Path.Combine(root, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"));
        }

        if (localAppData is not null)
        {
            protectedDirs.Add(Path.Combine(localAppData, "Programs"));
            protectedDirs.Add(Path.Combine(localAppData, "Packages"));
            protectedDirs.Add(Path.Combine(localAppData, "Temp"));
            protectedDirs.Add(Path.Combine(localAppData, "VirtualStore"));
            blocked.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps"));
        }

        if (programData is not null)
        {
            protectedDirs.Add(Path.Combine(programData, "Package Cache"));
            blocked.Add(Path.Combine(programData, "Microsoft", "Windows Defender"));
            blocked.Add(Path.Combine(programData, "Microsoft", "Crypto"));
        }

        foreach (var pf in new[] { programFiles, programFilesX86 })
        {
            if (pf is null) continue;
            protectedDirs.Add(Path.Combine(pf, "Common Files"));
            protectedDirs.Add(Path.Combine(pf, "Microsoft"));
            foreach (var name in new[]
                     {
                         "WindowsApps", "ModifiableWindowsApps", "Windows Defender",
                         "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player",
                         "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security",
                         "Windows Sidebar", "WindowsPowerShell", "Internet Explorer", "Reference Assemblies",
                     })
            {
                blocked.Add(Path.Combine(pf, name));
            }
        }

        if (windows is not null)
        {
            blocked.Add(windows);
            var systemRoot = Path.GetPathRoot(windows);
            if (systemRoot is not null)
            {
                foreach (var name in new[] { "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "$WinREAgent", "Config.Msi" })
                    blocked.Add(Path.Combine(systemRoot, name));
            }
        }

        // Never delete ourselves or our own backups.
        blocked.Add(AppContext.BaseDirectory);
        blocked.Add(AppDataPaths.Root);

        return new SafetyGuard(protectedDirs, blocked);
    }

    public bool IsDirectoryDeletable(string path, out string? reason)
    {
        if (!PathUtil.TryNormalize(path, out var full)) { reason = "not a valid local path"; return false; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) { reason = "network paths are never cleaned"; return false; }
        if (PathUtil.Depth(full) == 0) { reason = "drive roots are never deleted"; return false; }

        foreach (var tree in _blockedTrees)
        {
            if (PathUtil.IsWithinOrEqual(full, tree) || PathUtil.IsStrictlyWithin(tree, full))
            {
                reason = $"protected system location ({tree})";
                return false;
            }
        }

        foreach (var protectedDir in _protectedDirectories)
        {
            if (PathUtil.IsWithinOrEqual(protectedDir, full))
            {
                reason = $"protected folder ({protectedDir})";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public bool IsFileDeletable(string path, out string? reason)
    {
        if (!PathUtil.TryNormalize(path, out var full)) { reason = "not a valid local path"; return false; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) { reason = "network paths are never cleaned"; return false; }

        foreach (var tree in _blockedTrees)
        {
            if (PathUtil.IsWithinOrEqual(full, tree))
            {
                reason = $"protected system location ({tree})";
                return false;
            }
        }

        if (_protectedDirectories.Any(p => p.Equals(full, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "protected folder";
            return false;
        }

        reason = null;
        return true;
    }

    // Keys that must survive, along with every key above them.
    private static readonly string[] ProtectedKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
        @"SOFTWARE\Classes\CLSID",
        @"SOFTWARE\Classes\Applications",
        @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\Directory\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\Directory\Background\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\Folder\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\Drive\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\AllFilesystemObjects\shellex\ContextMenuHandlers",
        @"SOFTWARE\Clients\StartMenuInternet",
        @"SOFTWARE\Clients\Mail",
        @"SOFTWARE\RegisteredApplications",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    // Whole subtrees we never delete keys from.
    private static readonly string[] BlockedKeyTrees =
    [
        "SYSTEM", "SAM", "SECURITY", "HARDWARE", "BCD00000000",
        @"SOFTWARE\Policies",
        @"SOFTWARE\Microsoft\Windows NT",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate",
        @"SOFTWARE\Microsoft\Windows Defender",
        @"SOFTWARE\Microsoft\Cryptography",
        @"SOFTWARE\Classes\Installer",
        @"SOFTWARE\Classes\Interface",
        @"SOFTWARE\Classes\TypeLib",
        @"SOFTWARE\Classes\Local Settings",
    ];

    // Values may be deleted from these keys even though they sit in a blocked tree.
    private static readonly string[] ValueOnlyKeys =
    [
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules",
    ];

    private static readonly HashSet<string> ProtectedSoftwareChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "WOW6432Node", "Policies", "Clients", "RegisteredApplications", "Windows",
    };

    // File types, protocols and shell objects Windows depends on.
    private static readonly HashSet<string> EssentialClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "AllFilesystemObjects", "Directory", "Drive", "Folder", "Unknown", "DesktopBackground",
        "SystemFileAssociations", "MIME", "PROTOCOLS", "CLSID", "AppID", "Applications", "Interface", "TypeLib",
        "exefile", "lnkfile", "batfile", "cmdfile", "comfile", "dllfile", "txtfile", "inifile", "regfile",
        "htmlfile", "jsfile", "VBSFile", "WSFFile", "Msi.Package", "Msi.Patch", "Microsoft.PowerShellScript.1",
        "http", "https", "ftp", "file", "mailto", "ms-settings", "InternetShortcut", "WOW6432Node",
    };

    public bool IsRegistryKeyDeletable(RegistryLocation key, out string? reason)
    {
        if (key.Hive is not (RegistryHive.LocalMachine or RegistryHive.CurrentUser))
        {
            reason = "only HKLM and HKCU are cleaned";
            return false;
        }

        foreach (var subKey in new[] { Clean(key.SubKey), Clean(key.PhysicalSubKey) })
        {
            var segments = subKey.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                reason = "top-level keys are never deleted";
                return false;
            }

            if (segments.Length == 2 && segments[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)
                && ProtectedSoftwareChildren.Contains(segments[1]))
            {
                reason = "protected registry key";
                return false;
            }

            if (BlockedKeyTrees.Any(t => IsKeyWithinOrEqual(subKey, t)))
            {
                reason = "protected system registry area";
                return false;
            }

            if (ProtectedKeys.Any(p => IsKeyWithinOrEqual(p, subKey)))
            {
                reason = "protected registry key";
                return false;
            }

            // HKLM\SOFTWARE\Classes\<x>: never delete file extensions or essential classes.
            if (segments.Length == 3 && segments[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)
                && segments[1].Equals("Classes", StringComparison.OrdinalIgnoreCase)
                && (segments[2].StartsWith('.') || EssentialClasses.Contains(segments[2])))
            {
                reason = "file type registration Windows depends on";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public bool IsRegistryValueDeletable(RegistryLocation key, string valueName, out string? reason)
    {
        if (string.IsNullOrEmpty(valueName))
        {
            reason = "default values are never deleted";
            return false;
        }

        if (key.Hive is not (RegistryHive.LocalMachine or RegistryHive.CurrentUser))
        {
            reason = "only HKLM and HKCU are cleaned";
            return false;
        }

        var subKey = Clean(key.SubKey);
        if (ValueOnlyKeys.Any(k => k.Equals(subKey, StringComparison.OrdinalIgnoreCase)))
        {
            reason = null;
            return true;
        }

        if (BlockedKeyTrees.Any(t => IsKeyWithinOrEqual(subKey, t)))
        {
            reason = "protected system registry area";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool IsKeyWithinOrEqual(string key, string ancestor) =>
        key.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(ancestor + "\\", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string subKey) => subKey.Trim('\\');

    private static string? TryGetFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static List<string> NormalizeAll(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (PathUtil.TryNormalize(p, out var n) && !result.Contains(n, StringComparer.OrdinalIgnoreCase))
                result.Add(n);
        }
        return result;
    }
}
