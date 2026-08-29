using Afterglow.Core.Services;
using Afterglow.Core.Settings;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Tests;

public class AutomationEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static GpuSnapshot Snap(double memJunction, uint device = 0) => new()
    {
        Timestamp = T0,
        DeviceIndex = device,
        MemJunctionTempC = memJunction,
        GpuTempC = 60,
        PowerW = 300,
    };

    private static AutomationEngine Engine(int forSeconds = 30)
    {
        var engine = new AutomationEngine();
        engine.UpdateRules(
        [
            new AutomationRule
            {
                Metric = "memjunction",
                Threshold = 94,
                ForSeconds = forSeconds,
                Action = "fans",
                ActionFanPct = 90,
            },
        ]);
        return engine;
    }

    [Fact]
    public void Fires_only_after_the_breach_is_sustained()
    {
        var engine = Engine(forSeconds: 30);

        Assert.Empty(engine.Evaluate(Snap(96), T0));
        Assert.Empty(engine.Evaluate(Snap(97), T0.AddSeconds(15)));
        var fired = engine.Evaluate(Snap(95), T0.AddSeconds(30));

        var e = Assert.Single(fired);
        Assert.Equal(95, e.Value);
        Assert.Equal("fans", e.Rule.Action);
    }

    [Fact]
    public void A_dip_below_threshold_resets_the_clock()
    {
        var engine = Engine(forSeconds: 30);

        Assert.Empty(engine.Evaluate(Snap(96), T0));
        Assert.Empty(engine.Evaluate(Snap(80), T0.AddSeconds(20)));      // recovered
        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(25)));      // new breach starts here
        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(40)));      // only 15 s in
        Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(55)));
    }

    [Fact]
    public void Cooldown_prevents_immediate_refiring()
    {
        var engine = Engine(forSeconds: 10);

        Assert.Empty(engine.Evaluate(Snap(96), T0));
        Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(10)));
        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(60)));      // still cooling down
        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(310)));     // breach restarts after cooldown
        Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(320)));
    }

    [Fact]
    public void Missing_sensor_never_fires()
    {
        var engine = Engine(forSeconds: 0);
        var snapshot = new GpuSnapshot { Timestamp = T0, DeviceIndex = 0, MemJunctionTempC = null };

        Assert.Empty(engine.Evaluate(snapshot, T0));
        Assert.Empty(engine.Evaluate(snapshot, T0.AddSeconds(60)));
    }

    [Fact]
    public void Each_gpu_breaches_independently()
    {
        var engine = Engine(forSeconds: 30);

        // GPU 1 runs hot the whole time; GPU 0 stays cool. Only GPU 1 fires,
        // and its event names the card.
        Assert.Empty(engine.Evaluate(Snap(96, device: 1), T0));
        Assert.Empty(engine.Evaluate(Snap(60, device: 0), T0));
        Assert.Empty(engine.Evaluate(Snap(97, device: 1), T0.AddSeconds(15)));
        Assert.Empty(engine.Evaluate(Snap(60, device: 0), T0.AddSeconds(30)));

        var fired = Assert.Single(engine.Evaluate(Snap(96, device: 1), T0.AddSeconds(30)));
        Assert.Equal(1u, fired.DeviceIndex);
    }

    [Fact]
    public void A_cool_gpu_never_resets_the_hot_ones_breach_clock()
    {
        var engine = Engine(forSeconds: 30);

        Assert.Empty(engine.Evaluate(Snap(96, device: 1), T0));
        // The other card reporting below threshold must not clear device 1's timer.
        Assert.Empty(engine.Evaluate(Snap(60, device: 0), T0.AddSeconds(10)));
        Assert.Single(engine.Evaluate(Snap(96, device: 1), T0.AddSeconds(30)));
    }

    [Fact]
    public void One_gpus_cooldown_does_not_mask_the_other()
    {
        var engine = Engine(forSeconds: 10);

        Assert.Empty(engine.Evaluate(Snap(96, device: 0), T0));
        Assert.Single(engine.Evaluate(Snap(96, device: 0), T0.AddSeconds(10)));   // device 0 fires, cools down

        // Device 1 starts breaching while device 0 is in cooldown — it still fires.
        Assert.Empty(engine.Evaluate(Snap(96, device: 1), T0.AddSeconds(20)));
        var fired = Assert.Single(engine.Evaluate(Snap(96, device: 1), T0.AddSeconds(30)));
        Assert.Equal(1u, fired.DeviceIndex);
    }
}
