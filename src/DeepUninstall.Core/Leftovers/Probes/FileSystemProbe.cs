using DeepUninstall.Core.Models;
using DeepUninstall.Core.Safety;

namespace DeepUninstall.Core.Leftovers.Probes;

internal static class UninstallEntryProbe
{
    public static void Run(ScanContext ctx)
    {
        if (ctx.App.UninstallKey.Exists())
            ctx.AddKey(ctx.App.UninstallKey, Confidence.High, "The program's entry in Installed apps");
    }
}

/// <summary>The install folder, plus folders named after the program in the usual data locations.</summary>
internal static class FileSystemProbe
{
    public static void Run(ScanContext ctx)
    {
        foreach (var dir in ctx.TrustedDirectories)
        {
            if (Directory.Exists(dir)) ctx.AddDirectory(dir, Confidence.High, "Install folder");
        }

        foreach (var (root, label) in DataRoots())
        {
            ctx.Token.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;

            foreach (var sub in PathUtil.SafeEnumerateDirectories(root))
            {
                var name = Path.GetFileName(sub);
                if (ctx.Matcher.MatchProduct(name) is { } confidence)
                {
                    ctx.AddDirectory(sub, confidence, $"Folder named after the program in {label}");
                    continue;
                }

                if (!ctx.Matcher.MatchPublisher(name)) continue;

                foreach (var child in PathUtil.SafeEnumerateDirectories(sub))
                {
                    if (ctx.Matcher.MatchProductInPublisherContainer(Path.GetFileName(child)) is { } childConfidence)
                        ctx.AddDirectory(child, childConfidence, $"Program folder inside the publisher's folder in {label}", cleanupParent: sub);
                }

                if (PathUtil.IsDirectoryEmpty(sub))
                    ctx.AddDirectory(sub, Confidence.Low, $"Empty publisher folder in {label}");
            }
        }
    }

    internal static IEnumerable<(string Path, string Label)> DataRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<(string, string)>();
        void Add(string? path, string label)
        {
            if (!string.IsNullOrEmpty(path) && seen.Add(path)) roots.Add((path, label));
        }

        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);

        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var profile = F(Environment.SpecialFolder.UserProfile);

        Add(F(Environment.SpecialFolder.ProgramFiles), "Program Files");
        Add(F(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)");
        Add(F(Environment.SpecialFolder.CommonProgramFiles), "Common Files");
        Add(F(Environment.SpecialFolder.CommonProgramFilesX86), "Common Files (x86)");
        Add(F(Environment.SpecialFolder.CommonApplicationData), "ProgramData");
        Add(F(Environment.SpecialFolder.ApplicationData), @"AppData\Roaming");
        Add(local, @"AppData\Local");
        if (!string.IsNullOrEmpty(local))
        {
            Add(Path.Combine(local, "Programs"), @"AppData\Local\Programs");
            Add(Path.Combine(local, "VirtualStore", "Program Files"), "VirtualStore");
            Add(Path.Combine(local, "VirtualStore", "Program Files (x86)"), "VirtualStore");
            Add(Path.Combine(local, "VirtualStore", "ProgramData"), "VirtualStore");
        }
        if (!string.IsNullOrEmpty(profile))
            Add(Path.Combine(profile, "AppData", "LocalLow"), @"AppData\LocalLow");

        return roots;
    }
}

/// <summary>HKCU\SOFTWARE\&lt;Program&gt;, HKLM\SOFTWARE\&lt;Publisher&gt;\&lt;Program&gt; and friends.</summary>
internal static class SoftwareKeyProbe
{
    public static void Run(ScanContext ctx)
    {
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            var software = new RegistryLocation(hive, view, "SOFTWARE");
            using var root = software.Open();
            if (root is null) continue;

            foreach (var name in RegistryHelpers.SubKeyNames(root))
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (ctx.Matcher.MatchProduct(name) is { } confidence)
                {
                    ctx.AddKey(software.Child(name), confidence, "Registry settings named after the program");
                    continue;
                }

                if (!ctx.Matcher.MatchPublisher(name)) continue;

                using var publisherKey = RegistryHelpers.OpenSubKey(root, name);
                if (publisherKey is null) continue;
                var publisherLocation = software.Child(name);

                foreach (var child in RegistryHelpers.SubKeyNames(publisherKey))
                {
                    if (ctx.Matcher.MatchProductInPublisherContainer(child) is { } childConfidence)
                        ctx.AddKey(publisherLocation.Child(child), childConfidence, "Program settings inside the publisher's registry key", cleanupParent: publisherLocation);
                }

                if (RegistryHelpers.IsEmpty(publisherKey))
                    ctx.AddKey(publisherLocation, Confidence.Low, "Empty publisher registry key");
            }
        }
    }
}
