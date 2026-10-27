using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// The NVIDIA all-fans write over <see cref="FakeNvidiaDriver"/>. NVAPI writes
/// every cooler in one call; when it can't, the tuner falls back to NVML, one
/// fan per call, where fan 0 can take a manual duty and fan 1 refuse it. R-06
/// made the fan service treat any refusal as "possibly manual", but only in
/// memory: a hard crash afterwards left no record and no banner, and a refusal
/// where nothing moved could still end in a release the driver refused and an
/// unclean record for fans nobody touched. The write is now all-or-nothing
/// where the driver allows it, and on record at once where it does not.
/// </summary>
[Collection("AppPaths")]
public sealed class NvidiaFanWriteTests : IDisposable
{
    private readonly StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    private static GpuTuner Tuner(FakeNvidiaDriver driver) => new(driver, driver, driver.Uuid);

    /// <summary>A card whose NVAPI fan interface is missing, so every write goes through NVML's per-fan fallback.</summary>
    private static FakeNvidiaDriver NvmlOnly() => new() { SetAllFansRc = NvapiStatus.NotSupported };

    [Fact]
    public void A_per_fan_write_one_fan_refused_is_rolled_back_so_no_fan_is_left_on_it()
    {
        // Fan 0 stopped, fan 1 refused: the call reported failure while fan 0
        // stayed stopped. It is handed back to firmware control before the
        // call returns, so the refusal means what every caller reads it as.
        var driver = NvmlOnly();
        driver.FanSetRcs[1] = NvmlReturn.NoPermission;

        Assert.Equal(NvmlReturn.NoPermission, Tuner(driver).SetAllFansRaw(0));

        Assert.Empty(driver.ManualFans);
        Assert.Null(AppliedStateStore.Load(driver.Uuid));   // nothing left behind, nothing to answer for
    }

    [Fact]
    public void A_partial_write_that_cannot_be_rolled_back_is_on_record_before_anything_else_runs()
    {
        // The hand-back is refused too, so fan 0 stays stopped. No exit runs
        // in this test: what is on disk now is what a hard crash would leave,
        // and the next launch must find an unclean fan record in it.
        var driver = NvmlOnly();
        driver.FanSetRcs[1] = NvmlReturn.NoPermission;
        driver.DefaultFanSpeedRc = NvmlReturn.Unknown;

        Assert.NotEqual(NvmlReturn.Success, Tuner(driver).SetAllFansRaw(0));

        Assert.Equal(0u, driver.ManualFans[0]);
        var record = AppliedStateStore.Load(driver.Uuid);
        Assert.NotNull(record);
        Assert.Equal("fixed", record.FanMode);
        Assert.Equal(0u, record.FanDuty);
        Assert.False(record.CleanShutdown);
    }

    [Fact]
    public void The_fan_service_releases_at_exit_what_a_failed_rollback_left_behind()
    {
        var driver = NvmlOnly();
        driver.FanSetRcs[1] = NvmlReturn.NoPermission;
        driver.DefaultFanSpeedRc = NvmlReturn.Unknown;
        using var fans = new FanControlService(Tuner(driver));

        Assert.False(fans.SetFixed(0));
        Assert.False(AppliedStateStore.Load(driver.Uuid)!.CleanShutdown);

        // NVAPI can still hand every cooler back at exit.
        Assert.True(fans.ReleaseForShutdown());
        Assert.Empty(driver.ManualFans);
    }

    [Fact]
    public void A_write_no_fan_took_leaves_nothing_to_release_and_no_false_record()
    {
        // Not elevated: the driver refuses the write on fan 0 and refuses the
        // release as well. Nothing moved, so there is nothing to hand back. The
        // service used to try anyway, got the refusal, and recorded fans "left
        // in manual control" that the next launch's banner then warned about.
        var driver = NvmlOnly();
        driver.FanSetRcs[0] = NvmlReturn.NoPermission;
        driver.FanSetRcs[1] = NvmlReturn.NoPermission;
        driver.RestoreAutoFansRc = NvapiStatus.Error;
        driver.DefaultFanSpeedRc = NvmlReturn.NoPermission;
        using var fans = new FanControlService(Tuner(driver));

        Assert.False(fans.SetFixed(50));

        Assert.True(fans.ReleaseForShutdown());
        Assert.Equal(0, driver.NvmlFanReleases);
        Assert.Null(AppliedStateStore.Load(driver.Uuid));
    }

    [Fact]
    public void A_card_that_reports_no_fans_takes_no_fixed_duty_and_owes_no_release()
    {
        // NVML answering "0 fans" is no fan interface, as the release already
        // reads it. The write used to report success for it: the page said
        // "Fixed 50% applied", and at exit the release's "nothing to release"
        // counted as a failed hand-back of a takeover, so the next launch warned.
        var driver = NvmlOnly();
        driver.NumFans = 0;
        driver.RestoreAutoFansRc = NvapiStatus.NotSupported;
        using var fans = new FanControlService(Tuner(driver));

        Assert.False(fans.SetFixed(50));

        Assert.True(fans.ReleaseForShutdown());
        Assert.Null(AppliedStateStore.Load(driver.Uuid));
    }

    [Fact]
    public void A_rolled_back_write_still_leaves_the_earlier_duty_to_release()
    {
        // The rollback undoes only the write that failed, handing its fan to
        // firmware control; fan 1 still holds the earlier accepted duty, so the
        // service still owns a takeover and releases it at exit.
        var driver = NvmlOnly();
        var tuner = Tuner(driver);
        using var fans = new FanControlService(tuner);
        Assert.True(fans.SetFixed(60));

        driver.FanSetRcs[1] = NvmlReturn.Unknown;
        Assert.False(fans.SetFixed(90));
        Assert.Equal(new Dictionary<uint, uint> { [1] = 60 }, driver.ManualFans);

        Assert.True(fans.ReleaseForShutdown());
        Assert.Empty(driver.ManualFans);
    }

    [Fact]
    public void The_nvapi_all_fans_write_is_one_call_with_no_per_fan_fallback()
    {
        var driver = new FakeNvidiaDriver();
        using var fans = new FanControlService(Tuner(driver));

        Assert.True(fans.SetFixed(50));

        Assert.Equal(new Dictionary<uint, uint> { [0] = 50, [1] = 50 }, driver.ManualFans);
        Assert.Empty(driver.FanSetCalls);
        Assert.Equal(50u, AppliedStateStore.Load(driver.Uuid)!.FanDuty);
        Assert.True(fans.ReleaseForShutdown());
        Assert.Empty(driver.ManualFans);
    }

    [Fact]
    public void A_per_fan_write_every_fan_took_is_a_success_with_nothing_rolled_back()
    {
        var driver = NvmlOnly();

        Assert.Equal(NvmlReturn.Success, Tuner(driver).SetAllFansRaw(10));

        // Raised to the card's 30 % minimum spin duty, on both fans.
        Assert.Equal(new Dictionary<uint, uint> { [0] = 30, [1] = 30 }, driver.ManualFans);
        Assert.Equal(0, driver.NvmlFanReleases);
    }
}
