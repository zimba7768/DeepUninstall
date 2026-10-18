using DeepUninstall.Core.Models;
using DeepUninstall.Core.Safety;
using Microsoft.Win32;

namespace DeepUninstall.Core.Tests;

public class SafetyGuardTests
{
    private static readonly SafetyGuard Guard = new(
        protectedDirectories:
        [
            @"C:\Program Files",
            @"C:\Program Files\Common Files",
            @"C:\Users\alice",
            @"C:\Users\alice\AppData\Roaming",
            @"C:\Users\alice\Desktop",
        ],
        blockedTrees: [@"C:\Windows", @"C:\Program Files\WindowsApps"]);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\drivers")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\Common Files")]
    [InlineData(@"C:\Program Files\WindowsApps\Something")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\alice")]
    [InlineData(@"C:\Users\alice\AppData")]
    [InlineData(@"\\server\share\folder")]
    public void Refuses_protected_directories_and_their_ancestors(string path) =>
        Assert.False(Guard.IsDirectoryDeletable(path, out _));

    [Theory]
    [InlineData(@"C:\Program Files\Foo")]
    [InlineData(@"C:\Program Files\Common Files\Foo")]
    [InlineData(@"C:\Users\alice\AppData\Roaming\Foo")]
    public void Allows_program_folders(string path) =>
        Assert.True(Guard.IsDirectoryDeletable(path, out _));

    [Fact]
    public void Allows_a_shortcut_on_a_protected_desktop_but_not_system_files()
    {
        Assert.True(Guard.IsFileDeletable(@"C:\Users\alice\Desktop\Foo.lnk", out _));
        Assert.False(Guard.IsFileDeletable(@"C:\Windows\System32\kernel32.dll", out _));
    }

    private static RegistryLocation Key(string subKey, RegistryHive hive = RegistryHive.LocalMachine) =>
        new(hive, RegistryView.Registry64, subKey);

    [Theory]
    [InlineData("SOFTWARE")]
    [InlineData(@"SOFTWARE\Microsoft")]
    [InlineData(@"SOFTWARE\Classes")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"SOFTWARE\Classes\CLSID")]
    [InlineData(@"SOFTWARE\Classes\.txt")]
    [InlineData(@"SOFTWARE\Classes\txtfile")]
    [InlineData(@"SOFTWARE\Classes\exefile")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Foo")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    public void Refuses_protected_registry_keys(string subKey) =>
        Assert.False(Guard.IsRegistryKeyDeletable(Key(subKey), out _));

    [Theory]
    [InlineData(@"SOFTWARE\Foo")]
    [InlineData(@"SOFTWARE\Acme\Foo")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo")]
    [InlineData(@"SOFTWARE\Classes\CLSID\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"SOFTWARE\Classes\Foo.Document")]
    public void Allows_program_registry_keys(string subKey) =>
        Assert.True(Guard.IsRegistryKeyDeletable(Key(subKey), out _));

    [Fact]
    public void Refuses_other_hives()
    {
        var key = new RegistryLocation(RegistryHive.Users, RegistryView.Registry64, @"S-1-5-21\SOFTWARE\Foo");
        Assert.False(Guard.IsRegistryKeyDeletable(key, out _));
    }

    [Fact]
    public void Allows_firewall_rule_values_but_not_other_system_values()
    {
        var firewall = Key(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
        Assert.True(Guard.IsRegistryValueDeletable(firewall, "{rule-id}", out _));
        Assert.False(Guard.IsRegistryValueDeletable(Key(@"SYSTEM\CurrentControlSet\Control\Session Manager"), "PendingFileRenameOperations", out _));
        Assert.False(Guard.IsRegistryValueDeletable(Key(@"SOFTWARE\Foo"), "", out _));
    }

    [Fact]
    public void Default_guard_protects_real_system_folders()
    {
        var guard = SafetyGuard.CreateDefault();
        Assert.False(guard.IsDirectoryDeletable(Environment.GetFolderPath(Environment.SpecialFolder.Windows), out _));
        Assert.False(guard.IsDirectoryDeletable(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), out _));
        Assert.False(guard.IsDirectoryDeletable(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out _));
        Assert.False(guard.IsDirectoryDeletable(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), out _));
        Assert.False(guard.IsDirectoryDeletable(AppContext.BaseDirectory, out _));
    }
}
