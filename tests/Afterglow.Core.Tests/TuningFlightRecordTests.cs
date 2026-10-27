using Afterglow.Core.Diagnostics;
using Afterglow.Core.Profiles;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// R-43: the tuning a card was running when it died has to be in its flight
/// recording, not a minute-old sample of the two offsets. Each test drives a
/// tuner write through the production notification into a real recorder, then
/// reads the recording back while the recorder is still open — exactly what
/// the next launch sees after a crash, with no clean-shutdown marker.
/// </summary>
[Collection("AppPaths")]
public sealed class TuningFlightRecordTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);

    private readonly StoreScope _store = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afterglow-tuning-flight-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LogPath => Path.Combine(_dir, "current.log");

    /// <summary>Every line written so far, read while the recorder still holds the file — as after a crash.</summary>
    private List<string> Lines()
    {
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>The tuning markers written so far (text after the timestamp).</summary>
    private List<string> TuningMarkers()
    {
        var markers = new List<string>();
        foreach (string line in Lines())
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (line.StartsWith('#') && space > 1 && line.AsSpan(space + 1).StartsWith("offsets ", StringComparison.Ordinal))
            {
                markers.Add(line[(space + 1)..]);
            }
        }

        return markers;
    }

    /// <summary>Waits for the background writer to put a tuning marker matching <paramref name="match"/> on disk.</summary>
    private string WaitForMarker(Func<string, bool> match)
    {
        string? found = null;
        bool landed = SpinWait.SpinUntil(
            () => (found = TuningMarkers().LastOrDefault(match)) is not null, TimeSpan.FromSeconds(5));
        Assert.True(landed, "the tuning marker never reached the recording; markers: " + string.Join(" | ", TuningMarkers()));
        return found!;
    }

    private static GpuSnapshot Idle(DateTimeOffset at) => new()
    {
        Timestamp = at,
        DeviceIndex = 0,
        CoreClockMHz = 300,
        GpuTempC = 40,
        PowerW = 30,
        GpuUtilPct = 2,
    };

    /// <summary>Kernel-Power 41 with BugcheckCode 0 and a 6008, logged promptly: the hard-reset signature.</summary>
    private static CrashForensics.EventEvidence HardReset() => new(true, 0, true, 0, 0, 0);

    [Fact]
    public void A_lock_unconfirmed_since_a_sleep_is_recorded_as_unknown_not_none()
    {
        var tuner = new FakeTuner { UnconfirmedLock = 1800 };

        var state = TuningFlightSampler.Read(tuner, FakeTuner.Uuid);

        // "lock=none" here would let a crash on this card read as stock.
        Assert.False(state.LockRecorded);
        Assert.False(state.FullyRecorded);
        Assert.Null(state.LockedCoreClockMHz);

        // A lock applied since the wake is known again, whatever came before.
        var confirmed = new FakeTuner { UnconfirmedLock = null };
        Assert.True(TuningFlightSampler.Read(confirmed, FakeTuner.Uuid).LockRecorded);
    }

    [Fact]
    public void A_profile_applied_just_after_a_stock_sample_is_on_record_before_the_next_sample()
    {
        // The review's case: the periodic sample saw stock, a profile landed a
        // moment later, and the machine reset before the next sample. The
        // report then blamed power delivery "at stock settings".
        var tuner = new FakeTuner();
        using var recorder = new FlightRecorder(_dir);
        var sampler = new TuningFlightSampler(tuner, recorder, FakeTuner.Uuid);

        recorder.Record(Idle(T0));
        sampler.OnSnapshot(T0);
        WaitForMarker(m => m.Contains("core=0 mem=0", StringComparison.Ordinal));

        tuner.Apply(new TuningProfile { Name = "oc", CoreOffsetMHz = 150, MemOffsetMHz = 0 });
        recorder.Record(Idle(T0.AddSeconds(1)));

        // No further snapshot reaches the sampler: the write alone put it on record.
        string marker = WaitForMarker(m => m.Contains("core=150", StringComparison.Ordinal));
        Assert.Contains($"gpu={FakeTuner.Uuid}", marker, StringComparison.Ordinal);

        var session = FlightSession.Load(LogPath);
        Assert.NotNull(session);
        Assert.False(session.CleanShutdown);
        Assert.Equal(150, session.CoreOffsetMHz);
        Assert.Equal(FakeTuner.Uuid, session.Tuning?.GpuId);

        var evidence = CrashForensics.BuildEvidence(session, session.EndedAt!.Value, HardReset());
        var verdict = CrashClassifier.Classify(evidence);

        Assert.NotNull(verdict);
        Assert.Equal(CrashCause.HardReset, verdict.Cause);
        Assert.Contains("core offset", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+150", verdict.Interpretation, StringComparison.Ordinal);
        Assert.DoesNotContain("stock", verdict.Headline, StringComparison.OrdinalIgnoreCase);

        // The pasted report states the full tuning and the card it was read from.
        string report = CrashForensics.BuildReport(session, evidence, HardReset(), verdict, "GPU 0 — Fake");
        Assert.Contains("Offsets applied: +150 MHz core, +0 MHz memory", report, StringComparison.Ordinal);
        Assert.Contains("Clock lock applied by Afterglow: none", report, StringComparison.Ordinal);
        Assert.Contains("Per-point V/F edits: none", report, StringComparison.Ordinal);
        Assert.Contains($"Tuning recorded for: {FakeTuner.Uuid}", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lock_only_change_is_recorded_at_once_and_a_factory_ceiling_is_not_a_lock()
    {
        // The real Arc tuner on a card whose factory ceiling sits below the
        // domain maximum: ReadCurrent reports that ceiling as an observed
        // clamp, which must not make a stock session read as tuned.
        var dev = new FakeArcDevice { FactoryMax = 2200 };
        var tuner = ArcScenario.Tuner(dev);
        using var recorder = new FlightRecorder(_dir);
        var sampler = new TuningFlightSampler(tuner, recorder, ArcScenario.Uuid);

        sampler.Capture();
        WaitForMarker(m => m.Contains("lock=none", StringComparison.Ordinal));

        var applied = tuner.Apply(ArcScenario.Lock(1500));
        Assert.True(applied.AllSucceeded, applied.Summary);

        // Offsets unchanged (Arc has none): the old offsets-only schema could
        // not see this write at all, at any sampling rate.
        string marker = WaitForMarker(m => m.Contains("lock=1500", StringComparison.Ordinal));
        Assert.StartsWith("offsets core=0 mem=0 ", marker, StringComparison.Ordinal);
        Assert.Contains("vf=0", marker, StringComparison.Ordinal);

        var session = FlightSession.Load(LogPath)!;
        Assert.Equal(1500u, session.Tuning?.LockedCoreClockMHz);
        Assert.True(session.Tuning?.FullyRecorded);

        var evidence = CrashForensics.BuildEvidence(session, session.EndedAt!.Value, HardReset());
        Assert.True(evidence.TuningApplied);
        var verdict = CrashClassifier.Classify(evidence);

        Assert.NotNull(verdict);
        Assert.DoesNotContain("stock", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not implicated", verdict.Recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1500 MHz clock lock", verdict.Interpretation, StringComparison.Ordinal);
    }

    [Fact]
    public void Per_point_edits_are_recorded_and_a_raised_point_is_not_called_stock()
    {
        var tuner = new FakeTuner(vfPoints: true);
        using var recorder = new FlightRecorder(_dir);
        _ = new TuningFlightSampler(tuner, recorder, FakeTuner.Uuid);

        // A global +100 levels every slot to +100: that is the offset, not an edit.
        tuner.Apply(new TuningProfile { Name = "oc", CoreOffsetMHz = 100 });
        WaitForMarker(m => m.Contains("core=100", StringComparison.Ordinal) && m.Contains("vf=0", StringComparison.Ordinal));

        // Then a per-point edit with the global offset back at 0: two points
        // differ from it, the highest raised by 175. Point-only, like a V/F
        // page or `vfpoints --set` edit.
        tuner.Apply(new TuningProfile { Name = "stock" });
        tuner.SetVfPointOffsets(new Dictionary<int, int> { [4] = 175, [5] = -50 });
        string marker = WaitForMarker(m => m.Contains("vf=2", StringComparison.Ordinal));
        Assert.Contains("core=0 mem=0", marker, StringComparison.Ordinal);
        Assert.Contains("vfmax=175", marker, StringComparison.Ordinal);

        var session = FlightSession.Load(LogPath)!;
        var evidence = CrashForensics.BuildEvidence(session, session.EndedAt!.Value, HardReset());
        Assert.True(evidence.CoreClockRaised);

        var verdict = CrashClassifier.Classify(evidence);
        Assert.NotNull(verdict);
        Assert.Contains("V/F points", verdict.Headline, StringComparison.Ordinal);
        Assert.Contains("+175", verdict.Interpretation, StringComparison.Ordinal);
        Assert.DoesNotContain("stock", verdict.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unreadable_vf_table_is_recorded_as_unknown_not_as_no_edits()
    {
        var tuner = new FakeTuner(vfPoints: true) { VfReadFails = true };
        using var recorder = new FlightRecorder(_dir);
        var sampler = new TuningFlightSampler(tuner, recorder, FakeTuner.Uuid);

        sampler.Capture();
        string marker = WaitForMarker(m => m.Contains("lock=none", StringComparison.Ordinal));
        Assert.DoesNotContain("vf=", marker, StringComparison.Ordinal);

        var session = FlightSession.Load(LogPath)!;
        Assert.Null(session.Tuning?.VfPointsEdited);
        Assert.False(session.Tuning?.FullyRecorded);

        var verdict = CrashClassifier.Classify(
            CrashForensics.BuildEvidence(session, session.EndedAt!.Value, HardReset()));

        // Offsets at stock, but the recording cannot vouch for the curve.
        Assert.NotNull(verdict);
        Assert.DoesNotContain("stock settings", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not implicated", verdict.Recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not say", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tuning_changed_by_another_process_is_picked_up_on_a_fixed_period()
    {
        // The CLI and the MCP server write the card from their own process, so
        // no notification reaches this one; the periodic re-read is what sees
        // them. It is a wall-clock period now, not every 60th snapshot (ten
        // minutes at 10 s polling).
        var tuner = new FakeTuner();
        using var recorder = new FlightRecorder(_dir);
        var sampler = new TuningFlightSampler(tuner, recorder, FakeTuner.Uuid);

        sampler.OnSnapshot(T0);
        Assert.Equal(1, tuner.ReadCount);

        tuner.SetStateSilently(core: 90, mem: 1000);
        sampler.OnSnapshot(T0.AddSeconds(5));
        Assert.Equal(1, tuner.ReadCount); // within the period: no driver reads on the telemetry tick

        sampler.OnSnapshot(T0 + TuningFlightSampler.PollPeriod);
        Assert.Equal(2, tuner.ReadCount);
        WaitForMarker(m => m.Contains("core=90 mem=1000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_read_that_started_before_a_write_cannot_land_after_the_writes_own_capture()
    {
        // The telemetry thread's periodic read takes the stock state, then
        // stalls (a slow driver call). Meanwhile a profile is applied and its
        // own capture records +150. When the stalled read finally completes it
        // must not be recorded last — the recording would end on "stock" with
        // the card at +150, the exact misattribution R-43 is about.
        var tuner = new FakeTuner();
        using var recorder = new FlightRecorder(_dir);
        var sampler = new TuningFlightSampler(tuner, recorder, FakeTuner.Uuid);

        using var stalled = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int stalls = 0;
        tuner.AfterRead = () =>
        {
            if (Interlocked.Increment(ref stalls) == 1)
            {
                stalled.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var slowRead = Task.Run(sampler.Capture, TestContext.Current.CancellationToken);
        Assert.True(stalled.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        tuner.Apply(new TuningProfile { Name = "oc", CoreOffsetMHz = 150 });
        WaitForMarker(m => m.Contains("core=150", StringComparison.Ordinal));

        release.Set();
        await slowRead.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Anything the stalled capture enqueued is on disk before this marker.
        recorder.Marker("after-the-stalled-read");
        Assert.True(SpinWait.SpinUntil(
            () => Lines().Exists(l => l.EndsWith(" after-the-stalled-read", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)));

        Assert.Contains("core=150", TuningMarkers()[^1], StringComparison.Ordinal);
        Assert.Equal(150, FlightSession.Load(LogPath)!.CoreOffsetMHz);
    }
}
