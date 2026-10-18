using System.Security;
using Microsoft.Win32;

namespace DeepUninstall.Core.Models;

/// <summary>
/// A registry key addressed by hive, view (32/64-bit) and logical sub key path.
/// </summary>
public sealed record RegistryLocation(RegistryHive Hive, RegistryView View, string SubKey)
{
    public string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        _ => Hive.ToString(),
    };

    public string ShortHiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        _ => Hive.ToString(),
    };

    /// <summary>Last segment of the key path.</summary>
    public string Name
    {
        get
        {
            var i = SubKey.LastIndexOf('\\');
            return i < 0 ? SubKey : SubKey[(i + 1)..];
        }
    }

    public RegistryLocation? Parent
    {
        get
        {
            var i = SubKey.LastIndexOf('\\');
            return i < 0 ? null : this with { SubKey = SubKey[..i] };
        }
    }

    /// <summary>
    /// The path where the key physically lives. A 32-bit view of HKLM\SOFTWARE is redirected
    /// to WOW6432Node; this matters for .reg backups, which are always imported through the 64-bit view.
    /// </summary>
    public string PhysicalSubKey
    {
        get
        {
            if (Hive != RegistryHive.LocalMachine || View != RegistryView.Registry32 || !Environment.Is64BitOperatingSystem)
                return SubKey;

            const string classesClsid = @"SOFTWARE\Classes\CLSID";
            if (SubKey.StartsWith(classesClsid, StringComparison.OrdinalIgnoreCase))
                return @"SOFTWARE\Classes\WOW6432Node\CLSID" + SubKey[classesClsid.Length..];

            if (SubKey.StartsWith(@"SOFTWARE\Classes", StringComparison.OrdinalIgnoreCase))
                return SubKey;

            if (SubKey.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase)
                && !SubKey.StartsWith(@"SOFTWARE\WOW6432Node", StringComparison.OrdinalIgnoreCase))
                return @"SOFTWARE\WOW6432Node\" + SubKey[@"SOFTWARE\".Length..];

            return SubKey;
        }
    }

    /// <summary>Full physical path, e.g. HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Foo.</summary>
    public string FullPhysicalPath => $@"{HiveName}\{PhysicalSubKey}";

    public RegistryLocation Child(string name) => this with { SubKey = $@"{SubKey}\{name}" };

    public RegistryKey? Open(bool writable = false)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(Hive, View);
            return baseKey.OpenSubKey(SubKey, writable);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool Exists()
    {
        using var key = Open();
        return key is not null;
    }

    public override string ToString() => $@"{ShortHiveName}\{PhysicalSubKey}";
}
