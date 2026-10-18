using DeepUninstall.Core.Models;
using DeepUninstall.Core.Safety;
using Microsoft.Win32;

namespace DeepUninstall.Core.Leftovers;

/// <summary>Collects findings, de-duplicates them and drops items already covered by a parent.</summary>
internal sealed class LeftoverCollection
{
    private readonly Dictionary<string, Leftover> _items = new(StringComparer.Ordinal);

    public void Add(Leftover item)
    {
        if (_items.TryGetValue(item.Identity, out var existing))
        {
            if (item.Confidence > existing.Confidence) _items[item.Identity] = item;
            return;
        }
        _items[item.Identity] = item;
    }

    public IEnumerable<Leftover> Items => _items.Values;

    /// <summary>Is this key (or one of its parents) already marked for removal?</summary>
    public bool CoversKey(RegistryHive hive, string subKey) =>
        _items.Values.Any(i => i.Kind == LeftoverKind.RegistryKey && i.Key is { } k && k.Hive == hive
                               && (IsKeyWithinOrEqual(subKey, k.SubKey) || IsKeyWithinOrEqual(subKey, k.PhysicalSubKey)));

    public IReadOnlyList<Leftover> Build()
    {
        var all = _items.Values.ToList();
        var dirs = all.Where(i => i.Kind == LeftoverKind.Directory).ToList();
        var keys = all.Where(i => i.Kind == LeftoverKind.RegistryKey && i.Key is not null).ToList();

        bool CoveredByDirectory(Leftover item) =>
            item.Kind is LeftoverKind.Directory or LeftoverKind.File
            && dirs.Any(d => !ReferenceEquals(d, item) && d.Confidence >= item.Confidence && PathUtil.IsStrictlyWithin(item.Path, d.Path));

        bool CoveredByKey(Leftover item) =>
            item.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue && item.Key is { } key
            && keys.Any(k => !ReferenceEquals(k, item) && k.Confidence >= item.Confidence && k.Key!.Hive == key.Hive
                             && (item.Kind == LeftoverKind.RegistryValue
                                 ? IsKeyWithinOrEqual(key.PhysicalSubKey, k.Key.PhysicalSubKey)
                                 : IsKeyStrictlyWithin(key.PhysicalSubKey, k.Key.PhysicalSubKey)));

        return all
            .Where(i => !CoveredByDirectory(i) && !CoveredByKey(i))
            .OrderByDescending(i => i.Confidence)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsKeyWithinOrEqual(string key, string ancestor) =>
        key.Equals(ancestor, StringComparison.OrdinalIgnoreCase) || IsKeyStrictlyWithin(key, ancestor);

    private static bool IsKeyStrictlyWithin(string key, string ancestor) =>
        key.StartsWith(ancestor + "\\", StringComparison.OrdinalIgnoreCase);
}
