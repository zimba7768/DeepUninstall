using System.Security.Principal;
using DeepUninstall.Core.Discovery;
using DeepUninstall.Core.Leftovers;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Removal;
using DeepUninstall.Core.Safety;
using DeepUninstall.Core.Uninstall;

namespace DeepUninstall.Core;

/// <summary>
/// One complete uninstall: run the program's own uninstaller, scan for what it left behind, remove
/// the chosen leftovers. Used by both the desktop app and the command line.
/// </summary>
public sealed class UninstallSession
{
    private readonly SafetyGuard _guard;

    public UninstallSession(InstalledApp app, SafetyGuard? guard = null)
    {
        App = app;
        _guard = guard ?? SafetyGuard.CreateDefault();
    }

    /// <summary>The program as registered before the uninstall started.</summary>
    public InstalledApp App { get; }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Is the program still registered as installed?</summary>
    public bool IsStillRegistered() => App.UninstallKey.Exists();

    public bool CanRunUninstaller => !App.UninstallerMissing && UninstallCommandBuilder.Build(App, silent: false) is not null;

    public bool SupportsSilent => UninstallCommandBuilder.SupportsSilent(App);

    public async Task<UninstallResult> RunUninstallerAsync(bool silent, IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        var command = UninstallCommandBuilder.Build(App, silent);
        if (command is null)
            return new UninstallResult(UninstallOutcome.Failed, null, silent ? "This program has no silent uninstall option." : "This program has no uninstall command.");

        var result = await new UninstallRunner().RunAsync(command, log, cancellationToken).ConfigureAwait(false);
        log?.Report(result.Message);

        // Some uninstallers remove their registry entry a few seconds after they exit.
        if (result.LooksSuccessful)
        {
            for (var i = 0; i < 10 && IsStillRegistered(); i++)
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
        }
        return result;
    }

    public Task<IReadOnlyList<Leftover>> ScanLeftoversAsync(IProgress<string>? log = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var others = new InstalledAppScanner().Scan().Where(a => !a.UninstallKey.Equals(App.UninstallKey)).ToList();
            return new LeftoverScanner(_guard).Scan(App, others, log, cancellationToken);
        }, cancellationToken);

    public Task<RemovalReport> RemoveAsync(IEnumerable<Leftover> items, RemovalOptions? options = null, IProgress<string>? log = null)
    {
        var list = items.ToList();
        options = (options ?? new RemovalOptions()) with { Label = App.DisplayName };
        return Task.Run(() => new LeftoverRemover(_guard).Remove(list, options, log));
    }
}
