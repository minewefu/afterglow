using System.Collections.Concurrent;

namespace Afterglow.Core.Metrics;

public sealed record TrackedApp(int ProcessId, string Application, string PresentMode, double LastSeenMs, int RecentFrames);

/// <summary>
/// Aggregates PresentMon events into per-process rolling windows and serves
/// statistics for "the game" — either an explicitly selected process or the
/// busiest one. Swap chains are tracked separately per process and the busiest
/// chain that is still presenting wins, so launcher/overlay swap chains — and a
/// chain the game has replaced — don't pollute game numbers.
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

        /// <summary>When this capture first saw the process present; a process started later is a reused id.</summary>
        public required DateTime FirstSeenUtc { get; init; }

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

    /// <summary>
    /// This process. Afterglow's own window presents too, and as the foreground
    /// window it became "the game" whenever the user switched to it — the readout
    /// showed Afterglow's frame rate mid-game, and pressing Stop after a game
    /// exited recorded the session as Afterglow's (or dropped it). The app is
    /// never the subject of its own measurement.
    /// </summary>
    private static readonly int OwnProcessId = Environment.ProcessId;

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

    /// <summary>
    /// Starts a capture; while one is running this changes nothing. A new capture
    /// starts from nothing (R-26): the GUI's Stop ended PresentMon but kept these
    /// windows, and a new PresentMon's timestamps restart at zero, so the old
    /// frames never aged out and the next capture reported a blend of both runs.
    /// The session is stopped before the windows are cleared, so no line of the
    /// old capture can still arrive after the clear.
    /// </summary>
    public bool Start(string? exePath = null)
    {
        if (_session.State == PresentMonState.Running)
        {
            return true;
        }

        _session.Stop();
        ClearCapture();
        return _session.Start(exePath);
    }

    /// <summary>
    /// Ends the capture and drops its windows, so nothing can serve them as a
    /// current reading afterwards. Read what a report needs before calling this.
    /// </summary>
    public void Stop()
    {
        _session.Stop();
        ClearCapture();
    }

    private void ClearCapture()
    {
        _processes.Clear();
        Volatile.Write(ref _lastEventMs, 0);
    }

    private void OnFrame(PresentEvent e)
    {
        // Ignore the compositor itself, and this app's own window.
        if (e.ProcessId == OwnProcessId || e.Application.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var entry = _processes.GetOrAdd(e.ProcessId, static (_, app) => new ProcessEntry
        {
            Application = app,
            FirstSeenUtc = DateTime.UtcNow,
        }, e.Application);
        var chain = entry.Chains.GetOrAdd(e.SwapChain, _ => new ChainWindow
        {
            Window = new FrametimeWindow(_statsWindow),
        });

        chain.Window.Add(e.TimestampMs, e.FrametimeMs);
        Volatile.Write(ref chain.LastSeenMs, e.TimestampMs);
        chain.PresentMode = e.PresentMode;
        if (entry.Chains.Count > 1)
        {
            AgeOtherChains(entry, chain, e.TimestampMs);
        }

        Volatile.Write(ref entry.LastSeenMs, e.TimestampMs);
        Volatile.Write(ref entry.LastSeenTicks, Environment.TickCount64);
        Volatile.Write(ref _lastEventMs, e.TimestampMs);

        // Opportunistic cleanup of long-idle processes.
        if (_processes.Count > 32)
        {
            Prune(e.TimestampMs);
        }
    }

    /// <summary>
    /// The process's other swap chains age on this frame too, against the
    /// capture's shared timeline (R-27). A window used to be aged only by its own
    /// frames, so a chain the game stopped using — after a resolution change or a
    /// fullscreen toggle — kept its whole window forever. A chain silent for a
    /// whole window holds nothing any more and is dropped, so a game that
    /// recreates its swap chain on every mode change doesn't accumulate them.
    /// </summary>
    private void AgeOtherChains(ProcessEntry entry, ChainWindow current, double nowMs)
    {
        foreach (var (key, other) in entry.Chains)
        {
            if (ReferenceEquals(other, current))
            {
                continue;
            }

            other.Window.Age(nowMs);
            if (other.Window.Count == 0 && nowMs - Volatile.Read(ref other.LastSeenMs) > _statsWindow.TotalMilliseconds)
            {
                entry.Chains.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Drops processes idle for a minute beyond the stats window, and never sooner:
    /// a process whose window could still be reported stays, so a capture no
    /// longer than its window (the CLI sizes the window to the capture) keeps
    /// every participant for its summary (R-30).
    /// </summary>
    private void Prune(double nowMs)
    {
        double idleMs = _statsWindow.TotalMilliseconds + 60_000;
        foreach (var (pid, entry) in _processes)
        {
            if (nowMs - Volatile.Read(ref entry.LastSeenMs) > idleMs)
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
    internal const long StaleAfterMs = 2000;

    /// <summary>
    /// How far a swap chain's last frame may trail its process's newest frame and
    /// still count as a chain the process presents through (R-27). The same two
    /// seconds as a process's own freshness, on the PresentMon timeline.
    /// </summary>
    private const double ChainStaleMs = 2000;

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

    /// <summary>
    /// The chain with the most frames among those still presenting. A chain is
    /// judged against its process's newest frame, not the wall clock: for a live
    /// process that is the capture's current time, and for a process that quit
    /// before a finished capture ended it is the moment it quit, so the summary
    /// still reports the chain it was presenting through. A stale chain can't
    /// win (R-27): it used to out-count the chain the game presents through now.
    /// </summary>
    private static ChainWindow? BusiestChain(ProcessEntry entry)
    {
        double newest = Volatile.Read(ref entry.LastSeenMs);
        ChainWindow? best = null;
        int bestCount = 0;
        foreach (var chain in entry.Chains.Values)
        {
            if (newest - Volatile.Read(ref chain.LastSeenMs) > ChainStaleMs)
            {
                continue;
            }

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

    /// <summary>
    /// Whether the process that presented as <paramref name="pid"/> in this capture
    /// is still running — that same process, not a newer one reusing the id.
    /// Session reports use it to tell a tuning change made while the game ran
    /// from one made after it exited (R-28).
    /// </summary>
    public bool IsStillRunning(int pid) =>
        _processes.TryGetValue(pid, out var entry) && ProcessLiveness.IsRunning(pid, entry.FirstSeenUtc);

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

    /// <summary>The live view: applications that presented within the last ten seconds of the capture.</summary>
    public IReadOnlyList<TrackedApp> GetTrackedApps() => Apps(maxIdleMs: 10_000);

    /// <summary>
    /// Every application that presented during this capture, however long ago it
    /// stopped — what a report over a finished capture lists (R-30). The CLI's
    /// summary used the live view, which drops an application ten seconds after
    /// its last frame: a benchmark that finished fifteen seconds before the
    /// capture did vanished while a background browser stayed.
    /// </summary>
    public IReadOnlyList<TrackedApp> GetCapturedApps() => Apps(maxIdleMs: null);

    private TrackedApp[] Apps(double? maxIdleMs)
    {
        double now = Volatile.Read(ref _lastEventMs);
        var apps = new List<TrackedApp>();
        foreach (var (pid, entry) in _processes)
        {
            double lastSeen = Volatile.Read(ref entry.LastSeenMs);
            if (maxIdleMs is { } idle && now - lastSeen > idle)
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
