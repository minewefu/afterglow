namespace Afterglow.Core.Stress;

/// <summary>
/// The load engine as the V/F probe drives it. <see cref="GpuStressTest"/> is
/// the real burn; tests substitute an in-memory load so the probe's clock-lock
/// choreography (release before, pin, restore, record) runs without a D3D
/// device.
/// </summary>
public interface IProbeLoad : IDisposable
{
    event Action<StressProgress>? ProgressChanged;

    StressProgress Progress { get; }

    void Start();

    /// <summary>False when the worker was still running at the timeout.</summary>
    bool StopAndWait(TimeSpan timeout);
}
