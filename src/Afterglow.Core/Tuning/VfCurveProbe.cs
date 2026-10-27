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

    /// <summary>
    /// Something other than the probe wrote this card's tuning mid-sweep (a
    /// Tuning-page Reset, a profile, a hotkey, a game or automation rule): the
    /// probe stopped there and did not pin the clock again (R-02).
    /// </summary>
    Interrupted,
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
    string? LoadFailure = null,

    /// <summary>
    /// Measured steps whose voltage bin ended below
    /// <see cref="VfCurveRecorder.PlanMinBinSamples"/>, so no undervolt can be
    /// planned at that voltage yet (R-47). Independent of
    /// <see cref="Outcome"/>: every target can be measured and still be too
    /// thinly sampled to drive a hardware write.
    /// </summary>
    int StepsBelowPlanCoverage = 0);

/// <summary>
/// Actively probes the GPU's voltage/frequency curve: locks the core clock at a
/// series of frequencies, applies a compute load so the GPU actually boosts to
/// the locked clock, and records the voltage the driver selects for it. That is
/// the curve, measured point by point on the real silicon — including the effect
/// of any applied offset.
///
/// This complements the driver's stored V/F table (which Afterglow reads and
/// writes on every GPU that answers the probe, RTX 50 included): the table says
/// what the driver intends, the probe measures what the GPU actually runs under
/// load. (This comment used to say RTX 50 removed the curve query and rejected
/// curve writes — retracted: that was a struct-layout bug of ours, F-51.)
/// Requires elevation (clock locking is a privileged operation) and restores
/// the previous lock state when finished or cancelled.
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

    /// <summary>
    /// Minimum seconds of sampling per clock step. A step keeps sampling past
    /// it until its voltage bin holds <see cref="VfCurveRecorder.PlanMinBinSamples"/>
    /// samples, or <see cref="MaxSamplesPerStep"/> polls have been taken.
    /// </summary>
    public double SampleSeconds { get; set; } = 1.5;

    /// <summary>
    /// Most telemetry polls one step may take while its voltage bin fills to
    /// <see cref="VfCurveRecorder.PlanMinBinSamples"/>. The window above alone
    /// (1.5 s at 150 ms) gave ten samples against a plan minimum of twenty, so
    /// a completed fresh sweep could not plan anywhere (R-47). Three times the
    /// minimum leaves room for samples that straddle a bin boundary; a step
    /// that still falls short is counted in
    /// <see cref="VfProbeProgress.StepsBelowPlanCoverage"/>, never hidden.
    /// </summary>
    public int MaxSamplesPerStep { get; set; } = (int)(3 * VfCurveRecorder.PlanMinBinSamples);

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

    // --- Writes the probe did not make (R-02) --------------------------------
    // A Tuning-page Reset released the probe's pin, and the sweep simply went
    // on to the next step and pinned the clock again — undoing the user's
    // Reset for the rest of the run. Every tuner write raises StateWritten on
    // the writing thread; the probe's own three calls do so on the run thread
    // while _ownWrite is set. Anything else is somebody else changing this
    // card under the measurement, and the sweep stops without another pin.

    /// <summary>Set (once) by the first foreign write of a run; read with Volatile.</summary>
    private string? _interruption;

    /// <summary>The thread <see cref="Run"/> executes on — the only thread the probe writes from.</summary>
    private int _runThreadId;

    /// <summary>True while the run thread is inside one of the probe's own tuner calls.</summary>
    private bool _ownWrite;

    private bool Interrupted => Volatile.Read(ref _interruption) is not null;

    /// <summary>Cancelled by the user, or interrupted by a write the probe did not make.</summary>
    private bool Stopping => _cancel || Interrupted;

    private void OnTunerStateWritten(IGpuTuner tuner)
    {
        // Thread first: _ownWrite is only meaningful on the run thread, and a
        // write from any other thread is foreign whatever the run thread is doing.
        if (Environment.CurrentManagedThreadId == _runThreadId && _ownWrite)
        {
            return;
        }

        Interlocked.CompareExchange(
            ref _interruption,
            "interrupted — this card's tuning was changed outside the probe (a reset, a profile, a hotkey, " +
            "a rule or another page) while it ran, so it stopped there and did not pin the clock again",
            null);
    }

    /// <summary>Runs one of the probe's own tuner writes, so its StateWritten echo is recognised as ours.</summary>
    private T OwnWrite<T>(Func<T> write)
    {
        _ownWrite = true;
        try
        {
            return write();
        }
        finally
        {
            _ownWrite = false;
        }
    }

    /// <summary>
    /// False when the last run ended without the driver accepting the restore of
    /// the pre-probe lock state — the probe's exact clock pin may still be in
    /// force. True before any run and after a restore the driver accepted.
    /// </summary>
    public bool LastRestoreSucceeded { get; private set; } = true;

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
        return _thread is not { } thread || thread.Join(timeout);
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

        // Listen for writes before the first one the probe makes, and until
        // after its last: a foreign write anywhere in between stops the sweep.
        _runThreadId = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _interruption, null);
        _tuner.StateWritten += OnTunerStateWritten;
        try
        {
            Sweep(recorder, maxClock);
        }
        finally
        {
            _tuner.StateWritten -= OnTunerStateWritten;
        }
    }

    private void Sweep(VfCurveRecorder recorder, uint maxClock)
    {
        // The sweep's samples belong to the tuning the card runs now (R-20):
        // bins measured under any other tuning — or under tuning nobody
        // recorded — are set aside first rather than merged with these.
        recorder.SetTuningState(VfTuningState.Read(_tuner));

        // The pre-probe state to put back afterwards: the lock Afterglow
        // APPLIED, never a ceiling merely observed. Refresh first where the
        // driver reads the lock back, so a record from a crashed session that a
        // reboot has since cleared is dropped rather than "restored" as a fresh
        // clamp — but take the value from AppliedLockMHz, which is
        // provenance-aware: restoring an observed factory ceiling wrote it back
        // as "written by Afterglow" and blocked the adoption path forever.

        // Release whatever clamp the driver shows BEFORE the sweep — the probe
        // restores or releases at the end anyway. Sweeping under a clamp had
        // two failure shapes: a leftover pin from a dead session read as the
        // ceiling and produced a two-point "complete" curve, and a tuning lock
        // hid the factory ceiling so the last target was refused every time.
        // Once released, the tuner knows the true ceiling and the targets are
        // capped at it. A clamp this process did not apply (another tool's, or
        // the factory ceiling) is released with the rest, and the outcome says so.
        // The tuner prepares the sweep: it remembers the lock this process
        // applied (so AppliedLockMHz keeps answering with it while pins
        // stand), releases whatever clamp is on the card so the sweep runs
        // against the true ceiling, and says how high the sweep may pin.
        var start = OwnWrite(_tuner.BeginProbe);
        if (start.Refusal is { } refusal)
        {
            Report(new VfProbeProgress(
                false, 0, 0, 0, null, null,
                $"the clock lock present before the probe could not be released ({refusal}); nothing was pinned",
                VfProbeOutcome.Aborted));
            return;
        }

        string? foreignClampNote = start.ForeignClampNote;
        if (start.MaxClockMHz > 0 && start.MaxClockMHz < maxClock)
        {
            maxClock = start.MaxClockMHz;
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
        var abortLock = new Lock();
        int stepsDone = 0;
        int stepsBelowCoverage = 0;

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
            for (int i = 0; i < targets.Count && !Stopping; i++)
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

                // Checked again right before the pin: the report's handlers ran
                // on this thread and may have written the card themselves.
                if (Stopping)
                {
                    break;
                }

                // The tuner puts the pin on record before it lands and resolves
                // it on every verified release, so a process killed anywhere
                // in the sweep leaves the truthful record behind.
                var lockRc = OwnWrite(() => _tuner.LockClockForProbe(target));
                if (lockRc != NvmlReturn.Success)
                {
                    // Unknown means the driver ACCEPTED the write and only the
                    // readback failed to confirm it; the tuner tracks that a
                    // pin landed, so EndProbe still releases it.
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

                // A foreign write can still race this pin: it releases the card
                // under the tuner's lock, this pin lands, and only then does its
                // notification arrive. Every wait, sample and pin below checks
                // first, so the sweep stops at the next check and the finally
                // block undoes the pin: back to the lock that write applied, or
                // released when it applied none. Refusing the pin itself would
                // need the tuner to own the check (it holds the lock both writes take).
                Sleep(SettleSeconds);
                if (Stopping)
                {
                    break;
                }

                // Sample for at least SampleSeconds, then on until this step's
                // voltage bin can drive a plan (R-47) or the poll budget is spent.
                double voltageSum = 0, clockSum = 0;
                int samples = 0;
                int polls = 0;
                var stepBins = new Dictionary<double, int>();
                var minUntil = DateTime.UtcNow.AddSeconds(SampleSeconds);
                while (!Stopping)
                {
                    if (DateTime.UtcNow >= minUntil && (polls >= MaxSamplesPerStep || StepCanPlan(recorder, stepBins)))
                    {
                        break;
                    }

                    Thread.Sleep(SampleIntervalMs);
                    if (Interrupted)
                    {
                        break; // a reading taken after somebody else's write is not this sweep's
                    }

                    var snapshot = _sample();
                    polls++;
                    if (recorder.TryAdd(snapshot, out double bin))
                    {
                        stepBins[bin] = stepBins.GetValueOrDefault(bin) + 1;
                    }

                    if (snapshot.CoreVoltageMv is double mv && snapshot.CoreClockMHz is uint mhz)
                    {
                        voltageSum += mv;
                        clockSum += mhz;
                        samples++;
                    }
                }

                if (Interrupted)
                {
                    break; // cut short by a foreign write: not a measured step
                }

                if (!_cancel && !StepCanPlan(recorder, stepBins))
                {
                    stepsBelowCoverage++;
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
            // Whether a foreign write stopped the SWEEP. Read before EndProbe:
            // a write landing during the restore or the teardown did not cut
            // any measurement short.
            string? interruption = Volatile.Read(ref _interruption);

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
            // The tuner puts back the lock BeginProbe remembered — or the one a
            // write that interrupted the sweep applied, which supersedes it — or
            // releases the pin when there is none. The result is checked: this is the
            // call that undoes an exact clock pin, and discarding its return
            // left the GPU pinned while the UI announced the previous state
            // was restored.
            var end = OwnWrite(_tuner.EndProbe);
            string? restoreFailure = end.Applied ? null : end.Detail;
            LastRestoreSucceeded = end.Applied;

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
                : interruption is not null ? VfProbeOutcome.Interrupted
                : _cancel ? VfProbeOutcome.Cancelled
                : VfProbeOutcome.Completed;

            string phase = abortReason ?? interruption ?? (_cancel ? "cancelled" : "complete");
            if (abortReason is not null && interruption is not null)
            {
                phase = $"{phase} — note: {interruption}";
            }

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

            // Measured is not plannable (R-47): say how many steps the plan
            // gate will still refuse, rather than let a "complete" sweep imply
            // every voltage it visited can now drive an undervolt.
            if (stepsBelowCoverage > 0)
            {
                phase = $"{phase} — note: {stepsBelowCoverage} of {stepsDone} measured step(s) have fewer than " +
                    $"{VfCurveRecorder.PlanMinBinSamples} samples in their voltage bin, so an undervolt cannot be " +
                    "planned at those voltages yet";
            }

            Log.Info($"V/F probe finished ({outcome}, {stepsDone}/{targets.Count} steps, " +
                $"{stepsBelowCoverage} below plan coverage).");
            Report(new VfProbeProgress(
                false, stepsDone, targets.Count, 0, null, null, phase, outcome,
                RestoreFailed: restoreFailure is not null,
                LoadFailure: loadFailure,
                StepsBelowPlanCoverage: stepsBelowCoverage));
        }
    }

    /// <summary>
    /// True once the bin most of this step's samples fell into holds enough
    /// samples (this run's and any earlier ones) for a plan at that voltage.
    /// </summary>
    private static bool StepCanPlan(VfCurveRecorder recorder, Dictionary<double, int> stepBins) =>
        stepBins.Count > 0
        && recorder.SamplesInBinAt(stepBins.MaxBy(pair => pair.Value).Key) >= VfCurveRecorder.PlanMinBinSamples;

    private void Sleep(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !Stopping)
        {
            Thread.Sleep(100);
        }
    }

    private void Report(VfProbeProgress progress) => ProgressChanged?.Invoke(progress);
}
