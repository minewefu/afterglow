using System.ComponentModel;
using System.Diagnostics;

namespace Afterglow.Core.Metrics;

/// <summary>
/// Whether a process id still names a running process — and, optionally, the
/// same process that was seen earlier: Windows reuses ids, so an id on its own
/// can name a newer, unrelated process.
/// </summary>
internal static class ProcessLiveness
{
    /// <param name="pid">The process id.</param>
    /// <param name="startedNoLaterThanUtc">
    /// When set, a process that started after this instant is a reuse of the id,
    /// not the process that was seen, and does not count as it running.
    /// </param>
    public static bool IsRunning(int pid, DateTime? startedNoLaterThanUtc = null)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            return startedNoLaterThanUtc is not { } limit || process.StartTime.ToUniversalTime() <= limit;
        }
        catch (ArgumentException)
        {
            return false; // no process has this id
        }
        catch (InvalidOperationException)
        {
            return false; // it exited while being queried
        }
        catch (Win32Exception)
        {
            // It exists but this process may not query it (a protected process):
            // running, as far as can be told.
            return true;
        }
    }
}
