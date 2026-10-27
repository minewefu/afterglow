using System.Diagnostics;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Stress;

public sealed record StepperOptions
{
    /// <summary>MHz added per step.</summary>
    public int StepMHz { get; init; } = 30;

    /// <summary>Burn time per step.</summary>
    public int SecondsPerStep { get; init; } = 60;

    /// <summary>Longest offset to try (clamped to the driver range).</summary>
    public int MaxOffsetMHz { get; init; } = 400;

    /// <summary>Confirmation burn length after the first failure backs off.</summary>
    public int ConfirmSeconds { get; init; } = 120;
}

public sealed record StepperStatus(
    bool Running,
    string Phase,
    int CurrentOffsetMHz,
    int? LastGoodOffsetMHz,
    TimeSpan StepElapsed,
    TimeSpan StepDuration,

    /// <summary>
    /// The run's log so far, capped at <see cref="RunLog.DefaultCapacity"/>
    /// lines plus a marker for any dropped. From a run this is a
    /// <see cref="RunLogLines"/>, numbered so a viewer can add only new lines.
    /// </summary>
    IReadOnlyList<string> Log,
    int? ResultOffsetMHz,

    /// <summary>
    /// Null while running. When the run ended EARLY (cancelled, failed): whether
    /// the starting offset was actually put back — false means the restore
    /// write FAILED and the card is still at the offset under test. At a normal
    /// finish (phase "done") the card is deliberately left at the result offset,
    /// and this instead reports whether the closing re-write of that result was
    /// confirmed by the driver — false means the result stands but could not
    /// be re-confirmed as applied; MCP exposes it as result_offset_confirmed.
    /// When another writer interrupted the run (phase "interrupted"), the
    /// stepper writes nothing more: false means the card still reads the offset
    /// this run last applied, null that the other writer moved it.
    /// <para>
    /// Callers used to infer this from "a terminal status arrived", which only
    /// says the thread unwound. ApplyOffset gives up after three tries in about
    /// 4.5 s, so a client waiting 90 s for a cancel would be told "the starting
    /// offset was restored" over a card left at an untested overclock.
    /// </para>
    /// </summary>
    bool? StartOffsetRestored = null);


/// <summary>
/// Guided core-offset stability search: step the offset up, burn each step with
/// the error-checking stress test, back off on the first artifact/TDR, then run
/// a longer confirmation burn. Produces a defensible "stable offset" — the
/// open-source answer to NVIDIA's closed OC Scanner, with a visible method.
/// <para>
/// A step is scored only against the configuration it applied. When another
/// writer touches the card mid-run (the Tuning page, a profile hotkey, a game
/// rule, automation, a panic reset), or the readback shows the offset moved,
/// the run ends as "interrupted" without scoring the step and without writing
/// anything more (R-02): it used to score a step the device had been reset
/// under, walk on, and re-apply its offset over the user's reset.
/// </para>
/// </summary>
public sealed class StabilityStepper
{
    private const string ForeignWrite =
        "another tuning write reached this GPU during the run — the Tuning page, a profile hotkey, a game rule, " +
        "automation or a reset";

    private readonly IGpuTuner _tuner;
    private readonly Lock _lock = new();
    // The whole run's log, capped (#7): the app now shows all of it, not the last 14 lines.
    private readonly RunLog _log = new();
    private Thread? _thread;
    private volatile bool _cancel;
    private StepperStatus _status = new(false, "idle", 0, null, TimeSpan.Zero, TimeSpan.Zero, RunLogLines.Empty, null);

    /// <summary>The current run's ownership of the tuning; set and used on the run thread only.</summary>
    private TuningRunGuard? _guard;

    /// <summary>The core offset this run last wrote successfully (null before the first write).</summary>
    private int? _lastWritten;

    public event Action<StepperStatus>? StatusChanged;

    public StabilityStepper(IGpuTuner tuner)
    {
        _tuner = tuner;
    }

    /// <summary>Binds the burn to the tuned card on multi-GPU systems (null = largest NVIDIA).</summary>
    public uint? TargetPciBusId { get; set; }

    /// <summary>PCI vendor of the card being tuned (defaults to NVIDIA).</summary>
    public uint TargetVendorId { get; set; } = StressAdapter.NvidiaVendorId;

    /// <summary>
    /// The burn engine for each step. Null means the real burn
    /// (<see cref="GpuStressTest"/>); tests supply a load that runs nothing, so
    /// the sweep's write/score choreography runs without a D3D device.
    /// </summary>
    internal Func<IProbeLoad>? LoadFactory { get; set; }

    public StepperStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// True while the run thread is alive — including the unwind after Cancel(),
    /// when the burn is still being stopped and the starting offset put back.
    /// A cancelled run is still a run in progress, and still owns the GPU.
    /// </summary>
    public bool IsRunning => _thread is { IsAlive: true };

    private static volatile bool _anyRunning;

    /// <summary>
    /// True while any stepper deliberately provokes instability; the app-level
    /// TDR watchdog should not auto-reset during this window (the stepper owns
    /// recovery for its own induced driver resets).
    /// </summary>
    public static bool AnyRunning
    {
        get => _anyRunning;
        private set => _anyRunning = value;
    }

    public void Start(StepperOptions options)
    {
        if (IsRunning)
        {
            // A cancelled run is still unwinding — stopping the burn and putting
            // the starting offset back. A second thread here would burn and apply
            // offsets twice on one GPU, off a stale starting-offset snapshot.
            Log("Ignored a start request — the previous run is still stopping.");
            return;
        }

        _cancel = false;
        lock (_lock)
        {
            _log.Clear();
        }

        _thread = new Thread(() =>
        {
            AnyRunning = true;
            try
            {
                Run(options);
            }
            finally
            {
                AnyRunning = false;
            }
        })
        {
            Name = "Afterglow stepper",
            IsBackground = true,
        };
        _thread.Start();
    }

    public void Cancel()
    {
        _cancel = true;
    }

    /// <summary>
    /// Cancels and waits, bounded, for the run thread to unwind — so the cancel
    /// path's restore of the starting offset actually lands before the caller
    /// moves on (app shutdown). Returns false if the run was still unwinding
    /// when the timeout expired; the caller must not then claim the offset was
    /// restored. The unwind is bounded by, at worst, an offset apply already in
    /// flight (3 retries, 1.5 s apart), one poll tick, stopping the burn — which
    /// costs up to 5 s in Burn and up to 5 s again when the using-scope disposes
    /// it — and the restoring apply. A caller wanting near-certainty should
    /// allow ~20 s; the app deliberately waits less and reports the shortfall
    /// rather than holding shutdown open. Mirrors GpuStressTest.StopAndWait.
    /// </summary>
    public bool CancelAndWait(TimeSpan timeout)
    {
        _cancel = true;
        return _thread?.Join(timeout) ?? true;
    }

    private void Log(string line)
    {
        lock (_lock)
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        }
    }

    private void Publish(bool running, string phase, int offset, int? lastGood, TimeSpan stepElapsed, TimeSpan stepDuration, int? result = null, bool? restored = null)
    {
        StepperStatus status;
        lock (_lock)
        {
            // A cached snapshot: a progress tick that added no line copies nothing.
            status = new StepperStatus(running, phase, offset, lastGood, stepElapsed, stepDuration, _log.Snapshot(), result, restored);
            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    /// <summary>True once a write this run did not make has reached the card.</summary>
    private bool Interrupted => _guard?.Interrupted == true;

    /// <summary>The interruption reason when a write from elsewhere has reached the card, else null.</summary>
    private string? ForeignWriteIfAny => Interrupted ? ForeignWrite : null;

    private void Run(StepperOptions options)
    {
        // Without a core-offset knob there is nothing to step: CoreOffsetMaxMHz
        // is 0, every "step" stays at offset 0, and the sweep used to burn a full
        // cycle and then report +0 MHz as a confirmed stable offset — a verdict
        // about a control the device does not have.
        if (!_tuner.Capabilities.SupportsCoreOffset)
        {
            Log("This GPU exposes no core-clock offset, so there is no offset to step. " +
                "Nothing was applied and no stability verdict was produced.");
            Publish(false, "failed", 0, null, TimeSpan.Zero, TimeSpan.Zero, restored: true); // nothing was written
            return;
        }

        _lastWritten = null;
        using var guard = new TuningRunGuard(_tuner);
        _guard = guard;
        try
        {
            Sweep(options);
        }
        finally
        {
            _guard = null;
        }
    }

    private void Sweep(StepperOptions options)
    {
        int step = Math.Max(5, options.StepMHz);
        int maxOffset = Math.Min(options.MaxOffsetMHz, _tuner.Capabilities.CoreOffsetMaxMHz);
        int startOffset = _tuner.ReadCurrent().CoreOffsetMHz;
        int current = startOffset;
        int? lastGood = null;

        Log($"Starting from the current core offset ({startOffset} MHz), stepping +{step} MHz up to +{maxOffset} MHz.");
        Log($"Each step burns {options.SecondsPerStep} s with bit-exact error checking; first failure backs off and confirms for {options.ConfirmSeconds} s.");

        while (!_cancel)
        {
            bool applied = ApplyOffset(current);
            if (EndIfInterrupted(ForeignWriteIfAny, lastGood, startOffset))
            {
                return;
            }

            if (!applied)
            {
                if (lastGood is null && current == startOffset)
                {
                    // The very first write, of the offset the card already
                    // holds, was refused: nothing changed, so there is nothing
                    // to restore — re-issuing the same write "as a restore"
                    // failed the same way and told the user the GPU "may still
                    // be at" an offset that was never written.
                    Log($"Could not apply the starting offset (+{current} MHz) — stopping; nothing was changed. " +
                        "(Administrator rights required.)");
                    Publish(false, "failed", startOffset, null, TimeSpan.Zero, TimeSpan.Zero, restored: true);
                    return;
                }

                Log($"Could not apply +{current} MHz — stopping. (Administrator rights required.)");
                // The refused offset never landed: the card holds the last one
                // that did (the previous step, or the start), and that is where
                // it "may still be" if the restore fails too.
                int cardMayBeAt = lastGood ?? startOffset;
                bool backAtStart = RestoreStart(startOffset, cardMayBeAt);
                if (EndIfInterrupted(ForeignWriteIfAny, lastGood, startOffset))
                {
                    return;
                }

                Publish(false, "failed", backAtStart ? startOffset : cardMayBeAt, lastGood,
                    TimeSpan.Zero, TimeSpan.Zero, restored: backAtStart);
                return;
            }

            Log($"Testing {Fmt(current)} for {options.SecondsPerStep} s…");
            var verdict = Burn(TimeSpan.FromSeconds(options.SecondsPerStep), current, lastGood);
            if (EndIfInterrupted(InterruptionAfterBurn(verdict), lastGood, startOffset))
            {
                return;
            }

            if (_cancel)
            {
                break;
            }

            if (verdict == StressState.Stopped)
            {
                lastGood = current;
                Log($"{Fmt(current)} passed.");
                if (current + step > maxOffset)
                {
                    Log($"Reached the configured ceiling (+{maxOffset} MHz).");
                    Finish(lastGood, startOffset);
                    return;
                }

                current += step;
            }
            else if (verdict is StressState.ArtifactDetected or StressState.DeviceLost)
            {
                Log($"{Fmt(current)} FAILED ({(verdict == StressState.DeviceLost ? "driver reset" : "computation errors")}).");
                int backoff = current - step - (step / 2);
                if (lastGood is int good && good < current)
                {
                    backoff = Math.Min(backoff, good);
                }

                if (backoff <= startOffset)
                {
                    Log("No stable headroom found above the starting offset.");
                    PublishEndOfSweep(current, lastGood, startOffset);
                    return;
                }

                int failedAt = current;
                current = backoff;
                Log($"Backing off to {Fmt(current)} for a {options.ConfirmSeconds} s confirmation burn…");
                bool backedOff = ApplyOffset(current);
                if (EndIfInterrupted(ForeignWriteIfAny, lastGood, startOffset))
                {
                    return;
                }

                if (!backedOff)
                {
                    // Retract the line above: the backoff did NOT happen. The
                    // card is still at the offset that just failed — and right
                    // after a driver reset (see ApplyOffset) that refusal is the
                    // expected outcome, not a rare one. Saying nothing left the
                    // "Backing off to +N MHz" line standing as the last word in
                    // a log both the app and MCP agents read.
                    Log($"Could not apply the backoff {Fmt(current)} — this GPU is still at {Fmt(failedAt)}.");
                    bool backAtStart = RestoreStart(startOffset, failedAt);
                    if (EndIfInterrupted(ForeignWriteIfAny, lastGood, startOffset))
                    {
                        return;
                    }

                    Publish(false, "failed", backAtStart ? startOffset : failedAt, lastGood,
                        TimeSpan.Zero, TimeSpan.Zero, restored: backAtStart);
                    return;
                }

                var confirm = Burn(TimeSpan.FromSeconds(options.ConfirmSeconds), current, lastGood);
                if (EndIfInterrupted(InterruptionAfterBurn(confirm), lastGood, startOffset))
                {
                    return;
                }

                if (_cancel)
                {
                    break;
                }

                if (confirm == StressState.Stopped)
                {
                    Log($"{Fmt(current)} confirmed stable.");
                    Finish(current, startOffset);
                }
                else
                {
                    Log($"{Fmt(current)} still failed — falling back to the starting offset.");
                    PublishEndOfSweep(current, lastGood, startOffset);
                }

                return;
            }
            else
            {
                Log($"Stress test could not run ({verdict}). Stopping.");
                bool restoredAfterFailure = RestoreStart(startOffset, current);
                if (EndIfInterrupted(ForeignWriteIfAny, lastGood, startOffset))
                {
                    return;
                }

                Publish(false, "failed", restoredAfterFailure ? startOffset : current, lastGood,
                    TimeSpan.Zero, TimeSpan.Zero, restored: restoredAfterFailure);
                return;
            }
        }

        Log("Cancelled — restoring the starting offset.");

        // The offset published is where the card actually is, not where we asked
        // it to be: reporting startOffset after a failed restore is a fabricated
        // reading of the very state the caller needs to act on.
        bool restored = RestoreStart(startOffset, current);
        if (EndIfInterrupted(ForeignWriteIfAny, lastGood, startOffset))
        {
            return;
        }

        Publish(false, "cancelled", restored ? startOffset : current, lastGood,
            TimeSpan.Zero, TimeSpan.Zero, restored: restored);

    }

    /// <summary>
    /// Why the step that just burned cannot be scored, or null when it can: a
    /// write from elsewhere reached the card during it, or — for changes no
    /// in-process event reports, such as another process — the card no longer
    /// reads the configuration this step applied. A driver reset is exempt from
    /// the readback: recovering from one may itself move the offsets, and that
    /// step is being scored as a failure in any case. So is a cancel, whose
    /// restore of the starting offset is what the user asked for.
    /// </summary>
    private string? InterruptionAfterBurn(StressState verdict) =>
        ForeignWriteIfAny ?? (_cancel || verdict == StressState.DeviceLost ? null : _guard?.Drift());

    /// <summary>
    /// Ends the run as "interrupted" when <paramref name="why"/> says it was, and
    /// returns whether it did. Nothing is written from here on — no next step, no
    /// closing re-write, no restore: whoever wrote last owns the card, and
    /// overwriting a deliberate act (a reset, a hotkey, a game profile) is worse
    /// than leaving an offset the user can see and reset. Where the card is left
    /// is read back and published, and said out loud when it is still the
    /// offset this run applied.
    /// </summary>
    private bool EndIfInterrupted(string? why, int? lastGood, int startOffset)
    {
        if (why is null)
        {
            return false;
        }

        Log($"Interrupted: {why}.");
        Log("The step is not scored and nothing more is written to the GPU's tuning — it stays as that change left it.");

        int live = _tuner.ReadCurrent().CoreOffsetMHz;
        bool ourOffsetLeft = _lastWritten is int written && live == written && written != startOffset;
        Log(ourOffsetLeft
            ? $"The card still reads {Fmt(live)}, the offset this run applied — the other change did not move it. " +
              "Reset it from the Tuning page or with `afterglow-cli reset` if that is not what you want."
            : $"The card now reads {Fmt(live)} core offset.");
        if (_guard?.InterruptedDuringOwnWrite == true)
        {
            Log("That change landed while the stepper was writing an offset, so either write may be the one in " +
                "force — check the Tuning page.");
        }

        Publish(false, "interrupted", live, lastGood, TimeSpan.Zero, TimeSpan.Zero,
            restored: ourOffsetLeft ? false : null);
        return true;
    }

    /// <summary>
    /// Ends a sweep that ran out of headroom, restoring the starting offset and
    /// reporting only what is true afterwards.
    /// <para>
    /// Two rules live here so neither can be applied to one path and forgotten
    /// on its twin. First, the starting offset is a RESULT only if a burn
    /// actually passed at it — otherwise nothing was ever verified. Second, a
    /// run whose restore FAILED is not "done": the card is still at the offset
    /// that just miscalculated or reset the driver, so reporting a completed
    /// sweep with a stable offset would hand an agent a clean finish over
    /// exactly the state it was searching to avoid.
    /// </para>
    /// </summary>
    /// <param name="failedAt">The offset the sweep failed at — where the card is
    /// left if the restore does not land.</param>
    private void PublishEndOfSweep(int failedAt, int? lastGood, int startOffset)
    {
        bool restored = RestoreStart(startOffset, failedAt);
        if (EndIfInterrupted(ForeignWriteIfAny, lastGood, startOffset))
        {
            return;
        }

        bool completed = lastGood is not null && restored;

        Publish(
            false,
            completed ? "done" : "failed",
            restored ? startOffset : failedAt,
            lastGood,
            TimeSpan.Zero,
            TimeSpan.Zero,
            completed ? startOffset : null,
            restored: restored);
    }

    /// <summary>
    /// Puts the starting offset back, and says so out loud when it could not be.
    /// <para>
    /// Every terminal path goes through this. The restore was open-coded at five
    /// sites, and two successive reviews each found a DIFFERENT subset of them
    /// discarding the result, skipping the log line, or omitting the restore
    /// entirely — the paths that follow a detected hardware fault, which are
    /// exactly the ones where the driver is most likely to refuse the write.
    /// </para>
    /// <para>
    /// After an interruption nothing is written, so nothing is claimed either:
    /// the caller ends the run as interrupted instead.
    /// </para>
    /// </summary>
    /// <param name="cardMayBeAt">Where the card is left if the restore fails.</param>
    private bool RestoreStart(int startOffset, int cardMayBeAt)
    {
        if (ApplyOffset(startOffset))
        {
            return true;
        }

        if (Interrupted)
        {
            return false;
        }

        Log($"The starting offset ({Fmt(startOffset)}) could NOT be restored — this GPU may still be at " +
            $"{Fmt(cardMayBeAt)}, the offset that just failed. Reset it from the Tuning page or with " +
            "`afterglow-cli reset`.");
        return false;
    }

    private void Finish(int? stable, int startOffset)
    {
        int result = stable ?? 0;

        // The sweep's finding stands either way, but "applied" is a separate
        // claim from "stable" and this one used to be made without checking.
        bool applied = ApplyOffset(result);
        if (EndIfInterrupted(
                Interrupted
                    ? $"{ForeignWrite}, just as the sweep finished. It had found {Fmt(result)} stable, but that is not " +
                      "applied over the other change and not reported as a result"
                    : null,
                stable, startOffset))
        {
            return;
        }

        if (!applied)
        {
            // The last burn ran AT this offset, so the card most likely still
            // holds it; what failed is the confirming re-write. Say that, not
            // "the card moved" — which the evidence does not support.
            Log($"WARNING: {Fmt(result)} passed, but the closing re-write of it was refused by the driver, so " +
                "the applied offset could not be re-confirmed. Check it on the Tuning page.");
        }

        Log($"Result: {Fmt(result)} core offset is stable under this test. " +
            "Validate in real games before marking the profile stable; game workloads can differ.");
        Publish(false, "done", result, stable, TimeSpan.Zero, TimeSpan.Zero, result, restored: applied);
    }

    /// <summary>
    /// Writes one core offset, keeping every other knob as it reads now. Never
    /// writes once another writer has acted (R-02) — that returns false with
    /// nothing written, and the caller ends the run as interrupted. A write that
    /// lands is read back as the configuration the next burn is scored against.
    /// </summary>
    private bool ApplyOffset(int offsetMHz)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Interrupted || _guard is not { } guard)
            {
                return false;
            }

            // Preserve everything except the core offset under test.
            var current = _tuner.ReadCurrent();
            var result = guard.Own(() => _tuner.Apply(new Profiles.TuningProfile
            {
                Name = "stepper",
                CoreOffsetMHz = offsetMHz,
                MemOffsetMHz = current.MemOffsetMHz,
                LockedCoreClockMHz = _tuner.AppliedLockMHz, // applied, never the observed ceiling
                VoltageBoostPct = current.VoltageBoostPct,
            }, reconcileVfPoints: false));
            if (result.AllSucceeded)
            {
                _lastWritten = offsetMHz;
                guard.CaptureApplied();
                return true;
            }

            // After a TDR the driver can refuse calls briefly.
            Thread.Sleep(1500);
        }

        return false;
    }

    private StressState Burn(TimeSpan duration, int offset, int? lastGood)
    {
        using IProbeLoad stress = LoadFactory?.Invoke() ?? new GpuStressTest
        {
            TargetPciBusId = TargetPciBusId,
            TargetVendorId = TargetVendorId,
        };
        var done = new ManualResetEventSlim(false);
        StressState final = StressState.Failed;

        // Set once the step is over, so the burn's own progress events can no
        // longer publish "burning" over the status we replace it with — stopping
        // a burn emits a final Stopped event, which would otherwise scrub the
        // "stopping" notice a fraction of a second after it appeared.
        bool stepOver = false;

        stress.ProgressChanged += progress =>
        {
            if (Volatile.Read(ref stepOver))
            {
                return;
            }

            Publish(true, "burning", offset, lastGood, progress.Elapsed, duration);
            if (progress.State is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed)
            {
                final = progress.State;
                done.Set();
            }
        };

        if (_cancel)
        {
            // Cancelled while the previous step's offset was being applied:
            // don't spin up a device and shaders for a burn we won't run — but
            // still say the stop registered, since the unwind takes seconds.
            Publish(true, "stopping", offset, lastGood, duration, duration);
            return StressState.Stopped;
        }

        stress.Start();

        // Another writer ends the step within a tick too: whatever the burn
        // goes on to measure is no longer about this offset.
        bool terminal = WaitForBurn(done, duration, () => _cancel || Interrupted);
        Volatile.Write(ref stepOver, true);
        if (_cancel && !Interrupted)
        {
            // Say the stop registered — ending the burn and putting the starting
            // offset back takes seconds more, and until then the last thing
            // published would still read as a step burning away normally.
            // Full elapsed/duration, not zero: a determinate progress bar bound
            // to these must not snap back to empty at the moment of the stop.
            Publish(true, "stopping", offset, lastGood, duration, duration);
        }

        bool stoppedCleanly = stress.StopAndWait(TimeSpan.FromSeconds(30));
        if (!terminal)
        {
            final = _cancel ? StressState.Stopped : stress.Progress.State switch
            {
                StressState.ArtifactDetected => StressState.ArtifactDetected,
                StressState.DeviceLost => StressState.DeviceLost,
                StressState.Failed => StressState.Failed,

                // A burn still running past its stop was abandoned, not passed.
                // Calling that Stopped would advance the sweep to a higher
                // offset on evidence this step never actually gathered.
                _ => stoppedCleanly ? StressState.Stopped : StressState.Failed,
            };
        }

        // A step that ran no load dispatches proved nothing about this offset —
        // the engine's dispatch counter includes a one-off reference pass, and
        // the closing verification on such a run compares the reference buffer
        // against itself. Passing it would mark the offset good and raise the
        // sweep, and find_stable_offset hands that number to an agent.
        if (final == StressState.Stopped && !_cancel && !Interrupted && !stress.Progress.IsCleanPass)
        {
            Log($"This step earned no verdict: {stress.Progress.VerdictGap ?? "the burn did not end in a clean stop"} " +
                "— nothing was proven at this offset.");
            return StressState.Failed;
        }

        return final;
    }

    /// <summary>How often the burn wait re-checks for a cancel request.</summary>
    private static readonly TimeSpan BurnPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Waits out one burn step: true when the burn signalled a terminal state,
    /// false when the step ran its full duration or the run was cancelled.
    /// Polls rather than blocking for the whole step, so Stop ends the step
    /// within a tick instead of leaving the GPU burning at an abandoned offset
    /// for minutes. (The MCP tool bounds its own wait one level up, in
    /// find_stable_offset, by cancelling on a time budget.)
    /// Static and internal so it can be exercised without a GPU.
    /// </summary>
    internal static bool WaitForBurn(ManualResetEventSlim done, TimeSpan duration, Func<bool> cancelled)
    {
        var burnClock = Stopwatch.StartNew();
        while (!done.Wait(BurnPollInterval))
        {
            if (cancelled() || burnClock.Elapsed >= duration)
            {
                return false;
            }
        }

        return true;
    }

    private static string Fmt(int offset) => offset >= 0 ? $"+{offset} MHz" : $"{offset} MHz";
}
