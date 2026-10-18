using System.Text;
using DeepUninstall.Core.Models;

namespace DeepUninstall.Core.Discovery;

public static class InstallerDetector
{
    public static InstallerType Detect(string keyName, bool isWindowsInstaller, string? uninstallString, IReadOnlyCollection<string> valueNames)
    {
        var u = uninstallString ?? "";
        bool Has(string value) => valueNames.Contains(value, StringComparer.OrdinalIgnoreCase);

        if (Has("BundleCachePath") || Has("BundleUpgradeCode") || Has("BundleProviderKey"))
            return InstallerType.WixBurn;
        if (isWindowsInstaller || u.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            return InstallerType.Msi;
        if (keyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) || Has("Inno Setup: Setup Version"))
            return InstallerType.InnoSetup;
        if (u.Contains("InstallShield Installation Information", StringComparison.OrdinalIgnoreCase)
            || u.Contains("-runfromtemp", StringComparison.OrdinalIgnoreCase)
            || u.Contains("IsUninst", StringComparison.OrdinalIgnoreCase))
            return InstallerType.InstallShield;
        if (u.Contains("Update.exe", StringComparison.OrdinalIgnoreCase) && u.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
            return InstallerType.Squirrel;

        return InstallerType.Unknown;
    }

    private static readonly byte[] NsisMarker = Encoding.ASCII.GetBytes("Nullsoft");

    /// <summary>NSIS executables embed "Nullsoft" in their manifest near the start of the file.</summary>
    public static bool LooksLikeNsis(string exePath)
    {
        try
        {
            using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(stream.Length, 1024 * 1024)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return buffer.AsSpan(0, read).IndexOf(NsisMarker) >= 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
