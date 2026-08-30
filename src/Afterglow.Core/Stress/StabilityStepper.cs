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
    IReadOnlyList<string> Log,
    int? ResultOffsetMHz);

/// <summary>
/// Guided core-offset stability search: step the offset up, burn each step with
/// the error-checking stress test, back off on the first artifact/TDR, then run
/// a longer confirmation burn. Produces a defensible "stable offset" — the
/// open-source answer to NVIDIA's closed OC Scanner, with a visible method.
/// </summary>
public sealed class StabilityStepper
{
    private readonly GpuTuner _tuner;
    private readonly object _lock = new();
    private readonly List<string> _log = [];
    private Thread? _thread;
    private volatile bool _cancel;
    private StepperStatus _status = new(false, "idle", 0, null, TimeSpan.Zero, TimeSpan.Zero, [], null);

    public event Action<StepperStatus>? StatusChanged;

    public StabilityStepper(GpuTuner tuner)
    {
        _tuner = tuner;
    }

    /// <summary>Binds the burn to the tuned card on multi-GPU systems (null = largest NVIDIA).</summary>
    public uint? TargetPciBusId { get; set; }

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

    private void Publish(bool running, string phase, int offset, int? lastGood, TimeSpan stepElapsed, TimeSpan stepDuration, int? result = null)
    {
        StepperStatus status;
        lock (_lock)
        {
            status = new StepperStatus(running, phase, offset, lastGood, stepElapsed, stepDuration, _log.ToArray(), result);
            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    private void Run(StepperOptions options)
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
            if (!ApplyOffset(current))
            {
                Log($"Could not apply +{current} MHz — stopping. (Administrator rights required.)");
                Publish(false, "failed", current, lastGood, TimeSpan.Zero, TimeSpan.Zero);
                return;
            }

            Log($"Testing {Fmt(current)} for {options.SecondsPerStep} s…");
            var verdict = Burn(TimeSpan.FromSeconds(options.SecondsPerStep), current, lastGood);
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
                    Finish(lastGood, options);
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
                    _ = ApplyOffset(startOffset);
                    Publish(false, "done", startOffset, lastGood, TimeSpan.Zero, TimeSpan.Zero, startOffset);
                    return;
                }

                current = backoff;
                Log($"Backing off to {Fmt(current)} for a {options.ConfirmSeconds} s confirmation burn…");
                if (!ApplyOffset(current))
                {
                    Publish(false, "failed", current, lastGood, TimeSpan.Zero, TimeSpan.Zero);
                    return;
                }

                var confirm = Burn(TimeSpan.FromSeconds(options.ConfirmSeconds), current, lastGood);
                if (_cancel)
                {
                    break;
                }

                if (confirm == StressState.Stopped)
                {
                    Log($"{Fmt(current)} confirmed stable.");
                    Finish(current, options);
                }
                else
                {
                    Log($"{Fmt(current)} still failed — falling back to the starting offset.");
                    _ = ApplyOffset(startOffset);
                    Publish(false, "done", startOffset, lastGood, TimeSpan.Zero, TimeSpan.Zero, startOffset);
                }

                return;
            }
            else
            {
                Log($"Stress test could not run ({verdict}). Stopping.");
                _ = ApplyOffset(startOffset);
                Publish(false, "failed", startOffset, lastGood, TimeSpan.Zero, TimeSpan.Zero);
                return;
            }
        }

        Log("Cancelled — restoring the starting offset.");
        _ = ApplyOffset(startOffset);
        Publish(false, "cancelled", startOffset, lastGood, TimeSpan.Zero, TimeSpan.Zero);
    }

    private void Finish(int? stable, StepperOptions options)
    {
        int result = stable ?? 0;
        _ = ApplyOffset(result);
        Log($"Result: {Fmt(result)} core offset is stable under this test. " +
            "Validate in real games before marking the profile stable; game workloads can differ.");
        Publish(false, "done", result, stable, TimeSpan.Zero, TimeSpan.Zero, result);
    }

    private bool ApplyOffset(int offsetMHz)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Preserve everything except the core offset under test.
            var current = _tuner.ReadCurrent();
            if (_tuner.Apply(new Profiles.TuningProfile
            {
                Name = "stepper",
                CoreOffsetMHz = offsetMHz,
                MemOffsetMHz = current.MemOffsetMHz,
                LockedCoreClockMHz = current.LockedCoreClockMHz,
                VoltageBoostPct = current.VoltageBoostPct,
            }, reconcileVfPoints: false).AllSucceeded)
            {
                return true;
            }

            // After a TDR the driver can refuse calls briefly.
            Thread.Sleep(1500);
        }

        return false;
    }

    private StressState Burn(TimeSpan duration, int offset, int? lastGood)
    {
        using var stress = new GpuStressTest { TargetPciBusId = TargetPciBusId };
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
        bool terminal = WaitForBurn(done, duration, () => _cancel);
        Volatile.Write(ref stepOver, true);
        if (_cancel)
        {
            // Say the stop registered — ending the burn and putting the starting
            // offset back takes seconds more, and until then the last thing
            // published would still read as a step burning away normally.
            // Full elapsed/duration, not zero: a determinate progress bar bound
            // to these must not snap back to empty at the moment of the stop.
            Publish(true, "stopping", offset, lastGood, duration, duration);
        }

        stress.StopAndWait(TimeSpan.FromSeconds(5));
        if (!terminal)
        {
            final = _cancel ? StressState.Stopped : stress.Progress.State switch
            {
                StressState.ArtifactDetected => StressState.ArtifactDetected,
                StressState.DeviceLost => StressState.DeviceLost,
                StressState.Failed => StressState.Failed,
                _ => StressState.Stopped,
            };
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
