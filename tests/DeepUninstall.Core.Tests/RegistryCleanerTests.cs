using DeepUninstall.Core.Models;
using DeepUninstall.Core.RegistryCleaning;
using DeepUninstall.Core.Removal;
using Microsoft.Win32;

namespace DeepUninstall.Core.Tests;

public class MissingFileCheckerTests
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    // A fake disk: C: and D: are fixed and ready, E: is a removable drive.
    private static MissingFileChecker Checker(params string[] existing)
    {
        var files = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return new MissingFileChecker(
            fileExists: files.Contains,
            directoryExists: _ => false,
            isCheckableRoot: root => root.StartsWith("C:", StringComparison.OrdinalIgnoreCase) || root.StartsWith("D:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_file_on_a_fixed_drive_is_missing() =>
        Assert.True(Checker().IsPathMissing(@"C:\Apps\Gone\gone.exe"));

    [Fact]
    public void Existing_file_is_not_missing() =>
        Assert.False(Checker(@"C:\Apps\Here\here.exe").IsPathMissing(@"C:\Apps\Here\here.exe"));

    [Theory]
    [InlineData(@"E:\Portable\tool.exe")]          // removable or unplugged drive
    [InlineData(@"\\server\share\tool.exe")]       // network
    [InlineData(@"tool.exe")]                      // relative: found through the search path
    [InlineData(@"%NOT_A_REAL_VARIABLE%\x.exe")]   // unexpanded variable
    [InlineData("")]
    public void Uncertain_paths_are_never_reported_missing(string path) =>
        Assert.False(Checker().IsPathMissing(path));

    [Fact]
    public void Quotes_and_icon_indexes_are_ignored() =>
        Assert.True(Checker().IsPathMissing("\"C:\\Apps\\Gone\\gone.exe\",0"));

    [Fact]
    public void Paths_with_spaces_are_not_split()
    {
        // As a command line this would be ambiguous, but as a plain path it's one file.
        Assert.False(Checker(@"C:\Program Files\My App\lib.dll").IsPathMissing(@"C:\Program Files\My App\lib.dll"));
    }

    [Fact]
    public void System32_entry_is_found_in_SysWOW64()
    {
        var checker = Checker(Path.Combine(Windows, "SysWOW64", "thing.dll"));
        Assert.False(checker.IsPathMissing(Path.Combine(Windows, "System32", "thing.dll")));
    }

    [Fact]
    public void ProgramFiles_variable_also_tries_the_x86_folder()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var checker = Checker(Path.Combine(x86, "Foo", "foo.exe"));
        Assert.False(checker.IsPathMissing(@"%ProgramFiles%\Foo\foo.exe"));
    }

    [Fact]
    public void Command_with_arguments_checks_the_executable()
    {
        Assert.True(Checker().IsCommandTargetMissing("\"C:\\Apps\\Gone\\gone.exe\" --background"));
        Assert.False(Checker(@"C:\Apps\Here\here.exe").IsCommandTargetMissing("\"C:\\Apps\\Here\\here.exe\" \"%1\""));
    }

    [Theory]
    [InlineData(@"rundll32.exe C:\Gone\gone.dll,Run")]
    [InlineData(@"C:\Windows\System32\rundll32.exe C:\Gone\gone.dll,Run")]
    [InlineData(@"msiexec.exe /x {11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"C:\Python\pythonw.exe C:\Gone\script.pyw")]
    public void Commands_run_through_host_programs_are_not_judged(string command) =>
        Assert.False(Checker().IsCommandTargetMissing(command));

    [Fact]
    public void Command_targets_must_be_executables() =>
        Assert.False(Checker().IsCommandTargetMissing(@"C:\Gone\document.txt"));
}

public class RegistryCleanerLevelTests
{
    [Fact]
    public void Each_level_includes_the_ones_below()
    {
        var safe = RegistryCleaner.CategoriesFor(ScanLevel.Safe);
        var moderate = RegistryCleaner.CategoriesFor(ScanLevel.Moderate);
        var advanced = RegistryCleaner.CategoriesFor(ScanLevel.Advanced);
        Assert.Subset(moderate.ToHashSet(), safe.ToHashSet());
        Assert.Subset(advanced.ToHashSet(), moderate.ToHashSet());
        Assert.Equal(RegistryCleaner.Categories.Count, advanced.Count);
        Assert.Contains(RegistryIssueCategory.MissingStartupPrograms, safe);
        Assert.DoesNotContain(RegistryIssueCategory.MissingComComponents, moderate);
    }
}

public class ReportWriterTests
{
    private static ReportContext Context()
    {
        var key = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Foo <script>");
        var removed = new Leftover { Kind = LeftoverKind.RegistryKey, Key = key, Path = key.ToString(), Confidence = Confidence.High, Reason = "Registry settings named after the program" };
        var folder = new Leftover { Kind = LeftoverKind.Directory, Path = @"C:\Apps\Foo", Confidence = Confidence.High, Reason = "Install folder" };
        var kept = new Leftover { Kind = LeftoverKind.Directory, Path = @"C:\Data\Foo", Confidence = Confidence.Medium, Reason = "Folder named after the program" };
        var report = new RemovalReport(
            [new RemovalEntry(removed, RemovalStatus.Removed, "backup: 001.reg"), new RemovalEntry(folder, RemovalStatus.ScheduledForReboot, "files in use")],
            @"C:\Backups\x");
        return new ReportContext
        {
            Title = "Uninstall report",
            Subject = "Foo & Bar",
            Facts = [new("Publisher", "Foo Inc."), new("Uninstaller", "Finished")],
            Removal = report,
            Kept = [kept],
            Log = "line one\nline two",
        };
    }

    [Fact]
    public void Text_report_lists_everything()
    {
        var text = ReportWriter.ToText(Context());
        Assert.Contains("Uninstall report: Foo & Bar", text);
        Assert.Contains("Publisher:", text);
        Assert.Contains(@"[Removed] Registry key: HKCU\SOFTWARE\Foo <script>", text);
        Assert.Contains(@"[Removed at restart] Folder: C:\Apps\Foo", text);
        Assert.Contains(@"C:\Data\Foo", text);
        Assert.Contains(@"C:\Backups\x", text);
        Assert.Contains("line two", text);
    }

    [Fact]
    public void Html_report_is_escaped_and_self_contained()
    {
        var html = ReportWriter.ToHtml(Context());
        Assert.StartsWith("<!doctype html>", html.TrimStart());
        Assert.Contains("Foo &amp; Bar", html);
        Assert.Contains("SOFTWARE\\Foo &lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("src=\"http", html);
    }
}
