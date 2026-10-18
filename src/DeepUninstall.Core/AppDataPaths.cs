namespace DeepUninstall.Core;

/// <summary>Where DeepUninstall keeps its own data.</summary>
public static class AppDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepUninstall");

    public static string Backups => Path.Combine(Root, "Backups");
}
