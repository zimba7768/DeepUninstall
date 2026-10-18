using DeepUninstall.Core.Discovery;
using DeepUninstall.Core.Models;

namespace DeepUninstall.Core.Uninstall;

/// <summary>Builds the command that removes a program, interactively or silently.</summary>
public static class UninstallCommandBuilder
{
    private static string MsiExec => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    /// <returns>The command, or null when no (silent) uninstall command can be built.</returns>
    public static ParsedCommand? Build(InstalledApp app, bool silent, Func<string, bool>? isNsis = null)
    {
        // MSI entries often say "MsiExec.exe /I{code}", which opens the repair/modify dialog.
        // Always use /X so the product is actually removed.
        if (app.InstallerType == InstallerType.Msi && app.MsiProductCode is { } code)
            return new ParsedCommand(MsiExec, silent ? $"/X{code} /qn /norestart" : $"/X{code}");

        if (!silent) return CommandLineParser.Parse(app.UninstallString);

        if (!string.IsNullOrWhiteSpace(app.QuietUninstallString))
            return CommandLineParser.Parse(app.QuietUninstallString);

        var command = CommandLineParser.Parse(app.UninstallString);
        if (command is null) return null;

        var type = app.InstallerType;
        if (type == InstallerType.Unknown && (isNsis ?? InstallerDetector.LooksLikeNsis)(command.FileName))
            type = InstallerType.Nsis;

        var extra = SilentArguments(type, command.Arguments);
        return extra is null ? null : command with { Arguments = Append(command.Arguments, extra) };
    }

    public static bool SupportsSilent(InstalledApp app) => Build(app, silent: true) is not null;

    private static string? SilentArguments(InstallerType type, string existing) => type switch
    {
        InstallerType.InnoSetup => Missing(existing, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"),
        InstallerType.Nsis => Missing(existing, "/S"),
        InstallerType.WixBurn => Missing(existing, "/uninstall", "/quiet", "/norestart"),
        InstallerType.Squirrel => Missing(existing, "--uninstall", "-s"),
        _ => null,
    };

    private static string Missing(string existing, params string[] flags)
    {
        var tokens = existing.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', flags.Where(f => !tokens.Contains(f, StringComparer.OrdinalIgnoreCase)));
    }

    private static string Append(string args, string extra) =>
        string.IsNullOrWhiteSpace(extra) ? args : string.IsNullOrWhiteSpace(args) ? extra : $"{args} {extra}";
}
