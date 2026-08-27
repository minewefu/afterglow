using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

public class ThrottleDescriberTests
{
    [Fact]
    public void Known_reason_produces_its_chip()
    {
        var chips = ThrottleDescriber.Describe(NvmlClocksEventReasons.SwPowerCap);

        var chip = Assert.Single(chips);
        Assert.Equal("Power limit", chip.Label);
    }

    [Fact]
    public void Unknown_bit_is_surfaced_raw_instead_of_hidden()
    {
        // Driver 616.xx reports 0x400 in ordinary operation; no build may
        // render an unknown reason as "not throttling".
        var chips = ThrottleDescriber.Describe((NvmlClocksEventReasons)0x400);

        var chip = Assert.Single(chips);
        Assert.Contains("0x400", chip.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_bit_rides_alongside_known_ones()
    {
        var chips = ThrottleDescriber.Describe(
            NvmlClocksEventReasons.SwPowerCap | (NvmlClocksEventReasons)0x400);

        Assert.Equal(2, chips.Count);
        Assert.Contains(chips, c => c.Label == "Power limit");
        Assert.Contains(chips, c => c.Label.Contains("0x400", StringComparison.OrdinalIgnoreCase));
    }
}

public class VfCurveRecorderTests
{
    private static GpuSnapshot Sample(double mv, uint mhz, uint load = 95) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        DeviceIndex = 0,
        CoreVoltageMv = mv,
        CoreClockMHz = mhz,
        GpuUtilPct = load,
    };

    private static VfCurveRecorder WellSampledCurve()
    {
        var recorder = new VfCurveRecorder();
        foreach ((double mv, uint mhz) in new[] { (900.0, 2800u), (950.0, 2900u), (1000.0, 3000u) })
        {
            for (int i = 0; i < 25; i++)
            {
                recorder.Add(Sample(mv, mhz));
            }
        }

        return recorder;
    }

    [Fact]
    public void ClockAt_interpolates_between_bins()
    {
        var recorder = WellSampledCurve();

        Assert.Equal(2850, recorder.ClockAt(925)!.Value, precision: 0);
    }

    [Fact]
    public void ClockAt_refuses_to_extrapolate()
    {
        Assert.Null(WellSampledCurve().ClockAt(1200));
    }

    [Fact]
    public void Plan_computes_offset_from_the_measured_curve()
    {
        var plan = WellSampledCurve().PlanUndervolt(900, 2900, currentOffsetMHz: 0);

        Assert.NotNull(plan);
        Assert.Equal(100, plan.CoreOffsetMHz);
        Assert.Equal(2900u, plan.LockClockMHz);
        Assert.True(plan.BinSamples >= VfCurveRecorder.PlanMinBinSamples);
    }

    [Fact]
    public void Plan_refuses_absurd_targets_instead_of_describing_them()
    {
        // Previously: "offset +6200 MHz, lock 9000 MHz" in a confident tone.
        Assert.Null(WellSampledCurve().PlanUndervolt(900, 9000, currentOffsetMHz: 0));
    }

    [Fact]
    public void Plan_respects_driver_capabilities_when_provided()
    {
        var caps = new TuningCapabilities
        {
            SupportsCoreOffset = true,
            CoreOffsetMinMHz = -50,
            CoreOffsetMaxMHz = 50,
        };

        // Needs +100 core, driver allows +/-50 -> not plannable on this GPU.
        Assert.Null(WellSampledCurve().PlanUndervolt(900, 2900, 0, caps));
    }

    [Fact]
    public void Thin_bins_draw_the_curve_but_may_not_drive_a_write()
    {
        var recorder = new VfCurveRecorder();
        for (int i = 0; i < 3; i++)
        {
            recorder.Add(Sample(900, 2800));
        }

        Assert.NotEmpty(recorder.GetCurve());                       // drawing: ok
        Assert.Null(recorder.PlanUndervolt(900, 2810, 0));          // hardware write: refused
    }

    [Fact]
    public void Load_drops_out_of_bounds_bins()
    {
        string path = Path.Combine(Path.GetTempPath(), $"afterglow-vf-{Guid.NewGuid():N}.json");
        try
        {
            // Bin key 180 => 900 mV (valid); key 20 => 100 mV (out of range);
            // one valid-voltage bin carrying an absurd 9000 MHz clock.
            File.WriteAllText(path,
                """
                [
                  {"Key":180,"MaxClock":2800,"ClockSum":70000,"Samples":25},
                  {"Key":20,"MaxClock":2800,"ClockSum":70000,"Samples":25},
                  {"Key":190,"MaxClock":9000,"ClockSum":225000,"Samples":25}
                ]
                """);

            var recorder = new VfCurveRecorder();
            recorder.Load(path);
            var curve = recorder.GetCurve();

            var bin = Assert.Single(curve);
            Assert.Equal(900, bin.VoltageMv, precision: 1);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
