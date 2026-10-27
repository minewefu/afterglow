using Afterglow.Core;
using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Mode handling in FanControlService, driven through its internal test seam
/// (the two driver calls as delegates) so no GPU is involved. Each test
/// redirects AppPaths into a throwaway directory, like the applied-state tests.
/// </summary>
[Collection("AppPaths")]
public sealed class FanControlServiceTests : IDisposable
{
    private const string Uuid = "GPU-ffff1111-2222-3333-4444-555566667777";

    private readonly Fakes.StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    /// <summary>
    /// Records what the service asked the driver to do, and answers as told.
    /// With <see cref="SetResult"/> at Success it models the NVIDIA per-cooler
    /// fallback: each cooler is written in turn, a refusing cooler does not stop
    /// the ones before it from changing, and the call reports the worst result.
    /// </summary>
    private sealed class FakeDriver
    {
        public List<uint> SetCalls { get; } = [];

        public int RestoreCalls { get; private set; }

        public NvmlReturn SetResult { get; set; } = NvmlReturn.Success;

        public NvmlReturn RestoreResult { get; set; } = NvmlReturn.Success;

        /// <summary>Each cooler's manual duty; null = firmware (auto) control.</summary>
        public uint?[] Coolers { get; } = new uint?[2];

        /// <summary>Coolers from this index on refuse a manual duty (R-06's partial write).</summary>
        public int RefuseFromCooler { get; set; } = int.MaxValue;

        /// <summary>When set, a fan write signals <see cref="SetEntered"/> and then blocks until this opens (R-07).</summary>
        public ManualResetEventSlim? HoldSet { get; set; }

        public ManualResetEventSlim? SetEntered { get; set; }

        public NvmlReturn SetAllFans(uint duty)
        {
            SetCalls.Add(duty);
            if (HoldSet is { } hold)
            {
                SetEntered?.Set();
                hold.Wait(TimeSpan.FromSeconds(30));
            }

            if (SetResult != NvmlReturn.Success)
            {
                return SetResult;
            }

            var worst = NvmlReturn.Success;
            for (int cooler = 0; cooler < Coolers.Length; cooler++)
            {
                if (cooler >= RefuseFromCooler)
                {
                    worst = NvmlReturn.Unknown;
                    continue;
                }

                Coolers[cooler] = duty;
            }

            return worst;
        }

        public NvmlReturn RestoreAuto()
        {
            RestoreCalls++;
            if (RestoreResult == NvmlReturn.Success)
            {
                Array.Clear(Coolers);
            }

            return RestoreResult;
        }
    }

    private static FanCurveConfig LinearCurve() => new()
    {
        Points = [new(40, 40), new(80, 100)],
        ZeroRpmBelowC = 0,
        HysteresisC = 0,
        MinSpinDutyPct = 30,
    };

    private static FanControlService Service(FakeDriver driver) =>
        new(driver.SetAllFans, driver.RestoreAuto, Uuid, 30);

    private static GpuSnapshot Snapshot(uint tempC) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        DeviceIndex = 0,
        GpuTempC = tempC,
    };

    [Fact]
    public void SetAuto_releases_fans_this_instance_never_took_over()
    {
        // The card can be in manual mode from a path that never went through this
        // service: the per-fan buttons, `afterglow-cli set --fan`, or an unclean
        // termination. "Firmware (auto)" must still reach the driver.
        var driver = new FakeDriver();
        using var service = Service(driver);

        Assert.True(service.SetAuto());

        Assert.Equal(1, driver.RestoreCalls);
    }

    [Fact]
        public void A_refused_release_is_surfaced_kept_recorded_and_retried()
        {
            var driver = new FakeDriver { RestoreResult = NvmlReturn.NoPermission };
            using var service = Service(driver);
            NvmlReturn? failed = null;
            service.CommandFailed += rc => failed = rc;
    
            service.SetFixed(70);
            Assert.False(service.SetAuto());
    
            Assert.Equal(NvmlReturn.NoPermission, failed);
    
            // The fans really are still manual — the record must not claim otherwise.
            Assert.Equal("fixed", AppliedStateStore.Load(Uuid)!.FanMode);
    
            // And a second attempt must issue the release again, not assume it is done.
            driver.RestoreResult = NvmlReturn.Success;
            Assert.True(service.SetAuto());
            Assert.Equal(2, driver.RestoreCalls);
            Assert.Null(AppliedStateStore.Load(Uuid)!.FanMode);
        }

    [Fact]
        public void A_refused_fixed_command_does_not_disarm_the_later_release()
        {
            var driver = new FakeDriver { SetResult = NvmlReturn.NoPermission };
            using var service = Service(driver);
    
            service.SetFixed(70);          // refused: no manual control is claimed
            driver.SetResult = NvmlReturn.Success;
    
            Assert.True(service.SetAuto());
    
            Assert.Equal(1, driver.RestoreCalls);
        }

    [Fact]
        public void SetAuto_stops_the_curve_and_releases_exactly_once()
        {
            var driver = new FakeDriver();
            using var service = Service(driver);
            service.SetCurve(new FanCurveConfig
            {
                Points = [new(40, 40), new(80, 100)],
                ZeroRpmBelowC = 0,
                HysteresisC = 0,
                MinSpinDutyPct = 30,
            });
    
            service.OnSnapshot(Snapshot(70));   // 70 °C on that curve → 85 %
            Assert.Equal([85u], driver.SetCalls);
    
            Assert.True(service.SetAuto());
            service.OnSnapshot(Snapshot(85));
    
            Assert.Equal([85u], driver.SetCalls);  // no command after the release
            Assert.Equal(1, driver.RestoreCalls);
        }

    [Fact]
    public void Dispose_releases_the_fans_and_a_late_tick_cannot_take_them_back()
    {
        var driver = new FakeDriver();
        var service = Service(driver);
        service.SetCurve(new FanCurveConfig
        {
            Points = [new(40, 40), new(80, 100)],
            ZeroRpmBelowC = 0,
            HysteresisC = 0,
            MinSpinDutyPct = 30,
        });
        service.OnSnapshot(Snapshot(70));

        service.Dispose();
        service.OnSnapshot(Snapshot(85));   // the telemetry thread's last tick, after the release

        Assert.Equal([85u], driver.SetCalls);
        Assert.Equal(1, driver.RestoreCalls);
    }

    [Fact]
    public void A_refused_write_that_moved_one_cooler_is_still_released_on_dispose()
    {
        // R-06: the per-cooler fallback pinned cooler 0 at 0 % and then failed
        // cooler 1. The write reports failure, but cooler 0 is stopped all the
        // same, and shutdown used to release nothing because only a fully
        // accepted write counted as a takeover.
        var driver = new FakeDriver { RefuseFromCooler = 1 };
        var service = Service(driver);

        Assert.False(service.SetFixed(0));
        Assert.Equal(new uint?[] { 0, null }, driver.Coolers);

        service.Dispose();

        Assert.Equal(1, driver.RestoreCalls);
        Assert.Equal(new uint?[] { null, null }, driver.Coolers);
    }

    [Fact]
    public void A_partial_curve_write_is_recommanded_when_the_curve_comes_back_to_the_last_duty()
    {
        // R-06, curve side: after a partial write the "last commanded duty"
        // memo no longer describes the card. Keeping it made the curve skip its
        // next write whenever it asked for that duty again, leaving the cooler
        // the partial write had moved at the wrong duty for as long as the
        // temperature held.
        var driver = new FakeDriver();
        using var service = Service(driver);
        service.SetCurve(LinearCurve());

        service.OnSnapshot(Snapshot(70));   // 85 %, accepted by both coolers
        driver.RefuseFromCooler = 1;
        service.OnSnapshot(Snapshot(80));   // 100 % lands on cooler 0 only
        driver.RefuseFromCooler = int.MaxValue;
        service.OnSnapshot(Snapshot(70));   // back to 85 %: must be written again

        Assert.Equal([85u, 100u, 85u], driver.SetCalls);
        Assert.Equal(new uint?[] { 85, 85 }, driver.Coolers);
    }

    [Fact]
    public void A_card_with_no_fan_control_leaves_no_record_after_a_refused_write()
    {
        // The other side of R-06: every write to a card with no fan interface
        // (Arc) is refused as NotSupported. Treating that as a possible
        // takeover sends one harmless release at shutdown, and the release's
        // NotSupported must not be persisted as fans left in manual control.
        var driver = new FakeDriver { SetResult = NvmlReturn.NotSupported, RestoreResult = NvmlReturn.NotSupported };
        using var service = Service(driver);

        Assert.False(service.SetFixed(50));

        Assert.True(service.ReleaseForShutdown());
        Assert.Equal(1, driver.RestoreCalls);
        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void A_partial_write_whose_release_is_refused_keeps_an_unclean_record_with_no_duty()
    {
        // R-05/R-06: the release is the only thing that could undo the partial
        // write, so its refusal must survive to the next launch's banner. The
        // duty is unknown (cooler 0 took it, cooler 1 did not) and says so.
        var driver = new FakeDriver { RefuseFromCooler = 1, RestoreResult = NvmlReturn.NoPermission };
        using var service = Service(driver);

        Assert.False(service.SetFixed(0));

        Assert.False(service.ReleaseForShutdown());
        var record = AppliedStateStore.Load(Uuid);
        Assert.NotNull(record);
        Assert.Equal("fixed", record.FanMode);
        Assert.Null(record.FanDuty);
        Assert.False(record.CleanShutdown);
    }

    [Fact]
    public void The_shutdown_release_says_whether_the_fans_were_handed_back()
    {
        // R-05: the app stamps the session clean only on true, so each outcome
        // has to be told apart — and asking again must not re-issue the release
        // or turn a refusal into a success.
        var untouched = new FakeDriver();
        using var idle = Service(untouched);
        Assert.True(idle.ReleaseForShutdown());
        Assert.Equal(0, untouched.RestoreCalls);   // nothing taken over, nothing released

        var accepted = new FakeDriver();
        using var released = Service(accepted);
        Assert.True(released.SetFixed(40));
        Assert.True(released.ReleaseForShutdown());
        Assert.Equal(1, accepted.RestoreCalls);

        var refused = new FakeDriver { RestoreResult = NvmlReturn.NoPermission };
        using var pinned = Service(refused);
        Assert.True(pinned.SetFixed(40));
        Assert.False(pinned.ReleaseForShutdown());
        Assert.False(pinned.ReleaseForShutdown());
        pinned.Dispose();
        Assert.Equal(1, refused.RestoreCalls);
        Assert.Equal(40u, AppliedStateStore.Load(Uuid)!.FanDuty);
        Assert.False(AppliedStateStore.Load(Uuid)!.CleanShutdown);
    }

    [Fact]
    public async Task Dispose_waits_for_an_in_flight_first_takeover_and_releases_it()
    {
        // R-07: Dispose used to read the ownership flag before taking the
        // command lock. During the FIRST curve write the flag is still false,
        // so Dispose released nothing, and the write then landed after it:
        // fans manual, nothing left to hand them back.
        using var hold = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var driver = new FakeDriver { HoldSet = hold, SetEntered = entered };
        using var service = Service(driver);
        service.SetCurve(LinearCurve());

        var tick = Task.Run(() => service.OnSnapshot(Snapshot(70)), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the curve never wrote");

        var dispose = Task.Run(service.Dispose, TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => service.Mode == FanControlMode.Auto, TimeSpan.FromSeconds(10)), "Dispose never started");

        hold.Set();
        await tick.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await dispose.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, driver.RestoreCalls);
        Assert.Equal(new uint?[] { null, null }, driver.Coolers);
        Assert.Null(service.CommandedDuty);
    }

    [Fact]
    public void A_fan_command_after_dispose_is_refused()
    {
        // The release decision is final: a late command (a UI action or an
        // automation rule racing the exit) must not take the fans back after it.
        var driver = new FakeDriver();
        var service = Service(driver);
        service.Dispose();

        Assert.False(service.SetFixed(40));

        Assert.Empty(driver.SetCalls);
        Assert.Equal(new uint?[] { null, null }, driver.Coolers);
    }
}
