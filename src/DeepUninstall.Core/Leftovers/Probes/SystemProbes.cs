using System.Xml.Linq;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Safety;
using DeepUninstall.Core.Uninstall;
using Microsoft.Win32;

namespace DeepUninstall.Core.Leftovers.Probes;

/// <summary>Windows services whose executable lives in the install folder.</summary>
internal static class ServiceProbe
{
    public static void Run(ScanContext ctx)
    {
        if (ctx.TrustedDirectories.Count == 0) return;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(new RegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services")))
        {
            var image = RegistryHelpers.GetString(key, null, "ImagePath");
            if (string.IsNullOrWhiteSpace(image)) continue;

            if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                image = windows + image[@"\SystemRoot".Length..];

            if (!ctx.ReferencesInstallFolder(image)) continue;

            var display = RegistryHelpers.GetString(key, null, "DisplayName") ?? name;
            if (display.StartsWith('@')) display = name;
            ctx.Add(new Leftover
            {
                Kind = LeftoverKind.Service,
                Path = name,
                Confidence = Confidence.High,
                Reason = $"Service \"{display}\" runs from the install folder",
            });
        }
    }
}

/// <summary>Task Scheduler tasks that run something from the install folder.</summary>
internal static class ScheduledTaskProbe
{
    public static void Run(ScanContext ctx)
    {
        var tasksDir = Path.Combine(Environment.SystemDirectory, "Tasks");
        if (!Directory.Exists(tasksDir)) return;

        foreach (var file in PathUtil.SafeEnumerateFiles(tasksDir, "*", recursive: true))
        {
            ctx.Token.ThrowIfCancellationRequested();
            var taskPath = "\\" + Path.GetRelativePath(tasksDir, file);
            if (taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;

            List<string> commands;
            try
            {
                var doc = XDocument.Load(file);
                commands = doc.Descendants()
                    .Where(e => e.Name.LocalName is "Command" or "Arguments" or "WorkingDirectory")
                    .Select(e => e.Value.Trim())
                    .Where(v => v.Length > 0)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }

            if (commands.Any(ctx.ReferencesInstallFolder))
            {
                ctx.Add(new Leftover
                {
                    Kind = LeftoverKind.ScheduledTask,
                    Path = taskPath,
                    Confidence = Confidence.High,
                    Reason = "Scheduled task runs a program from the install folder",
                });
                continue;
            }

            if (ctx.Matcher.MatchProduct(Path.GetFileName(file)) is not null)
            {
                var exe = commands.Count > 0 ? CommandLineParser.Parse(commands[0])?.FileName : null;
                if (exe is not null && Path.IsPathFullyQualified(exe) && !File.Exists(exe))
                {
                    ctx.Add(new Leftover
                    {
                        Kind = LeftoverKind.ScheduledTask,
                        Path = taskPath,
                        Confidence = Confidence.Medium,
                        Reason = "Scheduled task named after the program runs a file that no longer exists",
                    });
                }
            }
        }
    }
}

/// <summary>Run / RunOnce entries and their "startup approved" companions.</summary>
internal static class AutorunProbe
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";

    public static void Run(ScanContext ctx)
    {
        var removedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            foreach (var keyName in new[] { "Run", "RunOnce" })
            {
                var location = new RegistryLocation(hive, view, $@"{CurrentVersion}\{keyName}");
                using var key = location.Open();
                if (key is null) continue;

                foreach (var valueName in RegistryHelpers.ValueNames(key))
                {
                    if (valueName.Length == 0) continue;
                    var data = RegistryHelpers.GetString(key, null, valueName);
                    if (data is null) continue;

                    if (ctx.ReferencesInstallFolder(data))
                    {
                        ctx.AddValue(location, valueName, Confidence.High, "Starts a program from the install folder when Windows starts");
                        removedNames.Add(valueName);
                        continue;
                    }

                    if (ctx.Matcher.MatchProduct(valueName) is not null)
                    {
                        var exe = CommandLineParser.Parse(data)?.FileName;
                        if (exe is not null && Path.IsPathFullyQualified(exe) && !File.Exists(exe))
                        {
                            ctx.AddValue(location, valueName, Confidence.Medium, "Startup entry named after the program runs a file that no longer exists");
                            removedNames.Add(valueName);
                        }
                    }
                }
            }
        }

        // Task Manager's "Startup apps" enabled/disabled state.
        var startupShortcuts = ctx.Results.Items
            .Where(i => i.Kind == LeftoverKind.File && i.Path.Contains(@"\Start Menu\Programs\Startup\", StringComparison.OrdinalIgnoreCase))
            .Select(i => Path.GetFileName(i.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var (approvedKey, names) in new[] { ("Run", removedNames), ("Run32", removedNames), ("StartupFolder", startupShortcuts) })
            {
                if (names.Count == 0) continue;
                var location = new RegistryLocation(hive, RegistryView.Registry64, $@"{CurrentVersion}\Explorer\StartupApproved\{approvedKey}");
                using var key = location.Open();
                if (key is null) continue;
                foreach (var valueName in RegistryHelpers.ValueNames(key).Where(names.Contains))
                    ctx.AddValue(location, valueName, Confidence.High, "Startup setting for a removed startup entry");
            }
        }
    }
}

/// <summary>App Paths entries that let Windows find the program's executable by name.</summary>
internal static class AppPathsProbe
{
    public static void Run(ScanContext ctx)
    {
        if (ctx.TrustedDirectories.Count == 0) return;
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            var root = new RegistryLocation(hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(root))
            {
                if (ctx.ReferencesInstallFolder(RegistryHelpers.GetString(key, null))
                    || ctx.ReferencesInstallFolder(RegistryHelpers.GetString(key, null, "Path")))
                {
                    ctx.AddKey(root.Child(name), Confidence.High, "App Paths registration for the program's executable");
                }
            }
        }
    }
}

/// <summary>Windows Firewall rules for executables in the install folder.</summary>
internal static class FirewallProbe
{
    public const string RulesKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    public static void Run(ScanContext ctx)
    {
        if (ctx.TrustedDirectories.Count == 0) return;
        var location = new RegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, RulesKey);
        using var key = location.Open();
        if (key is null) return;

        var byProgram = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var valueName in RegistryHelpers.ValueNames(key))
        {
            var data = RegistryHelpers.GetString(key, null, valueName);
            var app = data is null ? null : ExtractField(data, "App");
            if (app is null || !ctx.ReferencesInstallFolder(app)) continue;

            var program = Environment.ExpandEnvironmentVariables(app);
            if (!byProgram.TryGetValue(program, out var list)) byProgram[program] = list = [];
            list.Add(valueName);
        }

        foreach (var (program, values) in byProgram)
        {
            ctx.Add(new Leftover
            {
                Kind = LeftoverKind.FirewallRule,
                Path = program,
                Confidence = Confidence.High,
                Reason = $"{values.Count} Windows Firewall rule(s) for a program in the install folder",
                RelatedValueNames = values,
                Key = location,
            });
        }
    }

    /// <summary>Rules look like "v2.30|Action=Allow|Dir=In|App=C:\x\y.exe|Name=...|".</summary>
    internal static string? ExtractField(string rule, string field)
    {
        foreach (var part in rule.Split('|'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals(field, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..];
        }
        return null;
    }
}
