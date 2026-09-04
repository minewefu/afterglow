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

        /// <summary>
        /// Wall clock (<see cref="Environment.TickCount64"/>) of the last frame.
        /// <see cref="LastSeenMs"/> is a PresentMon timeline stamp, which simply
        /// stops advancing when capture stops — comparing it against itself can
        /// never detect staleness, so freshness needs a clock that keeps running.
        /// </summary>
        public long LastSeenTicks;
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
        Volatile.Write(ref entry.LastSeenTicks, Environment.TickCount64);
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

    /// <summary>
    /// How long after the last frame a process's numbers stop counting as live.
    /// Past this, the overlay and dashboard must show nothing rather than keep
    /// repainting the final average as though the game were still running.
    /// </summary>
    private static readonly long StaleAfterMs = 2000;

    private static bool IsFresh(ProcessEntry entry) =>
        Environment.TickCount64 - Volatile.Read(ref entry.LastSeenTicks) <= StaleAfterMs;

    /// <summary>The process whose stats the overlay/UI should show.</summary>
    public int? ResolveTargetPid()
    {
        // Freshness is checked on every branch. The selected/foreground branches
        // had no age test at all, so a game that stopped presenting — or a
        // capture the user stopped — kept serving its last numbers forever.
        if (SelectedProcessId is int selected
            && _processes.TryGetValue(selected, out var selectedEntry) && IsFresh(selectedEntry))
        {
            return selected;
        }

        if (ForegroundProcessId is int foreground
            && _processes.TryGetValue(foreground, out var foregroundEntry) && IsFresh(foregroundEntry))
        {
            return foreground;
        }

        // Fall back to the busiest recently-active process.
        int? best = null;
        int bestFrames = 0;
        foreach (var (pid, entry) in _processes)
        {
            if (!IsFresh(entry))
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

    /// <summary>Whether the process has presented within the freshness window.</summary>
    public bool IsLive(int pid) => _processes.TryGetValue(pid, out var entry) && IsFresh(entry);

    /// <param name="pid">The process to report on.</param>
    /// <param name="requireFresh">
    /// True (the live readouts) withholds a window nothing has presented into
    /// recently. False is for a report over a FINISHED capture — the CLI's
    /// end-of-run summary — where a game that quit a few seconds before the
    /// window closed still has its whole frame window retained and the
    /// freshness gate would throw the captured statistics away.
    /// </param>
    public (TrackedApp App, FrameWindowStats Stats)? GetStats(int pid, bool requireFresh = true)
    {
        if (!_processes.TryGetValue(pid, out var entry))
        {
            return null;
        }

        // Nothing has presented recently: report "no stats" rather than the last
        // computed average. Every caller treats non-null as live data, so a
        // stale window here became a frozen FPS number painted indefinitely on
        // the overlay and the dashboard tile.
        if (requireFresh && !IsFresh(entry))
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

        // Capture is over: drop the windows so nothing can serve their contents
        // as a current reading afterwards.
        _processes.Clear();
    }
}
