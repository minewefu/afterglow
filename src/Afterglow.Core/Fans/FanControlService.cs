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
/// is raised outside it too. Because the calls run unlocked, they are
/// serialised by <c>_commandLock</c> and stamped with a generation captured at
/// decision time — a command whose generation is stale by the time it holds the
/// command lock is dropped, so a mode change can never be overwritten by a
/// slower in-flight command from the previous mode.
/// </summary>
public sealed class FanControlService : IDisposable
{
    private readonly Func<uint, NvmlReturn> _setAllFans;
    private readonly Func<NvmlReturn> _restoreAutoFans;
    private readonly string? _gpuUuid;
    private readonly uint _fanMinDutyPct;
    private readonly object _lock = new();
    private readonly object _commandLock = new();

    private FanControlMode _mode = FanControlMode.Auto;
    private FanCurveEvaluator? _evaluator;
    private FanTempSource _tempSource;
    private double _lastCommandedDuty = -1;
    private bool _weControlFans;
    private long _generation;

    public FanControlService(GpuTuner tuner)
        : this(tuner.SetAllFansRaw, tuner.RestoreAutoFansRaw, tuner.GpuUuid, tuner.Capabilities.FanMinDutyPct)
    {
    }

    /// <summary>
    /// Test seam: the same control logic over the two driver calls, so mode
    /// handling can be exercised without a GPU. The UUID and the minimum spin
    /// duty are read once — both are fixed for the life of the tuner.
    /// </summary>
    internal FanControlService(
        Func<uint, NvmlReturn> setAllFans,
        Func<NvmlReturn> restoreAutoFans,
        string? gpuUuid,
        uint fanMinDutyPct)
    {
        _setAllFans = setAllFans;
        _restoreAutoFans = restoreAutoFans;
        _gpuUuid = gpuUuid;
        _fanMinDutyPct = fanMinDutyPct;
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

    /// <summary>Raised (outside the internal locks) when a fan command fails, e.g. lost elevation.</summary>
    public event Action<NvmlReturn>? CommandFailed;

    /// <summary>
    /// Returns the fans to firmware control. The release is issued whether or not
    /// this instance took the fans over (unless a newer mode has since claimed
    /// them — see the generation check below): the card can be in manual mode from a
    /// path that never went through this service (the per-fan buttons, the CLI's
    /// <c>--fan</c>, an unclean termination), and "Firmware (auto)" is an explicit
    /// request — the same reasoning as <see cref="GpuTuner.ForceUnlock"/> for a
    /// clock lock that outlived its session. Returns true only when the driver
    /// left the fans under no control of ours: the driver accepted the release,
    /// or the card has no fan control to release, or a newer mode deliberately
    /// claimed them first. A driver refusal returns false, is logged and raised
    /// through <see cref="CommandFailed"/>, and leaves both the manual-control
    /// flag and the persisted record untouched, since the release did not happen.
    /// </summary>
    public bool SetAuto()
    {
        long generation;
        lock (_lock)
        {
            _mode = FanControlMode.Auto;
            _evaluator = null;
            _lastCommandedDuty = -1;
            generation = ++_generation;
        }

        NvmlReturn rc;
        lock (_commandLock)
        {
            if (!IsCurrent(generation))
            {
                // A newer mode claimed the fans while we waited for the command
                // lock. It owns them deliberately and must not be undone here;
                // nothing is left under a control this call is answerable for,
                // so this is not reported to the user as a failure.
                return true;
            }

            rc = _restoreAutoFans();
            if (rc == NvmlReturn.Success)
            {
                lock (_lock)
                {
                    _weControlFans = false;
                }
            }
        }

        if (rc == NvmlReturn.NotSupported)
        {
            // This card exposes no fan control, so there was nothing to hand
            // back. Reporting a failure here would fail every Auto-fan profile
            // apply on such a card and pop an administrator-rights balloon that
            // has nothing to do with what happened.
            return true;
        }

        if (rc != NvmlReturn.Success)
        {
            // Still manual: keep _weControlFans as it was so Dispose and the next
            // SetAuto retry, and keep the recorded fan mode — clearing it would
            // persist a release that did not happen.
            Diagnostics.Log.Warn($"Fan release to firmware control failed: {rc}");
            CommandFailed?.Invoke(rc);
            return false;
        }

        AppliedStateStore.RecordFans(null, null, _gpuUuid);
        return true;
    }

    /// <summary>
    /// Pins every fan at one duty. Returns whether the driver accepted it, so a
    /// caller that reports to the user does not claim a fan change that did not
    /// happen. Callers that ignore the result must not describe the fans at all.
    /// </summary>
    public bool SetFixed(uint dutyPct)
    {
        uint duty;
        long generation;
        lock (_lock)
        {
            _mode = FanControlMode.Fixed;
            _evaluator = null;
            generation = ++_generation;

            // 0 = stop; anything between 1 and the hardware minimum rounds up.
            duty = TuningMath.NormalizeFixedFanDuty(dutyPct, _fanMinDutyPct);
        }

        // Record manual control only when the command actually landed — a
        // failed command (e.g. not elevated) must not persist a claim that
        // Afterglow took the fans over.
        if (!Command(duty, generation))
        {
            return false;
        }

        AppliedStateStore.RecordFans("fixed", duty, _gpuUuid);
        return true;
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
            _generation++;
        }

        AppliedStateStore.RecordFans("curve", null, _gpuUuid);
    }

    /// <summary>Feed one telemetry snapshot (called on the polling thread).</summary>
    public void OnSnapshot(GpuSnapshot snapshot)
    {
        double duty;
        long generation;
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

            generation = _generation;
        }

        _ = Command(duty, generation);
    }

    private bool IsCurrent(long generation)
    {
        lock (_lock)
        {
            return _generation == generation;
        }
    }

    /// <summary>
    /// Issues the driver command, serialised and generation-checked so that a
    /// stale command computed before a mode change is dropped instead of
    /// landing after (and silently undoing) the newer command.
    /// </summary>
    private bool Command(double duty, long generation)
    {
        NvmlReturn rc;
        lock (_commandLock)
        {
            if (!IsCurrent(generation))
            {
                return false;
            }

            rc = _setAllFans((uint)Math.Round(duty));
            if (rc == NvmlReturn.Success)
            {
                lock (_lock)
                {
                    _lastCommandedDuty = duty;
                    _weControlFans = true;
                }
            }
        }

        if (rc == NvmlReturn.Success)
        {
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
            _generation++;
        }

        if (!release)
        {
            // Deliberately narrower than SetAuto: shutdown is not a request to
            // release, so a manual mode this service never set (another tool,
            // another process) is left exactly as the user left it.
            return;
        }

        NvmlReturn rc;
        lock (_commandLock)
        {
            rc = _restoreAutoFans();
        }

        if (rc != NvmlReturn.Success)
        {
            // Logged, not raised: subscribers are being torn down with us. No
            // applied-state write here either — App marks the clean shutdown
            // before disposing services, and RecordFans clears that flag.
            Diagnostics.Log.Warn($"Fan release to firmware control on shutdown failed: {rc}");
        }
    }
}
