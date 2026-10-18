using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Uninstall;

namespace DeepUninstall.App.Services;

/// <summary>Extracts program icons from DisplayIcon, falling back to the uninstaller's icon.</summary>
internal static class IconLoader
{
    public static ImageSource? Load(InstalledApp app)
    {
        foreach (var (path, index) in Candidates(app))
        {
            var icon = Extract(path, index);
            if (icon is not null) return icon;
        }
        return null;
    }

    private static IEnumerable<(string Path, int Index)> Candidates(InstalledApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.DisplayIcon))
        {
            var s = Environment.ExpandEnvironmentVariables(app.DisplayIcon.Trim());
            var index = 0;
            var comma = s.LastIndexOf(',');
            if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out var parsed))
            {
                index = parsed;
                s = s[..comma];
            }
            yield return (s.Trim().Trim('"'), index);
        }

        var uninstaller = CommandLineParser.Parse(app.UninstallString)?.FileName;
        if (uninstaller is not null && !uninstaller.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            yield return (uninstaller, 0);
    }

    private static ImageSource? Extract(string path, int index)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;

        var large = new IntPtr[1];
        try
        {
            if (ExtractIconEx(path, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception e) when (e is COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
