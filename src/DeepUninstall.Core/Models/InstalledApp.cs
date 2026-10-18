using Microsoft.Win32;

namespace DeepUninstall.Core.Models;

/// <summary>
/// A program registered under one of the Windows "Uninstall" registry keys.
/// </summary>
public sealed record InstalledApp
{
    public required string DisplayName { get; init; }
    public string? DisplayVersion { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }
    public DateTime? InstallDate { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public bool IsWindowsInstaller { get; init; }
    public string? MsiProductCode { get; init; }
    public InstallerType InstallerType { get; init; }

    /// <summary>The program's entry under ...\CurrentVersion\Uninstall.</summary>
    public required RegistryLocation UninstallKey { get; init; }

    /// <summary>True when the uninstall command points at an executable that no longer exists.</summary>
    public bool UninstallerMissing { get; init; }

    public string KeyName => UninstallKey.Name;
    public bool IsPerUser => UninstallKey.Hive == RegistryHive.CurrentUser;
    public bool Is32Bit => UninstallKey.View == RegistryView.Registry32;
    public string Id => UninstallKey.ToString();

    public override string ToString() =>
        string.IsNullOrEmpty(DisplayVersion) || DisplayName.Contains(DisplayVersion, StringComparison.OrdinalIgnoreCase)
            ? DisplayName
            : $"{DisplayName} {DisplayVersion}";
}
