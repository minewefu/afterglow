using Afterglow.Core.Services;
using Afterglow.Core.Settings;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Tests;

public class AutomationEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static GpuSnapshot Snap(double memJunction) => new()
    {
        Timestamp = T0,
        DeviceIndex = 0,
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
}
