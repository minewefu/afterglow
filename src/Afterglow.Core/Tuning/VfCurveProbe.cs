using Afterglow.Core.Diagnostics;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Stress;

namespace Afterglow.Core.Tuning;

/// <summary>How a probe run ended. A sweep that stopped early must never be
/// presentable as a finished one.</summary>
public enum VfProbeOutcome
{
    /// <summary>Still sweeping (progress records).</summary>
    Running,

    /// <summary>Every target clock was measured.</summary>
    Completed,

    /// <summary>The user stopped it.</summary>
    Cancelled,

    /// <summary>It stopped early — the reason is in <see cref="VfProbeProgress.Phase"/>.</summary>
    Aborted,
}

public sealed record VfProbeProgress(
    bool Running,
    int StepIndex,
    int StepCount,
    uint TargetClockMHz,
    double? MeasuredVoltageMv,
    double? MeasuredClockMHz,
    string Phase,
    VfProbeOutcome Outcome = VfProbeOutcome.Running,

    /// <summary>
    /// The probe could not put the clock state back, so the GPU may still be
    /// pinned. Independent of <see cref="Outcome"/>, which describes coverage
    /// only — a fully measured sweep can still fail to restore.
    /// </summary>
    bool RestoreFailed = false,

    /// <summary>
    /// The load engine reported a hardware fault (miscalculation or driver
    /// reset), during the sweep or while winding down after the clock restore.
    /// Independent of <see cref="Outcome"/> for the same reason: a fully
    /// measured sweep can still end with the GPU proving unstable. Null when
    /// nothing was detected — a load that merely failed to stop in time is
    /// noted in <see cref="Phase"/>, never reported here.
    /// </summary>
    string? LoadFailure = null);

/// <summary>
/// Actively probes the GPU's voltage/frequency curve: locks the core clock at a
/// series of frequencies, applies a compute load so the GPU actually boosts to
/// the locked clock, and records the voltage the driver selects for it. That is
/// the curve, measured point by point on the real silicon — including the effect
/// of any applied offset.
///
/// This is how a V/F curve can still be obtained on RTX 50, where NVIDIA has
/// removed the curve query and rejects curve writes. Requires elevation (clock
/// locking is a privileged operation) and restores the previous lock state when
/// finished or cancelled.
/// </summary>
public sealed class VfCurveProbe
{
    private readonly IGpuTuner _tuner;
    private readonly Func<Telemetry.GpuSnapshot> _sample;
    private volatile bool _cancel;
    private Thread? _thread;

    /// <summary>Seconds to hold each clock before sampling (settle time).</summary>
    public double SettleSeconds { get; set; } = 2.5;

    /// <summary>Milliseconds between samples while a clock is held; tests shorten it.</summary>
    public int SampleIntervalMs { get; set; } = 150;

    /// <summary>Seconds of sampling per clock step.</summary>
    public double SampleSeconds { get; set; } = 1.5;

    /// <summary>Clock step in MHz.</summary>
    public uint StepMHz { get; set; } = 150;

    /// <summary>Lowest clock to probe.</summary>
    public uint MinClockMHz { get; set; } = 600;

    public event Action<VfProbeProgress>? ProgressChanged;

    public bool IsRunning => _thread is { IsAlive: true };

    public VfCurveProbe(IGpuTuner tuner, Func<Telemetry.GpuSnapshot> sample)
    {
        _tuner = tuner;
        _sample = sample;
    }

    /// <summary>Binds the probe's load to the tuned card on multi-GPU systems.</summary>
    public uint? TargetPciBusId { get; set; }

    /// <summary>PCI vendor of the card being tuned (defaults to NVIDIA).</summary>
    public uint TargetVendorId { get; set; } = Stress.StressAdapter.NvidiaVendorId;

    /// <summary>
    /// The load engine to drive during the sweep. Null means the real burn
    /// (<see cref="GpuStressTest"/>); tests supply a load that runs nothing.
    /// </summary>
    public Func<IProbeLoad>? LoadFactory { get; set; }

    public void Cancel() => _cancel = true;

    /// <summary>
    /// Cancels the sweep and waits for the worker to finish its restore.
    /// <para>
    /// The probe runs on a background thread and pins the core clock at an EXACT
    /// frequency; that pin is undone only in the worker's finally block. A
    /// process exit kills a background thread without running finally, so
    /// closing the app mid-probe left the GPU pinned at whatever clock the sweep
    /// had reached — persisting at the driver level until an explicit unlock or
    /// a reboot. Shutdown must call this before the process goes away.
    /// </para>
    /// Returns false if the worker was still running at the timeout.
    /// </summary>
    public bool CancelAndWait(TimeSpan timeout)
    {
        _cancel = true;
        return _thread?.Join(timeout) ?? true;
    }

    /// <summary>Runs the sweep on a background thread, feeding points into the recorder.</summary>
    public void Start(VfCurveRecorder recorder)
    {
        if (IsRunning)
        {
            return;
        }

        _cancel = false;
        _thread = new Thread(() => Run(recorder))
        {
            Name = "Afterglow VF probe",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>Runs the sweep synchronously (CLI path).</summary>
    public void Run(VfCurveRecorder recorder)
    {
        uint maxClock = _tuner.Capabilities.MaxCoreClockMHz;
        if (maxClock == 0)
        {
            // Aborted, not Running: this is the one exit that never carried an
            // outcome, so the CLI's Aborted check missed it and `vfcurve --probe`
            // printed a previously persisted curve and exited 0 on a GPU where
            // nothing had been locked or measured.
            Report(new VfProbeProgress(
                false, 0, 0, 0, null, null,
                "clock locking is not supported on this GPU", VfProbeOutcome.Aborted));
            return;
        }

        // The pre-probe state to put back afterwards: the lock Afterglow
        // APPLIED, never a ceiling merely observed. Refresh first where the
        // driver reads the lock back, so a record from a crashed session that a
        // reboot has since cleared is dropped rather than "restored" as a fresh
        // clamp — but take the value from AppliedLockMHz, which is
        // provenance-aware: restoring an observed factory ceiling wrote it back
        // as "written by Afterglow" and blocked the adoption path forever.
        uint? observedClamp = null;
        if (_tuner.LockIsDriverReadable)
        {
            observedClamp = _tuner.ReadCurrent().LockedCoreClockMHz;
        }

        uint? previousLock = _tuner.AppliedLockMHz;

        // Release whatever clamp the driver shows BEFORE the sweep — the probe
        // restores or releases at the end anyway. Sweeping under a clamp had
        // two failure shapes: a leftover pin from a dead session read as the
        // ceiling and produced a two-point "complete" curve, and a tuning lock
        // hid the factory ceiling so the last target was refused every time.
        // Once released, the tuner knows the true ceiling and the targets are
        // capped at it. A clamp this process did not apply (another tool's, or
        // the factory ceiling) is released with the rest, and the outcome says so.
        string? foreignClampNote = null;
        uint? sweepCeiling = null;
        if (observedClamp is uint observed && observed > 0)
        {
            var pre = _tuner.ForceUnlock();
            if (!pre.Applied && previousLock is uint held)
            {
                // The lock this process applied sits where its release cannot
                // be verified — at a ceiling this process has never seen
                // released — so it stays on, the sweep runs up to it, and the
                // restore below puts it back exactly as before.
                Log.Warn($"V/F probe: the {held} MHz clock lock could not be verifiably released before the sweep " +
                    $"({pre.Detail}); sweeping up to it instead.");
                sweepCeiling = held;
            }
            else if (!pre.Applied)
            {
                Report(new VfProbeProgress(
                    false, 0, 0, 0, null, null,
                    $"the clock lock present before the probe could not be released ({pre.Detail}); nothing was pinned",
                    VfProbeOutcome.Aborted));
                return;
            }
            else if (previousLock is null)
            {
                foreignClampNote = $"the driver reported a {observed} MHz clock ceiling this process had not applied " +
                    "(another tool's clamp, or the factory ceiling); it was released before the sweep, " +
                    "and the card is at its factory range";
            }
        }

        uint lockable = sweepCeiling ?? _tuner.MaxLockableClockMHz;
        if (lockable > 0 && lockable < maxClock)
        {
            maxClock = lockable;
        }

        var targets = new List<uint>();
        for (uint clock = MinClockMHz; clock <= maxClock; clock += StepMHz)
        {
            targets.Add(clock);
        }

        if (targets.Count == 0 || targets[^1] < maxClock - 50)
        {
            targets.Add(maxClock);
        }
        using IProbeLoad load = LoadFactory?.Invoke() ?? new GpuStressTest
        {
            IterationsPerDispatch = 2048,
            TargetPciBusId = TargetPciBusId,
            TargetVendorId = TargetVendorId,
        };

        // Written on the load engine's thread, read by the probe's loop; one
        // lock and `??=` make "first reason wins, never overwritten" true by
        // construction instead of by an argument about barriers.
        string? abortReason = null;
        var abortLock = new object();
        int stepsDone = 0;

        // Nothing to restore unless a pin actually landed. On a non-elevated
        // session step 0's lock is refused, nothing is ever pinned, and the
        // unconditional release then fails for the SAME reason — which was being
        // reported as "this GPU may still be pinned", latching an unclean-shutdown
        // record and a next-launch banner over a session that changed nothing.
        bool anyLockLanded = false;

        // Watch the load. Without a load the GPU never boosts to the locked
        // clock, so every sample is an idle-voltage reading — a curve that is
        // simply wrong, reported as "your GPU's measured V/F map".
        // Refusing a guessed adapter (the engine default) makes that failure MORE
        // likely — it is a hard refusal before the D3D device is even created —
        // so ignoring the engine's verdict would have turned a safety check into
        // a silent source of bad data.
        void OnLoadProgress(StressProgress p)
        {
            // Three different things, three different meanings. Failed is the
            // load never running (a refused adapter, no D3D device) — the sweep
            // would then sample idle voltages and call them a curve. The other
            // two are the OPPOSITE: the load ran correctly and the GPU
            // miscalculated or reset at the clock being held, which is the most
            // valuable result this tool can produce. Reporting that as "the load
            // could not run" would bury the finding.
            string? reason = p.State switch
            {
                StressState.Failed =>
                    $"the load could not run ({p.Detail ?? "no detail"}), so no clock would be held under load " +
                    "and the readings would be idle voltages",
                StressState.ArtifactDetected =>
                    $"the GPU miscalculated under load at this clock ({p.Detail ?? "bit-exact check failed"}) — " +
                    "these settings are unstable here",
                StressState.DeviceLost =>
                    $"the GPU driver reset under load at this clock ({p.Detail ?? "device removed"}) — " +
                    "these settings are unstable here",
                _ => null,
            };

            lock (abortLock)
            {
                abortReason ??= reason;
            }
        }

        load.ProgressChanged += OnLoadProgress;
        load.Start();

        try
        {
            for (int i = 0; i < targets.Count && !_cancel; i++)
            {
                string? loadAbort;
                lock (abortLock)
                {
                    loadAbort = abortReason;
                }

                if (loadAbort is not null)
                {
                    Report(new VfProbeProgress(true, i, targets.Count, 0, null, null, loadAbort));
                    break;
                }

                uint target = targets[i];
                Report(new VfProbeProgress(true, i, targets.Count, target, null, null, "settling"));

                // The tuner puts the pin on record before it lands and resolves
                // it on every verified release, so a process killed anywhere
                // in the sweep leaves the truthful record behind.
                var lockRc = _tuner.LockClockForProbe(target);
                if (lockRc != NvmlReturn.Success)
                {
                    // Unknown means the driver ACCEPTED the write and only the
                    // readback failed to confirm it (see ArcGpuTuner's
                    // WriteClampVerified), so a pin may well be on the card and
                    // the release below must be held to account for it. Treating
                    // it as "refused" dropped a failed release on the floor and
                    // let shutdown stamp the session clean over a pinned GPU.
                    if (lockRc == NvmlReturn.Unknown)
                    {
                        anyLockLanded = true;
                    }

                    // Report what the driver actually said. Only NoPermission
                    // means elevation; blaming administrator rights for every
                    // return code sent users chasing the wrong cause.
                    string refused = lockRc == NvmlReturn.NoPermission
                        ? $"clock lock refused at {target} MHz — administrator rights required"
                        : $"clock lock refused at {target} MHz ({lockRc})";
                    lock (abortLock)
                    {
                        abortReason ??= refused;
                    }

                    Report(new VfProbeProgress(true, i, targets.Count, target, null, null, refused));
                    break;
                }

                anyLockLanded = true;
                Sleep(SettleSeconds);
                if (_cancel)
                {
                    break;
                }

                double voltageSum = 0, clockSum = 0;
                int samples = 0;
                var until = DateTime.UtcNow.AddSeconds(SampleSeconds);
                while (DateTime.UtcNow < until && !_cancel)
                {
                    Thread.Sleep(SampleIntervalMs);
                    var snapshot = _sample();
                    recorder.Add(snapshot);
                    if (snapshot.CoreVoltageMv is double mv && snapshot.CoreClockMHz is uint mhz)
                    {
                        voltageSum += mv;
                        clockSum += mhz;
                        samples++;
                    }
                }

                stepsDone = i + 1;
                Report(new VfProbeProgress(true, i + 1, targets.Count, target,
                    samples > 0 ? voltageSum / samples : null,
                    samples > 0 ? clockSum / samples : null,
                    "measured"));
            }
        }
        finally
        {
            // Unhook before stopping so the burn's teardown cannot rewrite the
            // COVERAGE outcome: every step had already been measured, and letting
            // it set abortReason turned a complete 12-of-12 curve into an
            // "aborted" run that exits non-zero and is discarded.
            load.ProgressChanged -= OnLoadProgress;

            // Restore the clock state FIRST, before the load's teardown. App
            // shutdown joins this worker for 10 s and then disposes the GPU
            // services; with the 30 s teardown ahead of the restore, a slow
            // closing verification left the card exact-pinned until reboot
            // with the restore never reached.
            //
            // Restore whatever lock state existed before the probe — as the
            // RANGE lock profiles apply, never as an exact pin (which would
            // hold full clocks at idle). The result is checked: this is the call
            // that undoes an exact clock pin, and discarding its return left the
            // GPU pinned while the UI announced the previous state was restored.
            string? restoreFailure = null;
            if (previousLock is uint restore)
            {
                var restoreRc = _tuner.RestoreTuningLock(restore);
                if (restoreRc != NvmlReturn.Success)
                {
                    restoreFailure = $"the previous {restore} MHz clock lock could not be restored ({restoreRc})";
                }
            }
            else
            {
                var unlocked = _tuner.ForceUnlock();
                if (!unlocked.Applied && anyLockLanded)
                {
                    restoreFailure = $"the probe's clock lock could not be released ({unlocked.Detail})";
                }
            }

            // The same 30 s budget every verdict site uses, and the result is
            // checked: the closing verification on a slow iGPU can outlast 5 s,
            // and reading Progress after a timed-out join returned a stale
            // Running snapshot — an artifact raised by that closing check was
            // dropped and the probe reported no load fault at all. It now runs
            // after the restore, so a fault here is a fault at the restored
            // clocks — still a hardware finding, carried as a separate warning
            // so the curve stays valid.
            bool loadStopped = load.StopAndWait(TimeSpan.FromSeconds(30));

            // Only a hardware verdict is a load FAILURE. A worker still running
            // at the timeout is an unknown, noted in the phase text below,
            // never an instability verdict — the CLI prints every LoadFailure
            // as "the GPU proved unstable" and exits 1 on it.
            var teardown = load.Progress;
            string? loadFailure = teardown.IsHardwareVerdict
                ? $"the GPU {(teardown.State == StressState.DeviceLost ? "driver reset" : "miscalculated")} " +
                  "under load while the sweep was winding down, " +
                  (restoreFailure is null ? "after the clock was restored " : "after the clock restore was attempted ") +
                  $"({teardown.Detail ?? teardown.State.ToString()})"
                : null;

            recorder.Save();

            // A sweep that stopped early is not a finished sweep. Reporting the
            // full step count and "complete" regardless of how the loop exited
            // let the app present a refused or half-finished probe as "your
            // GPU's measured V/F map".
            //
            // The outcome describes COVERAGE only. A failed clock-lock restore is
            // a serious warning about hardware state, but it says nothing about
            // whether the curve was measured — folding it in here labelled a full
            // 12-of-12 sweep "incomplete" while the same line said "complete".
            var outcome = abortReason is not null ? VfProbeOutcome.Aborted
                : _cancel ? VfProbeOutcome.Cancelled
                : VfProbeOutcome.Completed;

            string phase = abortReason ?? (_cancel ? "cancelled" : "complete");
            if (restoreFailure is not null)
            {
                Log.Warn($"V/F probe: {restoreFailure}");
                phase = $"{phase} — WARNING: {restoreFailure}. The GPU may still be clock-locked; " +
                    "use Reset on the Tuning page.";
            }
            else if (foreignClampNote is not null)
            {
                // The release ran and succeeded, so whatever ceiling was there
                // before is gone — a clamp another process wrote included. The
                // old code restored it; say what happened rather than nothing.
                Log.Warn($"V/F probe: {foreignClampNote}.");
                phase = $"{phase} — note: {foreignClampNote}";
            }

            if (!loadStopped)
            {
                const string StopTimeout =
                    "the load engine did not stop within 30 s, so its closing verification is unknown";
                Log.Warn($"V/F probe: {StopTimeout}.");
                phase = $"{phase} — note: {StopTimeout}";
            }

            Log.Info($"V/F probe finished ({outcome}, {stepsDone}/{targets.Count} steps).");
            Report(new VfProbeProgress(
                false, stepsDone, targets.Count, 0, null, null, phase, outcome,
                RestoreFailed: restoreFailure is not null,
                LoadFailure: loadFailure));
        }
    }

    private void Sleep(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !_cancel)
        {
            Thread.Sleep(100);
        }
    }

    private void Report(VfProbeProgress progress) => ProgressChanged?.Invoke(progress);
}
