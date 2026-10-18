using DeepUninstall.Core.Models;
using DeepUninstall.Core.Native;
using DeepUninstall.Core.Safety;

namespace DeepUninstall.Core.Leftovers.Probes;

/// <summary>Start Menu, Desktop and taskbar shortcuts that point into the install folder or are broken.</summary>
internal static class ShortcutProbe
{
    public static void Run(ScanContext ctx)
    {
        foreach (var (dir, recursive, label) in Locations())
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var lnk in PathUtil.SafeEnumerateFiles(dir, "*.lnk", recursive))
            {
                ctx.Token.ThrowIfCancellationRequested();
                var info = ShellLink.Read(lnk);
                var parent = Path.GetDirectoryName(lnk);
                var cleanup = parent is not null && !parent.Equals(dir, StringComparison.OrdinalIgnoreCase) ? parent : null;

                var pointsIntoInstallFolder = info is not null
                    && (ctx.ReferencesInstallFolder(info.TargetPath)
                        || (info.TargetPath is null && ctx.ReferencesInstallFolder(info.IconLocation)));
                if (pointsIntoInstallFolder)
                {
                    ctx.AddFile(lnk, Confidence.High, $"{label} shortcut into the install folder", cleanup);
                    continue;
                }

                if (ctx.Matcher.MatchProduct(Path.GetFileNameWithoutExtension(lnk)) is not { } nameMatch) continue;

                if (info?.TargetPath is { } target && Path.IsPathFullyQualified(target))
                {
                    if (!File.Exists(target) && !Directory.Exists(target))
                        ctx.AddFile(lnk, nameMatch, $"Broken {label} shortcut named after the program", cleanup);
                }
                else
                {
                    // Advertised (MSI) shortcut or unreadable link: no target to verify.
                    ctx.AddFile(lnk, Confidence.Medium, $"{label} shortcut named after the program", cleanup);
                }
            }

            if (!recursive) continue;
            foreach (var sub in PathUtil.SafeEnumerateDirectories(dir))
            {
                if (ctx.Matcher.MatchProduct(Path.GetFileName(sub)) is { } confidence)
                    ctx.AddDirectory(sub, confidence, $"{label} folder named after the program");
            }
        }
    }

    private static IEnumerable<(string Dir, bool Recursive, string Label)> Locations()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);

        yield return (F(Environment.SpecialFolder.Programs), true, "Start Menu");
        yield return (F(Environment.SpecialFolder.CommonPrograms), true, "Start Menu");
        yield return (F(Environment.SpecialFolder.DesktopDirectory), false, "Desktop");
        yield return (F(Environment.SpecialFolder.CommonDesktopDirectory), false, "Desktop");
        var appData = F(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            yield return (Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch"), true, "Taskbar");
    }
}
