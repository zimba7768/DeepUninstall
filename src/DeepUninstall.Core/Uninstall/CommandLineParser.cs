namespace DeepUninstall.Core.Uninstall;

/// <summary>An executable and its arguments.</summary>
public sealed record ParsedCommand(string FileName, string Arguments)
{
    public string ToCommandLine() =>
        string.IsNullOrWhiteSpace(Arguments) ? $"\"{FileName}\"" : $"\"{FileName}\" {Arguments}";

    public override string ToString() => ToCommandLine();
}

/// <summary>
/// Splits the messy command lines found in the registry ("UninstallString", "ImagePath", Run
/// values...) into an executable and arguments. Handles quoted paths, unquoted paths with spaces,
/// environment variables and bare executable names like "MsiExec.exe".
/// </summary>
public static class CommandLineParser
{
    public static ParsedCommand? Parse(string? commandLine, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        fileExists ??= File.Exists;

        var s = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];

        string exe;
        string args;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end < 0)
            {
                exe = s[1..];
                args = "";
            }
            else
            {
                exe = s[1..end];
                args = s[(end + 1)..].Trim();
            }
        }
        else
        {
            (exe, args) = SplitUnquoted(s, fileExists);
        }

        exe = exe.Trim();
        if (exe.Length == 0) return null;
        return new ParsedCommand(ResolveExecutable(exe, fileExists), args);
    }

    private static (string Exe, string Args) SplitUnquoted(string s, Func<string, bool> fileExists)
    {
        // An unquoted path may contain spaces: "C:\Program Files\Foo\uninst.exe /S".
        // Try every ".exe" boundary and prefer the first one that exists on disk.
        var firstEnd = -1;
        var index = 0;
        while ((index = s.IndexOf(".exe", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = index + 4;
            if (end == s.Length || char.IsWhiteSpace(s[end]) || s[end] is '/' or '"')
            {
                var candidate = s[..end];
                if (Path.IsPathRooted(candidate) && fileExists(candidate))
                    return (candidate, s[end..].Trim());
                if (firstEnd < 0) firstEnd = end;
            }
            index = end;
        }

        if (firstEnd >= 0) return (s[..firstEnd], s[firstEnd..].Trim());

        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }

    /// <summary>Turns "msiexec" or "RunDll32" into a full path under System32 when possible.</summary>
    private static string ResolveExecutable(string exe, Func<string, bool> fileExists)
    {
        if (Path.IsPathRooted(exe)) return exe;

        var names = Path.HasExtension(exe) ? [exe] : new[] { exe + ".exe", exe };
        var searchDirs = new List<string>
        {
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is not null) searchDirs.AddRange(pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var dir in searchDirs)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (fileExists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry.
                }
            }
        }

        return names[0];
    }
}
