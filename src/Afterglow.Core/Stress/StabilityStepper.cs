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

    public bool IsRunning => _thread is { IsAlive: true } && !_cancel;

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
            }).AllSucceeded)
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
        using var stress = new GpuStressTest();
        var done = new ManualResetEventSlim(false);
        StressState final = StressState.Failed;

        stress.ProgressChanged += progress =>
        {
            Publish(true, "burning", offset, lastGood, progress.Elapsed, duration);
            if (progress.State is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed)
            {
                final = progress.State;
                done.Set();
            }
        };

        stress.Start();
        if (!done.Wait(duration))
        {
            stress.StopAndWait(TimeSpan.FromSeconds(5));
            final = _cancel ? StressState.Stopped : stress.Progress.State switch
            {
                StressState.ArtifactDetected => StressState.ArtifactDetected,
                StressState.DeviceLost => StressState.DeviceLost,
                StressState.Failed => StressState.Failed,
                _ => StressState.Stopped,
            };
        }
        else
        {
            stress.StopAndWait(TimeSpan.FromSeconds(5));
        }

        return final;
    }

    private static string Fmt(int offset) => offset >= 0 ? $"+{offset} MHz" : $"{offset} MHz";
}
