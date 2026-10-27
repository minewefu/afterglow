using Afterglow.Core.Profiles;
using Afterglow.Core.Stress;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// A burn that runs nothing and reports what a real one would at the points
/// the certifier and the stepper listen to: a long, clean Running report on
/// start (or the scripted hardware verdict), then Stopped when asked to stop.
/// <see cref="OnStart"/> runs on the run's own thread while the "burn" is
/// live, which is where tests make other writers act.
/// </summary>
internal sealed class ScriptedBurn(StressPattern pattern = StressPattern.Sustained) : IProbeLoad
{
    /// <summary>Runs inside <see cref="Start"/>, before the first progress report.</summary>
    public Action? OnStart { get; init; }

    /// <summary>
    /// Stopped for a clean pass; ArtifactDetected or DeviceLost for a
    /// demonstrated failure; Failed for an engine that could not run.
    /// </summary>
    public StressState Verdict { get; init; } = StressState.Stopped;

    public event Action<StressProgress>? ProgressChanged;

    public StressProgress Progress { get; private set; } = new(StressState.Idle, TimeSpan.Zero, 0, 0, 0, null);

    public void Start()
    {
        OnStart?.Invoke();
        Progress = Verdict switch
        {
            StressState.ArtifactDetected => new StressProgress(
                StressState.ArtifactDetected, TimeSpan.FromSeconds(20), 5, 101, 1,
                "INJECTED bit mismatch", BurnDispatches: 100, Pattern: pattern),
            StressState.DeviceLost => new StressProgress(
                StressState.DeviceLost, TimeSpan.FromSeconds(20), 5, 101, 0,
                "INJECTED device removed (TDR)", BurnDispatches: 100, Pattern: pattern),
            StressState.Failed => new StressProgress(
                StressState.Failed, TimeSpan.Zero, 0, 0, 0, "INJECTED no adapter", Pattern: pattern),

            // Elapsed far past any mode's window, so the certifier's poll ends
            // the mode on its first tick instead of waiting out 90 s.
            _ => new StressProgress(
                StressState.Running, TimeSpan.FromHours(1), 100, 6001, 0, null,
                Transitions: 12, BurnDispatches: 6000, Pattern: pattern),
        };
        ProgressChanged?.Invoke(Progress);
    }

    public bool StopAndWait(TimeSpan timeout)
    {
        if (Progress.State == StressState.Running)
        {
            Progress = Progress with { State = StressState.Stopped };
            ProgressChanged?.Invoke(Progress);
        }

        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>The VRAM twin of <see cref="ScriptedBurn"/>: one full round, clean, on start.</summary>
internal sealed class ScriptedVram : ICertifierVramLoad
{
    public Action? OnStart { get; init; }

    public StressState Verdict { get; init; } = StressState.Stopped;

    public event Action<VramProgress>? ProgressChanged;

    public VramProgress Progress { get; private set; } = new(StressState.Idle, TimeSpan.Zero, 0, 0, 0, 0, 0, null);

    public void Start()
    {
        OnStart?.Invoke();
        Progress = Verdict is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed
            ? new VramProgress(Verdict, TimeSpan.FromSeconds(20), 8L << 30, 1L << 30, 0, 1, 10, "INJECTED VRAM mismatch")
            : new VramProgress(StressState.Running, TimeSpan.FromHours(1), 8L << 30, 8L << 30, 1, 0, 10, null);
        ProgressChanged?.Invoke(Progress);
    }

    public bool StopAndWait(TimeSpan timeout)
    {
        if (Progress.State == StressState.Running)
        {
            Progress = Progress with { State = StressState.Stopped };
            ProgressChanged?.Invoke(Progress);
        }

        return true;
    }

    public void Dispose()
    {
    }
}
