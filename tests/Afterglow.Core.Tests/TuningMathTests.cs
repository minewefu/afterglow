using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

public class TuningMathTests
{
    [Theory]
    [InlineData(150, -1000, 1000, 150, false)]
    [InlineData(1500, -1000, 1000, 1000, true)]
    [InlineData(-1500, -1000, 1000, -1000, true)]
    [InlineData(0, -1000, 1000, 0, false)]
    public void Offset_clamps_to_driver_range(int requested, int min, int max, int expected, bool clamped)
    {
        var (value, wasClamped) = TuningMath.ClampOffset(requested, min, max);
        Assert.Equal(expected, value);
        Assert.Equal(clamped, wasClamped);
    }

    [Theory]
    [InlineData(500, 400, 575, 500, false)]
    [InlineData(700, 400, 575, 575, true)]
    [InlineData(100, 400, 575, 400, true)]
    public void Power_clamps_to_driver_range(double requested, double min, double max, double expected, bool clamped)
    {
        var (value, wasClamped) = TuningMath.ClampPower(requested, min, max);
        Assert.Equal(expected, value, precision: 6);
        Assert.Equal(clamped, wasClamped);
    }

    [Theory]
    [InlineData(0u, 30u, 0u)]    // 0 = stop stays stop
    [InlineData(15u, 30u, 30u)]  // below min spin rounds up
    [InlineData(30u, 30u, 30u)]
    [InlineData(60u, 30u, 60u)]
    [InlineData(150u, 30u, 100u)]
    public void Fixed_fan_duty_respects_stop_and_min_spin(uint requested, uint minSpin, uint expected)
    {
        Assert.Equal(expected, TuningMath.NormalizeFixedFanDuty(requested, minSpin));
    }
}

public class FanSettingsPersistenceTests
{
    [Fact]
    public void Fan_settings_roundtrip_through_json()
    {
        var settings = new Core.Settings.FanSettings
        {
            Mode = "curve",
            FixedDutyPct = 65,
            Curve = new Core.Fans.FanCurveConfig
            {
                TempSource = Core.Fans.FanTempSource.MemJunction,
                HysteresisC = 5,
                ZeroRpmBelowC = 48,
                Points = [new(40, 30), new(70, 60), new(90, 100)],
            },
        };

        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<Core.Settings.FanSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal("curve", loaded.Mode);
        Assert.Equal(65u, loaded.FixedDutyPct);
        Assert.Equal(Core.Fans.FanTempSource.MemJunction, loaded.Curve.TempSource);
        Assert.Equal(3, loaded.Curve.Points.Count);
        Assert.Equal(70, loaded.Curve.Points[1].TempC);
        Assert.Null(loaded.Curve.Validate());
    }
}
