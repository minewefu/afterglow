using Afterglow.Core.Diagnostics;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// A crash or TDR caused by a tuning write itself (R-43's remaining limit).
/// The flight recorder learned of a write only once it returned, so a machine
/// that died inside the driver call left a recording that still stated the
/// tuning BEFORE the write — and the report blamed or cleared that. Each test
/// runs the real tuner over a fake driver and, from inside the driver call,
/// copies the recording exactly as the disk holds it at that instant: what the
/// next launch would read after a hard reset at that moment.
/// </summary>
[Collection("AppPaths")]
public sealed class TuningWriteInFlightTests : IDisposable
{
    private readonly StoreScope _store = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afterglow-write-in-flight-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>The recording as it stands, read while the recorder still holds the file.</summary>
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

    private void WaitFor(Func<string, bool> match)
    {
        Assert.True(
            SpinWait.SpinUntil(() => Lines().Any(match), TimeSpan.FromSeconds(5)),
            "the line never reached the recording; lines: " + string.Join(" | ", Lines()));
    }

    /// <summary>Kernel-Power 41 with BugcheckCode 0 and a 6008, logged promptly: the hard-reset signature.</summary>
    private static CrashForensics.EventEvidence HardReset() => new(true, 0, true, 0, 0, 0);

    /// <summary>A driver reset (Display 4101) near the end of the session.</summary>
    private static CrashForensics.EventEvidence Tdr() => new(true, null, false, 0, 1, 4);

    private static CrashVerdict Verdict(IReadOnlyList<string> recording, CrashForensics.EventEvidence events)
    {
        var session = FlightSession.Parse(recording);
        Assert.False(session.CleanShutdown);
        var verdict = CrashClassifier.Classify(CrashForensics.BuildEvidence(session, session.EndedAt!.Value, events));
        Assert.NotNull(verdict);
        return verdict;
    }

    private static GpuTuner Nvidia(FakeNvidiaDriver driver) => new(driver, driver, driver.Uuid);

    [Fact]
    public void A_hard_reset_inside_a_profile_apply_is_that_applys_not_a_stock_failure()
    {
        // Stock on record, then a +150 MHz profile whose offset write never
        // returns. The old recording said "stock", and the report cleared the
        // tuning: "Hard reset at stock settings… Afterglow's tuning is not implicated".
        var driver = new FakeNvidiaDriver();
        var tuner = Nvidia(driver);
        using var recorder = new FlightRecorder(_dir);
        var sampler = new TuningFlightSampler(tuner, recorder, driver.Uuid);
        sampler.Capture();
        WaitFor(l => l.Contains("offsets core=0 mem=0", StringComparison.Ordinal));

        List<string>? atDeath = null;
        driver.OnClockOffsetWrite = () => atDeath ??= Lines();
        Assert.True(tuner.Apply(new TuningProfile { Name = "oc", CoreOffsetMHz = 150, MemOffsetMHz = 500 }).AllSucceeded);
        Assert.NotNull(atDeath);

        var verdict = Verdict(atDeath!, HardReset());

        Assert.Equal(CrashCause.HardReset, verdict.Cause);
        Assert.Contains("while Afterglow was applying profile 'oc'", verdict.Headline, StringComparison.Ordinal);
        Assert.Contains("core +150 MHz", verdict.Interpretation, StringComparison.Ordinal);
        Assert.Contains("memory +500 MHz", verdict.Interpretation, StringComparison.Ordinal);
        Assert.Contains("No tuning was in force before the write", verdict.Interpretation, StringComparison.Ordinal);
        Assert.DoesNotContain("stock", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not implicated", verdict.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tdr_inside_a_v_f_probe_pin_names_the_pin()
    {
        var dev = new FakeArcDevice();
        var tuner = ArcScenario.Tuner(dev);
        using var recorder = new FlightRecorder(_dir);
        _ = new TuningFlightSampler(tuner, recorder, ArcScenario.Uuid);

        List<string>? atDeath = null;
        dev.OnWrite = (min, max) =>
        {
            if (min > 0 && min == max)
            {
                atDeath ??= Lines();
            }
        };
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1800));
        Assert.NotNull(atDeath);

        var verdict = Verdict(atDeath!, Tdr());

        Assert.Equal(CrashCause.DriverReset, verdict.Cause);
        Assert.Contains("TDR", verdict.Headline, StringComparison.Ordinal);
        Assert.Contains("pinning the core clock at 1800 MHz for the V/F probe", verdict.Headline, StringComparison.Ordinal);
        Assert.Contains("V/F probe", verdict.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_crash_inside_a_reset_names_the_reset_and_suspects_what_it_was_undoing()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Nvidia(driver);
        using var recorder = new FlightRecorder(_dir);
        _ = new TuningFlightSampler(tuner, recorder, driver.Uuid);
        Assert.True(tuner.Apply(new TuningProfile { Name = "oc", CoreOffsetMHz = 120 }).AllSucceeded);
        WaitFor(l => l.Contains("offsets core=120", StringComparison.Ordinal));

        List<string>? atDeath = null;
        driver.OnClockOffsetWrite = () => atDeath ??= Lines();
        _ = tuner.ResetToDefaults();
        Assert.NotNull(atDeath);

        var verdict = Verdict(atDeath!, HardReset());

        Assert.Contains("while Afterglow was resetting the card to driver defaults", verdict.Headline, StringComparison.Ordinal);
        Assert.Contains("In force before the write: +120 MHz core", verdict.Interpretation, StringComparison.Ordinal);
        Assert.Contains("tuning in force before it is the likelier suspect", verdict.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_crash_inside_a_direct_v_f_edit_names_the_points()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Nvidia(driver);
        using var recorder = new FlightRecorder(_dir);
        _ = new TuningFlightSampler(tuner, recorder, driver.Uuid);

        List<string>? atDeath = null;
        driver.OnVfpWrite = () => atDeath ??= Lines();
        Assert.True(tuner.SetVfPointOffsets(new Dictionary<int, int> { [30] = 200, [31] = -100 }).Applied);
        Assert.NotNull(atDeath);

        var verdict = Verdict(atDeath!, HardReset());

        Assert.Contains("writing per-point V/F offsets (2 V/F points, highest +200 MHz)", verdict.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_that_returned_is_not_blamed_for_a_crash_after_it()
    {
        // The other half of the rule: once a write has returned, a crash is
        // read against the tuning it left — never as "during" it, however soon.
        var driver = new FakeNvidiaDriver();
        var tuner = Nvidia(driver);
        using var recorder = new FlightRecorder(_dir);
        _ = new TuningFlightSampler(tuner, recorder, driver.Uuid);

        Assert.True(tuner.Apply(new TuningProfile { Name = "oc", CoreOffsetMHz = 150 }).AllSucceeded);
        WaitFor(l => l.Contains("offsets core=150", StringComparison.Ordinal));

        var verdict = Verdict(Lines(), HardReset());

        Assert.DoesNotContain("while Afterglow was", verdict.Headline, StringComparison.Ordinal);
        Assert.Contains("core offset", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+150", verdict.Interpretation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_that_returned_and_was_superseded_is_not_the_one_named()
    {
        // Two writes; the machine dies inside the second. The first finished
        // and was superseded: its values are what was in force, not what was
        // being written.
        var driver = new FakeNvidiaDriver();
        var tuner = Nvidia(driver);
        using var recorder = new FlightRecorder(_dir);
        _ = new TuningFlightSampler(tuner, recorder, driver.Uuid);
        Assert.True(tuner.Apply(new TuningProfile { Name = "first", CoreOffsetMHz = 90 }).AllSucceeded);
        WaitFor(l => l.Contains("offsets core=90", StringComparison.Ordinal));

        List<string>? atDeath = null;
        driver.OnLockWrite = () => atDeath ??= Lines();
        Assert.Equal(NvmlReturn.Success, tuner.RestoreTuningLock(2400));
        Assert.NotNull(atDeath);

        var verdict = Verdict(atDeath!, HardReset());

        Assert.Contains("while Afterglow was setting a 210..2400 MHz clock lock", verdict.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("'first'", verdict.Headline, StringComparison.Ordinal);
        Assert.Contains("In force before the write: +90 MHz core", verdict.Interpretation, StringComparison.Ordinal);
        Assert.Contains("clock-lock change", verdict.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_written_report_names_the_unfinished_write_and_dates_the_tuning_before_it()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Nvidia(driver);
        using var recorder = new FlightRecorder(_dir);
        _ = new TuningFlightSampler(tuner, recorder, driver.Uuid);

        List<string>? atDeath = null;
        driver.OnClockOffsetWrite = () => atDeath ??= Lines();
        Assert.True(tuner.Apply(new TuningProfile { Name = "oc", CoreOffsetMHz = 150 }).AllSucceeded);

        var session = FlightSession.Parse(atDeath!);
        var evidence = CrashForensics.BuildEvidence(session, session.EndedAt!.Value, HardReset());
        var verdict = CrashClassifier.Classify(evidence)!;
        string report = CrashForensics.BuildReport(session, evidence, HardReset(), verdict, "GPU 0 — Fake");

        Assert.Contains("Unfinished tuning write: applying profile 'oc'", report, StringComparison.Ordinal);
        Assert.Contains("the settings above are from before it", report, StringComparison.Ordinal);
    }
}
