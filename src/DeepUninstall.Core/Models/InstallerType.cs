namespace DeepUninstall.Core.Models;

/// <summary>The setup technology a program was installed with, as far as we can tell.</summary>
public enum InstallerType
{
    Unknown,
    Msi,
    InnoSetup,
    Nsis,
    InstallShield,
    WixBurn,
    Squirrel,
}
