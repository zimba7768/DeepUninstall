using DeepUninstall.Core.Uninstall;

namespace DeepUninstall.Core.RegistryCleaning;

/// <summary>
/// Decides whether a registry entry points at a file that is definitely gone.
/// </summary>
/// <remarks>
/// "Definitely" is the important word. When in doubt the answer is false (not missing):
/// relative names (found through the search path), network paths, removable or unready drives
/// (an unplugged USB stick is not a missing file), unexpanded variables, and commands that run
/// through a Windows host program such as rundll32 or msiexec are never judged.
/// </remarks>
public sealed class MissingFileChecker
{
    private static readonly HashSet<string> HostPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "msiexec.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
        "explorer.exe", "regsvr32.exe", "conhost.exe", "svchost.exe", "dllhost.exe", "mshta.exe", "javaw.exe",
        "java.exe", "py.exe", "pyw.exe", "python.exe", "pythonw.exe", "node.exe", "wt.exe",
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".cpl", ".msc", ".lnk",
    };

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _isCheckableRoot;
    private readonly string _windows;
    private readonly Dictionary<string, bool> _rootCache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="fileExists">Defaults to File.Exists.</param>
    /// <param name="directoryExists">Defaults to Directory.Exists.</param>
    /// <param name="isCheckableRoot">
    /// True for drive roots whose contents can be trusted (fixed and ready). Defaults to a DriveInfo check.
    /// </param>
    public MissingFileChecker(Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null, Func<string, bool>? isCheckableRoot = null)
    {
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
        _isCheckableRoot = isCheckableRoot ?? IsFixedReadyDrive;
        _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    /// <summary>For values that hold a plain path (App Paths, SharedDLLs, InprocServer32...).</summary>
    public bool IsPathMissing(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return false;
        var path = rawPath.Trim().Trim('"').Trim();
        var comma = path.IndexOf(',');
        if (comma > 2) path = path[..comma].Trim(); // "C:\x\y.dll,0" style icon references
        return AllCandidatesMissing(path);
    }

    /// <summary>For values that hold a command line (Run entries, shell verbs, LocalServer32...).</summary>
    public bool IsCommandTargetMissing(string? rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand)) return false;

        // Parse without touching the disk so the result doesn't depend on what exists.
        var command = CommandLineParser.Parse(ExpandForParsing(rawCommand), _ => false);
        if (command is null) return false;

        var fileName = Path.GetFileName(command.FileName);
        if (HostPrograms.Contains(fileName)) return false;
        if (!ExecutableExtensions.Contains(Path.GetExtension(command.FileName))) return false;

        // Re-derive the raw (unexpanded) executable so the WOW64 candidates below see the variables.
        var raw = rawCommand.Trim();
        string rawExe;
        if (raw.StartsWith('"'))
        {
            var end = raw.IndexOf('"', 1);
            rawExe = end > 0 ? raw[1..end] : raw[1..];
        }
        else
        {
            var exeIndex = raw.IndexOf(Path.GetExtension(command.FileName), StringComparison.OrdinalIgnoreCase);
            rawExe = exeIndex > 0 ? raw[..(exeIndex + Path.GetExtension(command.FileName).Length)] : command.FileName;
        }

        return AllCandidatesMissing(rawExe);
    }

    /// <summary>True only if every way of reading the path points at a missing file on a trusted drive.</summary>
    private bool AllCandidatesMissing(string rawPath)
    {
        if (rawPath.StartsWith(@"\??\", StringComparison.Ordinal)) rawPath = rawPath[4..];

        var any = false;
        foreach (var candidate in Candidates(rawPath))
        {
            if (candidate.Contains('%') || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || candidate.Contains('*') || candidate.Contains('?'))
                return false;
            if (candidate.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            if (!Path.IsPathFullyQualified(candidate)) return false;

            var root = Path.GetPathRoot(candidate);
            if (string.IsNullOrEmpty(root) || !IsCheckable(root)) return false;
            if (candidate.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false; // app execution aliases
            if (_fileExists(candidate) || _directoryExists(candidate)) return false;
            any = true;
        }
        return any;
    }

    private IEnumerable<string> Candidates(string raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> Expansions()
        {
            yield return Environment.ExpandEnvironmentVariables(raw);
            // A 32-bit program that wrote %ProgramFiles% meant "Program Files (x86)".
            var x86 = raw
                .Replace("%ProgramFiles%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase)
                .Replace("%CommonProgramFiles%", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), StringComparison.OrdinalIgnoreCase);
            yield return Environment.ExpandEnvironmentVariables(x86);
        }

        foreach (var expanded in Expansions())
        {
            var path = expanded.Trim().Trim('"');
            if (seen.Add(path)) yield return path;

            // A 32-bit view entry pointing at System32 really means SysWOW64, and vice versa.
            foreach (var (from, to) in new[] { ("System32", "SysWOW64"), ("SysWOW64", "System32"), ("Sysnative", "System32") })
            {
                var prefix = Path.Combine(_windows, from) + "\\";
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var alt = Path.Combine(_windows, to) + "\\" + path[prefix.Length..];
                    if (seen.Add(alt)) yield return alt;
                }
            }
        }
    }

    private static string ExpandForParsing(string raw) => Environment.ExpandEnvironmentVariables(raw);

    private bool IsCheckable(string root)
    {
        if (!_rootCache.TryGetValue(root, out var ok))
        {
            ok = _isCheckableRoot(root);
            _rootCache[root] = ok;
        }
        return ok;
    }

    private static bool IsFixedReadyDrive(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && drive.IsReady;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
