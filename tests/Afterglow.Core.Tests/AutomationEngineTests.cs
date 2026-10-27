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

    // Every settings save raises SettingsChanged, which hands the engine the
    // rules again — usually unchanged, as fresh instances read back from the
    // saved settings. Those must not touch a running timer or cooldown (R-38).
    private static AutomationRule MemRule(int forSeconds = 30, double threshold = 94) => new()
    {
        Metric = "memjunction",
        Threshold = threshold,
        ForSeconds = forSeconds,
        Action = "fans",
        ActionFanPct = 90,
    };

    [Fact]
    public void Reloading_unchanged_rules_keeps_the_cooldown()
    {
        // The review's sequence at 1 s polling, the card hot throughout: fired at
        // t5, an unrelated settings edit at t7, fired again at t12 instead of
        // staying quiet until the cooldown ends at t305.
        var engine = new AutomationEngine();
        engine.UpdateRules([MemRule(forSeconds: 5)]);

        var firedAt = new List<int>();
        for (int t = 0; t <= 320; t++)
        {
            if (t == 7)
            {
                engine.UpdateRules([MemRule(forSeconds: 5)]);
            }

            if (engine.Evaluate(Snap(96), T0.AddSeconds(t)).Count > 0)
            {
                firedAt.Add(t);
            }
        }

        // Cooldown ends at 5 + 300; the next breach starts then and lasts 5 s.
        Assert.Equal([5, 310], firedAt);
    }

    [Fact]
    public void Reloading_unchanged_rules_keeps_the_breach_timer()
    {
        var engine = new AutomationEngine();
        engine.UpdateRules([MemRule(forSeconds: 30)]);

        Assert.Empty(engine.Evaluate(Snap(96, device: 0), T0));
        Assert.Empty(engine.Evaluate(Snap(96, device: 1), T0.AddSeconds(5)));

        // A polling, overlay or alert edit 20 s into the breach used to restart
        // the 30 s count and postpone the thermal action.
        engine.UpdateRules([MemRule(forSeconds: 30)]);

        var fired = Assert.Single(engine.Evaluate(Snap(96, device: 0), T0.AddSeconds(30)));
        Assert.Equal(0u, fired.DeviceIndex);
        fired = Assert.Single(engine.Evaluate(Snap(96, device: 1), T0.AddSeconds(35)));
        Assert.Equal(1u, fired.DeviceIndex);
    }

    [Fact]
    public void Adding_a_rule_keeps_the_others_state_and_starts_the_new_one_fresh()
    {
        var engine = new AutomationEngine();
        var mem = MemRule(forSeconds: 30);
        engine.UpdateRules([mem]);

        Assert.Empty(engine.Evaluate(Snap(96), T0));

        // The new rule goes first, so the old one changes position too.
        var gpu = new AutomationRule { Metric = "gpu", Threshold = 50, ForSeconds = 30, Action = "reset" };
        engine.UpdateRules([gpu, mem]);

        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(10)));     // gpu rule's breach starts here
        var fired = Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(30)));
        Assert.Same(mem, fired.Rule);
        fired = Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(40)));
        Assert.Same(gpu, fired.Rule);
    }

    [Fact]
    public void A_changed_rule_starts_over()
    {
        var engine = new AutomationEngine();
        engine.UpdateRules([MemRule(forSeconds: 10)]);

        Assert.Empty(engine.Evaluate(Snap(96), T0));
        Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(10)));    // fires, cooling down

        // A different threshold is a different rule: no inherited cooldown.
        engine.UpdateRules([MemRule(forSeconds: 10, threshold: 95)]);

        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(20)));
        Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(30)));
    }

    [Fact]
    public void A_removed_rule_loses_its_state_even_when_added_back()
    {
        var engine = new AutomationEngine();
        engine.UpdateRules([MemRule(forSeconds: 10)]);

        Assert.Empty(engine.Evaluate(Snap(96), T0));
        Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(10)));

        engine.UpdateRules([]);
        engine.UpdateRules([MemRule(forSeconds: 10)]);

        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(20)));
        Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(30)));
    }

    [Fact]
    public void Identical_rules_each_keep_their_own_state()
    {
        // Two equal rules are two entries; a reload must not merge them into one
        // or hand both the same history.
        var engine = new AutomationEngine();
        engine.UpdateRules([MemRule(forSeconds: 10)]);

        Assert.Empty(engine.Evaluate(Snap(96), T0));
        Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(10)));    // the first copy cools down

        engine.UpdateRules([MemRule(forSeconds: 10), MemRule(forSeconds: 10)]);

        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(20)));     // the second copy's breach starts
        Assert.Single(engine.Evaluate(Snap(96), T0.AddSeconds(30)));    // only the second copy fires

        engine.UpdateRules([MemRule(forSeconds: 10)]);                   // one copy removed

        // Whichever copy survives, it is cooling down; a fresh entry would fire at t50.
        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(40)));
        Assert.Empty(engine.Evaluate(Snap(96), T0.AddSeconds(50)));
    }
}
