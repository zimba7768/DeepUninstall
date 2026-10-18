using DeepUninstall.Core.Discovery;
using DeepUninstall.Core.Leftovers.Probes;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Safety;
using DeepUninstall.Core.Uninstall;
using Microsoft.Win32;

namespace DeepUninstall.Core.Tests;

public class UninstallCommandBuilderTests
{
    private static InstalledApp App(InstallerType type, string? uninstall, string? quiet = null, string? productCode = null) => new()
    {
        DisplayName = "Test App",
        InstallerType = type,
        UninstallString = uninstall,
        QuietUninstallString = quiet,
        MsiProductCode = productCode,
        IsWindowsInstaller = type == InstallerType.Msi,
        UninstallKey = new RegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Test"),
    };

    private const string Code = "{11111111-2222-3333-4444-555555555555}";

    [Fact]
    public void Msi_uses_remove_not_repair()
    {
        var app = App(InstallerType.Msi, $"MsiExec.exe /I{Code}", productCode: Code);
        var cmd = UninstallCommandBuilder.Build(app, silent: false)!;
        Assert.EndsWith("msiexec.exe", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"/X{Code}", cmd.Arguments);
    }

    [Fact]
    public void Msi_silent_adds_qn()
    {
        var app = App(InstallerType.Msi, $"MsiExec.exe /I{Code}", productCode: Code);
        Assert.Equal($"/X{Code} /qn /norestart", UninstallCommandBuilder.Build(app, silent: true)!.Arguments);
    }

    [Fact]
    public void Inno_silent_flags()
    {
        var app = App(InstallerType.InnoSetup, "\"C:\\Apps\\Foo\\unins000.exe\"");
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", UninstallCommandBuilder.Build(app, silent: true)!.Arguments);
    }

    [Fact]
    public void Burn_does_not_duplicate_existing_flags()
    {
        var app = App(InstallerType.WixBurn, "\"C:\\ProgramData\\Package Cache\\{x}\\setup.exe\" /uninstall");
        Assert.Equal("/uninstall /quiet /norestart", UninstallCommandBuilder.Build(app, silent: true)!.Arguments);
    }

    [Fact]
    public void Quiet_uninstall_string_wins()
    {
        var app = App(InstallerType.Unknown, "\"C:\\Apps\\Foo\\uninst.exe\"", quiet: "\"C:\\Apps\\Foo\\uninst.exe\" --quiet");
        Assert.Equal("--quiet", UninstallCommandBuilder.Build(app, silent: true)!.Arguments);
    }

    [Fact]
    public void Unknown_installer_has_no_silent_mode()
    {
        var app = App(InstallerType.Unknown, "\"C:\\Apps\\Foo\\uninst.exe\"");
        Assert.Null(UninstallCommandBuilder.Build(app, silent: true, isNsis: _ => false));
    }

    [Fact]
    public void Nsis_is_detected_lazily()
    {
        var app = App(InstallerType.Unknown, "\"C:\\Apps\\Foo\\uninst.exe\"");
        Assert.Equal("/S", UninstallCommandBuilder.Build(app, silent: true, isNsis: _ => true)!.Arguments);
    }

    [Theory]
    [InlineData("Foo_is1", false, null, InstallerType.InnoSetup)]
    [InlineData("{X}", true, null, InstallerType.Msi)]
    [InlineData("Foo", false, "\"C:\\Users\\a\\AppData\\Local\\Foo\\Update.exe\" --uninstall", InstallerType.Squirrel)]
    [InlineData("Foo", false, "\"C:\\Program Files (x86)\\InstallShield Installation Information\\{X}\\setup.exe\" -runfromtemp", InstallerType.InstallShield)]
    public void Detects_installer_type(string keyName, bool msi, string? uninstall, InstallerType expected) =>
        Assert.Equal(expected, InstallerDetector.Detect(keyName, msi, uninstall, []));

    [Fact]
    public void Product_code_from_key_name_or_command()
    {
        Assert.Equal(Code, InstalledAppScanner.FindProductCode(Code, null));
        Assert.Equal(Code, InstalledAppScanner.FindProductCode("Foo", $"MsiExec.exe /X{Code.ToLowerInvariant()}"));
    }

    [Theory]
    [InlineData(0, UninstallOutcome.Succeeded)]
    [InlineData(3010, UninstallOutcome.SucceededRebootRequired)]
    [InlineData(1605, UninstallOutcome.NotInstalled)]
    [InlineData(1602, UninstallOutcome.Cancelled)]
    [InlineData(1, UninstallOutcome.Failed)]
    public void Interprets_exit_codes(int code, UninstallOutcome expected) =>
        Assert.Equal(expected, UninstallRunner.Interpret(code).Outcome);
}

public class PathUtilTests
{
    [Theory]
    [InlineData("\"C:\\Apps\\Foo\\foo.exe\" --run", @"C:\Apps\Foo", true)]
    [InlineData(@"C:\Apps\Foo", @"C:\Apps\Foo", true)]
    [InlineData(@"C:\Apps\Foo Bar\foo.exe", @"C:\Apps\Foo", false)]
    [InlineData(@"C:\Apps\FooBar\foo.exe", @"C:\Apps\Foo", false)]
    [InlineData(@"rundll32.exe C:\Apps\Foo\x.dll,Run", @"C:\Apps\Foo", true)]
    [InlineData(@"v2.30|Action=Allow|App=c:\apps\foo\foo.exe|Name=x|", @"C:\Apps\Foo", true)]
    public void References_directory_requires_a_path_boundary(string text, string dir, bool expected) =>
        Assert.Equal(expected, PathUtil.ReferencesDirectory(text, dir));

    [Fact]
    public void Firewall_rule_field_extraction() =>
        Assert.Equal(@"C:\x\y.exe", FirewallProbe.ExtractField(@"v2.30|Action=Allow|Dir=In|App=C:\x\y.exe|Name=Test|", "App"));

    [Theory]
    [InlineData(@"C:\", 0)]
    [InlineData(@"C:\Foo", 1)]
    [InlineData(@"C:\Foo\Bar", 2)]
    public void Depth(string path, int expected) => Assert.Equal(expected, PathUtil.Depth(path));
}
