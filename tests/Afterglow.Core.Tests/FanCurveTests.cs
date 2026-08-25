using Afterglow.Core.Fans;

namespace Afterglow.Core.Tests;

public class FanCurveTests
{
    private static FanCurveConfig SimpleCurve(double zeroRpmBelow = 0, double hysteresis = 0, double maxStep = 0) => new()
    {
        Points = [new(40, 30), new(60, 50), new(80, 100)],
        ZeroRpmBelowC = zeroRpmBelow,
        HysteresisC = hysteresis,
        MaxStepPerTick = maxStep,
        MinSpinDutyPct = 30,
    };

    [Theory]
    [InlineData(20, 30)]   // below first point → clamp to first duty
    [InlineData(40, 30)]
    [InlineData(50, 40)]   // midpoint of 40→60 maps to midpoint of 30→50
    [InlineData(60, 50)]
    [InlineData(70, 75)]   // midpoint of 60→80 maps to midpoint of 50→100
    [InlineData(80, 100)]
    [InlineData(95, 100)]  // above last point → clamp to last duty
    public void Evaluate_interpolates_linearly(double temp, double expected)
    {
        Assert.Equal(expected, SimpleCurve().Evaluate(temp), precision: 5);
    }

    [Fact]
    public void Rising_temperature_responds_immediately()
    {
        var evaluator = new FanCurveEvaluator(SimpleCurve(hysteresis: 5));
        Assert.Equal(40, evaluator.Step(50), precision: 5);
        Assert.Equal(75, evaluator.Step(70), precision: 5);
        Assert.Equal(100, evaluator.Step(85), precision: 5);
    }

    [Fact]
    public void Falling_temperature_is_damped_by_hysteresis()
    {
        var evaluator = new FanCurveEvaluator(SimpleCurve(hysteresis: 5));
        evaluator.Step(70); // duty 75

        // Dropping 2 °C: curve at (68+5)=73 gives 82.5 which is above 75 → duty must hold.
        Assert.Equal(75, evaluator.Step(68), precision: 5);

        // A real drop: curve at (55+5)=60 gives 50 < 75 → duty falls to 50.
        Assert.Equal(50, evaluator.Step(55), precision: 5);
    }

    [Fact]
    public void ZeroRpm_latches_until_restart_threshold()
    {
        var config = SimpleCurve(zeroRpmBelow: 45) with { ZeroRpmHysteresisC = 5 };
        var evaluator = new FanCurveEvaluator(config);

        Assert.Equal(0, evaluator.Step(40));  // cold start: stopped
        Assert.Equal(0, evaluator.Step(47));  // inside hysteresis band: still stopped
        Assert.True(evaluator.Step(51) > 0);  // past 45+5: spins up
        Assert.True(evaluator.Step(46) > 0);  // above stop threshold: keeps spinning
        Assert.Equal(0, evaluator.Step(44));  // at/below stop threshold: stops
    }

    [Fact]
    public void Duty_below_minimum_spin_is_rounded_up()
    {
        var config = new FanCurveConfig
        {
            Points = [new(30, 10), new(90, 100)],
            ZeroRpmBelowC = 0,
            MinSpinDutyPct = 30,
        };
        var evaluator = new FanCurveEvaluator(config);
        Assert.Equal(30, evaluator.Step(30), precision: 5); // curve says 10 → hardware minimum 30
    }

    [Fact]
    public void Ramp_limit_bounds_change_per_tick()
    {
        var evaluator = new FanCurveEvaluator(SimpleCurve(maxStep: 10));
        Assert.Equal(40, evaluator.Step(50), precision: 5);
        Assert.Equal(50, evaluator.Step(85), precision: 5);  // wants 100, limited to 40+10
        Assert.Equal(60, evaluator.Step(85), precision: 5);  // keeps ramping
    }

    [Fact]
    public void Validate_rejects_bad_curves()
    {
        Assert.NotNull(new FanCurveConfig { Points = [new(40, 30)] }.Validate());
        Assert.NotNull(new FanCurveConfig { Points = [new(40, 30), new(40, 50)] }.Validate());
        Assert.NotNull(new FanCurveConfig { Points = [new(40, 50), new(60, 30)] }.Validate());
        Assert.NotNull(new FanCurveConfig { Points = [new(40, 30), new(60, 120)] }.Validate());
        Assert.Null(new FanCurveConfig().Validate());
    }
}
