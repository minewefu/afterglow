using System.Collections.Concurrent;

namespace Afterglow.Core.Metrics;

public sealed record TrackedApp(int ProcessId, string Application, string PresentMode, double LastSeenMs, int RecentFrames);

/// <summary>
/// Aggregates PresentMon events into per-process rolling windows and serves
/// statistics for "the game" — either an explicitly selected process or the
/// busiest one. Swap chains are tracked separately per process and the busiest
/// chain wins, so launcher/overlay swap chains don't pollute game numbers.
/// </summary>
public sealed class FrameMetricsService : IDisposable
{
    private sealed class ChainWindow
    {
        public required FrametimeWindow Window { get; init; }
        public double LastSeenMs;
        public string PresentMode = string.Empty;
    }

    private sealed class ProcessEntry
    {
        public required string Application { get; init; }
        public readonly ConcurrentDictionary<string, ChainWindow> Chains = new();
        public double LastSeenMs;
    }

    private readonly PresentMonSession _session;
    private readonly ConcurrentDictionary<int, ProcessEntry> _processes = new();
    private readonly TimeSpan _statsWindow;
    private double _lastEventMs;

    /// <summary>Explicit process selection; null = automatic (foreground/busiest).</summary>
    public int? SelectedProcessId { get; set; }

    /// <summary>Set by the foreground tracker; used when no explicit selection exists.</summary>
    public int? ForegroundProcessId { get; set; }

    public PresentMonSession Session => _session;

    public FrameMetricsService(TimeSpan? statsWindow = null)
    {
        _statsWindow = statsWindow ?? TimeSpan.FromSeconds(30);
        _session = new PresentMonSession();
        _session.FramePresented += OnFrame;
    }

    public bool Start(string? exePath = null) => _session.Start(exePath);

    private void OnFrame(PresentEvent e)
    {
        // Ignore the compositor itself.
        if (e.Application.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var entry = _processes.GetOrAdd(e.ProcessId, _ => new ProcessEntry { Application = e.Application });
        var chain = entry.Chains.GetOrAdd(e.SwapChain, _ => new ChainWindow
        {
            Window = new FrametimeWindow(_statsWindow),
        });

        chain.Window.Add(e.TimestampMs, e.FrametimeMs);
        chain.LastSeenMs = e.TimestampMs;
        chain.PresentMode = e.PresentMode;
        entry.LastSeenMs = e.TimestampMs;
        Volatile.Write(ref _lastEventMs, e.TimestampMs);

        // Opportunistic cleanup: drop processes idle for >60 s.
        if (_processes.Count > 32)
        {
            Prune(e.TimestampMs);
        }
    }

    private void Prune(double nowMs)
    {
        foreach (var (pid, entry) in _processes)
        {
            if (nowMs - Volatile.Read(ref entry.LastSeenMs) > 60_000)
            {
                _processes.TryRemove(pid, out _);
            }
        }
    }

    /// <summary>The process whose stats the overlay/UI should show.</summary>
    public int? ResolveTargetPid()
    {
        if (SelectedProcessId is int selected && _processes.ContainsKey(selected))
        {
            return selected;
        }

        if (ForegroundProcessId is int foreground && _processes.ContainsKey(foreground))
        {
            return foreground;
        }

        // Fall back to the busiest recently-active process.
        double now = Volatile.Read(ref _lastEventMs);
        int? best = null;
        int bestFrames = 0;
        foreach (var (pid, entry) in _processes)
        {
            if (now - Volatile.Read(ref entry.LastSeenMs) > 5000)
            {
                continue;
            }

            int frames = BusiestChain(entry)?.Window.Count ?? 0;
            if (frames > bestFrames)
            {
                bestFrames = frames;
                best = pid;
            }
        }

        return best;
    }

    private static ChainWindow? BusiestChain(ProcessEntry entry)
    {
        ChainWindow? best = null;
        int bestCount = 0;
        foreach (var chain in entry.Chains.Values)
        {
            int count = chain.Window.Count;
            if (count > bestCount)
            {
                bestCount = count;
                best = chain;
            }
        }

        return best;
    }

    public (TrackedApp App, FrameWindowStats Stats)? GetStats(int pid)
    {
        if (!_processes.TryGetValue(pid, out var entry))
        {
            return null;
        }

        var chain = BusiestChain(entry);
        if (chain?.Window.ComputeStats() is not { } stats)
        {
            return null;
        }

        var app = new TrackedApp(pid, entry.Application, chain.PresentMode,
            Volatile.Read(ref entry.LastSeenMs), chain.Window.Count);
        return (app, stats);
    }

    public (TrackedApp App, FrameWindowStats Stats)? GetTargetStats() =>
        ResolveTargetPid() is int pid ? GetStats(pid) : null;

    public double[] GetTargetFrametimes(int maxCount)
    {
        if (ResolveTargetPid() is not int pid || !_processes.TryGetValue(pid, out var entry))
        {
            return [];
        }

        return BusiestChain(entry)?.Window.GetRecentFrametimes(maxCount) ?? [];
    }

    public IReadOnlyList<TrackedApp> GetTrackedApps()
    {
        double now = Volatile.Read(ref _lastEventMs);
        var apps = new List<TrackedApp>();
        foreach (var (pid, entry) in _processes)
        {
            double lastSeen = Volatile.Read(ref entry.LastSeenMs);
            if (now - lastSeen > 10_000)
            {
                continue;
            }

            var chain = BusiestChain(entry);
            apps.Add(new TrackedApp(pid, entry.Application, chain?.PresentMode ?? string.Empty,
                lastSeen, chain?.Window.Count ?? 0));
        }

        return apps.OrderByDescending(a => a.RecentFrames).ToArray();
    }

    public void Dispose()
    {
        _session.FramePresented -= OnFrame;
        _session.Dispose();
    }
}
