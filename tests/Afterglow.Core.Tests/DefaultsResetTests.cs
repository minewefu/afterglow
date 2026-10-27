using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Returning cards to driver defaults (Tuning page Reset, panic, driver crash):
/// the fan service is part of the reset, so a curve cannot re-arm on the next
/// tick (R-09), and every message is derived from what the driver answered —
/// a refused write is named, never announced as a clean reset (R-10). Fan
/// services run through their driver-free test seam; AppPaths is redirected
/// because they record applied state.
/// </summary>
[Collection("AppPaths")]
public sealed class DefaultsResetTests : IDisposable
{
    private readonly Fakes.StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    /// <summary>One card's driver: fan writes, and every release in order with who asked for it.</summary>
    private sealed class FakeDriver
    {
        public List<uint> FanWrites { get; } = [];

        public List<string> Releases { get; } = [];

        /// <summary>Answers to successive releases; Success once they run out.</summary>
        public Queue<NvmlReturn> ReleaseAnswers { get; } = new();

        public NvmlReturn SetAllFans(uint duty)
        {
            FanWrites.Add(duty);
            return NvmlReturn.Success;
        }

        public NvmlReturn Release(string who)
        {
            Releases.Add(who);
            return ReleaseAnswers.Count > 0 ? ReleaseAnswers.Dequeue() : NvmlReturn.Success;
        }
    }

    private static FanControlService Service(FakeDriver driver) =>
        new(driver.SetAllFans, () => driver.Release("service"), "GPU-reset-test", 30);

    /// <summary>
    /// A tuner whose reset answers as told and, like the NVIDIA tuner, releases
    /// the fans itself and reports that as the "fans" knob.
    /// </summary>
    private sealed class ResetTuner(FakeDriver driver, params KnobResult[] knobs) : IGpuTuner
    {
        public TuningCapabilities Capabilities { get; } = new() { SupportsCoreOffset = true, SupportsFanControl = true };

        public string? GpuUuid => "GPU-reset-test";

        public uint? AppliedLockMHz => null;

        public event Action<IGpuTuner>? StateWritten { add { } remove { } }

        public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz)
            ReadCurrent() => (0, 0, null, null, null);

        public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false) =>
            new(true, []);

        public ApplyResult ResetToDefaults()
        {
            var results = knobs.ToList();
            var rc = driver.Release("tuner");
            results.Add(rc == NvmlReturn.Success ? KnobResult.Ok("fans", "auto") : KnobResult.Fail("fans", rc.ToString()));
            return new ApplyResult(results.All(r => r.Applied), results);
        }

        public KnobResult ForceUnlock() => KnobResult.Ok("clock lock");

        public NvmlReturn RestoreTuningLock(uint lockMHz) => NvmlReturn.NotSupported;

        public NvmlReturn LockClockForProbe(uint clockMHz) => NvmlReturn.NotSupported;

        public NvmlReturn SetAllFansRaw(uint dutyPct) => driver.SetAllFans(dutyPct);

        public NvmlReturn SetFanRaw(uint coolerId, uint dutyPct) => NvmlReturn.NotSupported;

        public NvmlReturn RestoreAutoFansRaw() => driver.Release("tuner");

        public NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points)
        {
            points = [];
            return NvapiStatus.NotSupported;
        }

        public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex) =>
            KnobResult.Fail("V/F points", "stub");

        public KnobResult ClearVfPointOffsets() => KnobResult.Fail("V/F points", "stub");
    }

    private static readonly FanCurveConfig Curve = new()
    {
        Points = [new FanPoint(30, 30), new FanPoint(60, 45), new FanPoint(80, 70), new FanPoint(90, 100)],
        ZeroRpmBelowC = 0,
    };

    private static GpuSnapshot At(uint tempC) =>
        new() { Timestamp = DateTimeOffset.Now, DeviceIndex = 0, GpuTempC = tempC };

    private static CardResetResult Card(uint index, bool? fanServiceReleased, params KnobResult[] knobs) =>
        new(index, new ApplyResult(knobs.All(k => k.Applied), knobs), fanServiceReleased);

    [Fact]
    public void A_reset_disarms_the_fan_curve_so_the_next_tick_cannot_re_pin_the_fans()
    {
        // R-09: the Tuning page's Reset called only the tuner. The driver went
        // back to firmware control, the service stayed in Curve mode, and the
        // next hot snapshot commanded a manual duty again.
        var driver = new FakeDriver();
        var fans = Service(driver);
        fans.SetCurve(Curve);
        fans.OnSnapshot(At(65));
        Assert.Single(driver.FanWrites);

        var reset = DefaultsReset.Card(0, new ResetTuner(driver, KnobResult.Ok("core offset", "0 MHz")), fans);
        fans.OnSnapshot(At(83));

        Assert.Equal(FanControlMode.Auto, fans.Mode);
        Assert.Single(driver.FanWrites); // nothing commanded after the reset
        Assert.True(reset.AllReturned);
        Assert.True(reset.FansReturned);
    }

    [Fact]
    public void The_fan_service_is_released_before_the_tuner_resets()
    {
        // The order is what makes "either release landed" mean the fans are
        // free: with the curve disarmed first, nothing re-pins them between.
        var driver = new FakeDriver();
        _ = DefaultsReset.Card(0, new ResetTuner(driver), Service(driver));

        Assert.Equal(["service", "tuner"], driver.Releases);
    }

    [Fact]
    public void A_refused_knob_is_reported_as_possibly_still_applied_never_as_a_clean_reset()
    {
        // R-10: a fake tuner kept +500 MHz and answered AllSucceeded=false, and
        // the panic reset still said "all tuning returned to driver defaults".
        var driver = new FakeDriver();
        var reset = DefaultsReset.Card(
            0, new ResetTuner(driver, KnobResult.Fail("core offset", "driver refused")), Service(driver));

        Assert.False(reset.AllReturned);
        Assert.Equal(["core offset"], reset.NotReturned);
        Assert.False(reset.AsApplyResult().AllSucceeded);

        string panic = DefaultsReset.PanicMessage([reset], stopped: true, nameCards: false);
        Assert.DoesNotContain("all tuning returned", panic, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Panic reset incomplete.", panic, StringComparison.Ordinal);
        Assert.Contains("core offset", panic, StringComparison.Ordinal);
        Assert.Contains("Everything else returned to driver defaults.", panic, StringComparison.Ordinal); // the fans did

        string banner = DefaultsReset.DriverResetBanner([reset], nameCards: false, "nvlddmkm stopped responding");
        Assert.DoesNotContain("returned all tuning", banner, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("may still be applied", banner, StringComparison.Ordinal);
        Assert.Contains("core offset", banner, StringComparison.Ordinal);
        Assert.EndsWith("nvlddmkm stopped responding", banner, StringComparison.Ordinal);

        string tray = DefaultsReset.DriverResetTray([reset], nameCards: false);
        Assert.NotEqual("Tuning was reset to driver defaults for safety.", tray);
        Assert.Contains("core offset", tray, StringComparison.Ordinal);

        string automation = DefaultsReset.AutomationAction([reset], nameCards: false);
        Assert.DoesNotContain("all tuning reset", automation, StringComparison.Ordinal);
        Assert.Contains("core offset", automation, StringComparison.Ordinal);
    }

    [Fact]
    public void Fans_whose_release_both_paths_refused_are_named()
    {
        var driver = new FakeDriver();
        driver.ReleaseAnswers.Enqueue(NvmlReturn.NoPermission); // the service's release
        driver.ReleaseAnswers.Enqueue(NvmlReturn.NoPermission); // the tuner's
        var fans = Service(driver);
        Assert.True(fans.SetFixed(80));

        var reset = DefaultsReset.Card(0, new ResetTuner(driver, KnobResult.Ok("core offset", "0 MHz")), fans);

        Assert.False(reset.FansReturned);
        Assert.Equal(["fans"], reset.NotReturned);
        string panic = DefaultsReset.PanicMessage([reset], stopped: true, nameCards: false);
        Assert.Contains("manual fan control", panic, StringComparison.Ordinal);
        Assert.DoesNotContain("all tuning returned", panic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false)] // the service's release landed, the tuner's was refused
    [InlineData(false, true)] // the service's was refused, the tuner's landed
    public void Fans_are_free_when_either_release_landed(bool serviceLands, bool tunerLands)
    {
        var driver = new FakeDriver();
        driver.ReleaseAnswers.Enqueue(serviceLands ? NvmlReturn.Success : NvmlReturn.Unknown);
        driver.ReleaseAnswers.Enqueue(tunerLands ? NvmlReturn.Success : NvmlReturn.Unknown);

        var reset = DefaultsReset.Card(0, new ResetTuner(driver, KnobResult.Ok("core offset", "0 MHz")), Service(driver));

        Assert.True(reset.FansReturned);
        Assert.True(reset.AllReturned);
        Assert.Single(reset.Results, r => r.Knob == "fans");
        Assert.Equal("Panic reset: all tuning returned to driver defaults.",
            DefaultsReset.PanicMessage([reset], stopped: true, nameCards: false));
    }

    [Fact]
    public void A_refused_service_release_is_named_on_a_card_whose_tuner_says_nothing_about_fans()
    {
        // A tuner that does not own the fans (Arc) reports no "fans" knob; a
        // release the service had accepted adds none, a refused one must.
        var refused = Card(0, fanServiceReleased: false, KnobResult.Ok("clock lock", "released"));
        Assert.False(refused.AllReturned);
        Assert.Equal(["fans"], refused.NotReturned);

        var accepted = Card(0, fanServiceReleased: true, KnobResult.Ok("clock lock", "released"));
        Assert.True(accepted.AllReturned);
        Assert.DoesNotContain(accepted.Results, r => r.Knob == "fans");
        Assert.Equal("clock lock: ok (released)", accepted.AsApplyResult().Summary);

        var noService = Card(0, fanServiceReleased: null, KnobResult.Ok("clock lock", "released"));
        Assert.Null(noService.FansReturned);
    }

    [Fact]
    public void Every_message_keeps_its_wording_when_everything_returned()
    {
        var clean = Card(0, fanServiceReleased: true, KnobResult.Ok("core offset", "0 MHz"), KnobResult.Ok("fans", "auto"));

        Assert.Equal("Panic reset: all tuning returned to driver defaults.",
            DefaultsReset.PanicMessage([clean], stopped: true, nameCards: false));
        Assert.Equal(
            "Panic reset: tuning returned to driver defaults, but a stability run or V/F probe was still stopping " +
            "and may re-apply settings. Check the Stability page.",
            DefaultsReset.PanicMessage([clean], stopped: false, nameCards: false));
        Assert.Equal(
            "A GPU driver reset (TDR) was detected — Afterglow returned all tuning to driver defaults. If you were " +
            "testing an overclock, it is likely unstable. TDR at 12:00",
            DefaultsReset.DriverResetBanner([clean], nameCards: false, "TDR at 12:00"));
        Assert.Equal("Tuning was reset to driver defaults for safety.", DefaultsReset.DriverResetTray([clean], nameCards: false));
        Assert.Equal("all tuning reset to driver defaults", DefaultsReset.AutomationAction([clean], nameCards: false));
    }

    [Fact]
    public void On_a_multi_gpu_machine_each_card_is_named_and_only_its_own_knobs_listed()
    {
        var gpu0 = Card(0, fanServiceReleased: false, KnobResult.Ok("core offset", "0 MHz"));
        var gpu1 = Card(1, fanServiceReleased: true,
            KnobResult.Fail("core offset", "driver refused"), KnobResult.Fail("power limit", "driver refused"));

        Assert.Equal("GPU 0 manual fan control; GPU 1 core offset, power limit",
            DefaultsReset.NotReturnedList([gpu0, gpu1], nameCards: true));

        string panic = DefaultsReset.PanicMessage([gpu0, gpu1], stopped: false, nameCards: true);
        Assert.Contains("GPU 1 core offset, power limit", panic, StringComparison.Ordinal);
        Assert.Contains("Everything else returned to driver defaults.", panic, StringComparison.Ordinal);
        Assert.Contains("still stopping", panic, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reset_where_nothing_landed_does_not_claim_everything_else_did()
    {
        var nothing = Card(0, fanServiceReleased: false, KnobResult.Fail("core offset", "NoPermission"));

        string panic = DefaultsReset.PanicMessage([nothing], stopped: true, nameCards: false);
        Assert.DoesNotContain("Everything else", panic, StringComparison.Ordinal);
        Assert.Contains("core offset, manual fan control", panic, StringComparison.Ordinal);
    }
}
