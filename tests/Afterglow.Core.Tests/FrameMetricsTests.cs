using Afterglow.Core.Metrics;

namespace Afterglow.Core.Tests;

public class FrameMetricsTests
{
    [Fact]
    public void Average_fps_is_harmonic_not_mean_of_fps()
    {
        // 99 frames at 10 ms + 1 frame at 30 ms = 1020 ms for 100 frames.
        double[] frametimes = [.. Enumerable.Repeat(10.0, 99), 30.0];
        var stats = FrameMetrics.Compute(frametimes)!.Value;

        Assert.Equal(100 * 1000.0 / 1020.0, stats.AverageFps, precision: 6); // 98.039...
        Assert.Equal(10.2, stats.AverageFrametimeMs, precision: 6);
        Assert.Equal(30.0, stats.MaxFrametimeMs, precision: 6);
    }

    [Fact]
    public void P1_uses_interpolated_r7_percentile()
    {
        double[] frametimes = [.. Enumerable.Repeat(10.0, 99), 30.0];
        var stats = FrameMetrics.Compute(frametimes)!.Value;

        // Sorted: index 98 = 10, index 99 = 30. rank = 0.99 * 99 = 98.01
        // → 10 + 0.01·20 = 10.2 ms → 98.039 fps.
        Assert.Equal(1000.0 / 10.2, stats.P1Fps, precision: 6);
    }

    [Fact]
    public void One_percent_low_is_average_of_worst_frames()
    {
        double[] frametimes = [.. Enumerable.Repeat(10.0, 99), 30.0];
        var stats = FrameMetrics.Compute(frametimes)!.Value;

        // Worst 1% of 100 frames = the single 30 ms frame → 33.33 fps.
        Assert.Equal(1000.0 / 30.0, stats.Low1Fps, precision: 6);
    }

    [Fact]
    public void Percentile_edge_cases()
    {
        double[] single = [16.7];
        Assert.Equal(16.7, FrameMetrics.PercentileSorted(single, 0.99), precision: 6);

        double[] pair = [10.0, 20.0];
        Assert.Equal(10.0, FrameMetrics.PercentileSorted(pair, 0.0), precision: 6);
        Assert.Equal(20.0, FrameMetrics.PercentileSorted(pair, 1.0), precision: 6);
        Assert.Equal(15.0, FrameMetrics.PercentileSorted(pair, 0.5), precision: 6);
    }

    [Fact]
    public void Worst_fraction_always_includes_at_least_one_frame()
    {
        double[] sorted = [10.0, 12.0, 50.0];
        // 0.1% of 3 frames rounds down to 0 → clamped to 1 frame (the worst).
        Assert.Equal(50.0, FrameMetrics.WorstFractionAverageSorted(sorted, 0.001), precision: 6);
    }

    [Fact]
    public void Compute_returns_null_for_insufficient_data()
    {
        Assert.Null(FrameMetrics.Compute([]));
        Assert.Null(FrameMetrics.Compute([16.7]));
    }

    [Fact]
    public void Window_evicts_frames_older_than_duration()
    {
        var window = new FrametimeWindow(TimeSpan.FromSeconds(10));
        window.Add(0, 10);
        window.Add(5000, 10);
        window.Add(14_000, 20);   // evicts t=0 (cutoff 4000)
        Assert.Equal(2, window.Count);

        window.Add(30_000, 20);   // evicts everything before t=20000
        Assert.Equal(1, window.Count);
    }

    [Fact]
    public void Window_ignores_garbage_frametimes()
    {
        var window = new FrametimeWindow(TimeSpan.FromSeconds(60));
        window.Add(0, -5);
        window.Add(1, 0);
        window.Add(2, double.NaN);
        window.Add(3, double.PositiveInfinity);
        Assert.Equal(0, window.Count);
    }

    [Fact]
    public void Window_stats_match_direct_computation()
    {
        var window = new FrametimeWindow(TimeSpan.FromSeconds(60));
        double t = 0;
        var frametimes = new List<double>();
        for (int i = 0; i < 200; i++)
        {
            double ft = 8 + (i % 7);
            frametimes.Add(ft);
            t += ft;
            window.Add(t, ft);
        }

        var expected = FrameMetrics.Compute(frametimes.ToArray())!.Value;
        var actual = window.ComputeStats()!.Value;
        Assert.Equal(expected.AverageFps, actual.AverageFps, precision: 9);
        Assert.Equal(expected.P1Fps, actual.P1Fps, precision: 9);
        Assert.Equal(expected.Low1Fps, actual.Low1Fps, precision: 9);
    }
}
