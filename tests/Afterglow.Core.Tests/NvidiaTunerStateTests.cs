using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// The NVIDIA tuner end to end over a fake driver that behaves as the measured
/// RTX 5090 does (<see cref="FakeNvidiaDriver"/>): a partial apply must not
/// level a per-point curve it has no opinion on (R-12), a direct V/F edit must
/// be on record before it reaches the driver (R-13), a clock-lock shadow must
/// not outlive the boot it was applied in (R-14), and a failed fan release
/// must not pass for "no fans to release" (R-08). The boot clock is the test's
/// own; AppPaths is redirected because every write here is recorded.
/// </summary>
[Collection("AppPaths")]
public sealed class NvidiaTunerStateTests : IDisposable
{
    private static readonly Dictionary<int, int> Shape = new() { [30] = 200, [31] = -100 };

    private readonly StoreScope _store = new();

    // Two hours into a boot, at a fixed wall-clock moment.
    private (DateTimeOffset UtcNow, long UptimeMs) _clock =
        (new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), (long)TimeSpan.FromHours(2).TotalMilliseconds);

    public NvidiaTunerStateTests() => BootIdentity.Clock = () => _clock;

    public void Dispose()
    {
        BootIdentity.Clock = BootIdentity.SystemClock;
        _store.Dispose();
    }

    private static GpuTuner Tuner(FakeNvidiaDriver driver) => new(driver, driver, driver.Uuid);

    private void Elapse(TimeSpan span) =>
        _clock = (_clock.UtcNow + span, _clock.UptimeMs + (long)span.TotalMilliseconds);

    private void Restart(TimeSpan downtime, TimeSpan upSince) =>
        _clock = (_clock.UtcNow + downtime + upSince, (long)upSince.TotalMilliseconds);

    /// <summary>What `set` and MCP apply_tuning build when the core offset is not given: the current values carried forward.</summary>
    private static TuningProfile CarriedForward(GpuTuner tuner, string name, double? powerW = null)
    {
        var current = tuner.ReadCurrent();
        return new TuningProfile
        {
            Name = name,
            CoreOffsetMHz = current.CoreOffsetMHz,
            MemOffsetMHz = current.MemOffsetMHz,
            PowerLimitW = powerW,
            LockedCoreClockMHz = tuner.AppliedLockMHz,
        };
    }

    /// <summary>A card at +100 MHz with a per-point curve on top: slots 30/31 shaped, the rest at the global offset.</summary>
    private static (FakeNvidiaDriver Driver, GpuTuner Tuner) ShapedCard()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Tuner(driver);
        Assert.True(tuner.Apply(new TuningProfile { Name = "base", CoreOffsetMHz = 100 }).AllSucceeded);
        Assert.True(tuner.SetVfPointOffsets(Shape).Applied);
        Assert.Equal([200, -100, 100, 100, 100], driver.TableOffsets);
        return (driver, tuner);
    }

    // --- R-12: partial applies and the shared V/F table ---------------------------

    [Fact]
    public void A_power_only_set_keeps_the_per_point_curve()
    {
        var (driver, tuner) = ShapedCard();
        int coreWrites = driver.CoreOffsetWrites;

        // `afterglow-cli set --power-limit 250`
        var result = tuner.Apply(CarriedForward(tuner, "cli", powerW: 250));

        Assert.True(result.AllSucceeded, result.Summary);
        Assert.Equal(250_000u, driver.PowerLimitMw);
        Assert.Equal([200, -100, 100, 100, 100], driver.TableOffsets);
        Assert.Equal(coreWrites, driver.CoreOffsetWrites);
        Assert.Contains("already in force", Assert.Single(result.Results, r => r.Knob == "core offset").Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_fan_only_apply_tuning_keeps_the_per_point_curve()
    {
        var (driver, tuner) = ShapedCard();

        // MCP apply_tuning {"fan": "auto"}: no knob given, every value carried forward.
        var result = tuner.Apply(CarriedForward(tuner, "mcp"));

        Assert.True(result.AllSucceeded, result.Summary);
        Assert.Equal([200, -100, 100, 100, 100], driver.TableOffsets);
    }

    [Fact]
    public void A_changed_core_offset_is_still_written()
    {
        var (driver, tuner) = ShapedCard();

        var result = tuner.Apply(new TuningProfile { Name = "cli", CoreOffsetMHz = 150 });

        Assert.True(result.AllSucceeded, result.Summary);
        Assert.Equal(150, driver.CoreOffsetMHz);

        // The levelling is the driver's (driver-apis.md, behaviour 2), not the tuner's.
        Assert.Equal([150, 150, 150, 150, 150], driver.TableOffsets);
    }

    [Fact]
    public void A_profile_that_recorded_a_stock_curve_still_removes_the_shape()
    {
        var (driver, tuner) = ShapedCard();

        var result = tuner.Apply(new TuningProfile { Name = "stock curve", CoreOffsetMHz = 100, CapturedVfPoints = true });

        Assert.True(result.AllSucceeded, result.Summary);
        Assert.Equal([100, 100, 100, 100, 100], driver.TableOffsets);
    }

    [Fact]
    public void A_profile_carrying_points_still_writes_its_own_curve()
    {
        var (driver, tuner) = ShapedCard();

        var result = tuner.Apply(new TuningProfile
        {
            Name = "curve",
            CoreOffsetMHz = 100,
            VfPointOffsetsMHz = new Dictionary<int, int> { [30] = 100, [31] = 100, [32] = 100, [33] = -40, [34] = -40 },
            CapturedVfPoints = true,
        });

        Assert.True(result.AllSucceeded, result.Summary);
        Assert.Equal([100, 100, 100, -40, -40], driver.TableOffsets);
    }

    [Fact]
    public void An_unreadable_core_offset_is_written_as_before()
    {
        var (driver, tuner) = ShapedCard();
        int coreWrites = driver.CoreOffsetWrites;
        driver.CoreOffsetReadRc = NvmlReturn.Unknown;

        // The tuner cannot tell whether the value is in force, so it writes it.
        var result = tuner.Apply(new TuningProfile { Name = "cli", CoreOffsetMHz = 100, PowerLimitW = 250 });

        Assert.True(result.AllSucceeded, result.Summary);
        Assert.Equal(coreWrites + 1, driver.CoreOffsetWrites);
    }

    // --- R-13: direct V/F edits are on record ------------------------------------

    [Fact]
    public void A_direct_point_edit_is_on_record_before_it_reaches_the_driver()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Tuner(driver);
        AppliedStateStore.AppliedState? atWrite = null;
        driver.OnVfpWrite = () => atWrite = AppliedStateStore.Load(driver.Uuid);

        Assert.True(tuner.SetVfPointOffsets(Shape).Applied);

        Assert.NotNull(atWrite);
        Assert.False(atWrite!.CleanShutdown);
        Assert.Equal(AppliedStateStore.VfPointEditName, atWrite.ProfileName);

        var after = AppliedStateStore.Load(driver.Uuid);
        Assert.NotNull(after);
        Assert.False(after!.CleanShutdown);
        Assert.True(after.AllKnobsSucceeded);
        Assert.Single(AppliedStateStore.LoadAll(), s => !s.CleanShutdown);
    }

    [Fact]
    public void An_edit_after_a_clean_mark_is_crash_evidence_again_and_keeps_the_lock()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Tuner(driver);
        Assert.True(tuner.Apply(new TuningProfile { Name = "Gaming", LockedCoreClockMHz = 2500 }).AllSucceeded);
        AppliedStateStore.MarkCleanShutdown();

        Assert.True(tuner.SetVfPointOffsets(Shape).Applied);

        var state = AppliedStateStore.Load(driver.Uuid);
        Assert.NotNull(state);
        Assert.False(state!.CleanShutdown);
        Assert.Equal(2500u, state.LockedCoreClockMHz);
    }

    [Fact]
    public void A_refused_driver_write_leaves_the_record_pending()
    {
        var driver = new FakeNvidiaDriver { VfpWriteRc = NvapiStatus.Error };
        var tuner = Tuner(driver);

        Assert.False(tuner.SetVfPointOffsets(Shape).Applied);

        var state = AppliedStateStore.Load(driver.Uuid);
        Assert.NotNull(state);
        Assert.False(state!.CleanShutdown);
        Assert.False(state.AllKnobsSucceeded);
    }

    [Fact]
    public void A_request_refused_before_writing_records_nothing()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Tuner(driver);

        // Slot 5 is not a live core point: refused whole, before any write.
        Assert.False(tuner.SetVfPointOffsets(new Dictionary<int, int> { [30] = -50, [5] = -50 }).Applied);

        Assert.Equal(0, driver.VfpWrites);
        Assert.Null(AppliedStateStore.Load(driver.Uuid));
    }

    // --- R-14: the clock-lock shadow and the boot it was applied in --------------

    [Fact]
    public void A_lock_applied_before_a_restart_is_not_adopted_or_written_back()
    {
        var before = new FakeNvidiaDriver();
        Assert.True(Tuner(before).Apply(new TuningProfile { Name = "lock", LockedCoreClockMHz = 2500 }).AllSucceeded);
        AppliedStateStore.MarkCleanShutdown();

        // Up longer now than the old boot was at the write, so only the
        // wall-clock side of the rule can tell the boots apart.
        Restart(downtime: TimeSpan.FromMinutes(1), upSince: TimeSpan.FromHours(5));
        var driver = new FakeNvidiaDriver(before.Uuid); // the driver dropped the lock at the restart
        var tuner = Tuner(driver);

        Assert.Null(tuner.AppliedLockMHz);
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);

        // An unrelated memory-only `set` must not resurrect it.
        var memoryOnly = CarriedForward(tuner, "cli") with { MemOffsetMHz = 500 };
        Assert.True(tuner.Apply(memoryOnly).AllSucceeded);
        Assert.Empty(driver.LockWrites);
        Assert.Null(AppliedStateStore.Load(driver.Uuid)!.LockedCoreClockMHz);
    }

    [Fact]
    public void A_restart_is_caught_by_the_uptime_even_when_the_wall_clock_went_back()
    {
        var before = new FakeNvidiaDriver();
        Assert.True(Tuner(before).Apply(new TuningProfile { Name = "lock", LockedCoreClockMHz = 2500 }).AllSucceeded);

        // A restart whose firmware clock reads three hours early (a dual-boot
        // RTC skew, a dead CMOS battery): dated by the wall clock alone, the
        // new boot would seem to predate the lock.
        Restart(downtime: TimeSpan.FromSeconds(30), upSince: TimeSpan.FromMinutes(10));
        _clock = (_clock.UtcNow - TimeSpan.FromHours(3), _clock.UptimeMs);

        Assert.Null(Tuner(new FakeNvidiaDriver(before.Uuid)).AppliedLockMHz);
    }

    [Fact]
    public void A_lock_applied_in_this_boot_is_adopted_after_an_app_restart()
    {
        var driver = new FakeNvidiaDriver();
        Assert.True(Tuner(driver).Apply(new TuningProfile { Name = "lock", LockedCoreClockMHz = 2500 }).AllSucceeded);
        AppliedStateStore.MarkCleanShutdown();

        Elapse(TimeSpan.FromDays(3));

        Assert.Equal(2500u, Tuner(driver).AppliedLockMHz);
    }

    [Fact]
    public void Later_writes_of_other_fields_do_not_re_date_a_stale_lock()
    {
        var before = new FakeNvidiaDriver();
        Assert.True(Tuner(before).Apply(new TuningProfile { Name = "lock", LockedCoreClockMHz = 2500 }).AllSucceeded);
        Restart(downtime: TimeSpan.FromMinutes(1), upSince: TimeSpan.FromHours(1));

        // This boot's fan control and probe share the record and keep its lock field.
        AppliedStateStore.RecordFans("fixed", 70, before.Uuid);
        AppliedStateStore.RecordProbeLockPending(before.Uuid);
        Assert.Equal(2500u, AppliedStateStore.Load(before.Uuid)!.LockedCoreClockMHz);

        Assert.Null(Tuner(new FakeNvidiaDriver(before.Uuid)).AppliedLockMHz);
    }

    [Fact]
    public void An_unstamped_lock_from_an_older_build_is_judged_by_when_its_record_was_written()
    {
        var bootedAt = _clock.UtcNow - TimeSpan.FromMilliseconds(_clock.UptimeMs);
        var driver = new FakeNvidiaDriver();

        // Last written the day before this boot: the lock cannot have survived.
        AppliedStateStore.WriteLegacyRecord(new AppliedStateStore.AppliedState(
            "Daily OC", bootedAt - TimeSpan.FromDays(1), true, true, 2700));
        Assert.Null(Tuner(driver).AppliedLockMHz);

        // Written within this boot: adopted, as before the stamp existed.
        AppliedStateStore.Clear(driver.Uuid);
        AppliedStateStore.WriteLegacyRecord(new AppliedStateStore.AppliedState(
            "Daily OC", bootedAt + TimeSpan.FromMinutes(30), true, true, 2700));
        Assert.Equal(2700u, Tuner(driver).AppliedLockMHz);
    }

    // --- R-08: a failed fan release is a failure ---------------------------------

    [Fact]
    public void A_lost_gpu_is_not_reported_as_a_card_without_fans()
    {
        var driver = new FakeNvidiaDriver { RestoreAutoFansRc = NvapiStatus.Error, NumFansRc = NvmlReturn.GpuIsLost };
        var tuner = Tuner(driver);

        Assert.Equal(NvmlReturn.GpuIsLost, tuner.RestoreAutoFansRaw());

        using var fans = new FanControlService(tuner);
        NvmlReturn? failed = null;
        fans.CommandFailed += rc => failed = rc;
        Assert.False(fans.SetAuto());
        Assert.Equal(NvmlReturn.GpuIsLost, failed);
    }

    [Fact]
    public void An_nvapi_failure_with_no_nvml_fallback_is_a_failed_release()
    {
        var driver = new FakeNvidiaDriver { RestoreAutoFansRc = NvapiStatus.Error, NumFansRc = NvmlReturn.NotSupported };

        var rc = Tuner(driver).RestoreAutoFansRaw();

        Assert.NotEqual(NvmlReturn.Success, rc);
        Assert.NotEqual(NvmlReturn.NotSupported, rc);
    }

    [Fact]
    public void No_fan_interface_on_either_side_is_still_nothing_to_release()
    {
        var driver = new FakeNvidiaDriver { RestoreAutoFansRc = NvapiStatus.NotSupported, NumFansRc = NvmlReturn.NotSupported };
        var tuner = Tuner(driver);

        Assert.Equal(NvmlReturn.NotSupported, tuner.RestoreAutoFansRaw());
        using var fans = new FanControlService(tuner);
        Assert.True(fans.SetAuto());
    }

    [Fact]
    public void The_nvml_fallback_releases_every_fan_when_nvapi_has_no_fan_interface()
    {
        var driver = new FakeNvidiaDriver { RestoreAutoFansRc = NvapiStatus.FunctionNotFound };

        Assert.Equal(NvmlReturn.Success, Tuner(driver).RestoreAutoFansRaw());
        Assert.Equal(2, driver.NvmlFanReleases);
    }

    [Fact]
    public void A_reset_whose_fan_release_failed_says_so_and_keeps_the_record()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Tuner(driver);
        Assert.True(tuner.Apply(new TuningProfile { Name = "Gaming", CoreOffsetMHz = 100 }).AllSucceeded);
        driver.RestoreAutoFansRc = NvapiStatus.Error;
        driver.NumFansRc = NvmlReturn.GpuIsLost;

        var result = tuner.ResetToDefaults();

        Assert.False(result.AllSucceeded);
        Assert.False(Assert.Single(result.Results, r => r.Knob == "fans").Applied);
        Assert.NotNull(AppliedStateStore.Load(driver.Uuid));
    }
}
