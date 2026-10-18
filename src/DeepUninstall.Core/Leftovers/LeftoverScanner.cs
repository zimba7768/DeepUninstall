using DeepUninstall.Core.Leftovers.Probes;
using DeepUninstall.Core.Matching;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Safety;
using DeepUninstall.Core.Uninstall;

namespace DeepUninstall.Core.Leftovers;

/// <summary>
/// Finds files, folders, registry entries, services, scheduled tasks, shortcuts and shell
/// integration a program left behind.
/// </summary>
public sealed class LeftoverScanner
{
    private readonly SafetyGuard _guard;

    public LeftoverScanner(SafetyGuard? guard = null) => _guard = guard ?? SafetyGuard.CreateDefault();

    /// <param name="app">The program, as it was registered before it was uninstalled.</param>
    /// <param name="otherApps">Every other program that is still installed.</param>
    public IReadOnlyList<Leftover> Scan(InstalledApp app, IReadOnlyList<InstalledApp> otherApps, IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        var others = otherApps.Where(o => !o.UninstallKey.Equals(app.UninstallKey)).ToList();
        var matcher = NameMatcher.ForApp(app, others);
        var otherDirs = CollectAppDirectories(others);
        var trusted = ResolveInstallDirectories(app, others, otherDirs, log);

        if (trusted.Count == 0)
            log?.Report("No install folder could be confirmed. Only name-based matches will be reported.");
        else
            foreach (var dir in trusted) log?.Report($"Install folder: {dir}");

        if (matcher.ProductNames.Count == 0)
            log?.Report("The program's name is too generic, or shared with another installed program, to match by name.");

        var ctx = new ScanContext(app, matcher, _guard, trusted, otherDirs, log, cancellationToken);

        Run(ctx, "uninstall registration", UninstallEntryProbe.Run);
        Run(ctx, "files and folders", FileSystemProbe.Run);
        Run(ctx, "registry", SoftwareKeyProbe.Run);
        Run(ctx, "shortcuts", ShortcutProbe.Run);
        Run(ctx, "services", ServiceProbe.Run);
        Run(ctx, "scheduled tasks", ScheduledTaskProbe.Run);
        Run(ctx, "startup entries", AutorunProbe.Run);
        Run(ctx, "app paths", AppPathsProbe.Run);
        Run(ctx, "shell integration", ShellIntegrationProbe.Run);
        Run(ctx, "firewall rules", FirewallProbe.Run);

        var results = ctx.Results.Build();
        log?.Report($"Found {results.Count} leftover item(s).");
        return results;
    }

    private static void Run(ScanContext ctx, string name, Action<ScanContext> probe)
    {
        ctx.Token.ThrowIfCancellationRequested();
        ctx.Log?.Report($"Scanning {name}...");
        try
        {
            probe(ctx);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log?.Report($"  Skipped {name}: {e.Message}");
        }
    }

    internal static List<string> CollectAppDirectories(IEnumerable<InstalledApp> apps)
    {
        var dirs = new List<string>();
        void Add(string? dir)
        {
            if (PathUtil.TryNormalize(dir, out var full) && PathUtil.Depth(full) > 0 && !dirs.Contains(full, StringComparer.OrdinalIgnoreCase))
                dirs.Add(full);
        }

        foreach (var app in apps)
        {
            Add(app.InstallLocation);
            var uninstaller = CommandLineParser.Parse(app.UninstallString, _ => false)?.FileName;
            if (uninstaller is not null && Path.IsPathFullyQualified(uninstaller))
                Add(Path.GetDirectoryName(uninstaller));
        }
        return dirs;
    }

    /// <summary>
    /// Works out which folders can be treated as "the install folder". Everything that points into
    /// these folders is considered a strong leftover, so this is deliberately conservative.
    /// </summary>
    private List<string> ResolveInstallDirectories(InstalledApp app, IReadOnlyList<InstalledApp> others, List<string> otherDirs, IProgress<string>? log)
    {
        var result = new List<string>();
        var nameOnly = NameMatcher.ForApp(app, others, includeInstallFolderName: false);

        void Consider(string? dir, bool requireNameMatch, string source)
        {
            if (!PathUtil.TryNormalize(dir, out var full)) return;
            if (result.Contains(full, StringComparer.OrdinalIgnoreCase)) return;

            if (!_guard.IsDirectoryDeletable(full, out var reason))
            {
                if (!requireNameMatch) log?.Report($"Ignoring {source} \"{full}\": {reason}.");
                return;
            }

            if (otherDirs.Any(o => PathUtil.IsWithinOrEqual(o, full)))
            {
                log?.Report($"Ignoring {source} \"{full}\": another installed program uses it.");
                return;
            }

            // A folder right below a drive root (e.g. D:\Games) is only trusted if it is named after the program.
            if ((requireNameMatch || PathUtil.Depth(full) <= 1) && nameOnly.MatchProduct(Path.GetFileName(full)) is null)
                return;

            result.Add(full);
        }

        Consider(app.InstallLocation, requireNameMatch: false, "install location");

        foreach (var file in new[] { CommandLineParser.Parse(app.UninstallString, _ => false)?.FileName, IconPath(app.DisplayIcon) })
        {
            if (file is null || !Path.IsPathFullyQualified(file)) continue;
            var dir = Path.GetDirectoryName(file);
            Consider(dir, requireNameMatch: true, "folder");
            // Uninstallers often sit one level down, e.g. <install>\uninstall\uninst.exe.
            if (dir is not null) Consider(Path.GetDirectoryName(dir), requireNameMatch: true, "folder");
        }

        // Many MSI packages record no install location. Fall back to a folder in Program Files
        // whose name exactly matches a distinctive program name (not a version-stripped family name).
        if (result.Count == 0)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         string.IsNullOrEmpty(local) ? "" : Path.Combine(local, "Programs"),
                     }.Where(r => r.Length > 0 && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var sub in PathUtil.SafeEnumerateDirectories(root))
                {
                    var normalized = NameMatcher.Normalize(Path.GetFileName(sub));
                    if (nameOnly.ExactProductNames.Contains(normalized) && nameOnly.MatchProduct(Path.GetFileName(sub)) == Confidence.High)
                        Consider(sub, requireNameMatch: true, "folder");
                }
            }
        }

        return result;
    }

    private static string? IconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var s = displayIcon.Trim().Trim('"');
        var comma = s.LastIndexOf(',');
        if (comma > 2) s = s[..comma];
        return s.Trim().Trim('"');
    }
}
