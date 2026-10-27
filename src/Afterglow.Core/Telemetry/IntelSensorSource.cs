using Afterglow.Core.Interop.Igcl;

namespace Afterglow.Core.Telemetry;

/// <summary>
/// IGCL-backed sensor source for Intel GPUs. Every metric the driver does not
/// answer stays null — the honest "unavailable" the rest of the pipeline
/// already renders. Power and utilization only exist as deltas between two
/// monotonic counters, so the first poll after start reports them as null.
/// Power is the whole card's where the driver counts it and the GPU chip's
/// otherwise, and every snapshot says which (<see cref="GpuSnapshot.PowerIsChipOnly"/>).
/// Component handles are enumerated once in the constructor (IGCL handles stay
/// valid for the API's lifetime). <see cref="Poll"/> is called from more than
/// one thread — the telemetry loop, the V/F probe's sampler, and the CLI/MCP
/// priming paths — so the counter state the deltas are computed from is guarded.
/// </summary>
public sealed class IntelSensorSource : ISensorSource
{
    private readonly IIntelTelemetryDevice _device;
    private readonly nint _gpuFreqDomain;
    private readonly nint _mediaFreqDomain;
    private readonly nint _memoryFreqDomain;
    private readonly nint _memoryModule;
    private readonly bool _memoryIsShared;
    private readonly IReadOnlyList<(nint Handle, CtlTempSensor Type)> _tempSensors;

    private readonly Lock _deltaLock = new();
    private CtlPowerTelemetry _last;
    private bool _hasLast;

    public IntelSensorSource(IgclDevice device, uint deviceIndex)
        : this(new IgclTelemetryDevice(device), deviceIndex)
    {
    }

    internal IntelSensorSource(IIntelTelemetryDevice device, uint deviceIndex)
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
        bool? powerIsChipOnly = null;
        double? bulkVramTemp = null;

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

            // Unit-checked like the energy counters: a value the driver labels
            // as anything but °C is not read as one (R-19, used below).
            if (t.VramCurrentTemperature is { Supported: not 0, Units: CtlUnits.TemperatureCelsius }
                && double.IsFinite(t.VramCurrentTemperature.AsDouble()))
            {
                bulkVramTemp = Math.Round(t.VramCurrentTemperature.AsDouble(), 1);
            }

            double? cardW = null;
            double? chipW = null;

            // The previous sample is a multi-word struct read and written by
            // whichever threads call Poll — the telemetry loop, the V/F probe's
            // sampler, and the CLI/MCP priming paths all do. Unsynchronized,
            // one caller could read a half-updated counter pair and compute a
            // nonsense delta, which the guards below turn into a fabricated
            // 0 W / 0 % rather than an honest "—". Deltas are cheap; take the lock.
            lock (_deltaLock)
            {
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
                        cardW = PowerFromEnergyCounters(_last.TotalCardEnergyCounter, t.TotalCardEnergyCounter, dt);
                        chipW = PowerFromEnergyCounters(_last.GpuEnergyCounter, t.GpuEnergyCounter, dt);
                        gpuUtil = UtilFromActivityCounters(_last.GlobalActivityCounter, t.GlobalActivityCounter, dt);
                    }
                }

                _last = t;
                _hasLast = true;
            }

            // R-18: board power comes from the total-card counter. The IGCL
            // header documents gpuEnergyCounter as "the total energy consumed
            // by the GPU chip" — on a discrete board that leaves out VRAM,
            // regulators, fans and conversion losses, and reading it as board
            // power understated the card (chip +50 J, card +100 J in one
            // second came out as 50 W), which every power rule, session report
            // and FPS-per-watt comparison inherited. The chip counter stays the
            // fallback where the driver has no card counter in joules, and the
            // snapshot says so in PowerIsChipOnly. One counter answers all
            // three power fields per poll so that one label is true of each: a
            // card counter with no usable previous card sample gives no power
            // this tick rather than borrowing the chip's delta under the
            // card's label.
            bool cardCounter = t.TotalCardEnergyCounter is { Supported: not 0, Units: CtlUnits.EnergyJoules };

            // The card's energy includes the chip's, so a card counter that
            // advanced less than the chip counter over the same interval is
            // not counting the whole card (IGCL answering zeros for something
            // the device does not do is on record for the overclock getters,
            // docs/research/intel-driver-apis.md). That poll falls back to the
            // chip, labelled so, instead of reporting a stalled counter's 0 W
            // as board power.
            if (cardCounter && cardW is double card && chipW is double chip && card < chip)
            {
                cardCounter = false;
            }

            var energy = cardCounter ? t.TotalCardEnergyCounter : t.GpuEnergyCounter;
            if (energy is { Supported: not 0, Units: CtlUnits.EnergyJoules })
            {
                energyWh = energy.AsDouble() / 3600.0;
                powerW = cardCounter ? cardW : chipW;
                powerIsChipOnly = !cardCounter;
            }
        }

        double? coreVoltageMv = null;
        if (_gpuFreqDomain != 0 && _device.TryGetFrequencyState(_gpuFreqDomain, out var gpuState) == CtlResult.Success)
        {
            coreClock ??= gpuState.Actual >= 0 ? (uint)Math.Round(gpuState.Actual) : null;
            if (gpuState.CurrentVoltage >= 0)
            {
                coreVoltageMv = Math.Round(gpuState.CurrentVoltage * 1000.0, 1);
            }
        }

        uint? videoClock = null;
        if (_mediaFreqDomain != 0 && _device.TryGetFrequencyState(_mediaFreqDomain, out var mediaState) == CtlResult.Success
            && mediaState.Actual >= 0)
        {
            videoClock = (uint)Math.Round(mediaState.Actual);
        }

        uint? memClock = null;
        if (_memoryFreqDomain != 0 && _device.TryGetFrequencyState(_memoryFreqDomain, out var memState) == CtlResult.Success
            && memState.Actual >= 0)
        {
            memClock = (uint)Math.Round(memState.Actual);
        }

        double? memTemp = null;
        foreach (var (handle, type) in _tempSensors)
        {
            if (_device.TryGetTemperature(handle, out double c) != CtlResult.Success)
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
                    memTemp = Math.Round(c, 1);
                    break;
                default:
                    break;
            }
        }

        // R-19: a driver can answer the bulk block's VRAM temperature without
        // enumerating a memory sensor handle; reading only the handles turned
        // a supported 92 °C into null, so memory alerts and rules never fired.
        // An enumerated sensor that read still wins. Both are documented as
        // the hottest memory sensor — CTL_TEMP_SENSORS_MEMORY "the maximum
        // temperature across all sensors in the local memory",
        // vramCurrentTemperature "read from the sensor reporting the highest
        // value" — so either one is labelled a VRAM reading, not a junction.
        memTemp ??= bulkVramTemp;

        ulong? vramUsed = null;
        ulong? vramTotal = null;
        if (_memoryModule != 0 && _device.TryGetMemoryState(_memoryModule, out var mem) == CtlResult.Success
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
            MemJunctionTempC = memTemp,
            MemTempIsVramSensor = memTemp is null ? null : true,
            GpuUtilPct = gpuUtil,
            // Encoder/decoder stay null: IGCL's media activity counter covers
            // encode+decode combined, and labeling that as either would lie.
            VramUsedBytes = vramUsed,
            VramTotalBytes = vramTotal,
            MemoryIsShared = _memoryModule != 0 ? _memoryIsShared : null,
            PowerW = powerW,
            PowerAvgW = powerW,
            EnergyWh = energyWh,
            PowerIsChipOnly = powerIsChipOnly,
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

    /// <summary>The production seam: every read forwarded to the IGCL adapter, unchanged.</summary>
    private sealed class IgclTelemetryDevice(IgclDevice device) : IIntelTelemetryDevice
    {
        public IReadOnlyList<(nint Handle, CtlFreqProperties Properties)> GetFrequencyDomains() => device.GetFrequencyDomains();

        public IReadOnlyList<(nint Handle, CtlMemProperties Properties)> GetMemoryModules() => device.GetMemoryModules();

        public IReadOnlyList<(nint Handle, CtlTempProperties Properties)> GetTemperatureSensors() => device.GetTemperatureSensors();

        public CtlResult TryGetPowerTelemetry(out CtlPowerTelemetry telemetry) => device.TryGetPowerTelemetry(out telemetry);

        public CtlResult TryGetFrequencyState(nint domain, out CtlFreqState state) => IgclDevice.TryGetFrequencyState(domain, out state);

        public CtlResult TryGetTemperature(nint sensor, out double temperatureC) => IgclDevice.TryGetTemperature(sensor, out temperatureC);

        public CtlResult TryGetMemoryState(nint module, out CtlMemState state) => IgclDevice.TryGetMemoryState(module, out state);
    }
}

/// <summary>
/// The IGCL reads <see cref="IntelSensorSource"/> makes — reads only, so a
/// substitute can never stand in for a write. <see cref="IgclDevice"/> answers
/// them in production; the test suite substitutes an in-memory device so the
/// counter-scope and sensor-fallback rules run through the real
/// <see cref="IntelSensorSource.Poll"/> instead of an argument about it.
/// </summary>
internal interface IIntelTelemetryDevice
{
    IReadOnlyList<(nint Handle, CtlFreqProperties Properties)> GetFrequencyDomains();

    IReadOnlyList<(nint Handle, CtlMemProperties Properties)> GetMemoryModules();

    IReadOnlyList<(nint Handle, CtlTempProperties Properties)> GetTemperatureSensors();

    CtlResult TryGetPowerTelemetry(out CtlPowerTelemetry telemetry);

    CtlResult TryGetFrequencyState(nint domain, out CtlFreqState state);

    CtlResult TryGetTemperature(nint sensor, out double temperatureC);

    CtlResult TryGetMemoryState(nint module, out CtlMemState state);
}
