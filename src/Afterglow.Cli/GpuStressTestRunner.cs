using Afterglow.Core.Stress;

namespace Afterglow.Cli.Stress;

/// <summary>
/// Cycles the burn test through several load intensities, so a recording pass
/// visits multiple voltage/clock states instead of pinning one operating point.
/// </summary>
internal sealed class GpuStressTestRunner : IDisposable
{
    private static readonly uint[] Intensities = [512, 1024, 2048, 4096, 8192, 2048, 1024];

    private GpuStressTest? _current;
    private int _index;
    private string? _loadFailure;

    /// <summary>
    /// Set once any burn in the sweep reports a fault, and never cleared — the
    /// sweep restarts the engine at each intensity, so a failure not latched
    /// here disappears at the next restart. Null means every burn ran cleanly.
    /// <para>
    /// Callers need this because a recording pass that drives no load is not a
    /// lighter version of the same measurement: the GPU never boosts, so every
    /// sample is an idle-voltage reading recorded under the heading "under
    /// load". That is a wrong curve, not a thin one.
    /// </para>
    /// </summary>
    public string? LoadFailure => Volatile.Read(ref _loadFailure);

    /// <summary>
    /// True once the current burn has retired at least one dispatch — the GPU
    /// is actually under load, not still selecting an adapter, creating the
    /// device or filling its working set.
    /// </summary>
    public bool LoadRunning => _current?.Progress.BurnDispatches > 0;

    /// <summary>Binds every burn in the sweep to the tuned card on multi-GPU systems.</summary>
    public uint? TargetPciBusId { get; set; }

    /// <summary>PCI vendor of the card being tuned (defaults to NVIDIA).</summary>
    public uint TargetVendorId { get; set; } = StressAdapter.NvidiaVendorId;

    public void Start()
    {
        _current = CreateWatched();
        _current.Start();
    }

    /// <summary>
    /// Builds one burn and subscribes to its verdict, so a load that never ran
    /// — or a GPU that failed under it — is visible to the caller instead of
    /// being discarded at the next intensity change.
    /// </summary>
    private GpuStressTest CreateWatched()
    {
        var engine = new GpuStressTest
        {
            IterationsPerDispatch = Intensities[_index],
            TargetPciBusId = TargetPciBusId,
            TargetVendorId = TargetVendorId,
        };
        engine.ProgressChanged += p =>
        {
            if (_loadFailure is not null)
            {
                return;
            }

            string? failure = p.State switch
            {
                StressState.Failed =>
                    $"the load engine did not run ({p.Detail ?? "no detail"}), so these samples are idle "
                    + "voltages, not a curve measured under load",
                StressState.ArtifactDetected =>
                    $"the GPU miscalculated under load ({p.Detail ?? "no detail"}) — it is unstable at these settings",
                StressState.DeviceLost =>
                    $"the graphics device was reset under load ({p.Detail ?? "no detail"}) — it is unstable at these settings",
                _ => null,
            };
            if (failure is not null)
            {
                Volatile.Write(ref _loadFailure, failure);
            }
        };
        return engine;
    }


    /// <summary>Restarts the burn at the next intensity in the sweep.</summary>
    public void NextIntensity()
    {
        StopCurrent();

        // Once any burn has faulted — or failed to stop — the sweep is void, and
        // the caller reads LoadFailure. Starting another engine only stacked
        // workers on a device that may be dead, at 30 s of stop budget per call.
        if (_loadFailure is not null)
        {
            _current = null;
            return;
        }

        _index = (_index + 1) % Intensities.Length;
        _current = CreateWatched();
        _current.Start();
    }

    public void Dispose()
    {
        StopCurrent();
        _current = null;
    }

    /// <summary>
    /// Stops the running burn with the same 30 s budget every verdict site
    /// uses, and latches a join that timed out as a load failure: its closing
    /// verification never landed, and the next intensity used to start a second
    /// engine on the device while the abandoned one was still draining.
    /// </summary>
    private void StopCurrent()
    {
        if (_current is null)
        {
            return;
        }

        if (!_current.StopAndWait(TimeSpan.FromSeconds(30)) && _loadFailure is null)
        {
            Volatile.Write(ref _loadFailure,
                "the load engine did not stop within 30 s, so its closing verification is unknown");
        }

        _current.Dispose();
    }
}
