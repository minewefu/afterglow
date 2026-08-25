namespace Afterglow.Core.Telemetry;

/// <summary>
/// Anything that can produce <see cref="GpuSnapshot"/>s: the real NVML-backed
/// poller, or the demo source used for development, CI, and screenshots on
/// machines without NVIDIA hardware.
/// </summary>
public interface ISensorSource
{
    uint DeviceIndex { get; }

    GpuSnapshot Poll();
}
