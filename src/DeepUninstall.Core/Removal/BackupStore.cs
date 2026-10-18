using System.Diagnostics;
using System.Text;
using DeepUninstall.Core.Models;
using Microsoft.Win32;

namespace DeepUninstall.Core.Removal;

/// <summary>
/// Writes .reg backups (double-click to restore) and a log of what was removed, under
/// %LocalAppData%\DeepUninstall\Backups\&lt;timestamp&gt;-&lt;program&gt;.
/// </summary>
public sealed class BackupStore
{
    private int _counter;

    private BackupStore(string directory) => Directory = directory;

    public string Directory { get; }

    public static BackupStore Create(string? label)
    {
        var safeLabel = string.Concat((label ?? "cleanup").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safeLabel.Length > 60) safeLabel = safeLabel[..60];
        var dir = Path.Combine(AppDataPaths.Backups, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeLabel}");
        System.IO.Directory.CreateDirectory(dir);
        return new BackupStore(dir);
    }

    /// <summary>Exports a key and everything below it with reg.exe.</summary>
    /// <returns>The .reg file, or null if the export failed.</returns>
    public string? ExportKey(RegistryLocation key)
    {
        var file = NextFile(key.Name, ".reg");
        var regPath = $@"{key.HiveName}\{key.PhysicalSubKey}";
        var (exitCode, _) = Run(Path.Combine(Environment.SystemDirectory, "reg.exe"), $"export \"{regPath}\" \"{file}\" /y /reg:64");
        return exitCode == 0 && File.Exists(file) ? file : null;
    }

    /// <summary>Writes a single value to a .reg file.</summary>
    public string? ExportValue(RegistryLocation key, string valueName)
    {
        using var regKey = key.Open();
        if (regKey is null) return null;
        var line = RegFileWriter.FormatValue(regKey, valueName);
        if (line is null) return null;

        var file = NextFile($"{key.Name}-{valueName}", ".reg");
        var sb = new StringBuilder()
            .Append("Windows Registry Editor Version 5.00\r\n\r\n")
            .Append('[').Append(key.HiveName).Append('\\').Append(key.PhysicalSubKey).Append("]\r\n")
            .Append(line).Append("\r\n");
        File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
        return file;
    }

    public string SaveText(string name, string content)
    {
        var file = NextFile(name, ".txt");
        File.WriteAllText(file, content, Encoding.UTF8);
        return file;
    }

    public void WriteLog(IEnumerable<string> lines) =>
        File.WriteAllLines(Path.Combine(Directory, "removal-log.txt"), lines, Encoding.UTF8);

    private string NextFile(string name, string extension)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '{' or '}' ? '_' : c));
        if (safe.Length > 80) safe = safe[..80];
        return Path.Combine(Directory, $"{Interlocked.Increment(ref _counter):000}-{safe}{extension}");
    }

    internal static (int ExitCode, string Output) Run(string fileName, string arguments, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi);
        if (process is null) return (-1, "");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (-1, "timed out");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}

/// <summary>Formats registry values in .reg file syntax.</summary>
public static class RegFileWriter
{
    public static string? FormatValue(RegistryKey key, string valueName)
    {
        RegistryValueKind kind;
        object? raw;
        try
        {
            kind = key.GetValueKind(valueName);
            raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception e) when (e is IOException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
        if (raw is null) return null;
        return FormatValue(valueName, kind, raw);
    }

    public static string FormatValue(string valueName, RegistryValueKind kind, object raw)
    {
        var name = valueName.Length == 0 ? "@" : Quote(valueName);
        return kind switch
        {
            RegistryValueKind.String => $"{name}={Quote((string)raw)}",
            RegistryValueKind.ExpandString => $"{name}=hex(2):{Hex(Encoding.Unicode.GetBytes((string)raw + "\0"))}",
            RegistryValueKind.DWord => $"{name}=dword:{unchecked((uint)Convert.ToInt32(raw)):x8}",
            RegistryValueKind.QWord => $"{name}=hex(b):{Hex(BitConverter.GetBytes(Convert.ToInt64(raw)))}",
            RegistryValueKind.MultiString => $"{name}=hex(7):{Hex(Encoding.Unicode.GetBytes(string.Join("\0", (string[])raw) + "\0\0"))}",
            RegistryValueKind.Binary => $"{name}=hex:{Hex((byte[])raw)}",
            _ => raw is byte[] bytes ? $"{name}=hex:{Hex(bytes)}" : $"{name}={Quote(raw.ToString() ?? "")}",
        };
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Hex(byte[] bytes) => string.Join(",", bytes.Select(b => b.ToString("x2")));
}
