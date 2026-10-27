using System.Diagnostics;
using Afterglow.Core.Diagnostics;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// The machinery behind <see cref="TuningWriteInFlightTests"/>: every tuning
/// write is bracketed by a start and a finish, the start reaches the disk
/// before the driver call within a bounded wait, a segment roll carries an
/// unfinished write over, and the parser pairs starts with ends by id.
/// </summary>
[Collection("AppPaths")]
public sealed class FlightWriteMarkerTests : IDisposable
{
    private readonly StoreScope _store = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afterglow-write-markers-" + Guid.NewGuid().ToString("N"));

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

    private static string Marker(long ms, string text) => $"#{ms} {text}";

    private static string Start(long id, string kind, string what) => $"write-start {id} {kind} {what}";

    // --- parsing ---------------------------------------------------------------

    [Fact]
    public void A_start_without_its_end_is_the_write_in_progress()
    {
        var session = FlightSession.Parse(
        [
            Marker(1_000, "offsets core=0 mem=0 lock=none vf=0"),
            Marker(2_000, Start(7, "apply", "applying profile 'oc' (core +150 MHz, no clock lock)")),
        ]);

        Assert.NotNull(session.WriteInProgress);
        Assert.Equal(7, session.WriteInProgress!.Id);
        Assert.Equal(TuningWriteKind.Apply, session.WriteInProgress.Kind);
        Assert.Equal("applying profile 'oc' (core +150 MHz, no clock lock)", session.WriteInProgress.What);
    }

    [Fact]
    public void Ends_close_their_own_starts_only()
    {
        var finished = FlightSession.Parse(
        [
            Marker(1_000, Start(7, "apply", "applying profile 'a' (no clock lock)")),
            Marker(1_010, "write-end 7"),
            Marker(1_020, "offsets core=150 mem=0 lock=none vf=0"),
        ]);
        Assert.Null(finished.WriteInProgress);

        // Another thread's end must not close this write.
        var crossed = FlightSession.Parse(
        [
            Marker(1_000, Start(7, "lock", "setting a 210..2400 MHz clock lock")),
            Marker(1_005, Start(8, "probe-pin", "pinning the core clock at 1800 MHz for the V/F probe")),
            Marker(1_010, "write-end 7"),
        ]);
        Assert.Equal(8, crossed.WriteInProgress?.Id);
    }

    [Fact]
    public void A_clean_shutdown_leaves_no_write_in_progress_and_an_unknown_kind_still_counts()
    {
        Assert.Null(FlightSession.Parse(
        [
            Marker(1_000, Start(3, "reset", "resetting the card to driver defaults")),
            Marker(2_000, "clean-shutdown"),
        ]).WriteInProgress);

        var future = FlightSession.Parse([Marker(1_000, Start(4, "fan-curve", "something newer"))]);
        Assert.NotNull(future.WriteInProgress);
        Assert.Null(future.WriteInProgress!.Kind);
    }

    [Fact]
    public void A_start_restated_by_a_segment_roll_is_one_write()
    {
        string start = Marker(1_000, Start(9, "lock", "releasing the clock lock"));
        var session = FlightSession.Parse([start, "#flight v1 continued=2000", start, Marker(2_100, "write-end 9")]);

        Assert.Null(session.WriteInProgress);
    }

    [Fact]
    public void A_profile_name_cannot_break_the_marker_line()
    {
        var write = new TuningWrite(5, TuningWriteKind.Apply, "applying profile 'two\nlines' (no clock lock)");

        string text = FlightWrite.StartMarkerText(write);

        Assert.DoesNotContain('\n', text);
        Assert.True(FlightWrite.TryParseStart(text, out var parsed));
        Assert.Equal(5, parsed.Id);
        Assert.Equal("applying profile 'two lines' (no clock lock)", parsed.What);
    }

    // --- the recorder ------------------------------------------------------------

    [Fact]
    public void A_durable_marker_is_written_before_the_call_returns()
    {
        using var recorder = new FlightRecorder(_dir);

        Assert.True(recorder.MarkerDurable("write-start 1 lock releasing the clock lock", TimeSpan.FromSeconds(5)));

        using var stream = new FileStream(recorder.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        Assert.Contains(" write-start 1 lock releasing the clock lock", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    private static List<string> WriteStarts(FlightRecorder recorder)
    {
        using var stream = new FileStream(recorder.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("write-start", StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    [Fact]
    public void A_stalled_disk_costs_one_bounded_wait_and_loses_nothing()
    {
        using var recorder = new FlightRecorder(_dir);
        using var gate = new ManualResetEventSlim(false);
        recorder.BeforeWrite = line =>
        {
            if (line.Contains("write-start 1 ", StringComparison.Ordinal))
            {
                gate.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var timeout = TimeSpan.FromMilliseconds(300);
        var clock = Stopwatch.StartNew();
        Assert.False(recorder.MarkerDurable("write-start 1 lock releasing the clock lock", timeout));
        var first = clock.Elapsed;

        clock.Restart();
        Assert.False(recorder.MarkerDurable("write-start 2 lock releasing the clock lock", timeout));
        var second = clock.Elapsed;

        Assert.InRange(first, timeout - TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
        Assert.True(second < timeout, $"the second write waited {second.TotalMilliseconds:F0} ms on a disk known to be behind");

        // Once the disk answers, both are written, in order, and waiting resumes.
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => WriteStarts(recorder).Count == 2, TimeSpan.FromSeconds(5)));
        Assert.True(recorder.MarkerDurable("write-start 3 lock releasing the clock lock", TimeSpan.FromSeconds(5)));
        var lines = WriteStarts(recorder);
        Assert.Equal(3, lines.Count);
        Assert.Contains("write-start 1 ", lines[0], StringComparison.Ordinal);
        Assert.Contains("write-start 3 ", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void An_unfinished_write_is_carried_into_the_next_segment()
    {
        using var recorder = new FlightRecorder(_dir, segmentBytes: 2048);
        Assert.True(recorder.MarkerDurable("write-start 11 apply applying profile 'oc' (core +150 MHz, no clock lock)", TimeSpan.FromSeconds(5)));

        // Enough telemetry to roll the segment while the write is still open.
        for (int i = 0; i < 100; i++)
        {
            recorder.Marker($"filler {i} ................................................");
        }

        Assert.True(recorder.MarkerDurable("probe-after-roll", TimeSpan.FromSeconds(5)));
        Assert.True(File.Exists(Path.Combine(_dir, "current.older.log")), "the segment never rolled");

        var session = FlightSession.Load(recorder.CurrentPath);
        Assert.Equal(11, session?.WriteInProgress?.Id);
    }

    // --- the tuners announce every write -------------------------------------------

    /// <summary>Records the bracket events and the driver calls in one sequence.</summary>
    private sealed class Trace
    {
        public List<string> Events { get; } = [];

        public void Watch(IGpuTuner tuner)
        {
            tuner.WriteStarting += (_, w) => Events.Add($"start:{w.Id}:{w.Kind}");
            tuner.WriteFinished += (_, w) => Events.Add($"end:{w.Id}");
            tuner.StateWritten += _ => Events.Add("written");
        }

        public void Driver(string what) => Events.Add("driver:" + what);
    }

    [Fact]
    public void Every_nvidia_tuning_write_is_bracketed_and_fan_writes_are_not()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = new GpuTuner(driver, driver, driver.Uuid);
        var trace = new Trace();
        trace.Watch(tuner);
        driver.OnClockOffsetWrite = () => trace.Driver("offset");
        driver.OnLockWrite = () => trace.Driver("lock");
        driver.OnVfpWrite = () => trace.Driver("vfp");

        _ = tuner.Apply(new TuningProfile { Name = "oc", CoreOffsetMHz = 100 });
        _ = tuner.SetVfPointOffsets(new Dictionary<int, int> { [30] = 150 });
        _ = tuner.ClearVfPointOffsets();
        _ = tuner.RestoreTuningLock(2400);
        _ = tuner.BeginProbe();
        _ = tuner.LockClockForProbe(1800);
        _ = tuner.EndProbe();
        _ = tuner.ForceUnlock();
        _ = tuner.ResetToDefaults();
        _ = tuner.SetAllFansRaw(60);
        _ = tuner.RestoreAutoFansRaw();

        var kinds = trace.Events.Where(e => e.StartsWith("start:", StringComparison.Ordinal))
            .Select(e => e.Split(':')[2]).ToList();
        Assert.Equal(
            ["Apply", "VfPoints", "VfPoints", "Lock", "ProbePin", "Lock", "Lock", "Reset"],
            kinds);
        AssertBracketed(trace.Events);
    }

    [Fact]
    public void Every_arc_tuning_write_is_bracketed()
    {
        var dev = new FakeArcDevice();
        var tuner = ArcScenario.Tuner(dev);
        var trace = new Trace();
        trace.Watch(tuner);
        dev.OnWrite = (min, max) => trace.Driver($"{min}..{max}");

        _ = tuner.Apply(ArcScenario.Lock(1500));
        _ = tuner.BeginProbe();               // releases the 1500 clamp first
        _ = tuner.LockClockForProbe(1800);
        _ = tuner.EndProbe();                 // puts the 1500 clamp back
        _ = tuner.ForceUnlock();
        _ = tuner.RestoreTuningLock(1600);
        _ = tuner.ResetToDefaults();

        var kinds = trace.Events.Where(e => e.StartsWith("start:", StringComparison.Ordinal))
            .Select(e => e.Split(':')[2]).ToList();
        Assert.Equal(["Apply", "Lock", "ProbePin", "Lock", "Lock", "Lock", "Reset"], kinds);
        AssertBracketed(trace.Events);
    }

    /// <summary>Every driver call sits inside a start/end pair, every start is ended, and StateWritten comes after the end.</summary>
    private static void AssertBracketed(List<string> events)
    {
        string? open = null;
        foreach (string e in events)
        {
            if (e.StartsWith("start:", StringComparison.Ordinal))
            {
                Assert.Null(open);
                open = e.Split(':')[1];
            }
            else if (e.StartsWith("end:", StringComparison.Ordinal))
            {
                Assert.Equal(open, e.Split(':')[1]);
                open = null;
            }
            else if (e.StartsWith("driver:", StringComparison.Ordinal))
            {
                Assert.True(open is not null, $"a driver write outside any announced write: {string.Join(" ", events)}");
            }
            else
            {
                Assert.True(open is null, $"StateWritten before the write finished: {string.Join(" ", events)}");
            }
        }

        Assert.Null(open);
    }

    [Fact]
    public void A_write_that_throws_still_finishes()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = new GpuTuner(driver, driver, driver.Uuid);
        var trace = new Trace();
        trace.Watch(tuner);
        driver.OnLockWrite = () => throw new InvalidOperationException("driver fell over");

        _ = Assert.Throws<InvalidOperationException>(() => tuner.LockClockForProbe(1800));

        Assert.Equal(["start:" + trace.Events[0].Split(':')[1] + ":ProbePin", "end:" + trace.Events[0].Split(':')[1]], trace.Events);
    }

    [Fact]
    public void A_failing_subscriber_never_reaches_the_write()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = new GpuTuner(driver, driver, driver.Uuid);
        tuner.WriteStarting += (_, _) => throw new IOException("disk gone");

        Assert.Equal(NvmlReturn.Success, tuner.RestoreTuningLock(2400));
        Assert.Equal((210u, 2400u), driver.LockedClocks);
    }

    [Fact]
    public void A_write_nobody_listens_to_costs_no_id()
    {
        var scope = TuningWriteScope.Begin(new FakeTuner(), null, null, TuningWriteKind.Lock, "releasing the clock lock");
        scope.Dispose(); // nothing to raise, nothing to throw
        Assert.Equal(default, scope);
    }
}
