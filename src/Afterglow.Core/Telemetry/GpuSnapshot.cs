using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Core.Telemetry;

/// <summary>
/// One immutable reading of every sensor Afterglow tracks for a GPU.
/// A null field means the metric is unsupported on this GPU/driver or was not
/// sampled this tick. Values come from NVML unless noted; NVAPI-only sensors
/// (hot spot, memory junction, voltage, per-fan RPM) are filled in when the
/// NVAPI enrichment layer is attached.
/// </summary>
public sealed record GpuSnapshot
{
    public required DateTimeOffset Timestamp { get; init; }
    public required uint DeviceIndex { get; init; }

    // Clocks (MHz)
    public uint? CoreClockMHz { get; init; }
    public uint? MemClockMHz { get; init; }
    public uint? VideoClockMHz { get; init; }

    // Temperatures (°C)
    public uint? GpuTempC { get; init; }
    public double? HotSpotTempC { get; init; }
    public double? MemJunctionTempC { get; init; }

    // Utilization (%)
    public uint? GpuUtilPct { get; init; }
    public uint? MemCtrlUtilPct { get; init; }
    public uint? EncoderUtilPct { get; init; }
    public uint? DecoderUtilPct { get; init; }

    // Memory (bytes)
    public ulong? VramUsedBytes { get; init; }
    public ulong? VramTotalBytes { get; init; }

    // Power
    /// <summary>Board power, instantaneous where the driver exposes it (falls back to the averaged counter).</summary>
    public double? PowerW { get; init; }

    /// <summary>The driver's ~1-second averaged board power (always available).</summary>
    public double? PowerAvgW { get; init; }

    public double? PowerLimitW { get; init; }

    /// <summary>Cumulative board energy since driver load.</summary>
    public double? EnergyWh { get; init; }

    // Voltage (NVAPI)
    public double? CoreVoltageMv { get; init; }

    // State
    public uint? PerfState { get; init; }
    public NvmlClocksEventReasons? ThrottleReasons { get; init; }

    /// <summary>°C of headroom before the GPU starts throttling (driver-reported).</summary>
    public int? ThrottleMarginC { get; init; }

    // Fans
    public IReadOnlyList<uint>? FanPercents { get; init; }
    public IReadOnlyList<uint>? FanRpms { get; init; }

    // PCIe (sampled at a slower cadence; value is the most recent sample)
    public uint? PcieTxKBps { get; init; }
    public uint? PcieRxKBps { get; init; }

    /// <summary>Highest fan duty across all fans, for compact display.</summary>
    public uint? MaxFanPercent
    {
        get
        {
            if (FanPercents is not { Count: > 0 })
            {
                return null;
            }

            uint max = 0;
            foreach (uint f in FanPercents)
            {
                max = Math.Max(max, f);
            }

            return max;
        }
    }
}
