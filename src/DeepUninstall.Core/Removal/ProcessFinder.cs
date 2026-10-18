using System.Diagnostics;
using System.Text;
using DeepUninstall.Core.Native;
using DeepUninstall.Core.Safety;

namespace DeepUninstall.Core.Removal;

public sealed record RunningProcess(int Id, string Name, string ExecutablePath);

/// <summary>Finds processes whose executable is inside given folders, so they can be closed before deletion.</summary>
public static class ProcessFinder
{
    public static IReadOnlyList<RunningProcess> FindIn(IEnumerable<string> directories)
    {
        var dirs = directories.ToList();
        if (dirs.Count == 0) return [];

        var self = Environment.ProcessId;
        var result = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == self || process.Id <= 4) continue;
                var path = GetImagePath(process.Id);
                if (path is not null && dirs.Any(d => PathUtil.IsWithinOrEqual(path, d)))
                    result.Add(new RunningProcess(process.Id, process.ProcessName, path));
            }
        }
        return result;
    }

    public static void Terminate(IEnumerable<RunningProcess> processes, IProgress<string>? log = null)
    {
        foreach (var p in processes)
        {
            try
            {
                using var process = Process.GetProcessById(p.Id);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                log?.Report($"Closed {p.Name} (PID {p.Id}).");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log?.Report($"Could not close {p.Name} (PID {p.Id}): {e.Message}");
            }
        }
    }

    private static string? GetImagePath(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
