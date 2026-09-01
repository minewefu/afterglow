using Afterglow.Core.Interop.Igcl;

namespace Afterglow.Core.Telemetry;

/// <summary>
/// IGCL-backed sensor source for Intel GPUs. Every metric the driver does not
/// answer stays null — the honest "unavailable" the rest of the pipeline
/// already renders. Power and utilization only exist as deltas between two
/// monotonic counters, so the first poll after start reports them as null.
/// Component handles are enumerated once in the constructor (IGCL handles stay
/// valid for the API's lifetime) and Poll runs only on the telemetry thread.
/// </summary>
public sealed class IntelSensorSource : ISensorSource
{
    private readonly IgclDevice _device;
    private readonly nint _gpuFreqDomain;
    private readonly nint _mediaFreqDomain;
    private readonly nint _memoryFreqDomain;
    private readonly nint _memoryModule;
    private readonly bool _memoryIsShared;
    private readonly IReadOnlyList<(nint Handle, CtlTempSensor Type)> _tempSensors;

    private CtlPowerTelemetry _last;
    private bool _hasLast;

    public IntelSensorSource(IgclDevice device, uint deviceIndex)
    {
        _device = device;
        DeviceIndex = deviceIndex;

        foreach (var (handle, props) in device.GetFrequencyDomains())
        {
            switch (props.Type)
            {
                case CtlFreqDomain.Gpu when _gpuFreqDomain == 0:
                    _gpuFreqDomain = handle;
                    break;
                case CtlFreqDomain.Media when _mediaFreqDomain == 0:
                    _mediaFreqDomain = handle;
                    break;
                case CtlFreqDomain.Memory when _memoryFreqDomain == 0:
                    _memoryFreqDomain = handle;
                    break;
                default:
                    break;
            }
        }

        // One module is the norm; prefer dedicated (device-local) if several.
        foreach (var (handle, props) in device.GetMemoryModules())
        {
            if (_memoryModule == 0 || props.Location == CtlMemLocation.Device)
            {
                _memoryModule = handle;
                _memoryIsShared = props.Location == CtlMemLocation.System;
            }
        }

        _tempSensors = device.GetTemperatureSensors()
            .Select(s => (s.Handle, s.Properties.Type))
            .ToArray();
    }

    public uint DeviceIndex { get; }

    public GpuSnapshot Poll()
    {
        uint? coreClock = null;
        uint? gpuTemp = null;
        uint? gpuUtil = null;
        double? powerW = null;
        double? energyWh = null;

        if (_device.TryGetPowerTelemetry(out var t) == CtlResult.Success)
        {
            if (t.GpuCurrentClockFrequency.Supported != 0)
            {
                coreClock = (uint)Math.Max(0, Math.Round(t.GpuCurrentClockFrequency.AsDouble()));
            }

            if (t.GpuCurrentTemperature.Supported != 0)
            {
                gpuTemp = (uint)Math.Max(0, Math.Round(t.GpuCurrentTemperature.AsDouble()));
            }

            if (t.GpuEnergyCounter is { Supported: not 0, Units: CtlUnits.EnergyJoules })
            {
                energyWh = t.GpuEnergyCounter.AsDouble() / 3600.0;
            }

            if (_hasLast && t.TimeStamp.Supported != 0 && _last.TimeStamp.Supported != 0)
            {
                // After a long gap (system sleep, driver outage) the delta is a
                // true average over the whole gap but would be recorded as one
                // instantaneous tick — misleading in every graph and log. Treat
                // such a sample as a re-prime instead: 30 s is 3× the longest
                // poll interval TelemetryService allows.
                double dt = t.TimeStamp.AsDouble() - _last.TimeStamp.AsDouble();
                if (dt <= 30.0)
                {
                    powerW = PowerFromEnergyCounters(_last.GpuEnergyCounter, t.GpuEnergyCounter, dt);
                    gpuUtil = UtilFromActivityCounters(_last.GlobalActivityCounter, t.GlobalActivityCounter, dt);
                }
            }

            _last = t;
            _hasLast = true;
        }

        double? coreVoltageMv = null;
        if (_gpuFreqDomain != 0 && IgclDevice.TryGetFrequencyState(_gpuFreqDomain, out var gpuState) == CtlResult.Success)
        {
            coreClock ??= gpuState.Actual >= 0 ? (uint)Math.Round(gpuState.Actual) : null;
            if (gpuState.CurrentVoltage >= 0)
            {
                coreVoltageMv = Math.Round(gpuState.CurrentVoltage * 1000.0, 1);
            }
        }

        uint? videoClock = null;
        if (_mediaFreqDomain != 0 && IgclDevice.TryGetFrequencyState(_mediaFreqDomain, out var mediaState) == CtlResult.Success
            && mediaState.Actual >= 0)
        {
            videoClock = (uint)Math.Round(mediaState.Actual);
        }

        uint? memClock = null;
        if (_memoryFreqDomain != 0 && IgclDevice.TryGetFrequencyState(_memoryFreqDomain, out var memState) == CtlResult.Success
            && memState.Actual >= 0)
        {
            memClock = (uint)Math.Round(memState.Actual);
        }

        double? memJunction = null;
        foreach (var (handle, type) in _tempSensors)
        {
            if (IgclDevice.TryGetTemperature(handle, out double c) != CtlResult.Success)
            {
                continue;
            }

            switch (type)
            {
                case CtlTempSensor.Gpu:
                case CtlTempSensor.Global when gpuTemp is null:
                    gpuTemp = (uint)Math.Max(0, Math.Round(c));
                    break;
                case CtlTempSensor.Memory:
                    memJunction = Math.Round(c, 1);
                    break;
                default:
                    break;
            }
        }

        ulong? vramUsed = null;
        ulong? vramTotal = null;
        if (_memoryModule != 0 && IgclDevice.TryGetMemoryState(_memoryModule, out var mem) == CtlResult.Success
            && mem.Total > 0)
        {
            vramUsed = mem.Total - Math.Min(mem.Free, mem.Total);
            vramTotal = mem.Total;
        }

        return new GpuSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            DeviceIndex = DeviceIndex,
            CoreClockMHz = coreClock,
            MemClockMHz = memClock,
            VideoClockMHz = videoClock,
            GpuTempC = gpuTemp,
            MemJunctionTempC = memJunction,
            GpuUtilPct = gpuUtil,
            // Encoder/decoder stay null: IGCL's media activity counter covers
            // encode+decode combined, and labeling that as either would lie.
            VramUsedBytes = vramUsed,
            VramTotalBytes = vramTotal,
            MemoryIsShared = _memoryModule != 0 ? _memoryIsShared : null,
            PowerW = powerW,
            PowerAvgW = powerW,
            EnergyWh = energyWh,
            CoreVoltageMv = coreVoltageMv,
        };
    }

    /// <summary>
    /// Average watts between two snapshots of a monotonic energy counter.
    /// Null unless both samples are supported, both are in joules, time moved
    /// forward, and the counter did not go backwards (driver reset).
    /// </summary>
    internal static double? PowerFromEnergyCounters(in CtlTelemetryItem before, in CtlTelemetryItem after, double dtSeconds)
    {
        if (before.Supported == 0 || after.Supported == 0 || dtSeconds <= 0)
        {
            return null;
        }

        if (before.Units != CtlUnits.EnergyJoules || after.Units != CtlUnits.EnergyJoules)
        {
            return null;
        }

        double delta = after.AsDouble() - before.AsDouble();
        return delta < 0 ? null : Math.Round(delta / dtSeconds, 1);
    }

    /// <summary>
    /// Utilization percent between two snapshots of a monotonic busy-seconds
    /// counter, clamped to 0..100. Null on unsupported samples, non-second
    /// units, no time delta, or a counter reset.
    /// </summary>
    internal static uint? UtilFromActivityCounters(in CtlTelemetryItem before, in CtlTelemetryItem after, double dtSeconds)
    {
        if (before.Supported == 0 || after.Supported == 0 || dtSeconds <= 0)
        {
            return null;
        }

        if (before.Units != CtlUnits.TimeSeconds || after.Units != CtlUnits.TimeSeconds)
        {
            return null;
        }

        double busy = after.AsDouble() - before.AsDouble();
        if (busy < 0)
        {
            return null;
        }

        return (uint)Math.Clamp(Math.Round(100.0 * busy / dtSeconds), 0, 100);
    }
}
