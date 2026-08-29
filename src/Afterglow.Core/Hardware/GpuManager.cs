using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Hardware;

/// <summary>One GPU as seen by both driver APIs.</summary>
public sealed class GpuContext
{
    public required uint Index { get; init; }
    public required string Name { get; init; }
    public required NvmlDevice Nvml { get; init; }
    public NvapiGpu? Nvapi { get; init; }
    public uint Architecture { get; init; }
    public required SensorPoller Poller { get; init; }
    public required GpuTuner Tuner { get; init; }

    /// <summary>NVML UUID — the stable identity profiles and applied state are stamped with.</summary>
    public string? Uuid { get; init; }

    /// <summary>PCI bus number — binds stress/VRAM tests to this physical card.</summary>
    public uint? PciBusId { get; init; }
}

/// <summary>
/// Composition root for hardware access: initializes NVML and NVAPI, pairs the
/// devices by PCI bus id, and hands out pollers/tuners for each GPU.
/// </summary>
public sealed class GpuManager : IDisposable
{
    private readonly NvmlApi? _nvml;

    public IReadOnlyList<GpuContext> Gpus { get; }

    public string? DriverVersion { get; }

    public NvmlReturn NvmlStatus { get; }

    public NvapiStatus NvapiStatus { get; }

    public GpuManager()
    {
        _nvml = NvmlApi.TryCreate(out var nvmlStatus);
        NvmlStatus = nvmlStatus;
        var nvapi = NvapiApi.TryCreate(out var nvapiStatus);
        NvapiStatus = nvapiStatus;

        if (_nvml is null)
        {
            Gpus = [];
            return;
        }

        DriverVersion = _nvml.GetDriverVersion();
        Profiles.CertificationModes.CurrentDriverVersion = DriverVersion;

        // NVAPI exposes only the PCI bus number, so pairing with NVML is keyed
        // on it. Two GPUs sharing a bus number (different PCI domains) would
        // make the pairing ambiguous — in that case pair NEITHER rather than
        // silently attaching one card's NVAPI handle to the other's tuner.
        var nvapiByBus = new Dictionary<uint, NvapiGpu>();
        var ambiguousBuses = new HashSet<uint>();
        if (nvapi is not null)
        {
            foreach (var gpu in nvapi.GetGpus())
            {
                if (gpu.TryGetBusId(out uint busId) == NvapiStatus.Ok)
                {
                    if (!nvapiByBus.TryAdd(busId, gpu))
                    {
                        ambiguousBuses.Add(busId);
                        Diagnostics.Log.Warn($"Two NVAPI GPUs report PCI bus {busId}; skipping NVAPI pairing for that bus.");
                    }
                }
            }

            foreach (uint bus in ambiguousBuses)
            {
                nvapiByBus.Remove(bus);
            }
        }

        var contexts = new List<GpuContext>();
        foreach (var device in _nvml.GetDevices())
        {
            NvapiGpu? pairedNvapi = null;
            uint? pciBus = null;
            if (device.TryGetPciInfo(out _, out uint bus, out _, out _) == NvmlReturn.Success)
            {
                pciBus = bus;
                nvapiByBus.TryGetValue(bus, out pairedNvapi);
            }

            uint arch = 0;
            _ = device.TryGetArchitecture(out arch);
            if (pairedNvapi is not null)
            {
                pairedNvapi.Architecture = arch;
            }

            var poller = new SensorPoller(device);
            if (pairedNvapi is not null)
            {
                var enricher = new NvapiEnricher(device, pairedNvapi);
                poller.EnrichmentSource = enricher.Read;
            }

            contexts.Add(new GpuContext
            {
                Index = device.Index,
                Name = device.GetName() ?? $"GPU {device.Index}",
                Nvml = device,
                Nvapi = pairedNvapi,
                Architecture = arch,
                Poller = poller,
                Tuner = new GpuTuner(device, pairedNvapi),
                Uuid = device.GetUuid(),
                PciBusId = pciBus,
            });
        }

        Gpus = contexts;
    }

    public void Dispose()
    {
        _nvml?.Dispose();
    }
}

/// <summary>Fills the NVAPI-only sensor fields for each telemetry tick.</summary>
internal sealed class NvapiEnricher
{
    private readonly NvmlDevice _nvml;
    private readonly NvapiGpu _nvapi;
    private uint _fanCount;

    public NvapiEnricher(NvmlDevice nvml, NvapiGpu nvapi)
    {
        _nvml = nvml;
        _nvapi = nvapi;
        if (_nvml.TryGetNumFans(out uint fans) == NvmlReturn.Success)
        {
            _fanCount = Math.Min(fans, 16);
        }
    }

    public NvapiEnrichment Read()
    {
        var (hotSpot, memJunction) = _nvapi.GetPrivateThermals();

        double? voltage = null;
        if (_nvapi.TryGetCoreVoltageMv(out double mv) == NvapiStatus.Ok && mv > 0)
        {
            voltage = Math.Round(mv, 1);
        }

        uint[]? rpms = null;
        if (_fanCount > 0)
        {
            rpms = new uint[_fanCount];
            bool any = false;
            for (uint f = 0; f < _fanCount; f++)
            {
                if (_nvml.TryGetFanRpm(f, out uint rpm) == NvmlReturn.Success)
                {
                    rpms[f] = rpm;
                    any = true;
                }
            }

            if (!any)
            {
                rpms = null;
                _fanCount = 0;
            }
        }

        return new NvapiEnrichment
        {
            HotSpotTempC = hotSpot,
            MemJunctionTempC = memJunction,
            CoreVoltageMv = voltage,
            FanRpms = rpms,
        };
    }
}
