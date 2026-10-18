using System.Security;
using DeepUninstall.Core.Models;
using Microsoft.Win32;

namespace DeepUninstall.Core.Leftovers.Probes;

internal static class RegistryHelpers
{
    /// <summary>The (hive, view) pairs that hold machine-wide 64-bit, machine-wide 32-bit and per-user settings.</summary>
    public static IEnumerable<(RegistryHive Hive, RegistryView View)> SoftwareViews()
    {
        yield return (RegistryHive.CurrentUser, RegistryView.Default);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
        if (Environment.Is64BitOperatingSystem)
            yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
    }

    public static string[] SubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception e) when (IsAccessError(e))
        {
            return [];
        }
    }

    public static string[] ValueNames(RegistryKey key)
    {
        try
        {
            return key.GetValueNames();
        }
        catch (Exception e) when (IsAccessError(e))
        {
            return [];
        }
    }

    public static RegistryKey? OpenSubKey(RegistryKey key, string name)
    {
        try
        {
            return key.OpenSubKey(name);
        }
        catch (Exception e) when (IsAccessError(e))
        {
            return null;
        }
    }

    /// <summary>Reads a string value, optionally from a sub key. Use null or "" for the default value.</summary>
    public static string? GetString(RegistryKey key, string? subKey, string? valueName = null)
    {
        try
        {
            if (string.IsNullOrEmpty(subKey)) return key.GetValue(valueName) as string;
            using var sub = key.OpenSubKey(subKey);
            return sub?.GetValue(valueName) as string;
        }
        catch (Exception e) when (IsAccessError(e))
        {
            return null;
        }
    }

    public static IEnumerable<(string Name, RegistryKey Key)> EnumerateSubKeys(RegistryLocation location)
    {
        using var root = location.Open();
        if (root is null) yield break;
        foreach (var name in SubKeyNames(root))
        {
            var sub = OpenSubKey(root, name);
            if (sub is null) continue;
            using (sub)
            {
                yield return (name, sub);
            }
        }
    }

    public static bool IsEmpty(RegistryKey key)
    {
        try
        {
            if (key.SubKeyCount > 0) return false;
            var values = key.GetValueNames();
            return values.Length == 0 || (values.Length == 1 && values[0].Length == 0 && key.GetValue(null) is null or "");
        }
        catch (Exception e) when (IsAccessError(e))
        {
            return false;
        }
    }

    private static bool IsAccessError(Exception e) =>
        e is SecurityException or UnauthorizedAccessException or IOException;
}
