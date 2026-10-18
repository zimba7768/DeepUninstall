using DeepUninstall.Core.Removal;

namespace DeepUninstall.Core.Safety;

public sealed record RestorePointResult(bool Created, string Message);

/// <summary>Creates a System Restore point before anything is changed.</summary>
public static class RestorePoint
{
    public static Task<RestorePointResult> CreateAsync(string description) => Task.Run(() => Create(description));

    private static RestorePointResult Create(string description)
    {
        var safe = description.Replace("\"", "").Replace("'", "''");
        if (safe.Length > 200) safe = safe[..200];

        // Checkpoint-Computer is only available in Windows PowerShell 5.1, which ships with Windows.
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = $"$WarningPreference='Continue'; Checkpoint-Computer -Description '{safe}' -RestorePointType APPLICATION_UNINSTALL -ErrorAction Stop 3>&1";
        var (exitCode, output) = BackupStore.Run(powershell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"", timeoutMs: 180_000);
        output = output.Trim();

        if (exitCode != 0)
            return new(false, string.IsNullOrEmpty(output) ? "System Restore is unavailable or disabled." : FirstLine(output));
        if (output.Contains("1440", StringComparison.Ordinal) || output.Contains("cannot be created", StringComparison.OrdinalIgnoreCase))
            return new(false, "Windows only allows one restore point per day, and one already exists from the last 24 hours.");
        return new(true, "Restore point created.");
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? s;
        return line.Length > 300 ? line[..300] : line;
    }
}
