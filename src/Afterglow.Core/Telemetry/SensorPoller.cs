using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Core.Telemetry;

/// <summary>
/// Reads a full <see cref="GpuSnapshot"/> from one GPU. Remembers which metrics
/// the GPU/driver reported as unsupported and stops asking for those, so a poll
/// settles into the minimal set of driver calls after the first few ticks.
/// Not thread-safe; owned by the polling loop.
/// </summary>
public sealed class SensorPoller : ISensorSource
{
    private enum Cap : byte
    {
        Unknown = 0,
        Supported = 1,
        Unsupported = 2,
    }

    private readonly NvmlDevice _device;

    private Cap _coreClock, _memClock, _videoClock;
    private Cap _gpuTemp, _utilization, _encoderUtil, _decoderUtil;
    private Cap _memory, _power, _powerInstant, _powerLimit, _energy;
    private Cap _perfState, _throttle, _fans, _pcieThroughput, _throttleMargin;
    private readonly NvmlFieldValue[] _powerInstantField = [new() { FieldId = NvmlFieldValue.FiPowerInstant }];

    private uint _fanCount;
    private uint _lastPcieTx;
    private uint _lastPcieRx;
    private bool _hasPcieSample;
    private long _tick;

    /// <summary>Sample the (blocking, ~40 ms) PCIe throughput counters every Nth tick.</summary>
    public int PcieSampleInterval { get; set; } = 5;

    /// <summary>Optional NVAPI enrichment callback (hot spot, memory junction, voltage, RPM).</summary>
    public Func<NvapiEnrichment?>? EnrichmentSource { get; set; }

    public SensorPoller(NvmlDevice device)
    {
        _device = device;
        if (_device.TryGetNumFans(out uint fans) == NvmlReturn.Success)
        {
            _fanCount = Math.Min(fans, 16);
        }
    }

    public uint DeviceIndex => _device.Index;

    public GpuSnapshot Poll()
    {
        _tick++;

        uint? coreClock = ReadUint(ref _coreClock, static (NvmlDevice d, out uint v) => d.TryGetClock(NvmlClockType.Graphics, out v), _device);
        uint? memClock = ReadUint(ref _memClock, static (NvmlDevice d, out uint v) => d.TryGetClock(NvmlClockType.Mem, out v), _device);
        uint? videoClock = ReadUint(ref _videoClock, static (NvmlDevice d, out uint v) => d.TryGetClock(NvmlClockType.Video, out v), _device);
        uint? gpuTemp = ReadUint(ref _gpuTemp, static (NvmlDevice d, out uint v) => d.TryGetTemperature(NvmlTemperatureSensor.Gpu, out v), _device);

        uint? gpuUtil = null, memUtil = null;
        if (_utilization != Cap.Unsupported)
        {
            var rc = _device.TryGetUtilization(out var util);
            if (rc == NvmlReturn.Success)
            {
                _utilization = Cap.Supported;
                gpuUtil = util.Gpu;
                memUtil = util.Memory;
            }
            else
            {
                MarkIfUnsupported(ref _utilization, rc);
            }
        }

        uint? encUtil = ReadUint(ref _encoderUtil, static (NvmlDevice d, out uint v) => d.TryGetEncoderUtilization(out v), _device);
        uint? decUtil = ReadUint(ref _decoderUtil, static (NvmlDevice d, out uint v) => d.TryGetDecoderUtilization(out v), _device);

        ulong? vramUsed = null, vramTotal = null;
        if (_memory != Cap.Unsupported)
        {
            var rc = _device.TryGetMemoryInfo(out var mem);
            if (rc == NvmlReturn.Success)
            {
                _memory = Cap.Supported;
                vramUsed = mem.Used;
                vramTotal = mem.Total;
            }
            else
            {
                MarkIfUnsupported(ref _memory, rc);
            }
        }

        double? powerAvgW = null;
        if (ReadUint(ref _power, static (NvmlDevice d, out uint v) => d.TryGetPowerUsage(out v), _device) is uint mw)
        {
            powerAvgW = mw / 1000.0;
        }

        // Instantaneous board power (field 186); GetPowerUsage is a ~1 s average on Ampere+.
        double? powerInstantW = null;
        if (_powerInstant != Cap.Unsupported)
        {
            _powerInstantField[0].Status = NvmlReturn.Unknown;
            var rc = _device.TryGetFieldValues(_powerInstantField);
            if (rc == NvmlReturn.Success && _powerInstantField[0].Status == NvmlReturn.Success)
            {
                _powerInstant = Cap.Supported;
                powerInstantW = _powerInstantField[0].Value / 1000.0;
            }
            else
            {
                MarkIfUnsupported(ref _powerInstant,
                    rc != NvmlReturn.Success ? rc : _powerInstantField[0].Status);
            }
        }

        double? powerLimitW = null;
        if (ReadUint(ref _powerLimit, static (NvmlDevice d, out uint v) => d.TryGetEnforcedPowerLimit(out v), _device) is uint limitMw)
        {
            powerLimitW = limitMw / 1000.0;
        }

        double? energyWh = null;
        if (_energy != Cap.Unsupported)
        {
            var rc = _device.TryGetTotalEnergyConsumption(out ulong mj);
            if (rc == NvmlReturn.Success)
            {
                _energy = Cap.Supported;
                energyWh = mj / 3_600_000.0;
            }
            else
            {
                MarkIfUnsupported(ref _energy, rc);
            }
        }

        uint? perfState = ReadUint(ref _perfState, static (NvmlDevice d, out uint v) => d.TryGetPerformanceState(out v), _device);

        NvmlClocksEventReasons? throttle = null;
        if (_throttle != Cap.Unsupported)
        {
            var rc = _device.TryGetClocksEventReasons(out var reasons);
            if (rc == NvmlReturn.Success)
            {
                _throttle = Cap.Supported;
                throttle = reasons;
            }
            else
            {
                MarkIfUnsupported(ref _throttle, rc);
            }
        }

        int? throttleMargin = null;
        if (_throttleMargin != Cap.Unsupported)
        {
            var rc = _device.TryGetThrottleMargin(out int margin);
            if (rc == NvmlReturn.Success)
            {
                _throttleMargin = Cap.Supported;
                throttleMargin = margin;
            }
            else
            {
                MarkIfUnsupported(ref _throttleMargin, rc);
            }
        }

        uint[]? fanPercents = null;
        if (_fans != Cap.Unsupported && _fanCount > 0)
        {
            fanPercents = new uint[_fanCount];
            bool any = false;
            for (uint f = 0; f < _fanCount; f++)
            {
                if (_device.TryGetFanSpeed(f, out uint pct) == NvmlReturn.Success)
                {
                    fanPercents[f] = pct;
                    any = true;
                }
            }

            if (any)
            {
                _fans = Cap.Supported;
            }
            else
            {
                _fans = Cap.Unsupported;
                fanPercents = null;
            }
        }

        if (_pcieThroughput != Cap.Unsupported && _tick % Math.Max(1, PcieSampleInterval) == 0)
        {
            var rcTx = _device.TryGetPcieThroughput(NvmlPcieUtilCounter.TxBytes, out uint tx);
            var rcRx = _device.TryGetPcieThroughput(NvmlPcieUtilCounter.RxBytes, out uint rx);
            if (rcTx == NvmlReturn.Success && rcRx == NvmlReturn.Success)
            {
                _pcieThroughput = Cap.Supported;
                _lastPcieTx = tx;
                _lastPcieRx = rx;
                _hasPcieSample = true;
            }
            else
            {
                MarkIfUnsupported(ref _pcieThroughput, rcTx != NvmlReturn.Success ? rcTx : rcRx);
            }
        }

        var enrichment = EnrichmentSource?.Invoke();

        return new GpuSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            DeviceIndex = _device.Index,
            CoreClockMHz = coreClock,
            MemClockMHz = memClock,
            VideoClockMHz = videoClock,
            GpuTempC = gpuTemp,
            HotSpotTempC = enrichment?.HotSpotTempC,
            MemJunctionTempC = enrichment?.MemJunctionTempC,
            GpuUtilPct = gpuUtil,
            MemCtrlUtilPct = memUtil,
            EncoderUtilPct = encUtil,
            DecoderUtilPct = decUtil,
            VramUsedBytes = vramUsed,
            VramTotalBytes = vramTotal,
            PowerW = powerInstantW ?? powerAvgW,
            PowerAvgW = powerAvgW,
            PowerLimitW = powerLimitW,
            EnergyWh = energyWh,
            CoreVoltageMv = enrichment?.CoreVoltageMv,
            PerfState = perfState,
            ThrottleReasons = throttle,
            ThrottleMarginC = throttleMargin,
            FanPercents = fanPercents,
            FanRpms = enrichment?.FanRpms,
            PcieTxKBps = _hasPcieSample ? _lastPcieTx : null,
            PcieRxKBps = _hasPcieSample ? _lastPcieRx : null,
        };
    }

    private delegate NvmlReturn UintReader(NvmlDevice device, out uint value);

    private static uint? ReadUint(ref Cap cap, UintReader reader, NvmlDevice device)
    {
        if (cap == Cap.Unsupported)
        {
            return null;
        }

        var rc = reader(device, out uint value);
        if (rc == NvmlReturn.Success)
        {
            cap = Cap.Supported;
            return value;
        }

        MarkIfUnsupported(ref cap, rc);
        return null;
    }

    private static void MarkIfUnsupported(ref Cap cap, NvmlReturn rc)
    {
        if (rc is NvmlReturn.NotSupported or NvmlReturn.FunctionNotFound)
        {
            cap = Cap.Unsupported;
        }
    }
}

/// <summary>Sensor values only NVAPI can provide, merged into snapshots when available.</summary>
public sealed record NvapiEnrichment
{
    public double? HotSpotTempC { get; init; }
    public double? MemJunctionTempC { get; init; }
    public double? CoreVoltageMv { get; init; }
    public IReadOnlyList<uint>? FanRpms { get; init; }
}
