namespace Afterglow.Core.Fans;

/// <summary>Which temperature drives the fan curve.</summary>
public enum FanTempSource
{
    Gpu = 0,
    HotSpot = 1,
    MemJunction = 2,
}

public sealed record FanPoint(double TempC, double DutyPct);

/// <summary>
/// Immutable fan-curve configuration: interpolation points plus behavior settings.
/// Evaluation state (hysteresis, zero-RPM latch, ramp limiting) lives in
/// <see cref="FanCurveEvaluator"/>.
/// </summary>
public sealed record FanCurveConfig
{
    public const int MaxPoints = 16;

    /// <summary>Curve points, temperature-ascending, duty non-decreasing.</summary>
    public IReadOnlyList<FanPoint> Points { get; init; } = DefaultPoints;

    /// <summary>Temperature source the curve reads.</summary>
    public FanTempSource TempSource { get; init; } = FanTempSource.Gpu;

    /// <summary>°C the temperature must fall (beyond the curve) before duty is reduced.</summary>
    public double HysteresisC { get; init; } = 3;

    /// <summary>Keep fans stopped at or below this temperature (0 disables zero-RPM).</summary>
    public double ZeroRpmBelowC { get; init; } = 45;

    /// <summary>Extra °C above <see cref="ZeroRpmBelowC"/> before stopped fans restart.</summary>
    public double ZeroRpmHysteresisC { get; init; } = 5;

    /// <summary>Largest duty change per evaluation, in percentage points (0 = unlimited).</summary>
    public double MaxStepPerTick { get; init; }

    /// <summary>Lowest duty the hardware can actually spin at (reported by the driver; 30 on most cards).</summary>
    public double MinSpinDutyPct { get; init; } = 30;

    public static IReadOnlyList<FanPoint> DefaultPoints { get; } =
    [
        new(30, 30),
        new(50, 30),
        new(65, 45),
        new(75, 60),
        new(83, 80),
        new(90, 100),
    ];

    /// <summary>Validates points; returns an error description or null when valid.</summary>
    public string? Validate()
    {
        if (Points.Count is < 2 or > MaxPoints)
        {
            return $"Curve needs 2..{MaxPoints} points.";
        }

        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (p.TempC is < 0 or > 130 || p.DutyPct is < 0 or > 100)
            {
                return $"Point {i + 1} out of range (temp 0–130 °C, duty 0–100 %).";
            }

            if (i > 0)
            {
                if (p.TempC <= Points[i - 1].TempC)
                {
                    return "Point temperatures must be strictly increasing.";
                }

                if (p.DutyPct < Points[i - 1].DutyPct)
                {
                    return "Point duties must not decrease as temperature rises.";
                }
            }
        }

        if (HysteresisC is < 0 or > 20 || ZeroRpmHysteresisC is < 0 or > 20)
        {
            return "Hysteresis must be between 0 and 20 °C.";
        }

        return null;
    }

    /// <summary>Pure curve lookup: linear interpolation, clamped to the end points.</summary>
    public double Evaluate(double tempC)
    {
        var points = Points;
        if (tempC <= points[0].TempC)
        {
            return points[0].DutyPct;
        }

        for (int i = 1; i < points.Count; i++)
        {
            if (tempC <= points[i].TempC)
            {
                var a = points[i - 1];
                var b = points[i];
                double t = (tempC - a.TempC) / (b.TempC - a.TempC);
                return a.DutyPct + (t * (b.DutyPct - a.DutyPct));
            }
        }

        return points[^1].DutyPct;
    }
}

/// <summary>
/// Stateful evaluator that turns temperatures into fan-duty commands, applying
/// hysteresis (fast up, damped down), a zero-RPM window with restart hysteresis,
/// ramp limiting, and the hardware minimum spin duty.
/// </summary>
public sealed class FanCurveEvaluator
{
    private readonly FanCurveConfig _config;
    private double _currentDuty;
    private double _lastCommand;
    private bool _stopped = true;
    private bool _hasState;

    public FanCurveEvaluator(FanCurveConfig config)
    {
        _config = config;
    }

    public FanCurveConfig Config => _config;

    /// <summary>Computes the duty (0–100) to command for the given temperature.</summary>
    public double Step(double tempC)
    {
        double target = _config.Evaluate(tempC);

        // Zero-RPM window with restart hysteresis.
        bool zeroRpmEnabled = _config.ZeroRpmBelowC > 0;
        if (zeroRpmEnabled)
        {
            if (_stopped)
            {
                if (tempC >= _config.ZeroRpmBelowC + _config.ZeroRpmHysteresisC)
                {
                    _stopped = false;
                }
            }
            else if (tempC <= _config.ZeroRpmBelowC)
            {
                _stopped = true;
            }

            if (_stopped)
            {
                _currentDuty = 0;
                _hasState = true;
                return 0;
            }
        }
        else
        {
            _stopped = false;
        }

        if (!_hasState)
        {
            _hasState = true;
            _currentDuty = target;
        }
        else if (target >= _currentDuty)
        {
            // Rising demand responds immediately.
            _currentDuty = target;
        }
        else
        {
            // Falling demand only follows once the temperature has really dropped:
            // evaluate as if it were HysteresisC warmer.
            double damped = _config.Evaluate(tempC + _config.HysteresisC);
            if (damped < _currentDuty)
            {
                _currentDuty = damped;
            }
        }

        double duty = _currentDuty;

        // Ramp limiting between successive spinning commands (never delays a
        // stop, and spin-up jumps straight to at least the minimum spin duty).
        if (_config.MaxStepPerTick > 0 && _lastCommand > 0 && duty > 0)
        {
            duty = Math.Clamp(
                duty,
                _lastCommand - _config.MaxStepPerTick,
                _lastCommand + _config.MaxStepPerTick);
        }

        // Respect the hardware's minimum spinning duty: anything between 0 and the
        // minimum spin threshold is rounded up (fans cannot run slower).
        if (duty > 0 && duty < _config.MinSpinDutyPct)
        {
            duty = _config.MinSpinDutyPct;
        }

        duty = Math.Clamp(duty, 0, 100);
        _lastCommand = duty;
        return duty;
    }

    /// <summary>Reset internal state (e.g., when the curve is edited).</summary>
    public void Reset()
    {
        _hasState = false;
        _stopped = true;
        _currentDuty = 0;
        _lastCommand = 0;
    }
}
