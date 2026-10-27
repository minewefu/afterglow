using Afterglow.Core.Metrics;

namespace Afterglow.Core.Tests;

public class SessionReportTests
{
    private static SessionReport Report(string app, double fps, int core, int mem) => new()
    {
        Application = app,
        StartedAt = new DateTimeOffset(2026, 8, 28, 2, 0, 0, TimeSpan.Zero),
        DurationSeconds = 300,
        AvgFps = fps,
        Low1Fps = fps * 0.8,
        P1Fps = fps * 0.82,
        Frames = 50_000,
        AvgPowerW = 380,
        AvgGpuTempC = 68,
        AvgMemJunctionC = 82,
        CoreOffsetMHz = core,
        MemOffsetMHz = mem,
    };

    [Fact]
    public void Roundtrips_through_the_jsonl_store()
    {
        string path = Path.Combine(Path.GetTempPath(), $"afterglow-sessions-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new SessionReportStore(path);
            store.Append(Report("league of legends.exe", 168.3, 0, 0));
            store.Append(Report("league of legends.exe", 171.1, 100, 500));

            var loaded = new SessionReportStore(path).LoadAll();

            Assert.Equal(2, loaded.Count);
            Assert.Equal(0, loaded[0].CoreOffsetMHz);
            Assert.Equal(100, loaded[1].CoreOffsetMHz);
            Assert.Equal(171.1, loaded[1].AvgFps, precision: 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Markdown_table_carries_the_comparison_columns()
    {
        string markdown = SessionReportStore.ToMarkdown(
        [
            Report("cyberpunk2077.exe", 120.5, 100, 500),
        ]);

        Assert.Contains("| App | When | Length | Avg FPS | 1% low |", markdown, StringComparison.Ordinal);
        Assert.Contains("cyberpunk2077.exe", markdown, StringComparison.Ordinal);
        Assert.Contains("120.5", markdown, StringComparison.Ordinal);
        Assert.Contains("+100", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_whose_tuning_changed_mid_window_is_marked_wherever_it_is_exported()
    {
        // R-28: such a run measured neither configuration cleanly, and the table
        // and the A/B comparison are what get pasted into reports.
        var clean = Report("cyberpunk2077.exe", 120.5, 0, 0) with { TuningChangedDuringWindow = false };
        var mixed = Report("cyberpunk2077.exe", 131.0, 150, 1000) with
        {
            StartedAt = clean.StartedAt.AddHours(1),
            TuningChangedDuringWindow = true,
        };

        string table = SessionReportStore.ToMarkdown([mixed, clean]);
        Assert.Contains("| +150* | +1000* |", table, StringComparison.Ordinal);
        Assert.Contains("| 0 | 0 |", table, StringComparison.Ordinal);
        Assert.Contains(SessionReportStore.MixedTuningNote, table, StringComparison.Ordinal);
        Assert.DoesNotContain(SessionReportStore.MixedTuningNote, SessionReportStore.ToMarkdown([clean]), StringComparison.Ordinal);

        Assert.Contains("changed during B's measured window", SessionCompare.Describe(clean, mixed), StringComparison.Ordinal);
        Assert.Contains("changed during B's measured window", SessionCompare.ToMarkdown(mixed, clean), StringComparison.Ordinal);
        Assert.DoesNotContain("measured window", SessionCompare.Describe(clean, clean with { AvgFps = 121 }), StringComparison.Ordinal);
    }

    [Fact]
    public void The_mixed_tuning_flag_roundtrips_and_reads_as_unknown_on_older_records()
    {
        string path = Path.Combine(Path.GetTempPath(), $"afterglow-sessions-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path,
                """{"application":"old.exe","startedAt":"2026-08-28T02:00:00+00:00","durationSeconds":60,"avgFps":100,"coreOffsetMHz":100}""" +
                Environment.NewLine);
            var store = new SessionReportStore(path);
            store.Append(Report("new.exe", 140, 150, 1000) with { TuningChangedDuringWindow = true });

            var loaded = store.LoadAll();

            Assert.Null(loaded[0].TuningChangedDuringWindow);
            Assert.True(loaded[1].TuningChangedDuringWindow);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Store_is_capped_and_keeps_the_newest()
    {
        string path = Path.Combine(Path.GetTempPath(), $"afterglow-sessions-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new SessionReportStore(path);
            for (int i = 0; i < 210; i++)
            {
                store.Append(Report($"app{i}.exe", i, 0, 0));
            }

            var loaded = store.LoadAll();
            Assert.Equal(200, loaded.Count);
            Assert.Equal("app209.exe", loaded[^1].Application);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
