using DeepUninstall.Core.Matching;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Safety;

namespace DeepUninstall.Core.Leftovers;

/// <summary>Shared state for one leftover scan.</summary>
internal sealed class ScanContext
{
    private readonly List<string> _trustedAliases;

    public ScanContext(
        InstalledApp app,
        NameMatcher matcher,
        SafetyGuard guard,
        IReadOnlyList<string> trustedDirectories,
        IReadOnlyList<string> otherAppDirectories,
        IProgress<string>? log,
        CancellationToken token)
    {
        App = app;
        Matcher = matcher;
        Guard = guard;
        TrustedDirectories = trustedDirectories;
        OtherAppDirectories = otherAppDirectories;
        Log = log;
        Token = token;

        _trustedAliases = [.. trustedDirectories];
        foreach (var dir in trustedDirectories)
        {
            var shortPath = PathUtil.TryGetShortPath(dir);
            if (shortPath is not null) _trustedAliases.Add(shortPath);
        }
    }

    public InstalledApp App { get; }
    public NameMatcher Matcher { get; }
    public SafetyGuard Guard { get; }

    /// <summary>Folders we are confident belong to the program (its install folder).</summary>
    public IReadOnlyList<string> TrustedDirectories { get; }

    /// <summary>Install folders of every other installed program.</summary>
    public IReadOnlyList<string> OtherAppDirectories { get; }

    public IProgress<string>? Log { get; }
    public CancellationToken Token { get; }
    public LeftoverCollection Results { get; } = new();

    /// <summary>Does this text (command line, path, registry data) point into the install folder?</summary>
    public bool ReferencesInstallFolder(string? text) =>
        !string.IsNullOrEmpty(text) && _trustedAliases.Any(d => PathUtil.ReferencesDirectory(text, d));

    /// <summary>Another program lives in this folder or below it.</summary>
    public bool ContainsOtherApp(string directory) =>
        OtherAppDirectories.Any(o => PathUtil.IsWithinOrEqual(o, directory));

    public void AddDirectory(string path, Confidence confidence, string reason, string? cleanupParent = null)
    {
        if (!PathUtil.TryNormalize(path, out var full)) return;
        if (!Guard.IsDirectoryDeletable(full, out _)) return;
        if (ContainsOtherApp(full)) return;
        Results.Add(new Leftover
        {
            Kind = LeftoverKind.Directory,
            Path = full,
            Confidence = confidence,
            Reason = reason,
            CleanupParentDirectory = cleanupParent,
        });
    }

    public void AddFile(string path, Confidence confidence, string reason, string? cleanupParent = null)
    {
        if (!PathUtil.TryNormalize(path, out var full)) return;
        if (!Guard.IsFileDeletable(full, out _)) return;
        Results.Add(new Leftover
        {
            Kind = LeftoverKind.File,
            Path = full,
            Confidence = confidence,
            Reason = reason,
            CleanupParentDirectory = cleanupParent,
        });
    }

    public void AddKey(RegistryLocation key, Confidence confidence, string reason, RegistryLocation? cleanupParent = null)
    {
        if (!Guard.IsRegistryKeyDeletable(key, out _)) return;
        Results.Add(new Leftover
        {
            Kind = LeftoverKind.RegistryKey,
            Key = key,
            Path = key.ToString(),
            Confidence = confidence,
            Reason = reason,
            CleanupParentKey = cleanupParent,
        });
    }

    public void AddValue(RegistryLocation key, string valueName, Confidence confidence, string reason)
    {
        if (!Guard.IsRegistryValueDeletable(key, valueName, out _)) return;
        Results.Add(new Leftover
        {
            Kind = LeftoverKind.RegistryValue,
            Key = key,
            ValueName = valueName,
            Path = key.ToString(),
            Confidence = confidence,
            Reason = reason,
        });
    }

    public void Add(Leftover leftover) => Results.Add(leftover);
}
