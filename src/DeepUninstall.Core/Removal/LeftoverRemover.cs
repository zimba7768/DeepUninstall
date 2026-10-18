using System.Runtime.InteropServices;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Native;
using DeepUninstall.Core.Safety;
using Microsoft.Win32;

namespace DeepUninstall.Core.Removal;

public sealed record RemovalOptions
{
    /// <summary>Move files and folders to the Recycle Bin instead of deleting them permanently.</summary>
    public bool UseRecycleBin { get; init; } = true;

    /// <summary>Close processes running from folders that are about to be removed.</summary>
    public bool CloseRunningProcesses { get; init; } = true;

    /// <summary>Used to name the backup folder, usually the program name.</summary>
    public string? Label { get; init; }
}

public enum RemovalStatus
{
    Removed,
    ScheduledForReboot,
    AlreadyGone,
    Blocked,
    Failed,
}

public sealed record RemovalEntry(Leftover Item, RemovalStatus Status, string? Message);

public sealed record RemovalReport(IReadOnlyList<RemovalEntry> Entries, string BackupDirectory)
{
    /// <summary>Empty parent folders and keys that were tidied up afterwards.</summary>
    public IReadOnlyList<string> CleanedUpParents { get; init; } = [];

    public DateTime CompletedAt { get; init; } = DateTime.Now;

    public int Removed => Entries.Count(e => e.Status is RemovalStatus.Removed or RemovalStatus.AlreadyGone);
    public int Failed => Entries.Count(e => e.Status is RemovalStatus.Failed or RemovalStatus.Blocked);
    public bool RebootRequired => Entries.Any(e => e.Status == RemovalStatus.ScheduledForReboot);
}

/// <summary>Removes leftovers, safest first, with backups and safety checks on every item.</summary>
public sealed class LeftoverRemover
{
    private readonly SafetyGuard _guard;

    public LeftoverRemover(SafetyGuard? guard = null) => _guard = guard ?? SafetyGuard.CreateDefault();

    public RemovalReport Remove(IEnumerable<Leftover> items, RemovalOptions? options = null, IProgress<string>? log = null)
    {
        options ??= new RemovalOptions();
        var list = items.ToList();
        var backup = BackupStore.Create(options.Label);
        var entries = new List<RemovalEntry>();
        var logLines = new List<string> { $"DeepUninstall removal log, {DateTime.Now:O}", $"Program: {options.Label}", "" };

        if (options.CloseRunningProcesses)
        {
            var dirs = list.Where(i => i.Kind == LeftoverKind.Directory).Select(i => i.Path);
            var running = ProcessFinder.FindIn(dirs);
            if (running.Count > 0) ProcessFinder.Terminate(running, log);
        }

        foreach (var item in list.OrderBy(Order).ThenByDescending(Depth))
        {
            RemovalEntry entry;
            try
            {
                entry = RemoveOne(item, backup, options);
            }
            catch (Exception e)
            {
                entry = new RemovalEntry(item, RemovalStatus.Failed, e.Message);
            }

            entries.Add(entry);
            var line = $"[{entry.Status}] {item.Kind}: {item.DisplayPath}{(entry.Message is null ? "" : "  (" + entry.Message + ")")}";
            logLines.Add(line);
            log?.Report(line);
        }

        var parents = new List<string>();
        foreach (var line in CleanUpEmptyParents(list))
        {
            parents.Add(line);
            logLines.Add(line);
            log?.Report(line);
        }

        try
        {
            backup.WriteLog(logLines);
        }
        catch (IOException)
        {
            // The removal itself succeeded; a missing log is not worth failing over.
        }

        return new RemovalReport(entries, backup.Directory) { CleanedUpParents = parents };
    }

    private static int Order(Leftover item) => item.Kind switch
    {
        LeftoverKind.Service => 0,
        LeftoverKind.ScheduledTask => 1,
        LeftoverKind.FirewallRule => 2,
        LeftoverKind.RegistryValue => 3,
        LeftoverKind.RegistryKey => 4,
        LeftoverKind.File => 5,
        LeftoverKind.Directory => 6,
        _ => 7,
    };

    private static int Depth(Leftover item) => item.Kind switch
    {
        LeftoverKind.RegistryKey => item.Key?.SubKey.Count(c => c == '\\') ?? 0,
        LeftoverKind.File or LeftoverKind.Directory => item.Path.Count(c => c == '\\'),
        _ => 0,
    };

    private RemovalEntry RemoveOne(Leftover item, BackupStore backup, RemovalOptions options) => item.Kind switch
    {
        LeftoverKind.Directory => RemoveDirectory(item, options),
        LeftoverKind.File => RemoveFile(item, options),
        LeftoverKind.RegistryKey => RemoveKey(item, backup),
        LeftoverKind.RegistryValue => RemoveValue(item, backup),
        LeftoverKind.Service => RemoveService(item, backup),
        LeftoverKind.ScheduledTask => RemoveTask(item, backup),
        LeftoverKind.FirewallRule => RemoveFirewallRules(item, backup),
        _ => new RemovalEntry(item, RemovalStatus.Failed, "unsupported item"),
    };

    private RemovalEntry RemoveDirectory(Leftover item, RemovalOptions options)
    {
        if (!_guard.IsDirectoryDeletable(item.Path, out var reason)) return new(item, RemovalStatus.Blocked, reason);
        if (!Directory.Exists(item.Path)) return new(item, RemovalStatus.AlreadyGone, null);

        // A junction or symlink: remove the link only, never what it points to.
        if (PathUtil.IsReparsePoint(item.Path))
        {
            Directory.Delete(item.Path, recursive: false);
            return new(item, RemovalStatus.Removed, "link removed");
        }

        if (options.UseRecycleBin) TryRecycle(item.Path);
        if (Directory.Exists(item.Path))
        {
            try
            {
                Directory.Delete(item.Path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Fall through: some files are locked.
            }
        }

        if (!Directory.Exists(item.Path)) return new(item, RemovalStatus.Removed, null);

        return ScheduleDirectoryForReboot(item.Path)
            ? new(item, RemovalStatus.ScheduledForReboot, "files in use; they will be removed when Windows restarts")
            : new(item, RemovalStatus.Failed, "files are in use or access was denied");
    }

    private RemovalEntry RemoveFile(Leftover item, RemovalOptions options)
    {
        if (!_guard.IsFileDeletable(item.Path, out var reason)) return new(item, RemovalStatus.Blocked, reason);
        if (!File.Exists(item.Path)) return new(item, RemovalStatus.AlreadyGone, null);

        if (options.UseRecycleBin) TryRecycle(item.Path);
        if (File.Exists(item.Path))
        {
            try
            {
                File.SetAttributes(item.Path, FileAttributes.Normal);
                File.Delete(item.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return NativeMethods.MoveFileEx(item.Path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT)
                    ? new(item, RemovalStatus.ScheduledForReboot, "file in use; it will be removed when Windows restarts")
                    : new(item, RemovalStatus.Failed, e.Message);
            }
        }
        return new(item, RemovalStatus.Removed, null);
    }

    private RemovalEntry RemoveKey(Leftover item, BackupStore backup)
    {
        var key = item.Key!;
        if (!_guard.IsRegistryKeyDeletable(key, out var reason)) return new(item, RemovalStatus.Blocked, reason);
        if (!key.Exists()) return new(item, RemovalStatus.AlreadyGone, null);

        var file = backup.ExportKey(key);
        if (file is null) return new(item, RemovalStatus.Failed, "could not back up the key, so it was left alone");

        using var baseKey = RegistryKey.OpenBaseKey(key.Hive, key.View);
        baseKey.DeleteSubKeyTree(key.SubKey, throwOnMissingSubKey: false);
        return new(item, RemovalStatus.Removed, $"backup: {Path.GetFileName(file)}");
    }

    private RemovalEntry RemoveValue(Leftover item, BackupStore backup)
    {
        var key = item.Key!;
        var valueName = item.ValueName ?? "";
        if (!_guard.IsRegistryValueDeletable(key, valueName, out var reason)) return new(item, RemovalStatus.Blocked, reason);

        using var regKey = key.Open(writable: true);
        if (regKey is null || regKey.GetValue(valueName) is null) return new(item, RemovalStatus.AlreadyGone, null);

        var file = backup.ExportValue(key, valueName);
        if (file is null) return new(item, RemovalStatus.Failed, "could not back up the value, so it was left alone");

        regKey.DeleteValue(valueName, throwOnMissingValue: false);
        return new(item, RemovalStatus.Removed, $"backup: {Path.GetFileName(file)}");
    }

    private static RemovalEntry RemoveService(Leftover item, BackupStore backup)
    {
        var name = item.Path;
        var serviceKey = new RegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, $@"SYSTEM\CurrentControlSet\Services\{name}");
        if (!serviceKey.Exists()) return new(item, RemovalStatus.AlreadyGone, null);

        backup.ExportKey(serviceKey);
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        BackupStore.Run(sc, $"stop \"{name}\"");
        for (var i = 0; i < 40; i++)
        {
            var (_, output) = BackupStore.Run(sc, $"query \"{name}\"");
            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) || output.Contains("1060")) break;
            Thread.Sleep(500);
        }

        var (exitCode, deleteOutput) = BackupStore.Run(sc, $"delete \"{name}\"");
        return exitCode switch
        {
            0 => new(item, RemovalStatus.Removed, null),
            1072 => new(item, RemovalStatus.ScheduledForReboot, "marked for deletion; it will be gone after a restart"),
            1060 => new(item, RemovalStatus.AlreadyGone, null),
            _ => new(item, RemovalStatus.Failed, deleteOutput.Trim()),
        };
    }

    private static RemovalEntry RemoveTask(Leftover item, BackupStore backup)
    {
        var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var (queryCode, xml) = BackupStore.Run(schtasks, $"/Query /TN \"{item.Path}\" /XML");
        if (queryCode != 0) return new(item, RemovalStatus.AlreadyGone, null);
        backup.SaveText("task-" + item.Path.Replace('\\', '_'), xml);

        var (exitCode, output) = BackupStore.Run(schtasks, $"/Delete /TN \"{item.Path}\" /F");
        return exitCode == 0 ? new(item, RemovalStatus.Removed, null) : new(item, RemovalStatus.Failed, output.Trim());
    }

    private RemovalEntry RemoveFirewallRules(Leftover item, BackupStore backup)
    {
        var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        foreach (var value in item.RelatedValueNames)
            if (item.Key is not null) backup.ExportValue(item.Key, value);

        BackupStore.Run(netsh, $"advfirewall firewall delete rule name=all program=\"{item.Path}\"");

        // netsh matches the program path literally; remove whatever it did not catch directly.
        if (item.Key is not null)
        {
            using var key = item.Key.Open(writable: true);
            if (key is not null && _guard.IsRegistryValueDeletable(item.Key, "x", out _))
            {
                foreach (var value in item.RelatedValueNames)
                    if (key.GetValue(value) is not null) key.DeleteValue(value, throwOnMissingValue: false);
            }
        }
        return new(item, RemovalStatus.Removed, null);
    }

    private IEnumerable<string> CleanUpEmptyParents(List<Leftover> items)
    {
        foreach (var dir in items.Select(i => i.CleanupParentDirectory).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir) || !PathUtil.IsDirectoryEmpty(dir) || !_guard.IsDirectoryDeletable(dir, out _)) continue;
            string? line;
            try
            {
                Directory.Delete(dir);
                line = $"[Removed] Empty folder: {dir}";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                line = null;
            }
            if (line is not null) yield return line;
        }

        foreach (var key in items.Select(i => i.CleanupParentKey).OfType<RegistryLocation>().Distinct())
        {
            if (!_guard.IsRegistryKeyDeletable(key, out _)) continue;
            bool empty;
            using (var regKey = key.Open())
            {
                empty = regKey is not null && regKey.SubKeyCount == 0 && regKey.ValueCount == 0;
            }
            if (!empty) continue;
            using var baseKey = RegistryKey.OpenBaseKey(key.Hive, key.View);
            baseKey.DeleteSubKey(key.SubKey, throwOnMissingSubKey: false);
            yield return $"[Removed] Empty registry key: {key}";
        }
    }

    private static void TryRecycle(string path)
    {
        if (!Environment.Is64BitProcess) return; // SHFILEOPSTRUCT layout below is for 64-bit only.
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc = NativeMethods.FO_DELETE,
            pFrom = path + '\0',
            fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_NOERRORUI | NativeMethods.FOF_SILENT,
        };
        try
        {
            NativeMethods.SHFileOperation(ref op);
        }
        catch (Exception e) when (e is COMException or SEHException)
        {
            // Fall back to a normal delete.
        }
    }

    private static bool ScheduleDirectoryForReboot(string dir)
    {
        // Never walk into junctions or symlinks: their targets are not ours to delete.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var any = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", options))
                any |= NativeMethods.MoveFileEx(file, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options).OrderByDescending(d => d.Length))
                NativeMethods.MoveFileEx(sub, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
            any |= NativeMethods.MoveFileEx(dir, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Partial scheduling is still better than nothing.
        }
        return any;
    }
}
