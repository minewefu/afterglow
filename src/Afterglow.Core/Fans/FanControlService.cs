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

    /// <summary>Raised when a fan command fails (e.g., lost elevation).</summary>
    public event Action<NvmlReturn>? CommandFailed;

    public void SetAuto()
    {
        lock (_lock)
        {
            _mode = FanControlMode.Auto;
            _evaluator = null;
            ReleaseControl();
            Tuning.AppliedStateStore.RecordFans(null, null);
        }
    }

    public void SetFixed(uint dutyPct)
    {
        lock (_lock)
        {
            _mode = FanControlMode.Fixed;
            _evaluator = null;

            // 0 = stop; anything between 1 and the hardware minimum rounds up.
            uint duty = Tuning.TuningMath.NormalizeFixedFanDuty(dutyPct, _tuner.Capabilities.FanMinDutyPct);
            Command(duty);
            Tuning.AppliedStateStore.RecordFans("fixed", duty);
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
            Tuning.AppliedStateStore.RecordFans("curve", null);
        }
    }

    /// <summary>Feed one telemetry snapshot (called on the polling thread).</summary>
    public void OnSnapshot(GpuSnapshot snapshot)
    {
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

            double duty = _evaluator.Step(t);
            if (Math.Abs(duty - _lastCommandedDuty) >= 1)
            {
                Command(duty);
            }
        }
    }

    private void Command(double duty)
    {
        var rc = _tuner.SetAllFansRaw((uint)Math.Round(duty));
        if (rc == NvmlReturn.Success)
        {
            _lastCommandedDuty = duty;
            _weControlFans = true;
        }
        else
        {
            Diagnostics.Log.Warn($"Fan command {duty:F0}% failed: {rc}");
            CommandFailed?.Invoke(rc);
        }
    }

    private void ReleaseControl()
    {
        if (_weControlFans)
        {
            _weControlFans = false;
            _lastCommandedDuty = -1;
            _ = _tuner.RestoreAutoFansRaw();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            ReleaseControl();
        }
    }
}
