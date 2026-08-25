using System.Diagnostics.Eventing.Reader;

namespace Afterglow.Core.Services;

/// <summary>
/// Watches the Windows System event log for GPU driver resets (TDR — "Display
/// driver nvlddmkm stopped responding and has successfully recovered", event 4101)
/// while tuning is applied. On detection Afterglow can automatically reset to
/// driver defaults and flag the active profile as unstable — a safety net no
/// mainstream tuning tool provides.
/// </summary>
public sealed class TdrWatchdog : IDisposable
{
    private EventLogWatcher? _watcher;

    /// <summary>Raised (on a threadpool thread) when a display-driver reset is logged.</summary>
    public event Action<string>? DriverResetDetected;

    public bool IsRunning => _watcher is not null;

    /// <summary>Starts watching. Returns false when the event log is inaccessible (rare, non-admin).</summary>
    public bool Start()
    {
        if (_watcher is not null)
        {
            return true;
        }

        try
        {
            // Event 4101, provider "Display" — the standard Windows TDR recovery event.
            var query = new EventLogQuery(
                "System", PathType.LogName,
                "*[System[Provider[@Name='Display'] and (EventID=4101)]]");
            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnEvent;
            _watcher.Enabled = true;
            return true;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            _watcher = null;
            return false;
        }
    }

    private void OnEvent(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventRecord is null)
        {
            return;
        }

        string description;
        try
        {
            description = e.EventRecord.FormatDescription() ?? "Display driver reset (TDR) detected.";
        }
        catch (EventLogException)
        {
            description = "Display driver reset (TDR) detected.";
        }
        finally
        {
            e.EventRecord.Dispose();
        }

        DriverResetDetected?.Invoke(description);
    }

    public void Dispose()
    {
        if (_watcher is { } watcher)
        {
            _watcher = null;
            watcher.EventRecordWritten -= OnEvent;
            try
            {
                watcher.Enabled = false;
            }
            catch (EventLogException)
            {
            }

            watcher.Dispose();
        }
    }
}
