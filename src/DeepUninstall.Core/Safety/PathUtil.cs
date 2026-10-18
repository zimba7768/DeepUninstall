using System.Text;
using DeepUninstall.Core.Native;

namespace DeepUninstall.Core.Safety;

public static class PathUtil
{
    /// <summary>
    /// Expands environment variables, strips quotes, resolves to a full path, expands 8.3 short
    /// names and removes trailing separators (except for drive roots).
    /// </summary>
    public static string Normalize(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        p = p.Replace('/', '\\');
        p = Path.GetFullPath(p);
        if (p.Contains('~')) p = TryGetLongPath(p);

        var root = Path.GetPathRoot(p) ?? "";
        if (p.Length > root.Length) p = p.TrimEnd('\\');
        return p;
    }

    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalized = Normalize(path);
            return Path.IsPathFullyQualified(normalized);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>True if <paramref name="path"/> equals <paramref name="directory"/> or is inside it.</summary>
    public static bool IsWithinOrEqual(string path, string directory)
    {
        if (path.Equals(directory, StringComparison.OrdinalIgnoreCase)) return true;
        var dir = directory.EndsWith('\\') ? directory : directory + "\\";
        return path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if <paramref name="path"/> is strictly inside <paramref name="directory"/>.</summary>
    public static bool IsStrictlyWithin(string path, string directory) =>
        !path.Equals(directory, StringComparison.OrdinalIgnoreCase) && IsWithinOrEqual(path, directory);

    /// <summary>Number of path segments below the root. C:\ is 0, C:\Foo is 1.</summary>
    public static int Depth(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        var rest = fullPath[root.Length..].Trim('\\');
        return rest.Length == 0 ? 0 : rest.Split('\\').Length;
    }

    /// <summary>
    /// Does free-form text (a command line, a registry value, a shortcut target...) reference a
    /// file or folder inside <paramref name="directory"/>? Requires a path boundary after the match
    /// so that "C:\Apps\Foo" does not match "C:\Apps\Foo Bar\x.exe".
    /// </summary>
    public static bool ReferencesDirectory(string? text, string directory)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(directory)) return false;
        var haystack = text.Contains('%') ? Environment.ExpandEnvironmentVariables(text) : text;
        haystack = haystack.Replace('/', '\\');

        var index = 0;
        while ((index = haystack.IndexOf(directory, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = index + directory.Length;
            if (end == haystack.Length || haystack[end] is '\\' or '"' or ',' or '|' or ';' or '\0')
                return true;
            index = end;
        }
        return false;
    }

    public static string TryGetLongPath(string path)
    {
        var sb = new StringBuilder(1024);
        var n = NativeMethods.GetLongPathName(path, sb, sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }

    public static string? TryGetShortPath(string path)
    {
        var sb = new StringBuilder(1024);
        var n = NativeMethods.GetShortPathName(path, sb, sb.Capacity);
        if (n <= 0 || n >= sb.Capacity) return null;
        var s = sb.ToString().TrimEnd('\\');
        return s.Equals(path, StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    public static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    public static IEnumerable<string> SafeEnumerateFiles(string path, string pattern, bool recursive)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            return Directory.EnumerateFiles(path, pattern, options).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    public static bool IsDirectoryEmpty(string path)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
