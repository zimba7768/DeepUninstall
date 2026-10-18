using DeepUninstall.Core.Matching;
using DeepUninstall.Core.Models;
using Microsoft.Win32;

namespace DeepUninstall.Core.Tests;

public class NameMatcherTests
{
    private static InstalledApp App(string name, string? publisher = null, string? version = null, string keyName = "{00000000-0000-0000-0000-000000000000}", string? installLocation = null) => new()
    {
        DisplayName = name,
        Publisher = publisher,
        DisplayVersion = version,
        InstallLocation = installLocation,
        UninstallKey = new RegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}"),
    };

    [Fact]
    public void Strips_brackets_architecture_and_publisher_prefix()
    {
        var variants = NameMatcher.NameVariants("Mozilla Firefox (x64 en-US)", "Mozilla");
        Assert.Contains("Mozilla Firefox", variants);
        Assert.Contains("Firefox", variants);
    }

    [Fact]
    public void Exact_distinctive_name_is_high_confidence()
    {
        var matcher = NameMatcher.ForApp(App("Mozilla Firefox (x64 en-US)", "Mozilla Corporation"), []);
        Assert.Equal(Confidence.High, matcher.MatchProduct("Firefox"));
        Assert.Equal(Confidence.High, matcher.MatchProduct("Mozilla Firefox"));
    }

    [Fact]
    public void Prefixes_and_partial_names_never_match()
    {
        var matcher = NameMatcher.ForApp(App("Mozilla Firefox", "Mozilla"), []);
        Assert.Null(matcher.MatchProduct("Fire"));
        Assert.Null(matcher.MatchProduct("Firefox Backup"));
        Assert.Null(matcher.MatchProduct("Thunderbird"));
    }

    [Fact]
    public void Short_names_are_only_medium_confidence()
    {
        var matcher = NameMatcher.ForApp(App("Git", "The Git Development Community"), []);
        Assert.Equal(Confidence.Medium, matcher.MatchProduct("Git"));
    }

    [Fact]
    public void Generic_names_are_ignored()
    {
        var matcher = NameMatcher.ForApp(App("Microsoft Office", "Microsoft Corporation"), []);
        Assert.Null(matcher.MatchProduct("Office"));
        Assert.Null(matcher.MatchProduct("Microsoft"));
    }

    [Fact]
    public void Names_shared_with_other_installed_programs_are_ignored()
    {
        var removing = App("Zoom", "Zoom Video Communications", keyName: "ZoomUMX");
        var stillInstalled = App("Zoom", "Zoom Video Communications", keyName: "ZoomPerMachine");
        var matcher = NameMatcher.ForApp(removing, [stillInstalled]);
        Assert.Null(matcher.MatchProduct("Zoom"));
    }

    [Fact]
    public void Version_stripped_family_name_is_only_medium()
    {
        var matcher = NameMatcher.ForApp(App("Python 3.13.15 (64-bit)", "Python Software Foundation", "3.13.15150.0"), []);
        Assert.Equal(Confidence.Medium, matcher.MatchProduct("Python"));
    }

    [Fact]
    public void Folder_with_a_different_version_is_rejected()
    {
        var matcher = NameMatcher.ForApp(App("Python 3.13.15 (64-bit)", "Python Software Foundation", "3.13.15150.0"), []);
        Assert.Null(matcher.MatchProduct("Python 3.14"));
        Assert.NotNull(matcher.MatchProduct("Python 3.13"));
    }

    [Fact]
    public void Install_folder_name_is_an_exact_variant()
    {
        var matcher = NameMatcher.ForApp(App("WinSCP 6.5.6", "Martin Prikryl", "6.5.6", "winscp3_is1", @"C:\Program Files (x86)\WinSCP"), []);
        Assert.Equal(Confidence.High, matcher.MatchProduct("WinSCP"));
    }

    [Fact]
    public void Publisher_container_accepts_trailing_major_version()
    {
        var matcher = NameMatcher.ForApp(App("WinSCP 6.5.6", "Martin Prikryl", "6.5.6", "winscp3_is1", @"C:\Program Files (x86)\WinSCP"), []);
        Assert.True(matcher.MatchPublisher("Martin Prikryl"));
        Assert.Equal(Confidence.High, matcher.MatchProductInPublisherContainer("WinSCP 2"));
    }

    [Theory]
    [InlineData("Piriform Software Ltd", "Piriform")]
    [InlineData("Mozilla Corporation", "Mozilla")]
    [InlineData("win.rar GmbH", "win.rar")]
    public void Publisher_variants_drop_legal_suffixes(string publisher, string expected) =>
        Assert.Contains(expected, NameMatcher.PublisherVariants(publisher));

    [Theory]
    [InlineData("7-Zip", "7zip")]
    [InlineData("Notepad++", "notepad++")]
    [InlineData("VLC media player", "vlcmediaplayer")]
    public void Normalize_keeps_letters_digits_plus_and_hash(string input, string expected) =>
        Assert.Equal(expected, NameMatcher.Normalize(input));
}
