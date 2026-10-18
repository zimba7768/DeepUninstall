using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeepUninstall.Core.Native;

namespace DeepUninstall.Core.Uninstall;

public enum UninstallOutcome
{
    Succeeded,
    SucceededRebootRequired,
    NotInstalled,
    Cancelled,
    Failed,

    /// <summary>The user stopped waiting; the uninstaller may still be running.</summary>
    WaitAbandoned,
}

public sealed record UninstallResult(UninstallOutcome Outcome, int? ExitCode, string Message)
{
    public bool LooksSuccessful => Outcome is UninstallOutcome.Succeeded or UninstallOutcome.SucceededRebootRequired or UninstallOutcome.NotInstalled;
}

/// <summary>
/// Runs an uninstaller and waits for it to really finish.
/// </summary>
/// <remarks>
/// Many uninstallers (Inno Setup, NSIS, InstallShield...) copy themselves to %TEMP%, start the copy
/// and exit right away so they can delete their own folder. Waiting on the first process is not
/// enough. We start the uninstaller suspended inside a Job Object, so every process it spawns is
/// tracked, and wait until the job is empty.
/// </remarks>
public sealed class UninstallRunner
{
    // Processes an uninstaller may leave running that we should not wait for: "sorry to see you go"
    // browser pages, a restarted Explorer, console hosts.
    private static readonly HashSet<string> BackgroundProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "iexplore", "opera", "brave", "vivaldi", "msedgewebview2",
        "conhost", "explorer", "notepad",
    };

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public Task<UninstallResult> RunAsync(ParsedCommand command, IProgress<string>? log = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(command, log, cancellationToken), CancellationToken.None);

    private UninstallResult Run(ParsedCommand command, IProgress<string>? log, CancellationToken ct)
    {
        var commandLine = command.ToCommandLine();
        log?.Report($"Running: {commandLine}");

        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return new UninstallResult(UninstallOutcome.Failed, null, $"Could not create a job object: {new Win32Exception().Message}");

        try
        {
            var startupInfo = new NativeMethods.STARTUPINFO { cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>() };
            var buffer = new StringBuilder(commandLine, Math.Max(commandLine.Length + 1, 1024));

            // Run from %TEMP%, never from the install folder: a process whose working directory is
            // the install folder keeps that folder from being deleted.
            if (!NativeMethods.CreateProcess(null, buffer, IntPtr.Zero, IntPtr.Zero, false,
                    NativeMethods.CREATE_SUSPENDED | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero, Path.GetTempPath(), ref startupInfo, out var pi))
            {
                var error = Marshal.GetLastWin32Error();
                var message = error == NativeMethods.ERROR_ELEVATION_REQUIRED
                    ? "The uninstaller needs administrator rights. Run DeepUninstall as administrator."
                    : new Win32Exception(error).Message;
                return new UninstallResult(UninstallOutcome.Failed, null, $"Could not start the uninstaller: {message}");
            }

            try
            {
                var tracked = NativeMethods.AssignProcessToJobObject(job, pi.hProcess);
                if (!tracked)
                    log?.Report("Could not track child processes. Waiting for the main process only.");
                NativeMethods.ResumeThread(pi.hThread);

                var reportedHandOff = false;
                while (true)
                {
                    var mainExited = NativeMethods.WaitForSingleObject(pi.hProcess, 0) == NativeMethods.WAIT_OBJECT_0;
                    if (!tracked)
                    {
                        if (mainExited) break;
                    }
                    else
                    {
                        var active = GetJobProcessIds(job, out var queryFailed);
                        if (queryFailed)
                        {
                            tracked = false;
                            continue;
                        }
                        if (active is { Count: 0 }) break;
                        if (mainExited && active is not null)
                        {
                            if (!reportedHandOff)
                            {
                                log?.Report("The uninstaller handed off to another process. Still waiting...");
                                reportedHandOff = true;
                            }
                            if (active.All(IsBackgroundProcess))
                            {
                                log?.Report("Only browser or helper windows are left open. Not waiting for them.");
                                break;
                            }
                        }
                    }

                    if (ct.WaitHandle.WaitOne(PollInterval))
                        return new UninstallResult(UninstallOutcome.WaitAbandoned, null, "Stopped waiting for the uninstaller. It may still be running.");
                }

                NativeMethods.GetExitCodeProcess(pi.hProcess, out var exitCode);
                return Interpret(unchecked((int)exitCode));
            }
            finally
            {
                NativeMethods.CloseHandle(pi.hThread);
                NativeMethods.CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(job);
        }
    }

    internal static UninstallResult Interpret(int exitCode) => exitCode switch
    {
        0 => new(UninstallOutcome.Succeeded, exitCode, "The uninstaller finished."),
        3010 or 1641 => new(UninstallOutcome.SucceededRebootRequired, exitCode, "The uninstaller finished. A restart is required to complete removal."),
        1605 or 1614 => new(UninstallOutcome.NotInstalled, exitCode, "Windows Installer reports the product is not installed."),
        1602 or 1223 => new(UninstallOutcome.Cancelled, exitCode, "The uninstall was cancelled."),
        _ => new(UninstallOutcome.Failed, exitCode, $"The uninstaller exited with code {exitCode}. It may have failed or been cancelled."),
    };

    /// <returns>The process ids in the job, or null when there are too many to list.</returns>
    private static List<int>? GetJobProcessIds(IntPtr job, out bool queryFailed)
    {
        const int capacity = 256;
        var size = 8 + capacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        queryFailed = false;
        try
        {
            if (!NativeMethods.QueryInformationJobObject(job, NativeMethods.JobObjectBasicProcessIdList, buffer, size, out _))
            {
                queryFailed = Marshal.GetLastWin32Error() != NativeMethods.ERROR_MORE_DATA;
                return null;
            }

            var count = Marshal.ReadInt32(buffer, 4);
            var ids = new List<int>(count);
            for (var i = 0; i < count; i++)
                ids.Add((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size));
            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsBackgroundProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return BackgroundProcessNames.Contains(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return true; // Already exited.
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
