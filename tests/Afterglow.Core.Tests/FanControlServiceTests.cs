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

    private readonly string _root;

    public FanControlServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"afterglow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot = _root;
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Records what the service asked the driver to do, and answers as told.</summary>
    private sealed class FakeDriver
    {
        public List<uint> SetCalls { get; } = [];

        public int RestoreCalls { get; private set; }

        public NvmlReturn SetResult { get; set; } = NvmlReturn.Success;

        public NvmlReturn RestoreResult { get; set; } = NvmlReturn.Success;

        public NvmlReturn SetAllFans(uint duty)
        {
            SetCalls.Add(duty);
            return SetResult;
        }

        public NvmlReturn RestoreAuto()
        {
            RestoreCalls++;
            return RestoreResult;
        }
    }

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
}
