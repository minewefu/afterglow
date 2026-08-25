namespace Afterglow.Core.Metrics;

/// <summary>
/// Frame statistics for one window of frametimes.
/// Method notes (metric names in the UI must keep these meanings):
///  - <see cref="AverageFps"/>: harmonic average, N·1000/Σft — not a mean of per-frame FPS.
///  - <see cref="P1Fps"/>/<see cref="P01Fps"/>: single interpolated percentile (R-7) of the
///    frametime distribution, inverted. What FrameView-style "1% FPS" reports.
///  - <see cref="Low1Fps"/>/<see cref="Low01Fps"/>: average of the worst 1%/0.1% of frames,
///    inverted. The Gamers Nexus / CapFrameX "x% low average" method.
/// </summary>
public readonly record struct FrameWindowStats(
    int FrameCount,
    double AverageFps,
    double AverageFrametimeMs,
    double MaxFrametimeMs,
    double P1Fps,
    double P01Fps,
    double Low1Fps,
    double Low01Fps);

public static class FrameMetrics
{
    /// <summary>Computes all window statistics. Returns null when the window has fewer than 2 frames.</summary>
    public static FrameWindowStats? Compute(ReadOnlySpan<double> frametimesMs)
    {
        if (frametimesMs.Length < 2)
        {
            return null;
        }

        double sum = 0;
        double max = 0;
        foreach (double ft in frametimesMs)
        {
            sum += ft;
            max = Math.Max(max, ft);
        }

        if (sum <= 0)
        {
            return null;
        }

        double[] sorted = frametimesMs.ToArray();
        Array.Sort(sorted);

        double p99 = PercentileSorted(sorted, 0.99);
        double p999 = PercentileSorted(sorted, 0.999);

        return new FrameWindowStats(
            FrameCount: frametimesMs.Length,
            AverageFps: frametimesMs.Length * 1000.0 / sum,
            AverageFrametimeMs: sum / frametimesMs.Length,
            MaxFrametimeMs: max,
            P1Fps: 1000.0 / p99,
            P01Fps: 1000.0 / p999,
            Low1Fps: 1000.0 / WorstFractionAverageSorted(sorted, 0.01),
            Low01Fps: 1000.0 / WorstFractionAverageSorted(sorted, 0.001));
    }

    /// <summary>
    /// Interpolated percentile (R-7, the NumPy/Excel default): rank = p·(N−1),
    /// linear interpolation between the neighbors. Input must be sorted ascending.
    /// </summary>
    public static double PercentileSorted(IReadOnlyList<double> sorted, double p)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sorted.Count, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(p);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(p, 1);

        double rank = p * (sorted.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        double weight = rank - lower;
        return sorted[lower] + (weight * (sorted[upper] - sorted[lower]));
    }

    /// <summary>
    /// Average of the worst <paramref name="fraction"/> of frames (at least one frame).
    /// Input must be sorted ascending; returns the mean frametime of the tail.
    /// </summary>
    public static double WorstFractionAverageSorted(IReadOnlyList<double> sorted, double fraction)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sorted.Count, 1);
        int tail = Math.Max(1, (int)Math.Floor(sorted.Count * fraction));
        double sum = 0;
        for (int i = sorted.Count - tail; i < sorted.Count; i++)
        {
            sum += sorted[i];
        }

        return sum / tail;
    }
}

/// <summary>
/// Rolling frametime window for one swap chain: appends are O(1), old frames are
/// evicted by age. Thread-safe (writer = capture thread, readers = UI/overlay).
/// </summary>
public sealed class FrametimeWindow
{
    private readonly record struct Entry(double TimestampMs, double FrametimeMs);

    private readonly Queue<Entry> _entries = new();
    private readonly object _lock = new();
    private double _windowMs;

    public FrametimeWindow(TimeSpan window)
    {
        _windowMs = window.TotalMilliseconds;
    }

    public TimeSpan Window
    {
        get
        {
            lock (_lock)
            {
                return TimeSpan.FromMilliseconds(_windowMs);
            }
        }
        set
        {
            lock (_lock)
            {
                _windowMs = Math.Clamp(value.TotalMilliseconds, 1000, 600_000);
            }
        }
    }

    public void Add(double timestampMs, double frametimeMs)
    {
        if (frametimeMs <= 0 || double.IsNaN(frametimeMs) || double.IsInfinity(frametimeMs))
        {
            return;
        }

        lock (_lock)
        {
            _entries.Enqueue(new Entry(timestampMs, frametimeMs));
            double cutoff = timestampMs - _windowMs;
            while (_entries.Count > 0 && _entries.Peek().TimestampMs < cutoff)
            {
                _entries.Dequeue();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public FrameWindowStats? ComputeStats()
    {
        double[] frametimes;
        lock (_lock)
        {
            if (_entries.Count < 2)
            {
                return null;
            }

            frametimes = new double[_entries.Count];
            int i = 0;
            foreach (var entry in _entries)
            {
                frametimes[i++] = entry.FrametimeMs;
            }
        }

        return FrameMetrics.Compute(frametimes);
    }

    /// <summary>Most recent frametimes (up to <paramref name="maxCount"/>), oldest first, for graphing.</summary>
    public double[] GetRecentFrametimes(int maxCount)
    {
        lock (_lock)
        {
            int count = Math.Min(maxCount, _entries.Count);
            var result = new double[count];
            int skip = _entries.Count - count;
            int i = 0;
            foreach (var entry in _entries)
            {
                if (skip-- > 0)
                {
                    continue;
                }

                result[i++] = entry.FrametimeMs;
            }

            return result;
        }
    }
}
