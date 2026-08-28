using Afterglow.Core.Metrics;
using Afterglow.Core.Services;

namespace Afterglow.Core.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.0.3", "1.0.2", true)]
    [InlineData("1.0.3", "1.0.2", true)]
    [InlineData("v1.0.2", "1.0.2", false)]
    [InlineData("v1.0.1", "1.0.2", false)]
    [InlineData("v2.0", "1.9.9", true)]
    [InlineData("v1.1", "1.0.2", true)]
    public void IsNewer_compares_release_tags_against_the_running_version(
        string tag, string current, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(tag, Version.Parse(current)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("v")]
    public void Unparseable_tags_never_claim_an_update(string tag)
    {
        Assert.False(UpdateChecker.IsNewer(tag, new Version(1, 0, 2)));
    }

    [Fact]
    public void Four_part_assembly_versions_compare_on_three_parts()
    {
        // Assembly version 1.0.2.0 vs release tag v1.0.2 → same version, no update.
        Assert.False(UpdateChecker.IsNewer("v1.0.2", new Version(1, 0, 2, 0)));
    }
}

public class SessionCompareTests
{
    private static SessionReport Report(
        string app, int startOffsetMinutes, double fps, double low1, double watts,
        double temp, int core, int mem) => new()
    {
        Application = app,
        StartedAt = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero)
            .AddMinutes(startOffsetMinutes),
        DurationSeconds = 120,
        AvgFps = fps,
        Low1Fps = low1,
        P1Fps = low1 + 2,
        Frames = 10_000,
        AvgPowerW = watts,
        AvgGpuTempC = temp,
        AvgMemJunctionC = temp + 12,
        CoreOffsetMHz = core,
        MemOffsetMHz = mem,
    };

    [Fact]
    public void Deltas_are_newer_minus_older_regardless_of_argument_order()
    {
        var before = Report("game.exe", 0, 100.0, 80.0, 400, 70, 0, 0);
        var after = Report("game.exe", 30, 110.0, 88.0, 420, 72, 100, 500);

        // Same text whichever way the user clicked the two rows.
        string text = SessionCompare.Describe(after, before);
        Assert.Equal(text, SessionCompare.Describe(before, after));

        Assert.Contains("+10.0", text);          // avg fps delta
        Assert.Contains("+10.0%", text);         // and as a percentage of baseline
        Assert.Contains("+8.0", text);           // 1% low delta
        Assert.Contains("+20 W", text);          // power delta
        Assert.Contains("+100", text);           // newer session's core offset
    }

    [Fact]
    public void Different_applications_get_a_warning_not_a_silent_comparison()
    {
        var a = Report("gameA.exe", 0, 100, 80, 400, 70, 0, 0);
        var b = Report("gameB.exe", 30, 240, 190, 350, 65, 100, 500);

        Assert.Contains("not comparable", SessionCompare.Describe(a, b));
    }

    [Fact]
    public void Markdown_table_carries_both_sides_and_the_delta()
    {
        var before = Report("game.exe", 0, 100.0, 80.0, 400, 70, 0, 0);
        var after = Report("game.exe", 30, 90.0, 70.0, 380, 68, -50, 0);

        string md = SessionCompare.ToMarkdown(before, after);
        Assert.Contains("| Avg FPS | 100.0 | 90.0 | -10.0 |", md);
        Assert.Contains("| Board power (W) | 400 | 380 | -20 |", md);
    }
}
