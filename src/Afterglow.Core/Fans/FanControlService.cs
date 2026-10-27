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
    /// <summary>
    /// How long the shutdown release waits on this card's fan driver in all:
    /// first for a command already inside it (R-07), then for the release
    /// itself. A native call can't be cancelled, and one that never returns used
    /// to hang Afterglow's exit. Windows declares a GPU hung after 2 s without an
    /// answer (TdrDelay) and then gives threads 5 s to leave the driver
    /// (TdrDdiDelay) before it bug-checks, so a call held up by a driver reset is
    /// out within about 7 s; 8 s waits that out, while a healthy fan call takes
    /// milliseconds. NVML and NVAPI document no timeout of their own, so this is
    /// a judgement, not a guarantee: a call still inside after it is treated as
    /// stuck, and the card as possibly left on manual fans.
    /// </summary>
    public static readonly TimeSpan ShutdownDriverBudget = TimeSpan.FromSeconds(8);

    /// <summary>The least the release itself gets when a slow in-flight command used most of the budget.</summary>
    private static readonly TimeSpan ReleaseFloor = TimeSpan.FromSeconds(1);

    private readonly Func<uint, FanWriteResult> _setAllFans;
    private readonly Func<NvmlReturn> _restoreAutoFans;
    private readonly string _recordKey;
    private readonly uint _fanMinDutyPct;
    private readonly Lock _lock = new();
    private readonly Lock _commandLock = new();

    private FanControlMode _mode = FanControlMode.Auto;
    private FanCurveEvaluator? _evaluator;
    private FanTempSource _tempSource;
    private double _lastCommandedDuty = -1;

    /// <summary>
    /// The duty a per-fan manual set left the card at. Kept apart from
    /// <see cref="_lastCommandedDuty"/>, which that path deliberately clears
    /// (it is the curve's hysteresis memo), so the unclean-shutdown record can
    /// still name the percentage the fans are stuck at if the release fails.
    /// </summary>
    private double _lastManualDuty = -1;
    private bool _weControlFans;

    /// <summary>
    /// Set when a refused fan write may still have left a fan at its duty
    /// (R-06): NVIDIA's per-fan fallback writes one cooler at a time, so cooler
    /// 0 can be pinned (even stopped) while cooler 1 refused. The NVIDIA tuner
    /// now rolls such a write back and says when it could not
    /// (<see cref="FanWriteResult.FansMayBeManual"/>); a tuner that answers with
    /// one code counts every refusal but NotSupported. Shutdown releases it, and
    /// only a release the driver accepted clears it. A refusal that moved nothing
    /// no longer sets it, so it no longer ends in a release the driver refuses
    /// and an unclean record for fans nobody touched.
    /// </summary>
    private bool _fansMayBeManual;

    /// <summary>
    /// Set when the shutdown release gave up on the driver: a call into it did
    /// not come back within <see cref="ShutdownDriverBudget"/>. Nothing more is
    /// sent to this card's fans; another call could block behind the stuck one.
    /// </summary>
    private bool _driverHung;

    /// <summary>
    /// Set when the shutdown release begins (R-07). No fan command starts after
    /// it, so nothing can take the fans back once they were handed over.
    /// </summary>
    private bool _shutDown;

    /// <summary>The first shutdown release's outcome, returned to every later call.</summary>
    private bool? _shutdownReleased;
    private long _generation;

    public FanControlService(IGpuTuner tuner)
        : this(AllFansWriter(tuner), tuner.RestoreAutoFansRaw, tuner.RecordKey, tuner.Capabilities.FanMinDutyPct)
    {
    }

    /// <summary>
    /// Test seam: the same control logic over the two driver calls, so mode
    /// handling can be exercised without a GPU. The UUID and the minimum spin
    /// duty are read once — both are fixed for the life of the tuner. The
    /// write answers with one code, read as <see cref="FanWriteResult.FromRaw"/> does.
    /// </summary>
    internal FanControlService(
        Func<uint, NvmlReturn> setAllFans,
        Func<NvmlReturn> restoreAutoFans,
        string recordKey,
        uint fanMinDutyPct)
        : this(duty => FanWriteResult.FromRaw(setAllFans(duty)), restoreAutoFans, recordKey, fanMinDutyPct)
    {
    }

    /// <summary>Test seam over a write that says what it left on the card.</summary>
    internal FanControlService(
        Func<uint, FanWriteResult> setAllFans,
        Func<NvmlReturn> restoreAutoFans,
        string recordKey,
        uint fanMinDutyPct)
    {
        _setAllFans = setAllFans;
        _restoreAutoFans = restoreAutoFans;
        _recordKey = recordKey;
        _fanMinDutyPct = fanMinDutyPct;
    }

    /// <summary>See <see cref="FanWriteResult.SetAllFans"/>.</summary>
    private static Func<uint, FanWriteResult> AllFansWriter(IGpuTuner tuner) =>
        duty => FanWriteResult.SetAllFans(tuner, duty);

    /// <summary>
    /// True once the shutdown release gave up on this card's fan driver (a call
    /// into it did not return within <see cref="ShutdownDriverBudget"/>). The app
    /// then skips the driver library teardown, which could block behind it.
    /// </summary>
    public bool DriverHung
    {
        get
        {
            lock (_lock)
            {
                return _driverHung;
            }
        }
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
                // A per-fan manual set clears the curve memo on purpose; the
                // card is still at the manual duty, and saying null here read as
                // "firmware controls the fans" for a card Afterglow had pinned.
                return !_weControlFans ? null
                    : _lastCommandedDuty >= 0 ? _lastCommandedDuty
                    : _lastManualDuty >= 0 ? _lastManualDuty
                    : null;
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
    /// After <see cref="DriverHung"/> it returns false without calling the driver.
    /// </summary>
    public bool SetAuto()
    {
        long generation;
        lock (_lock)
        {
            _mode = FanControlMode.Auto;
            _evaluator = null;
            generation = ++_generation;

            // The duty memos are cleared only once the release SUCCEEDS (below):
            // clearing them here left a failed release — fans still pinned —
            // with nothing for the unclean-shutdown record to name but a stale
            // per-fan value.
        }

        // Before the command lock (a command stuck in the driver may hold it
        // for good) and again under it (the stuck call may be the shutdown's
        // own release, which holds no lock once abandoned).
        if (DriverHung)
        {
            return SkipHungDriver();
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

            if (DriverHung)
            {
                return SkipHungDriver();
            }

            rc = _restoreAutoFans();
            if (rc == NvmlReturn.Success)
            {
                lock (_lock)
                {
                    _weControlFans = false;
                    _fansMayBeManual = false;
                    _lastCommandedDuty = -1;
                    _lastManualDuty = -1;
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

        AppliedStateStore.RecordFans(null, null, _recordKey);
        return true;
    }

    /// <summary>
    /// The shutdown release found this card's driver stuck in a call that never
    /// came back; another call would block behind it, so none is made. Logged,
    /// not raised: it happens only once shutdown has begun.
    /// </summary>
    private static bool SkipHungDriver()
    {
        Diagnostics.Log.Warn("Fan release to firmware control skipped: this card's fan driver stopped answering at shutdown.");
        return false;
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
        // Afterglow took the fans over. It is still tracked in memory as a
        // possible takeover (R-06), so shutdown releases whatever it moved.
        if (!Command(duty, generation))
        {
            return false;
        }

        AppliedStateStore.RecordFans("fixed", duty, _recordKey);
        return true;
    }

    /// <summary>
    /// Registers a manual fan write that was issued directly against the tuner
    /// rather than through this service — today, the Fans page's per-cooler
    /// buttons, which drive one cooler and leave the others alone.
    /// <para>
    /// Those writes are just as persistent as <see cref="SetFixed"/>: the driver
    /// stays in manual control mode until something releases it. Without this,
    /// the service did not consider itself in control, so <see cref="Dispose"/>
    /// released nothing and no applied state was recorded — a cooler could be
    /// left pinned (or stopped) at the driver level after the process exited,
    /// with the next launch showing no warning at all. Arming the service here
    /// puts the release path, the unclean-shutdown record and "Firmware (auto)"
    /// back in charge of it. The mode is deliberately left alone: the per-fan
    /// buttons do not define a whole-card policy, they just take control.
    /// </para>
    /// </summary>
    public void NoteExternalManualControl(uint dutyPct)
    {
        lock (_lock)
        {
            _weControlFans = true;

            // Deliberately NOT written into _lastCommandedDuty. That field is the
            // curve controller's all-fan hysteresis memo ("we already commanded
            // this duty, skip the write"), and a single cooler's duty is not a
            // statement about all of them. Storing it there made the curve skip
            // its next matching update and strand the OTHER coolers at their
            // previous duty for the whole load, with the flat top of the curve
            // meaning it never self-healed. Clearing it instead makes the curve's
            // next evaluation re-command every cooler.
            _lastCommandedDuty = -1;
            _lastManualDuty = dutyPct;
        }

        AppliedStateStore.RecordFans("fixed", dutyPct, _recordKey);
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

        AppliedStateStore.RecordFans("curve", null, _recordKey);
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
            lock (_lock)
            {
                if (_shutDown || _generation != generation)
                {
                    return false;
                }
            }

            var outcome = _setAllFans((uint)Math.Round(duty));
            rc = outcome.Result;
            lock (_lock)
            {
                if (outcome.Succeeded)
                {
                    _lastCommandedDuty = duty;
                    _lastManualDuty = -1; // an all-fan command supersedes any per-fan set
                    _weControlFans = true;
                }
                else
                {
                    // R-06: a refusal that may have left some coolers at the
                    // duty makes the card possibly manual — release it on
                    // shutdown. One that moved nothing (rolled back, or no fan
                    // interface) does not. Either way neither duty memo
                    // describes the card any more (a rollback hands its coolers
                    // to firmware control): the curve's "already commanded" memo
                    // used to make it skip its next write whenever it asked for
                    // that duty again, leaving the cooler this write moved there.
                    _fansMayBeManual |= outcome.FansMayBeManual;
                    _lastCommandedDuty = -1;
                    _lastManualDuty = -1;
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

    public void Dispose() => _ = ReleaseForShutdown();

    /// <summary>
    /// The shutdown release <see cref="Dispose"/> performs, with its outcome
    /// (R-05): true when the fans are left under no control of ours — nothing
    /// was taken over, the driver accepted the release, or a card with no fan
    /// interface had nothing to release; false when a takeover may still be on
    /// the card, which is then re-recorded as unclean. The app stamps the
    /// session clean only on true. No fan command starts after the first call,
    /// and later calls return its answer without touching the driver (while
    /// the first is still waiting on it, they are told "not released").
    /// It waits on the driver for at most <see cref="ShutdownDriverBudget"/>,
    /// plus <see cref="ReleaseFloor"/> when an in-flight command used it up.
    /// </summary>
    public bool ReleaseForShutdown() => ReleaseForShutdown(ShutdownDriverBudget);

    /// <summary>Test seam: <see cref="ReleaseForShutdown()"/> with its own budget.</summary>
    internal bool ReleaseForShutdown(TimeSpan budget)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        lock (_lock)
        {
            // Stop the curve too, exactly as SetAuto does. Leaving _mode at
            // Curve with a live evaluator meant one telemetry snapshot arriving
            // after this point passed the mode guard, found no previous duty to
            // compare against (so hysteresis could not suppress it), read the
            // NEW generation and therefore passed the staleness check, and wrote
            // a fan duty AFTER the release — re-setting _weControlFans and
            // leaving the card on manual fans (0% on a zero-RPM curve) once the
            // process exited, with the session already stamped clean.
            _mode = FanControlMode.Auto;
            _evaluator = null;
            _generation++;
            _shutDown = true;

            if (_shutdownReleased is bool decided)
            {
                return decided;
            }

            // Until the driver answers: a release that throws, or never comes
            // back, must not let a later call report a clean hand-back.
            _shutdownReleased = false;
        }

        // R-07: ownership is read only while holding the command lock, so a
        // command already inside the driver when shutdown began has finished and
        // its takeover is seen. Reading it before the lock missed the FIRST
        // curve write — still in flight, flag still false — released nothing,
        // and let that write land after the "release". But the wait is bounded:
        // a command that never comes back (a hung driver; it runs on the
        // telemetry thread or another background thread) used to hang the exit.
        if (!_commandLock.TryEnter(budget))
        {
            // Whether the stuck write landed is unknown, and so recorded.
            return GiveUpOnDriver(-1, $"a fan command already in the driver did not return within {budget.TotalSeconds:0.#} s");
        }

        bool takenOver;
        double lastDuty;
        NvmlReturn rc = NvmlReturn.Success;
        bool released;
        try
        {
            bool mayBeManual;
            lock (_lock)
            {
                takenOver = _weControlFans;
                mayBeManual = _fansMayBeManual;

                // A per-fan manual set clears the curve memo on purpose, so fall
                // back to the duty it recorded: the unclean-shutdown record below
                // used to say "unknown" for a card stuck at exactly the value the
                // user had just typed.
                lastDuty = _lastCommandedDuty >= 0 ? _lastCommandedDuty : _lastManualDuty;
                _weControlFans = false;
                _fansMayBeManual = false;
                _lastCommandedDuty = -1;
                _lastManualDuty = -1;
            }

            if (!takenOver && !mayBeManual)
            {
                // Deliberately narrower than SetAuto: shutdown is not a request to
                // release, so a manual mode this service never set (another tool,
                // another process) is left exactly as the user left it.
                released = true;
            }
            else
            {
                // The release is a driver call too, and the driver may have
                // stopped answering with nothing of ours in flight (the
                // telemetry poll can be the call that is stuck). It runs on a
                // background thread so the exit can leave it behind.
                var left = budget - clock.Elapsed;
                var floor = budget < ReleaseFloor ? budget : ReleaseFloor;
                if (!Services.BoundedCall.TryRun(_restoreAutoFans, left > floor ? left : floor, "Afterglow fan release", out rc))
                {
                    return GiveUpOnDriver(lastDuty, "the fan release did not return in time");
                }

                // NotSupported after nothing but refused writes: the card has no
                // fan interface, so nothing moved and there is nothing to hand
                // back. After a write the driver accepted, it is a release that
                // did not happen.
                released = rc == NvmlReturn.Success || (rc == NvmlReturn.NotSupported && !takenOver);
            }

            lock (_lock)
            {
                _shutdownReleased = released;
            }
        }
        finally
        {
            _commandLock.Exit();
        }

        if (released)
        {
            return true;
        }

        // Logged, not raised: subscribers are being torn down with us.
        Diagnostics.Log.Warn($"Fan release to firmware control on shutdown failed: {rc}");
        RecordStillManual(lastDuty);
        return false;
    }

    /// <summary>
    /// The shutdown release stops waiting on a driver call that has not come
    /// back: it reports "not released", marks the driver hung so nothing more is
    /// sent to this card, and leaves the unclean record behind for the next launch.
    /// </summary>
    private bool GiveUpOnDriver(double lastDuty, string what)
    {
        lock (_lock)
        {
            _driverHung = true;
        }

        Diagnostics.Log.Warn($"Fan release on shutdown abandoned: {what}. No further fan calls go to this card; its fans may still be under manual control.");
        RecordStillManual(lastDuty);
        return false;
    }

    /// <summary>
    /// The fans are (or may be) still under Afterglow's manual control — pinned
    /// at the last commanded duty, possibly stopped. Re-recording keeps the
    /// record unclean on purpose: this shutdown did NOT leave the hardware as it
    /// found it, the next launch's banner must say so, and the Fans page must
    /// not claim "Firmware (auto)". App.OnExit stamps the session clean only
    /// when the release returned true (R-05). After a refused write the duty is
    /// unknown (the memos were cleared), and so recorded.
    /// </summary>
    private void RecordStillManual(double lastDuty) =>
        AppliedStateStore.RecordFans("fixed", lastDuty >= 0 ? (uint)Math.Round(lastDuty) : null, _recordKey);
}
