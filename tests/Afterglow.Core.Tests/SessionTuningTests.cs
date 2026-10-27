using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Metrics;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// R-28: a session report is labelled with the tuning in force while its frames
/// were presented. The report used to read the card when Stop was clicked, and
/// in the game-profile workflow the game's exit had already restored the
/// baseline by then — so the tuned run was stamped with untuned settings.
/// These drive <see cref="CaptureTuningTimeline"/> exactly as the Metrics page
/// does: once a second a reading, then the stats, then (if the target's process
/// still runs) a confirmation.
/// </summary>
public class SessionTuningTests
{
    private const int Game = 4242;

    private static readonly TuningReading Baseline = new(0, 0, 450, null, null);
    private static readonly TuningReading Tuned = new(150, 1000, 450, null, null);

    /// <summary>A trailing 30 s window at 60 FPS.</summary>
    private static readonly FrameWindowStats Window30s = new(1800, 60, 1000.0 / 60, 20, 55, 50, 52, 48);

    /// <summary>One refresh tick of the Metrics page.</summary>
    private static void Tick(CaptureTuningTimeline timeline, long atMs, TuningReading reading, bool gameRunning)
    {
        timeline.AddReading(atMs, reading);
        if (gameRunning)
        {
            timeline.ConfirmRunning(Game);
        }
    }

    /// <summary>
    /// The game-profile workflow: capture started, the game launched, its rule
    /// applied the tuned profile, the game ran, quit, the rule restored the
    /// baseline, and only then did the user press Stop.
    /// </summary>
    private static (CaptureTuningTimeline Timeline, long StatsAtMs) GameProfileRun(long restoreAtMs)
    {
        var timeline = new CaptureTuningTimeline();
        timeline.AddReading(0, Baseline);                     // Start
        for (long t = 1000; t <= 4000; t += 1000)
        {
            Tick(timeline, t, Baseline, gameRunning: false);   // no game yet
        }

        for (long t = 5000; t <= 7000; t += 1000)
        {
            Tick(timeline, t, Baseline, gameRunning: true);    // game up, profile not yet applied
        }

        for (long t = 8000; t <= 300_000; t += 1000)
        {
            Tick(timeline, t, Tuned, gameRunning: true);       // the rule's profile is in force
        }

        // The game quits at 300.5 s. Its frames stay "fresh" for two seconds, so
        // the page keeps taking its stats — with the process already gone.
        long statsAt = 0;
        for (long t = 301_000; t <= 320_000; t += 1000)
        {
            Tick(timeline, t, t > restoreAtMs ? Baseline : Tuned, gameRunning: false);
            if (t <= 302_000)
            {
                statsAt = t;
            }
        }

        return (timeline, statsAt);
    }

    [Fact]
    public void A_game_exit_restore_does_not_relabel_the_run()
    {
        var (timeline, statsAt) = GameProfileRun(restoreAtMs: 303_500);

        var tuning = timeline.AttributeTo(Game, statsAt, Window30s);

        // The old report read the card at Stop (320 s): the restored baseline.
        Assert.Equal(Tuned, tuning.Reading);
        Assert.False(tuning.ChangedDuringWindow);
    }

    [Fact]
    public void A_restore_that_lands_while_the_games_last_stats_are_still_fresh_does_not_relabel_it_either()
    {
        // GameWatcher can see the exit and restore inside the two seconds the
        // game's frames still count as live: the last stats are then taken after
        // the restore. The game was not running at that reading, so it labels nothing.
        var (timeline, statsAt) = GameProfileRun(restoreAtMs: 301_200);

        var tuning = timeline.AttributeTo(Game, statsAt, Window30s);

        Assert.Equal(Tuned, tuning.Reading);
        Assert.False(tuning.ChangedDuringWindow);
    }

    [Fact]
    public void A_change_inside_the_measured_window_is_reported_as_a_mix()
    {
        var timeline = new CaptureTuningTimeline();
        timeline.AddReading(0, Baseline);
        for (long t = 1000; t <= 300_000; t += 1000)
        {
            Tick(timeline, t, t < 290_000 ? Baseline : Tuned, gameRunning: true);
        }

        var tuning = timeline.AttributeTo(Game, 300_000, Window30s);

        Assert.Equal(Tuned, tuning.Reading);
        Assert.True(tuning.ChangedDuringWindow);   // 20 s baseline + 10 s tuned in one window
    }

    [Fact]
    public void A_change_long_before_the_window_is_not_a_mix()
    {
        var timeline = new CaptureTuningTimeline();
        timeline.AddReading(0, Baseline);
        for (long t = 1000; t <= 300_000; t += 1000)
        {
            Tick(timeline, t, t < 100_000 ? Baseline : Tuned, gameRunning: true);
        }

        var tuning = timeline.AttributeTo(Game, 300_000, Window30s);

        Assert.Equal(Tuned, tuning.Reading);
        Assert.False(tuning.ChangedDuringWindow);
    }

    [Fact]
    public void A_process_whose_liveness_could_not_be_read_gets_the_tuning_its_window_began_with()
    {
        var timeline = new CaptureTuningTimeline();
        timeline.AddReading(0, Baseline);
        timeline.AddReading(8000, Tuned);
        timeline.AddReading(303_000, Baseline);              // after the stats: irrelevant to them

        var clean = timeline.AttributeTo(Game, 302_000, Window30s);
        Assert.Equal(Tuned, clean.Reading);
        Assert.False(clean.ChangedDuringWindow);

        // A change between the window's start and the stats can't be placed
        // against the process's exit: same label, but flagged rather than hidden.
        timeline = new CaptureTuningTimeline();
        timeline.AddReading(0, Baseline);
        timeline.AddReading(8000, Tuned);
        timeline.AddReading(301_000, Baseline);

        var unsure = timeline.AttributeTo(Game, 302_000, Window30s);
        Assert.Equal(Tuned, unsure.Reading);
        Assert.True(unsure.ChangedDuringWindow);
    }

    [Fact]
    public void Failed_reads_and_power_read_back_jitter_are_not_changes()
    {
        var timeline = new CaptureTuningTimeline();
        timeline.AddReading(0, Baseline);
        for (long t = 1000; t <= 300_000; t += 1000)
        {
            timeline.AddReading(t, t % 3000 == 0 ? null : Baseline with { PowerLimitW = 450.2 });
            timeline.ConfirmRunning(Game);
        }

        var tuning = timeline.AttributeTo(Game, 300_000, Window30s);

        Assert.Equal(Baseline, tuning.Reading);
        Assert.False(tuning.ChangedDuringWindow);
        Assert.False(Baseline.SameTuning(Baseline with { PowerLimitW = 400 }));
        Assert.False(Baseline.SameTuning(Baseline with { PowerLimitW = null }));
    }

    [Fact]
    public void Without_a_readable_card_there_is_no_tuning_to_report()
    {
        // FPS capture needs no supported GPU; the report keeps "—", not 0.
        var timeline = new CaptureTuningTimeline();
        timeline.AddReading(0, null);
        timeline.AddReading(1000, null);
        timeline.ConfirmRunning(Game);

        var tuning = timeline.AttributeTo(Game, 1000, Window30s);

        Assert.Null(tuning.Reading);
        Assert.False(tuning.ChangedDuringWindow);
    }

    [Fact]
    public void A_reading_carries_offsets_only_for_knobs_the_card_has()
    {
        // Arc: no offset knobs, a power limit and a frequency clamp.
        var arc = new StubTuner(new TuningCapabilities { SupportsPowerLimit = true, SupportsLockedCoreClock = true },
            (0, 0, 180.0, null, 2400u));
        Assert.Equal(new TuningReading(null, null, 180.0, null, 2400u), TuningReading.Read(arc));

        var nvidia = new StubTuner(new TuningCapabilities { SupportsCoreOffset = true, SupportsMemOffset = true },
            (150, 1000, 450.0, 0u, null));
        Assert.Equal(new TuningReading(150, 1000, 450.0, 0u, null), TuningReading.Read(nvidia));

        Assert.Null(TuningReading.Read(new StubTuner(new TuningCapabilities(), null)));
    }

    /// <summary>A tuner that only answers ReadCurrent (or throws, like a lost device).</summary>
    private sealed class StubTuner(
        TuningCapabilities caps,
        (int, int, double?, uint?, uint?)? current) : IGpuTuner
    {
        public TuningCapabilities Capabilities { get; } = caps;

        public string? GpuUuid => "STUB";

        public uint? AppliedLockMHz => null;

        public event Action<IGpuTuner>? StateWritten { add { } remove { } }

        public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz)
            ReadCurrent() => current ?? throw new InvalidOperationException("device lost");

        public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false) =>
            throw new NotSupportedException();

        public ApplyResult ResetToDefaults() => throw new NotSupportedException();

        public KnobResult ForceUnlock() => throw new NotSupportedException();

        public NvmlReturn RestoreTuningLock(uint lockMHz) => NvmlReturn.NotSupported;

        public NvmlReturn LockClockForProbe(uint clockMHz) => NvmlReturn.NotSupported;

        public NvmlReturn SetAllFansRaw(uint dutyPct) => NvmlReturn.NotSupported;

        public NvmlReturn SetFanRaw(uint coolerId, uint dutyPct) => NvmlReturn.NotSupported;

        public NvmlReturn RestoreAutoFansRaw() => NvmlReturn.NotSupported;

        public NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points)
        {
            points = [];
            return NvapiStatus.NotSupported;
        }

        public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex) =>
            throw new NotSupportedException();

        public KnobResult ClearVfPointOffsets() => throw new NotSupportedException();
    }
}
