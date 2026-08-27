using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Fans;

public enum FanControlMode
{
    Auto,
    Fixed,
    Curve,
}

/// <summary>
/// Continuous fan control for one GPU: feeds telemetry temperatures through the
/// curve evaluator and commands the fans when the desired duty changes.
/// Restores firmware control on dispose and on mode change back to Auto.
/// Driver calls happen outside <c>_lock</c> (a slow kernel transition must not
/// block the telemetry poller or the UI thread), and <see cref="CommandFailed"/>
/// is raised outside it too.
/// </summary>
public sealed class FanControlService : IDisposable
{
    private readonly GpuTuner _tuner;
    private readonly object _lock = new();

    private FanControlMode _mode = FanControlMode.Auto;
    private FanCurveEvaluator? _evaluator;
    private FanTempSource _tempSource;
    private double _lastCommandedDuty = -1;
    private bool _weControlFans;

    public FanControlService(GpuTuner tuner)
    {
        _tuner = tuner;
    }

    public FanControlMode Mode
    {
        get
        {
            lock (_lock)
            {
                return _mode;
            }
        }
    }

    /// <summary>Last duty commanded by the service (null when firmware controls the fans).</summary>
    public double? CommandedDuty
    {
        get
        {
            lock (_lock)
            {
                return _weControlFans && _lastCommandedDuty >= 0 ? _lastCommandedDuty : null;
            }
        }
    }

    /// <summary>Raised (outside the internal lock) when a fan command fails, e.g. lost elevation.</summary>
    public event Action<NvmlReturn>? CommandFailed;

    public void SetAuto()
    {
        bool release;
        lock (_lock)
        {
            _mode = FanControlMode.Auto;
            _evaluator = null;
            release = _weControlFans;
            _weControlFans = false;
            _lastCommandedDuty = -1;
        }

        if (release)
        {
            _ = _tuner.RestoreAutoFansRaw();
        }

        AppliedStateStore.RecordFans(null, null);
    }

    public void SetFixed(uint dutyPct)
    {
        uint duty;
        lock (_lock)
        {
            _mode = FanControlMode.Fixed;
            _evaluator = null;

            // 0 = stop; anything between 1 and the hardware minimum rounds up.
            duty = TuningMath.NormalizeFixedFanDuty(dutyPct, _tuner.Capabilities.FanMinDutyPct);
        }

        // Record manual control only when the command actually landed — a
        // failed command (e.g. not elevated) must not persist a claim that
        // Afterglow took the fans over.
        if (Command(duty))
        {
            AppliedStateStore.RecordFans("fixed", duty);
        }
    }

    public void SetCurve(FanCurveConfig config)
    {
        if (config.Validate() is string error)
        {
            throw new ArgumentException(error, nameof(config));
        }

        lock (_lock)
        {
            _mode = FanControlMode.Curve;
            _tempSource = config.TempSource;
            _evaluator = new FanCurveEvaluator(config);
            _lastCommandedDuty = -1;
        }

        AppliedStateStore.RecordFans("curve", null);
    }

    /// <summary>Feed one telemetry snapshot (called on the polling thread).</summary>
    public void OnSnapshot(GpuSnapshot snapshot)
    {
        double duty;
        lock (_lock)
        {
            if (_mode != FanControlMode.Curve || _evaluator is null)
            {
                return;
            }

            double? temp = _tempSource switch
            {
                FanTempSource.HotSpot => snapshot.HotSpotTempC ?? snapshot.GpuTempC,
                FanTempSource.MemJunction => snapshot.MemJunctionTempC ?? snapshot.GpuTempC,
                _ => snapshot.GpuTempC,
            };

            if (temp is not double t)
            {
                return;
            }

            duty = _evaluator.Step(t);
            if (Math.Abs(duty - _lastCommandedDuty) < 1)
            {
                return;
            }
        }

        _ = Command(duty);
    }

    /// <summary>Issues the driver command outside the lock; updates state under it.</summary>
    private bool Command(double duty)
    {
        var rc = _tuner.SetAllFansRaw((uint)Math.Round(duty));
        if (rc == NvmlReturn.Success)
        {
            lock (_lock)
            {
                _lastCommandedDuty = duty;
                _weControlFans = true;
            }

            return true;
        }

        Diagnostics.Log.Warn($"Fan command {duty:F0}% failed: {rc}");
        CommandFailed?.Invoke(rc);
        return false;
    }

    public void Dispose()
    {
        bool release;
        lock (_lock)
        {
            release = _weControlFans;
            _weControlFans = false;
            _lastCommandedDuty = -1;
        }

        if (release)
        {
            _ = _tuner.RestoreAutoFansRaw();
        }
    }
}
