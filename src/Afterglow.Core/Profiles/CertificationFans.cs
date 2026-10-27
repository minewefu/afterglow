using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Profiles;

/// <summary>
/// The fan half of a certification run (R-11).
/// <para>
/// The certifier applied only <see cref="IGpuTuner.Apply"/>, which carries no
/// fan control, so a profile saved with fixed 30 % fans was burned under
/// whatever the controller was doing — 100 % fixed, say — and then marked
/// stable: clocks validated under a thermal configuration the profile never
/// runs with. The full-profile apply (MainViewModel.ApplyProfileFull) handles
/// fans separately through the card's own <see cref="FanControlService"/>; this
/// is the same policy, routed through the same service, plus the two things a
/// test run needs on top: put back what was there before, and notice when
/// something else takes the fans over mid-run.
/// </para>
/// </summary>
public sealed class CertificationFans
{
    private readonly FanControlService _service;
    private readonly Func<FanRestoreState?> _captureCurrent;
    private FanRestoreState? _before;
    private bool _attempted;
    private bool _applied;
    private FanControlMode _appliedMode;
    private double? _appliedDuty;
    private int _commandFailures;
    private int _lastFailure;

    /// <param name="service">The card's own fan service — the one its telemetry feeds, which is what runs a curve.</param>
    /// <param name="captureCurrent">
    /// What the card's fans are doing now (see <see cref="GameSessionRestore.CaptureFans"/>), read when the run
    /// starts so it can be put back afterwards; null when that cannot be determined, in which case the fans are
    /// handed back to firmware control instead.
    /// </param>
    public CertificationFans(FanControlService service, Func<FanRestoreState?> captureCurrent)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(captureCurrent);
        _service = service;
        _captureCurrent = captureCurrent;
    }

    /// <summary>The profile's fan policy in words, for the run's log.</summary>
    public static string Describe(TuningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.FanMode switch
        {
            FanMode.Fixed => $"fixed {profile.FixedFanPct}%",
            FanMode.Curve => "its fan curve",
            _ => "firmware (auto)",
        };
    }

    private static string Describe(FanRestoreState state) => state.Mode switch
    {
        FanMode.Fixed => $"fixed {state.FixedPct}%",
        FanMode.Curve => "the fan curve",
        _ => "firmware (auto) control",
    };

    /// <summary>
    /// Puts the profile's fan policy on the card. Null when it landed; otherwise
    /// why not, and the run must not go on — it would test the clocks under a
    /// thermal configuration that is not the profile's.
    /// </summary>
    internal string? Apply(TuningProfile profile)
    {
        _before = _captureCurrent();
        _attempted = true;
        _service.CommandFailed += OnCommandFailed;

        switch (profile.FanMode)
        {
            case FanMode.Fixed:
                if (!_service.SetFixed(profile.FixedFanPct))
                {
                    return $"the driver did not accept fixed {profile.FixedFanPct}% fans";
                }

                _appliedMode = FanControlMode.Fixed;
                _appliedDuty = _service.CommandedDuty;
                break;
            case FanMode.Curve when profile.FanCurve is not null:
                try
                {
                    _service.SetCurve(profile.FanCurve);
                }
                catch (ArgumentException ex)
                {
                    return $"its stored fan curve was rejected ({ex.Message})";
                }

                _appliedMode = FanControlMode.Curve;
                break;
            case FanMode.Curve:
                return "it selects a fan curve but carries none";
            default:
                if (!_service.SetAuto())
                {
                    return "the driver did not accept the release to firmware fan control";
                }

                _appliedMode = FanControlMode.Auto;
                break;
        }

        Interlocked.Exchange(ref _commandFailures, 0);
        _applied = true;
        return null;
    }

    private void OnCommandFailed(NvmlReturn rc)
    {
        Interlocked.Exchange(ref _lastFailure, (int)rc);
        Interlocked.Increment(ref _commandFailures);
    }

    /// <summary>
    /// Null while the fans still run the policy this run applied; otherwise
    /// what changed. Checked at the end of each mode, so a curve has had a whole
    /// mode of telemetry to take control.
    /// </summary>
    internal string? Drift()
    {
        if (!_applied)
        {
            return null;
        }

        if (TakenOver() is string takeover)
        {
            return takeover;
        }

        if (Volatile.Read(ref _commandFailures) > 0)
        {
            return $"a fan command failed during the run ({(NvmlReturn)Volatile.Read(ref _lastFailure)}), so the fans " +
                "were not following the profile's policy";
        }

        // A curve commands nothing until telemetry with a temperature reaches
        // it; until then the fans keep whatever they were doing before.
        return _appliedMode == FanControlMode.Curve && _service.CommandedDuty is null
            ? "the profile's fan curve never took control of the fans (no temperature reading reached it)"
            : null;
    }

    /// <summary>Another control moved the fans off this run's policy — they are no longer this run's to restore.</summary>
    private string? TakenOver()
    {
        var mode = _service.Mode;
        if (mode != _appliedMode)
        {
            return $"the fans were switched to {mode} by another control during the run";
        }

        return _appliedMode == FanControlMode.Fixed && _service.CommandedDuty != _appliedDuty
            ? $"the fans were set to {_service.CommandedDuty:F0}% by another control during the run"
            : null;
    }

    /// <summary>
    /// Puts back the fan configuration found when the run started, and says
    /// what happened in a line for the run's log. Leaves the fans alone when
    /// another control took them over mid-run: that choice is newer than ours.
    /// Null when the run never touched the fans.
    /// </summary>
    internal string? Restore()
    {
        if (!_attempted)
        {
            return null;
        }

        _service.CommandFailed -= OnCommandFailed;
        _attempted = false;

        if (_applied && TakenOver() is string takeover)
        {
            return $"Fans left as they are: {takeover}.";
        }

        if (_before is not { } before)
        {
            return _service.SetAuto()
                ? "Fans handed back to firmware control (their configuration before the run is not known here)."
                : "WARNING: the fans could not be handed back to firmware control — set them on the Fans page.";
        }

        if (_applied && _appliedMode == FanControlMode.Auto && before.Mode == FanMode.Auto)
        {
            return "Fans stay under firmware control, as before the run.";
        }

        return GameSessionRestore.ApplyFans(_service, before)
            ? $"Fans put back to {Describe(before)}, as before the run."
            : $"WARNING: the fans could not be put back to {Describe(before)} — set them on the Fans page.";
    }
}
