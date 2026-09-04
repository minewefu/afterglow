using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Hardware;

/// <summary>The driver stack a GPU is managed through.</summary>
public enum GpuVendor
{
    Nvidia,
    Intel,
}

/// <summary>One GPU as seen by its vendor's driver APIs.</summary>
public sealed class GpuContext
{
    public required uint Index { get; init; }
    public required string Name { get; init; }
    public required GpuVendor Vendor { get; init; }

    /// <summary>NVML device — null for non-NVIDIA GPUs.</summary>
    public NvmlDevice? Nvml { get; init; }
    public NvapiGpu? Nvapi { get; init; }

    /// <summary>IGCL adapter — null for non-Intel GPUs.</summary>
    public IgclDevice? Igcl { get; init; }

    public uint Architecture { get; init; }
    public required ISensorSource Poller { get; init; }
    public required IGpuTuner Tuner { get; init; }

    /// <summary>
    /// Stable identity profiles and applied state are stamped with: the NVML
    /// UUID ("GPU-…") for NVIDIA, or a PCI-derived "INTEL-…" string for Intel
    /// (the IGCL LUID is not stable across reboots).
    /// </summary>
    public string? Uuid { get; init; }

    /// <summary>
    /// The identity settings and records are keyed by: the UUID, or an index
    /// fallback when the driver reports none. One formula for the fan settings,
    /// the probe-lock records and anything else that must name a card — two
    /// copies had already drifted on culture.
    /// </summary>
    public string StableKey =>
        Uuid ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"index:{Index}");

    /// <summary>
    /// Why Afterglow cannot read this card's core voltage, or null when the
    /// answer rests with the driver's own telemetry. On NVIDIA the voltage
    /// comes only through NVAPI, so a card without a bus-paired NVAPI handle
    /// has no voltage source here at all — an Afterglow limitation that the
    /// CLI and the V/F page used to report as "this driver does not report
    /// core voltage", steering users away from the actual fix.
    /// </summary>
    public string? CoreVoltageUnavailableReason =>
        Vendor == GpuVendor.Nvidia && Nvapi is null
            ? "core voltage on NVIDIA is read through NVAPI, which is not available for this card " +
              "(nvapi64.dll could not be loaded, or the card could not be bus-paired) — an Afterglow " +
              "limitation, not the driver's"
            : null;

    /// <summary>PCI bus number — binds stress/VRAM tests to this physical card.</summary>
    public uint? PciBusId { get; init; }

    /// <summary>PCI vendor id (0x10DE / 0x8086) — adapter matching for stress binding.</summary>
    public uint PciVendorId { get; init; }

    /// <summary>This GPU's driver version (driver stacks differ per vendor).</summary>
    public string? DriverVersion { get; init; }
}

/// <summary>
/// Composition root for hardware access: initializes NVML + NVAPI (NVIDIA) and
/// IGCL (Intel), pairs NVML/NVAPI devices by PCI bus id, and hands out
/// pollers/tuners for each GPU. NVIDIA devices keep their NVML indices;
/// Intel devices are numbered after them so every per-index consumer
/// (history, fans, flight recorders, the GPU selector) works unchanged.
/// </summary>
public sealed class GpuManager : IDisposable
{
    private readonly NvmlApi? _nvml;
    private readonly IgclApi? _igcl;

    public IReadOnlyList<GpuContext> Gpus { get; }

    /// <summary>NVIDIA driver version, when NVML answered (see per-context DriverVersion).</summary>
    public string? DriverVersion { get; }

    public NvmlReturn NvmlStatus { get; }

    public NvapiStatus NvapiStatus { get; }

    public CtlResult IgclStatus { get; }

    public GpuManager()
    {
        _nvml = NvmlApi.TryCreate(out var nvmlStatus);
        NvmlStatus = nvmlStatus;
        var nvapi = NvapiApi.TryCreate(out var nvapiStatus);
        NvapiStatus = nvapiStatus;
        _igcl = IgclApi.TryCreate(out var igclStatus);
        IgclStatus = igclStatus;

        var contexts = new List<GpuContext>();
        uint nextIndex = 0;

        if (_nvml is not null)
        {
            DriverVersion = _nvml.GetDriverVersion();

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
                    Vendor = GpuVendor.Nvidia,
                    Nvml = device,
                    Nvapi = pairedNvapi,
                    Architecture = arch,
                    Poller = poller,
                    Tuner = new GpuTuner(device, pairedNvapi),
                    Uuid = device.GetUuid(),
                    PciBusId = pciBus,
                    PciVendorId = 0x10DE,
                    DriverVersion = DriverVersion,
                });
                nextIndex = Math.Max(nextIndex, device.Index + 1);
            }
        }

        if (_igcl is not null)
        {
            foreach (var device in _igcl.GetDevices())
            {
                // IGCL enumerates graphics adapters, not only Intel ones. Stamping
                // every one of them 0x8086 minted a phantom "Intel" GPU for another
                // vendor's card: its tuner would talk IGCL to a device that is not
                // Intel, and the stress engines — which bind by vendor id plus bus —
                // would hunt for an Intel adapter on that card's bus and either miss
                // or land on the wrong one. Trust the device's own vendor id.
                if (device.PciVendorId != Stress.StressAdapter.IntelVendorId)
                {
                    Diagnostics.Log.Warn(
                        $"IGCL enumerated a non-Intel adapter ({device.Name}, vendor 0x{device.PciVendorId:X4}); skipping it.");
                    continue;
                }

                string uuid = IntelUuid(device);
                contexts.Add(new GpuContext
                {
                    Index = nextIndex,
                    Name = device.Name,
                    Vendor = GpuVendor.Intel,
                    Igcl = device,
                    Poller = new IntelSensorSource(device, nextIndex),
                    Tuner = new ArcGpuTuner(device, uuid),
                    Uuid = uuid,

                    // Only publish a bus the driver actually reported. A guessed
                    // 0 here silently disabled the stress engines' own "cannot
                    // bind, refusing to guess" refusal and let a burn land on
                    // whichever adapter happened to sit on bus 0.
                    PciBusId = device.BdfValid ? device.Bdf.Bus : null,
                    PciVendorId = device.PciVendorId,
                    DriverVersion = device.DriverVersion,
                });
                nextIndex++;
            }
        }

        // The tuner resolves a V/F probe's pin record on every verified lock
        // release, and that record is keyed by the card's stable key — which a
        // UUID-less NVIDIA tuner cannot derive on its own.
        foreach (var context in contexts)
        {
            context.Tuner.ProbeRecordKey = context.StableKey;
        }

        Gpus = contexts;

        // Per-GPU first: on a hybrid machine each card's certifications must be
        // judged against ITS driver, not one process-wide string that preferred
        // NVML's. The global stays as the fallback for records with no GPU stamp
        // and for demo mode.
        var driverByGpu = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var context in contexts)
        {
            if (context.Uuid is { Length: > 0 } uuid && context.DriverVersion is { Length: > 0 } version)
            {
                driverByGpu[uuid] = version;
            }
        }

        Profiles.CertificationModes.DriverVersionByGpu = driverByGpu;
        Profiles.CertificationModes.CurrentDriverVersion =
            DriverVersion ?? contexts.FirstOrDefault()?.DriverVersion;
    }

    /// <summary>
    /// Stable, reboot-safe identity for an Intel GPU: full PCI location
    /// (domain:bus:device.function) + device id. The location leads because
    /// per-GPU file names keep only the first 12 alphanumerics after the
    /// vendor prefix — the entire PCI location fits inside that budget, so two
    /// identical cards always get distinct state files.
    /// </summary>
    internal static string IntelUuid(IgclDevice device)
    {
        // Without a driver-reported BDF the location would be a fabricated
        // 0000:00:00.0 that two different cards would share — and share state
        // files under. Say "location unknown" in the identity instead, so it can
        // never collide with a real location-derived one.
        if (!device.BdfValid)
        {
            Diagnostics.Log.Warn(
                $"No PCI location for {device.Name}; its identity falls back to enumeration order " +
                "and is not stable across hardware changes.");
            return $"INTEL-noloc{device.Index:x2}-{device.PciDeviceId:X4}";
        }

        uint domain = 0;
        if (device.TryGetPciProperties(out var pci) == CtlResult.Success)
        {
            domain = pci.Address.Domain;
        }
        else
        {
            Diagnostics.Log.Warn(
                $"IGCL PCI properties unavailable for {device.Name}; assuming PCI domain 0 in its identity.");
        }

        return $"INTEL-{domain:x4}:{device.Bdf.Bus:x2}:{device.Bdf.Device:x2}.{device.Bdf.Function:x1}-{device.PciDeviceId:X4}";
    }

    public void Dispose()
    {
        _nvml?.Dispose();
        _igcl?.Dispose();
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
