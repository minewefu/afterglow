using Afterglow.Core.Stress;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// A load engine that runs nothing. It reports what a real burn would report
/// at the points the probe listens to: a Running report with burn dispatches
/// on start (or a Failed one), and a teardown state on stop.
/// </summary>
internal sealed class FakeProbeLoad : IProbeLoad
{
    /// <summary>Report Failed on start, as an engine refused an adapter or a D3D device would.</summary>
    public bool FailOnStart { get; set; }

    /// <summary>StopAndWait reports the worker still running at the timeout.</summary>
    public bool HangOnStop { get; set; }

    /// <summary>The state the engine settles into when stopped (a hardware verdict here models the closing verification failing).</summary>
    public StressState TeardownState { get; set; } = StressState.Stopped;

    public int Starts { get; private set; }

    public bool Stopped { get; private set; }

    public event Action<StressProgress>? ProgressChanged;

    public StressProgress Progress { get; private set; } =
        new(StressState.Idle, TimeSpan.Zero, 0, 0, 0, null);

    public void Start()
    {
        Starts++;
        Progress = FailOnStart
            ? new StressProgress(StressState.Failed, TimeSpan.Zero, 0, 0, 0, "fake load refused to start")
            : new StressProgress(StressState.Running, TimeSpan.FromSeconds(1), 100, 10, 0, null, BurnDispatches: 10);
        ProgressChanged?.Invoke(Progress);
    }

    public bool StopAndWait(TimeSpan timeout)
    {
        Stopped = true;
        if (HangOnStop)
        {
            return false;
        }

        if (Progress.State == StressState.Running)
        {
            Progress = Progress with { State = TeardownState };
        }

        return true;
    }

    public void Dispose()
    {
    }
}
