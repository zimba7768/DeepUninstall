namespace DeepUninstall.Core.Models;

public enum LeftoverKind
{
    Directory,
    File,
    RegistryKey,
    RegistryValue,
    Service,
    ScheduledTask,
    FirewallRule,
}

/// <summary>How sure we are that an item belongs to the removed program.</summary>
public enum Confidence
{
    /// <summary>Weak evidence. Never selected by default.</summary>
    Low = 0,

    /// <summary>Name-based match on a short or ambiguous name. Not selected by default.</summary>
    Medium = 1,

    /// <summary>Strong evidence: points into the install folder, or a distinctive name match. Selected by default.</summary>
    High = 2,
}

/// <summary>Something a program left behind after it was uninstalled.</summary>
public sealed record Leftover
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>
    /// File system path for files and folders, service name for services, task path for scheduled
    /// tasks, program path for firewall rules. For registry items, use <see cref="Key"/>.
    /// </summary>
    public string Path { get; init; } = "";

    public required Confidence Confidence { get; init; }

    /// <summary>Human readable explanation of why this item was flagged.</summary>
    public required string Reason { get; init; }

    public RegistryLocation? Key { get; init; }
    public string? ValueName { get; init; }

    /// <summary>Registry value names that back a firewall rule leftover.</summary>
    public IReadOnlyList<string> RelatedValueNames { get; init; } = [];

    /// <summary>Folder to remove afterwards if it ends up empty (e.g. a publisher folder).</summary>
    public string? CleanupParentDirectory { get; init; }

    /// <summary>Registry key to remove afterwards if it ends up empty (e.g. a publisher key).</summary>
    public RegistryLocation? CleanupParentKey { get; init; }

    public string DisplayPath => Kind switch
    {
        LeftoverKind.RegistryKey => Key?.ToString() ?? Path,
        LeftoverKind.RegistryValue => $"{Key}  ->  {(string.IsNullOrEmpty(ValueName) ? "(Default)" : ValueName)}",
        _ => Path,
    };

    /// <summary>Stable key used to de-duplicate findings.</summary>
    public string Identity => $"{Kind}|{DisplayPath}".ToUpperInvariant();

    public bool IsRecommended => Confidence == Confidence.High;
}
