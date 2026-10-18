using System.Security;
using DeepUninstall.Core.Discovery;
using DeepUninstall.Core.Leftovers.Probes;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Safety;
using Microsoft.Win32;

namespace DeepUninstall.Core.RegistryCleaning;

public enum ScanLevel
{
    /// <summary>Entries that are clearly dead and harmless to remove.</summary>
    Safe,

    /// <summary>Adds program registrations whose files are gone.</summary>
    Moderate,

    /// <summary>Adds COM registrations and empty keys. Results are not pre-selected.</summary>
    Advanced,
}

public enum RegistryIssueCategory
{
    MissingStartupPrograms,
    MissingAppPaths,
    MissingOpenWithApplications,
    StaleMuiCache,
    MissingSharedDlls,
    OrphanedUninstallEntries,
    MissingFileTypeHandlers,
    FirewallRulesForMissingPrograms,
    InvalidShellExtensionApprovals,
    MissingComComponents,
    EmptySoftwareKeys,
}

public sealed record RegistryCategoryInfo(RegistryIssueCategory Category, string Name, string Description, ScanLevel Level);

public sealed record RegistryIssue(RegistryIssueCategory Category, Leftover Item);

/// <summary>
/// Finds registry entries that point at programs and files that no longer exist.
/// </summary>
/// <remarks>
/// Every finding needs hard evidence, normally a referenced file that is definitely missing
/// (see <see cref="MissingFileChecker"/>). Removal goes through the same safety guard and .reg
/// backups as uninstall leftovers, so everything can be restored from the Backups page.
/// </remarks>
public sealed class RegistryCleaner
{
    public static IReadOnlyList<RegistryCategoryInfo> Categories { get; } =
    [
        new(RegistryIssueCategory.MissingStartupPrograms, "Startup entries", "Programs set to start with Windows that no longer exist.", ScanLevel.Safe),
        new(RegistryIssueCategory.MissingAppPaths, "App Paths", "Shortcuts Windows uses to find programs by name, for programs that are gone.", ScanLevel.Safe),
        new(RegistryIssueCategory.MissingOpenWithApplications, "\"Open with\" programs", "Programs listed in \"Open with\" whose executable is gone.", ScanLevel.Safe),
        new(RegistryIssueCategory.StaleMuiCache, "Program name cache", "Cached display names of programs that no longer exist.", ScanLevel.Safe),
        new(RegistryIssueCategory.MissingSharedDlls, "Shared DLL counters", "Reference counts for shared files that no longer exist.", ScanLevel.Moderate),
        new(RegistryIssueCategory.OrphanedUninstallEntries, "Broken uninstall entries", "Installed-apps entries whose uninstaller and install folder are both gone.", ScanLevel.Moderate),
        new(RegistryIssueCategory.MissingFileTypeHandlers, "File type handlers", "File types and protocols whose opening program is gone.", ScanLevel.Moderate),
        new(RegistryIssueCategory.FirewallRulesForMissingPrograms, "Firewall rules", "Windows Firewall rules for programs that no longer exist.", ScanLevel.Moderate),
        new(RegistryIssueCategory.InvalidShellExtensionApprovals, "Shell extension approvals", "Explorer approvals for shell extensions that are no longer registered.", ScanLevel.Moderate),
        new(RegistryIssueCategory.MissingComComponents, "COM components", "Component registrations whose DLL or program file is gone.", ScanLevel.Advanced),
        new(RegistryIssueCategory.EmptySoftwareKeys, "Empty settings keys", "Empty keys under HKEY_CURRENT_USER\\Software left by removed programs.", ScanLevel.Advanced),
    ];

    private readonly SafetyGuard _guard;
    private readonly MissingFileChecker _files;

    public RegistryCleaner(SafetyGuard? guard = null, MissingFileChecker? files = null)
    {
        _guard = guard ?? SafetyGuard.CreateDefault();
        _files = files ?? new MissingFileChecker();
    }

    public static RegistryCategoryInfo Info(RegistryIssueCategory category) => Categories.First(c => c.Category == category);

    /// <summary>The categories included at a level (each level includes the ones below it).</summary>
    public static IReadOnlyList<RegistryIssueCategory> CategoriesFor(ScanLevel level) =>
        Categories.Where(c => c.Level <= level).Select(c => c.Category).ToList();

    public IReadOnlyList<RegistryIssue> Scan(IEnumerable<RegistryIssueCategory> categories, IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        var results = new List<RegistryIssue>();
        foreach (var category in categories.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = Info(category);
            log?.Report($"Checking {info.Name.ToLowerInvariant()}...");
            var confidence = info.Level == ScanLevel.Advanced ? Confidence.Medium : Confidence.High;
            var found = new List<Leftover>();
            try
            {
                Run(category, found, confidence, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                log?.Report($"  Skipped {info.Name}: {e.Message}");
            }

            foreach (var item in found.Where(IsAllowed).DistinctBy(i => i.Identity))
                results.Add(new RegistryIssue(category, item));
            log?.Report($"  {found.Count} found");
        }
        return results;
    }

    private bool IsAllowed(Leftover item) => item.Kind switch
    {
        LeftoverKind.RegistryKey => _guard.IsRegistryKeyDeletable(item.Key!, out _),
        LeftoverKind.RegistryValue => _guard.IsRegistryValueDeletable(item.Key!, item.ValueName ?? "", out _),
        LeftoverKind.FirewallRule => true,
        _ => false,
    };

    private void Run(RegistryIssueCategory category, List<Leftover> found, Confidence confidence, CancellationToken ct)
    {
        switch (category)
        {
            case RegistryIssueCategory.MissingStartupPrograms: StartupEntries(found, confidence); break;
            case RegistryIssueCategory.MissingAppPaths: AppPaths(found, confidence); break;
            case RegistryIssueCategory.MissingOpenWithApplications: OpenWithApplications(found, confidence); break;
            case RegistryIssueCategory.StaleMuiCache: MuiCache(found, confidence); break;
            case RegistryIssueCategory.MissingSharedDlls: SharedDlls(found, confidence); break;
            case RegistryIssueCategory.OrphanedUninstallEntries: UninstallEntries(found, confidence); break;
            case RegistryIssueCategory.MissingFileTypeHandlers: FileTypeHandlers(found, confidence, ct); break;
            case RegistryIssueCategory.FirewallRulesForMissingPrograms: FirewallRules(found, confidence); break;
            case RegistryIssueCategory.InvalidShellExtensionApprovals: ShellExtensionApprovals(found, confidence); break;
            case RegistryIssueCategory.MissingComComponents: ComComponents(found, confidence, ct); break;
            case RegistryIssueCategory.EmptySoftwareKeys: EmptyKeys(found, confidence); break;
        }
    }

    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    internal const string MuiCacheKey = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";

    private void StartupEntries(List<Leftover> found, Confidence confidence)
    {
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            foreach (var name in new[] { "Run", "RunOnce" })
            {
                var location = new RegistryLocation(hive, view, $@"{CurrentVersion}\{name}");
                using var key = location.Open();
                if (key is null) continue;
                foreach (var value in RegistryHelpers.ValueNames(key).Where(v => v.Length > 0))
                {
                    if (_files.IsCommandTargetMissing(GetRaw(key, null, value)))
                        found.Add(Value(location, value, confidence, $"Starts \"{value}\" with Windows, but its program no longer exists"));
                }
            }
        }
    }

    private void AppPaths(List<Leftover> found, Confidence confidence)
    {
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            var root = new RegistryLocation(hive, view, $@"{CurrentVersion}\App Paths");
            foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(root))
            {
                if (_files.IsPathMissing(GetRaw(key, null)))
                    found.Add(Key(root.Child(name), confidence, $"Points Windows to {name}, which no longer exists"));
            }
        }
    }

    private void OpenWithApplications(List<Leftover> found, Confidence confidence)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            var root = new RegistryLocation(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default, @"SOFTWARE\Classes\Applications");
            foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(root))
            {
                if (AllVerbsMissing(key))
                    found.Add(Key(root.Child(name), confidence, $"\"Open with\" entry for {name}, which no longer exists"));
            }
        }
    }

    private void MuiCache(List<Leftover> found, Confidence confidence)
    {
        var location = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Default, MuiCacheKey);
        using var key = location.Open();
        if (key is null) return;
        foreach (var value in RegistryHelpers.ValueNames(key))
        {
            if (value.StartsWith('@') || value.Length == 0) continue;
            // Value names look like "C:\Apps\Foo\foo.exe.FriendlyAppName".
            var dot = value.LastIndexOf('.');
            if (dot <= 0) continue;
            var path = value[..dot];
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (_files.IsPathMissing(path))
                found.Add(Value(location, value, confidence, $"Cached name for {Path.GetFileName(path)}, which no longer exists"));
        }
    }

    private void SharedDlls(List<Leftover> found, Confidence confidence)
    {
        foreach (var view in Views64And32())
        {
            var location = new RegistryLocation(RegistryHive.LocalMachine, view, $@"{CurrentVersion}\SharedDLLs");
            using var key = location.Open();
            if (key is null) continue;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var value in RegistryHelpers.ValueNames(key))
            {
                // Counters for Windows' own files are left alone, even dead ones.
                if (Environment.ExpandEnvironmentVariables(value).StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                if (_files.IsPathMissing(value))
                    found.Add(Value(location, value, confidence, $"Usage counter for {Path.GetFileName(value)}, which no longer exists"));
            }
        }
    }

    private void UninstallEntries(List<Leftover> found, Confidence confidence)
    {
        foreach (var app in new InstalledAppScanner().Scan())
        {
            if (app.IsWindowsInstaller || app.InstallerType == InstallerType.Msi || !app.UninstallerMissing) continue;
            if (!_files.IsCommandTargetMissing(app.UninstallString)) continue;
            // The install folder must be gone too (or never recorded), so nothing of the program remains.
            if (!string.IsNullOrWhiteSpace(app.InstallLocation) && !_files.IsPathMissing(app.InstallLocation)) continue;
            found.Add(Key(app.UninstallKey, confidence, $"\"{app.DisplayName}\" is listed as installed, but its uninstaller and files are gone"));
        }
    }

    private static readonly HashSet<string> NonProgIdClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "CLSID", "Interface", "TypeLib", "AppID", "Installer", "Applications", "SystemFileAssociations", "MIME",
        "Local Settings", "WOW6432Node", "Media Type", "MediaFoundation", "PROTOCOLS", "DirectShow",
        "Component Categories", "Record", "Directory", "Folder", "Drive", "AllFilesystemObjects", "DesktopBackground",
    };

    private void FileTypeHandlers(List<Leftover> found, Confidence confidence, CancellationToken ct)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            var classes = new RegistryLocation(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default, @"SOFTWARE\Classes");
            foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(classes))
            {
                ct.ThrowIfCancellationRequested();
                if (name.StartsWith('.') || NonProgIdClasses.Contains(name)) continue;
                if (!AllVerbsMissing(key)) continue;
                // A COM-backed type is judged by its CLSID instead.
                if (RegistryHelpers.GetString(key, "CLSID") is not null) continue;
                found.Add(Key(classes.Child(name), confidence, "File type or protocol whose opening program no longer exists"));
            }
        }
    }

    private void FirewallRules(List<Leftover> found, Confidence confidence)
    {
        var location = new RegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, FirewallProbe.RulesKey);
        using var key = location.Open();
        if (key is null) return;

        var byProgram = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in RegistryHelpers.ValueNames(key))
        {
            var app = RegistryHelpers.GetString(key, null, value) is { } data ? FirewallProbe.ExtractField(data, "App") : null;
            if (app is null || app.Equals("System", StringComparison.OrdinalIgnoreCase) || !_files.IsPathMissing(app)) continue;
            var program = Environment.ExpandEnvironmentVariables(app);
            if (!byProgram.TryGetValue(program, out var list)) byProgram[program] = list = [];
            list.Add(value);
        }

        foreach (var (program, values) in byProgram)
        {
            found.Add(new Leftover
            {
                Kind = LeftoverKind.FirewallRule,
                Path = program,
                Key = location,
                RelatedValueNames = values,
                Confidence = confidence,
                Reason = $"{values.Count} firewall rule(s) for {Path.GetFileName(program)}, which no longer exists",
            });
        }
    }

    private void ShellExtensionApprovals(List<Leftover> found, Confidence confidence)
    {
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            var location = new RegistryLocation(hive, view, $@"{CurrentVersion}\Shell Extensions\Approved");
            using var key = location.Open();
            if (key is null) continue;
            foreach (var value in RegistryHelpers.ValueNames(key))
            {
                if (!value.StartsWith('{') || ClsidExists(value)) continue;
                found.Add(Value(location, value, confidence, $"Approval for shell extension {value}, which is no longer registered"));
            }
        }
    }

    private void ComComponents(List<Leftover> found, Confidence confidence, CancellationToken ct)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            var root = new RegistryLocation(hive, view, @"SOFTWARE\Classes\CLSID");
            foreach (var (clsid, key) in RegistryHelpers.EnumerateSubKeys(root))
            {
                ct.ThrowIfCancellationRequested();
                var inproc = GetRaw(key, "InprocServer32");
                var local = GetRaw(key, "LocalServer32");
                if (inproc is null && local is null) continue;

                // Never judge Windows' own components: optional features come and go.
                var expanded = Environment.ExpandEnvironmentVariables(inproc ?? local ?? "").Trim('"');
                if (expanded.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;

                var inprocMissing = inproc is null || _files.IsPathMissing(inproc);
                var localMissing = local is null || _files.IsCommandTargetMissing(local);
                if (inprocMissing && localMissing)
                {
                    var file = Path.GetFileName(expanded.TrimEnd('\\'));
                    found.Add(Key(root.Child(clsid), confidence, $"COM component whose file ({(file.Length > 0 ? file : expanded)}) no longer exists"));
                }
            }
        }
    }

    private void EmptyKeys(List<Leftover> found, Confidence confidence)
    {
        var software = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Default, "SOFTWARE");
        foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(software))
        {
            if (name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) || name.Equals("Classes", StringComparison.OrdinalIgnoreCase)) continue;
            if (RegistryHelpers.IsEmpty(key))
            {
                found.Add(Key(software.Child(name), confidence, "Empty settings key"));
                continue;
            }
            foreach (var child in RegistryHelpers.SubKeyNames(key))
            {
                using var childKey = RegistryHelpers.OpenSubKey(key, child);
                if (childKey is not null && RegistryHelpers.IsEmpty(childKey))
                    found.Add(Key(software.Child(name).Child(child), confidence, "Empty settings key"));
            }
        }
    }

    private bool AllVerbsMissing(RegistryKey key)
    {
        using var shell = RegistryHelpers.OpenSubKey(key, "shell");
        if (shell is null) return false;
        var commands = RegistryHelpers.SubKeyNames(shell)
            .Select(verb => GetRaw(shell, $@"{verb}\command"))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
        return commands.Count > 0 && commands.All(_files.IsCommandTargetMissing);
    }

    private static bool ClsidExists(string clsid)
    {
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            if (new RegistryLocation(hive, view, $@"SOFTWARE\Classes\CLSID\{clsid}").Exists()) return true;
        }
        return false;
    }

    private static IEnumerable<RegistryView> Views64And32()
    {
        yield return RegistryView.Registry64;
        if (Environment.Is64BitOperatingSystem) yield return RegistryView.Registry32;
    }

    /// <summary>Reads a string without expanding %variables%, so the checker can try 32 and 64-bit meanings.</summary>
    private static string? GetRaw(RegistryKey key, string? subKey, string? valueName = null)
    {
        try
        {
            if (string.IsNullOrEmpty(subKey))
                return key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            using var sub = key.OpenSubKey(subKey);
            return sub?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static Leftover Key(RegistryLocation location, Confidence confidence, string reason) => new()
    {
        Kind = LeftoverKind.RegistryKey,
        Key = location,
        Path = location.ToString(),
        Confidence = confidence,
        Reason = reason,
    };

    private static Leftover Value(RegistryLocation location, string valueName, Confidence confidence, string reason) => new()
    {
        Kind = LeftoverKind.RegistryValue,
        Key = location,
        ValueName = valueName,
        Path = location.ToString(),
        Confidence = confidence,
        Reason = reason,
    };
}
