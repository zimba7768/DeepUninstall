using DeepUninstall.Core;
using DeepUninstall.Core.Discovery;
using DeepUninstall.Core.Leftovers;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Removal;
using DeepUninstall.Core.Safety;
using DeepUninstall.Core.Uninstall;

namespace DeepUninstall.Cli;

internal static class Program
{
    private const string Usage = """
        DeepUninstall - thorough, safe uninstaller for Windows

        Usage:
          deepuninstall list [filter]                 List installed programs
          deepuninstall info <program>                Show details and the uninstall commands
          deepuninstall scan <program>                Dry run: show what the leftover scan finds right now
          deepuninstall uninstall <program> [options] Uninstall, then clean up leftovers

        Options for uninstall:
          --silent            Use the program's silent uninstall mode when available
          --force             Skip the program's uninstaller (for broken entries) and clean up directly
          --include-medium    Also remove medium-confidence leftovers
          --yes               Do not ask for confirmation before removing leftovers
          --no-restore-point  Do not create a System Restore point
          --permanent         Delete files permanently instead of using the Recycle Bin

        <program> is matched against the display name (exact match first, then substring).
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var flags = args.Skip(1).Where(a => a.StartsWith("--", StringComparison.Ordinal)).Select(a => a.ToLowerInvariant()).ToHashSet();
        var query = string.Join(' ', args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)));

        try
        {
            return command switch
            {
                "list" => List(query),
                "info" => Info(query),
                "scan" => Scan(query),
                "uninstall" => await UninstallAsync(query, flags),
                _ => Fail($"Unknown command \"{args[0]}\".\n\n{Usage}"),
            };
        }
        catch (OperationCanceledException)
        {
            return Fail("Cancelled.");
        }
    }

    private static int List(string filter)
    {
        var apps = new InstalledAppScanner().Scan()
            .Where(a => filter.Length == 0 || a.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                           || (a.Publisher?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        foreach (var app in apps)
        {
            var flagsText = (app.IsPerUser ? " [user]" : "") + (app.UninstallerMissing ? " [broken]" : "");
            Console.WriteLine($"{Truncate(app.DisplayName, 50),-50}  {Truncate(app.DisplayVersion ?? "", 16),-16}  {Truncate(app.Publisher ?? "", 30)}{flagsText}");
        }
        Console.WriteLine($"\n{apps.Count} program(s).");
        return 0;
    }

    private static int Info(string query)
    {
        var app = Resolve(query);
        if (app is null) return 2;

        Console.WriteLine($"Name:            {app.DisplayName}");
        Console.WriteLine($"Version:         {app.DisplayVersion}");
        Console.WriteLine($"Publisher:       {app.Publisher}");
        Console.WriteLine($"Installed:       {app.InstallDate:yyyy-MM-dd}");
        Console.WriteLine($"Size:            {FormatSize(app.EstimatedSizeBytes)}");
        Console.WriteLine($"Install folder:  {app.InstallLocation}");
        Console.WriteLine($"Installer type:  {app.InstallerType}");
        Console.WriteLine($"Registry entry:  {app.UninstallKey}");
        Console.WriteLine($"Uninstall:       {UninstallCommandBuilder.Build(app, silent: false)?.ToCommandLine() ?? "(none)"}");
        Console.WriteLine($"Silent:          {UninstallCommandBuilder.Build(app, silent: true)?.ToCommandLine() ?? "(not supported)"}");
        if (app.UninstallerMissing) Console.WriteLine("Note:            the uninstaller is missing; use \"uninstall --force\".");
        return 0;
    }

    private static int Scan(string query)
    {
        var app = Resolve(query);
        if (app is null) return 2;

        Console.WriteLine($"Dry run for {app}. Nothing will be changed.");
        Console.WriteLine("The program is still installed, so this lists everything that would count as a leftover if it remained.\n");
        var others = new InstalledAppScanner().Scan();
        var leftovers = new LeftoverScanner().Scan(app, others, new SyncProgress(m => Console.WriteLine($"  {m}")));
        Console.WriteLine();
        Print(leftovers);
        return 0;
    }

    private static async Task<int> UninstallAsync(string query, HashSet<string> flags)
    {
        var app = Resolve(query);
        if (app is null) return 2;

        if (!UninstallSession.IsElevated)
            return Fail("Uninstalling needs administrator rights. Run this from an elevated terminal.");

        var log = new SyncProgress(m => Console.WriteLine($"  {m}"));
        var session = new UninstallSession(app);
        var force = flags.Contains("--force");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (!flags.Contains("--no-restore-point"))
        {
            Console.WriteLine("Creating a System Restore point...");
            var rp = await RestorePoint.CreateAsync($"DeepUninstall: {app.DisplayName}");
            Console.WriteLine($"  {rp.Message}");
        }

        if (!force)
        {
            if (!session.CanRunUninstaller)
                return Fail("The program's uninstaller is missing. Re-run with --force to clean it up directly.");

            var silent = flags.Contains("--silent") && session.SupportsSilent;
            if (flags.Contains("--silent") && !silent) Console.WriteLine("Silent mode is not supported for this program; running the normal uninstaller.");
            Console.WriteLine($"Running the uninstaller for {app}... (Ctrl+C stops waiting)");
            var result = await session.RunUninstallerAsync(silent, log, cts.Token);

            if (session.IsStillRegistered())
            {
                Console.WriteLine();
                return Fail("The program is still registered as installed, so its uninstaller was probably cancelled or failed.\n" +
                            "No leftovers were removed. Re-run with --force to clean up anyway.");
            }

            if (result.Outcome == UninstallOutcome.SucceededRebootRequired)
                Console.WriteLine("A restart is needed to finish the uninstall.");
        }

        Console.WriteLine("\nScanning for leftovers...");
        var leftovers = await session.ScanLeftoversAsync(log, cts.Token);
        Console.WriteLine();
        Print(leftovers);

        var minimum = flags.Contains("--include-medium") ? Confidence.Medium : Confidence.High;
        var selected = leftovers.Where(l => l.Confidence >= minimum).ToList();
        if (selected.Count == 0)
        {
            Console.WriteLine("Nothing to remove.");
            return 0;
        }

        if (!flags.Contains("--yes"))
        {
            Console.Write($"\nRemove {selected.Count} item(s) with {minimum} confidence or higher? [y/N] ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return Fail("Nothing was removed.");
        }

        var report = await session.RemoveAsync(selected, new RemovalOptions { UseRecycleBin = !flags.Contains("--permanent") }, log);
        Console.WriteLine($"\nRemoved {report.Removed}, failed {report.Failed}. Backups: {report.BackupDirectory}");
        if (report.RebootRequired) Console.WriteLine("Some items are in use and will be removed when Windows restarts.");
        return report.Failed == 0 ? 0 : 1;
    }

    private static InstalledApp? Resolve(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Fail("Specify a program name.");
            return null;
        }

        var apps = new InstalledAppScanner().Scan();
        var exact = apps.Where(a => a.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : apps.Where(a => a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0)
        {
            Fail($"No installed program matches \"{query}\".");
            return null;
        }

        Console.Error.WriteLine($"\"{query}\" matches {matches.Count} programs. Be more specific:");
        foreach (var m in matches) Console.Error.WriteLine($"  {m}  ({m.UninstallKey})");
        return null;
    }

    private static void Print(IReadOnlyList<Leftover> leftovers)
    {
        if (leftovers.Count == 0)
        {
            Console.WriteLine("No leftovers found.");
            return;
        }

        foreach (var group in leftovers.GroupBy(l => l.Confidence).OrderByDescending(g => g.Key))
        {
            Console.WriteLine($"{group.Key} confidence:");
            foreach (var item in group)
                Console.WriteLine($"  [{item.Kind}] {item.DisplayPath}\n      {item.Reason}");
            Console.WriteLine();
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string FormatSize(long? bytes) => bytes switch
    {
        null => "",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>Progress that reports synchronously, so output stays in order.</summary>
    private sealed class SyncProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }
}
