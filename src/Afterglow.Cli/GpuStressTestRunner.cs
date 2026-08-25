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

    public void Start()
    {
        _current = new GpuStressTest { IterationsPerDispatch = Intensities[_index] };
        _current.Start();
    }

    /// <summary>Restarts the burn at the next intensity in the sweep.</summary>
    public void NextIntensity()
    {
        _current?.StopAndWait(TimeSpan.FromSeconds(5));
        _current?.Dispose();
        _index = (_index + 1) % Intensities.Length;
        _current = new GpuStressTest { IterationsPerDispatch = Intensities[_index] };
        _current.Start();
    }

    public void Dispose()
    {
        _current?.StopAndWait(TimeSpan.FromSeconds(5));
        _current?.Dispose();
        _current = null;
    }
}
