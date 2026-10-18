using DeepUninstall.Core.Uninstall;

namespace DeepUninstall.Core.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void Quoted_path_with_arguments()
    {
        var cmd = CommandLineParser.Parse("\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT", _ => false)!;
        Assert.Equal(@"C:\Program Files\Foo\unins000.exe", cmd.FileName);
        Assert.Equal("/SILENT", cmd.Arguments);
    }

    [Fact]
    public void Unquoted_path_with_spaces_prefers_existing_file()
    {
        var existing = @"C:\Program Files\My App\uninstall.exe";
        var cmd = CommandLineParser.Parse(@"C:\Program Files\My App\uninstall.exe /S", p => p == existing)!;
        Assert.Equal(existing, cmd.FileName);
        Assert.Equal("/S", cmd.Arguments);
    }

    [Fact]
    public void Unquoted_path_with_spaces_without_existing_file_splits_at_exe()
    {
        var cmd = CommandLineParser.Parse(@"C:\Program Files\My App\uninstall.exe /S", _ => false)!;
        Assert.Equal(@"C:\Program Files\My App\uninstall.exe", cmd.FileName);
        Assert.Equal("/S", cmd.Arguments);
    }

    [Fact]
    public void Bare_msiexec_is_resolved_to_system32()
    {
        var expected = Path.Combine(Environment.SystemDirectory, "MsiExec.exe");
        var cmd = CommandLineParser.Parse("MsiExec.exe /I{11111111-2222-3333-4444-555555555555}",
            p => p.Equals(expected, StringComparison.OrdinalIgnoreCase))!;
        Assert.Equal(expected, cmd.FileName, ignoreCase: true);
        Assert.Equal("/I{11111111-2222-3333-4444-555555555555}", cmd.Arguments);
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var cmd = CommandLineParser.Parse("\"%SystemRoot%\\foo.exe\"", _ => false)!;
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), cmd.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_returns_null(string? input) => Assert.Null(CommandLineParser.Parse(input));
}
