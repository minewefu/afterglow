using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Afterglow.Core.Interop.Igcl;

/// <summary>
/// Managed lifecycle wrapper around IGCL (Intel Graphics Control Library).
/// Create via <see cref="TryCreate"/>, dispose to release the runtime. All
/// device access flows through <see cref="IgclDevice"/> instances obtained
/// from <see cref="GetDevices"/>.
/// </summary>
public sealed class IgclApi : IDisposable
{
    private readonly nint _handle;
    private bool _disposed;

    private IgclApi(nint handle, uint supportedVersion)
    {
        _handle = handle;
        SupportedVersion = supportedVersion;
    }

    /// <summary>The runtime's implementation version reported by ctlInit (major &lt;&lt; 16 | minor).</summary>
    public uint SupportedVersion { get; }

    /// <summary>
    /// Initializes IGCL with the Level Zero flag set (required for the telemetry,
    /// frequency, and component APIs). Returns null with the failure code when no
    /// Intel graphics driver is installed or ControlLib.dll cannot be loaded.
    /// </summary>
    public static IgclApi? TryCreate(out CtlResult status)
    {
        var args = new CtlInitArgs
        {
            Size = (uint)Unsafe.SizeOf<CtlInitArgs>(),
            Version = 0,
            AppVersion = CtlInitArgs.ImplVersion,
            Flags = CtlInitArgs.FlagUseLevelZero,
        };

        nint handle;
        try
        {
            status = IgclNative.ctlInit(ref args, out handle);
        }
        catch (DllNotFoundException)
        {
            status = CtlResult.LibraryNotFound;
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            status = CtlResult.FunctionNotFound;
            return null;
        }

        return status == CtlResult.Success ? new IgclApi(handle, args.SupportedVersion) : null;
    }

    /// <summary>
    /// Enumerates Intel graphics adapters. Devices whose properties cannot be
    /// read are skipped (the count is reported by the caller's own logging).
    /// </summary>
    public IReadOnlyList<IgclDevice> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        uint count = 0;
        if (IgclNative.ctlEnumerateDevices(_handle, ref count, null) != CtlResult.Success || count == 0)
        {
            return [];
        }

        var handles = new nint[count];
        if (IgclNative.ctlEnumerateDevices(_handle, ref count, handles) != CtlResult.Success)
        {
            return [];
        }

        var devices = new List<IgclDevice>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var device = IgclDevice.TryWrap(handles[i], i);
            if (device is not null)
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _ = IgclNative.ctlClose(_handle);
        }
    }
}

/// <summary>
/// One IGCL graphics adapter. Thin wrappers over the native calls; every method
/// reports the raw <see cref="CtlResult"/> so callers can distinguish
/// "unsupported on this device" from real errors. Exports missing from older
/// ControlLib runtimes surface as <see cref="CtlResult.FunctionNotFound"/>.
/// Component handles (frequency domains, sensors, fans, power domains) are
/// enumerated once and cached - IGCL handles stay valid for the API lifetime.
/// </summary>
public sealed class IgclDevice
{
    private readonly nint _handle;

    private IgclDevice(nint handle, uint index, in CtlDeviceAdapterProperties properties, ulong luid, string name)
    {
        _handle = handle;
        Index = index;
        Name = name;
        PciVendorId = properties.PciVendorId;
        PciDeviceId = properties.PciDeviceId;
        DriverVersionRaw = properties.DriverVersion;
        Bdf = properties.AdapterBdf;
        IsIntegrated = (properties.GraphicsAdapterProperties & CtlDeviceAdapterProperties.FlagIntegrated) != 0;
        Luid = luid;
    }

    /// <summary>Enumeration order within IGCL (not a stable identity).</summary>
    public uint Index { get; }

    public string Name { get; }

    public uint PciVendorId { get; }

    public uint PciDeviceId { get; }

    /// <summary>Raw uint64 driver version; format with <see cref="DriverVersion"/>.</summary>
    public ulong DriverVersionRaw { get; }

    /// <summary>PCI bus/device/function as reported in the adapter properties.</summary>
    public CtlAdapterBdf Bdf { get; }

    public bool IsIntegrated { get; }

    /// <summary>
    /// The Windows adapter LUID the driver wrote into the caller-supplied device
    /// id buffer. Matches the DXGI adapter LUID; NOT stable across reboots.
    /// </summary>
    public ulong Luid { get; }

    internal nint Handle => _handle;

    /// <summary>Dotted driver version from the packed uint64 (4 x 16-bit fields).</summary>
    public string DriverVersion =>
        $"{(DriverVersionRaw >> 48) & 0xFFFF}.{(DriverVersionRaw >> 32) & 0xFFFF}.{(DriverVersionRaw >> 16) & 0xFFFF}.{DriverVersionRaw & 0xFFFF}";

    internal static unsafe IgclDevice? TryWrap(nint handle, uint index)
    {
        ulong luid = 0;
        var properties = new CtlDeviceAdapterProperties
        {
            Size = (uint)sizeof(CtlDeviceAdapterProperties),
            Version = 2, // BDF + subsystem ids; the value Intel's own samples pass
            PDeviceId = (nint)(&luid),
            DeviceIdSize = sizeof(ulong), // sizeof(LUID) on Windows
        };

        CtlResult rc;
        try
        {
            rc = IgclNative.ctlGetDeviceProperties(handle, ref properties);
            if (rc == CtlResult.ErrorUnsupportedVersion)
            {
                properties.Version = 0;
                rc = IgclNative.ctlGetDeviceProperties(handle, ref properties);
            }
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        if (rc != CtlResult.Success || properties.DeviceType != CtlDeviceType.Graphics)
        {
            return null;
        }

        string name = FromAnsi(properties.Name, 100);
        return new IgclDevice(handle, index, in properties, luid, name);
    }

    private static CtlResult Guard(Func<CtlResult> call)
    {
        try
        {
            return call();
        }
        catch (EntryPointNotFoundException)
        {
            return CtlResult.FunctionNotFound;
        }
    }

    // --- Bulk power telemetry ------------------------------------------------

    /// <summary>
    /// The pre-V1 size of ctl_power_telemetry_t: the Version&gt;0 items were
    /// physically appended, so a runtime built against the older header expects
    /// this smaller Size. Equals the offset of the first appended field.
    /// </summary>
    private static readonly uint PowerTelemetryV0Size =
        (uint)(int)Marshal.OffsetOf<CtlPowerTelemetry>(nameof(CtlPowerTelemetry.GpuVrTemp));

    /// <summary>
    /// One snapshot of the adapter's bulk telemetry block. Tries Version 1 first
    /// (unlocks effective clock, VR temps, percent items on Arc-era drivers),
    /// retries Version 0 with the same size, then Version 0 with the historical
    /// pre-V1 size for runtimes built against the older header. Passing the
    /// smaller Size with the full managed buffer is safe — the driver writes at
    /// most Size bytes and the untouched tail items stay bSupported=false.
    /// </summary>
    public CtlResult TryGetPowerTelemetry(out CtlPowerTelemetry telemetry)
    {
        var data = new CtlPowerTelemetry
        {
            Size = (uint)Unsafe.SizeOf<CtlPowerTelemetry>(),
            Version = 1,
        };
        var rc = Guard(() => IgclNative.ctlPowerTelemetryGet(_handle, ref data));
        if (rc == CtlResult.ErrorUnsupportedVersion)
        {
            data = new CtlPowerTelemetry
            {
                Size = (uint)Unsafe.SizeOf<CtlPowerTelemetry>(),
                Version = 0,
            };
            rc = Guard(() => IgclNative.ctlPowerTelemetryGet(_handle, ref data));
        }

        if (rc is CtlResult.ErrorInvalidSize or CtlResult.ErrorUnsupportedSize)
        {
            data = new CtlPowerTelemetry { Size = PowerTelemetryV0Size, Version = 0 };
            rc = Guard(() => IgclNative.ctlPowerTelemetryGet(_handle, ref data));
        }

        telemetry = data;
        return rc;
    }

    // --- Components ----------------------------------------------------------

    private delegate CtlResult EnumCall(nint device, ref uint count, nint[]? handles);

    private nint[] EnumHandles(EnumCall call)
    {
        uint count = 0;
        if (Guard(() => call(_handle, ref count, null)) != CtlResult.Success || count == 0)
        {
            return [];
        }

        var handles = new nint[count];
        return Guard(() => call(_handle, ref count, handles)) == CtlResult.Success ? handles : [];
    }

    public IReadOnlyList<(nint Handle, CtlTempProperties Properties)> GetTemperatureSensors()
    {
        var result = new List<(nint, CtlTempProperties)>();
        foreach (nint h in EnumHandles(IgclNative.ctlEnumTemperatureSensors))
        {
            var props = new CtlTempProperties { Size = (uint)Unsafe.SizeOf<CtlTempProperties>() };
            if (Guard(() => IgclNative.ctlTemperatureGetProperties(h, ref props)) == CtlResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static CtlResult TryGetTemperature(nint sensor, out double temperatureC)
    {
        double value = 0;
        var rc = Guard(() => IgclNative.ctlTemperatureGetState(sensor, out value));
        temperatureC = value;
        return rc;
    }

    public IReadOnlyList<(nint Handle, CtlFreqProperties Properties)> GetFrequencyDomains()
    {
        var result = new List<(nint, CtlFreqProperties)>();
        foreach (nint h in EnumHandles(IgclNative.ctlEnumFrequencyDomains))
        {
            var props = new CtlFreqProperties { Size = (uint)Unsafe.SizeOf<CtlFreqProperties>() };
            if (Guard(() => IgclNative.ctlFrequencyGetProperties(h, ref props)) == CtlResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static CtlResult TryGetFrequencyState(nint domain, out CtlFreqState state)
    {
        var data = new CtlFreqState { Size = (uint)Unsafe.SizeOf<CtlFreqState>() };
        var rc = Guard(() => IgclNative.ctlFrequencyGetState(domain, ref data));
        state = data;
        return rc;
    }

    public static CtlResult TryGetFrequencyRange(nint domain, out CtlFreqRange range)
    {
        var data = new CtlFreqRange { Size = (uint)Unsafe.SizeOf<CtlFreqRange>() };
        var rc = Guard(() => IgclNative.ctlFrequencyGetRange(domain, ref data));
        range = data;
        return rc;
    }

    /// <summary>Clamps the domain to [minMhz, maxMhz]. 0 = hardware limit, -1 = factory default.</summary>
    public static CtlResult TrySetFrequencyRange(nint domain, double minMhz, double maxMhz)
    {
        var data = new CtlFreqRange
        {
            Size = (uint)Unsafe.SizeOf<CtlFreqRange>(),
            Min = minMhz,
            Max = maxMhz,
        };
        return Guard(() => IgclNative.ctlFrequencySetRange(domain, ref data));
    }

    public IReadOnlyList<(nint Handle, CtlMemProperties Properties)> GetMemoryModules()
    {
        var result = new List<(nint, CtlMemProperties)>();
        foreach (nint h in EnumHandles(IgclNative.ctlEnumMemoryModules))
        {
            var props = new CtlMemProperties { Size = (uint)Unsafe.SizeOf<CtlMemProperties>() };
            if (Guard(() => IgclNative.ctlMemoryGetProperties(h, ref props)) == CtlResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static CtlResult TryGetMemoryState(nint module, out CtlMemState state)
    {
        var data = new CtlMemState { Size = (uint)Unsafe.SizeOf<CtlMemState>() };
        var rc = Guard(() => IgclNative.ctlMemoryGetState(module, ref data));
        state = data;
        return rc;
    }

    public static CtlResult TryGetMemoryBandwidth(nint module, out CtlMemBandwidth bandwidth)
    {
        var data = new CtlMemBandwidth
        {
            Size = (uint)Unsafe.SizeOf<CtlMemBandwidth>(),
            Version = 1, // counters need Version > 0
        };
        var rc = Guard(() => IgclNative.ctlMemoryGetBandwidth(module, ref data));
        if (rc == CtlResult.ErrorUnsupportedVersion)
        {
            data = new CtlMemBandwidth { Size = (uint)Unsafe.SizeOf<CtlMemBandwidth>() };
            rc = Guard(() => IgclNative.ctlMemoryGetBandwidth(module, ref data));
        }

        bandwidth = data;
        return rc;
    }

    public IReadOnlyList<(nint Handle, CtlEngineProperties Properties)> GetEngineGroups()
    {
        var result = new List<(nint, CtlEngineProperties)>();
        foreach (nint h in EnumHandles(IgclNative.ctlEnumEngineGroups))
        {
            var props = new CtlEngineProperties { Size = (uint)Unsafe.SizeOf<CtlEngineProperties>() };
            if (Guard(() => IgclNative.ctlEngineGetProperties(h, ref props)) == CtlResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static CtlResult TryGetEngineActivity(nint engine, out CtlEngineStats stats)
    {
        var data = new CtlEngineStats { Size = (uint)Unsafe.SizeOf<CtlEngineStats>() };
        var rc = Guard(() => IgclNative.ctlEngineGetActivity(engine, ref data));
        stats = data;
        return rc;
    }

    // --- PCI -----------------------------------------------------------------

    public CtlResult TryGetPciProperties(out CtlPciProperties properties)
    {
        var data = new CtlPciProperties { Size = (uint)Unsafe.SizeOf<CtlPciProperties>() };
        var rc = Guard(() => IgclNative.ctlPciGetProperties(_handle, ref data));
        properties = data;
        return rc;
    }

    public CtlResult TryGetPciState(out CtlPciState state)
    {
        var data = new CtlPciState { Size = (uint)Unsafe.SizeOf<CtlPciState>() };
        var rc = Guard(() => IgclNative.ctlPciGetState(_handle, ref data));
        state = data;
        return rc;
    }

    // --- Fans ----------------------------------------------------------------

    public IReadOnlyList<(nint Handle, CtlFanProperties Properties)> GetFans()
    {
        var result = new List<(nint, CtlFanProperties)>();
        foreach (nint h in EnumHandles(IgclNative.ctlEnumFans))
        {
            var props = new CtlFanProperties { Size = (uint)Unsafe.SizeOf<CtlFanProperties>() };
            if (Guard(() => IgclNative.ctlFanGetProperties(h, ref props)) == CtlResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static CtlResult TryGetFanState(nint fan, CtlFanSpeedUnits units, out int speed)
    {
        int value = -1;
        var rc = Guard(() => IgclNative.ctlFanGetState(fan, units, out value));
        speed = value;
        return rc;
    }

    public static CtlResult TryGetFanConfig(nint fan, out CtlFanConfig config)
    {
        var data = new CtlFanConfig { Size = (uint)Unsafe.SizeOf<CtlFanConfig>() };
        var rc = Guard(() => IgclNative.ctlFanGetConfig(fan, ref data));
        config = data;
        return rc;
    }

    public static CtlResult TrySetFanDefaultMode(nint fan) =>
        Guard(() => IgclNative.ctlFanSetDefaultMode(fan));

    public static CtlResult TrySetFanFixedSpeed(nint fan, CtlFanSpeedUnits units, int speed)
    {
        var data = new CtlFanSpeed
        {
            Size = (uint)Unsafe.SizeOf<CtlFanSpeed>(),
            Speed = speed,
            Units = units,
        };
        return Guard(() => IgclNative.ctlFanSetFixedSpeedMode(fan, ref data));
    }

    // --- Power domains -------------------------------------------------------

    public IReadOnlyList<(nint Handle, CtlPowerDomainProperties Properties)> GetPowerDomains()
    {
        var result = new List<(nint, CtlPowerDomainProperties)>();
        foreach (nint h in EnumHandles(IgclNative.ctlEnumPowerDomains))
        {
            var props = new CtlPowerDomainProperties { Size = (uint)Unsafe.SizeOf<CtlPowerDomainProperties>() };
            if (Guard(() => IgclNative.ctlPowerGetProperties(h, ref props)) == CtlResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static CtlResult TryGetEnergyCounter(nint power, out CtlPowerEnergyCounter energy)
    {
        var data = new CtlPowerEnergyCounter { Size = (uint)Unsafe.SizeOf<CtlPowerEnergyCounter>() };
        var rc = Guard(() => IgclNative.ctlPowerGetEnergyCounter(power, ref data));
        energy = data;
        return rc;
    }

    public static CtlResult TryGetPowerLimits(nint power, out CtlPowerLimits limits)
    {
        var data = new CtlPowerLimits { Size = (uint)Unsafe.SizeOf<CtlPowerLimits>() };
        var rc = Guard(() => IgclNative.ctlPowerGetLimits(power, ref data));
        limits = data;
        return rc;
    }

    public static CtlResult TrySetPowerLimits(nint power, ref CtlPowerLimits limits)
    {
        var data = limits;
        data.Size = (uint)Unsafe.SizeOf<CtlPowerLimits>();
        var rc = Guard(() => IgclNative.ctlPowerSetLimits(power, ref data));
        limits = data;
        return rc;
    }

    // --- Overclock -----------------------------------------------------------

    /// <summary>
    /// The pre-V1 size of ctl_oc_properties_t (the three Version&gt;0 knob blocks
    /// were appended): offset of the first appended field.
    /// </summary>
    private static readonly uint OcPropertiesV0Size =
        (uint)(int)Marshal.OffsetOf<CtlOcProperties>(nameof(CtlOcProperties.VramMemSpeedLimit));

    /// <summary>
    /// The per-knob overclock capability report. Tries Version 1 first (unlocks
    /// VramMemSpeedLimit and the V/F-curve limit blocks), retries Version 0 with
    /// the same size, then Version 0 with the historical pre-V1 size.
    /// </summary>
    public CtlResult TryGetOcProperties(out CtlOcProperties properties)
    {
        var data = new CtlOcProperties
        {
            Size = (uint)Unsafe.SizeOf<CtlOcProperties>(),
            Version = 1,
        };
        var rc = Guard(() => IgclNative.ctlOverclockGetProperties(_handle, ref data));
        if (rc == CtlResult.ErrorUnsupportedVersion)
        {
            data = new CtlOcProperties
            {
                Size = (uint)Unsafe.SizeOf<CtlOcProperties>(),
                Version = 0,
            };
            rc = Guard(() => IgclNative.ctlOverclockGetProperties(_handle, ref data));
        }

        if (rc is CtlResult.ErrorInvalidSize or CtlResult.ErrorUnsupportedSize)
        {
            data = new CtlOcProperties { Size = OcPropertiesV0Size, Version = 0 };
            rc = Guard(() => IgclNative.ctlOverclockGetProperties(_handle, ref data));
        }

        properties = data;
        return rc;
    }

    /// <summary>
    /// Signs the driver's overclocking waiver for this session. Afterglow calls
    /// this only after the user has accepted the in-app warning - the driver
    /// refuses most overclock writes until it is set.
    /// </summary>
    public CtlResult TrySetOverclockWaiver() =>
        Guard(() => IgclNative.ctlOverclockWaiverSet(_handle));

    public CtlResult TryGetGpuFrequencyOffsetV2(out double offset)
    {
        double value = 0;
        var rc = Guard(() => IgclNative.ctlOverclockGpuFrequencyOffsetGetV2(_handle, out value));
        offset = value;
        return rc;
    }

    public CtlResult TrySetGpuFrequencyOffsetV2(double offset) =>
        Guard(() => IgclNative.ctlOverclockGpuFrequencyOffsetSetV2(_handle, offset));

    public CtlResult TryGetGpuFrequencyOffset(out double offsetMhz)
    {
        double value = 0;
        var rc = Guard(() => IgclNative.ctlOverclockGpuFrequencyOffsetGet(_handle, out value));
        offsetMhz = value;
        return rc;
    }

    public CtlResult TrySetGpuFrequencyOffset(double offsetMhz) =>
        Guard(() => IgclNative.ctlOverclockGpuFrequencyOffsetSet(_handle, offsetMhz));

    public CtlResult TryGetGpuVoltageOffsetV2(out double offset)
    {
        double value = 0;
        var rc = Guard(() => IgclNative.ctlOverclockGpuMaxVoltageOffsetGetV2(_handle, out value));
        offset = value;
        return rc;
    }

    public CtlResult TrySetGpuVoltageOffsetV2(double offset) =>
        Guard(() => IgclNative.ctlOverclockGpuMaxVoltageOffsetSetV2(_handle, offset));

    public CtlResult TryGetGpuVoltageOffset(out double offsetMv)
    {
        double value = 0;
        var rc = Guard(() => IgclNative.ctlOverclockGpuVoltageOffsetGet(_handle, out value));
        offsetMv = value;
        return rc;
    }

    public CtlResult TrySetGpuVoltageOffset(double offsetMv) =>
        Guard(() => IgclNative.ctlOverclockGpuVoltageOffsetSet(_handle, offsetMv));

    public CtlResult TryGetGpuLock(out CtlOcVfPair pair)
    {
        CtlOcVfPair value = default;
        var rc = Guard(() => IgclNative.ctlOverclockGpuLockGet(_handle, out value));
        pair = value;
        return rc;
    }

    /// <summary>Locks the GPU to a fixed V/F point; 0/0 returns to dynamic management.</summary>
    public CtlResult TrySetGpuLock(double voltageMv, double frequencyMhz)
    {
        var pair = new CtlOcVfPair
        {
            Size = (uint)Unsafe.SizeOf<CtlOcVfPair>(),
            Voltage = voltageMv,
            Frequency = frequencyMhz,
        };
        return Guard(() => IgclNative.ctlOverclockGpuLockSet(_handle, pair));
    }

    public CtlResult TryGetOcPowerLimitV2(out double limit)
    {
        double value = 0;
        var rc = Guard(() => IgclNative.ctlOverclockPowerLimitGetV2(_handle, out value));
        limit = value;
        return rc;
    }

    public CtlResult TrySetOcPowerLimitV2(double limit) =>
        Guard(() => IgclNative.ctlOverclockPowerLimitSetV2(_handle, limit));

    public CtlResult TryGetOcPowerLimit(out double sustainedMw)
    {
        double value = 0;
        var rc = Guard(() => IgclNative.ctlOverclockPowerLimitGet(_handle, out value));
        sustainedMw = value;
        return rc;
    }

    public CtlResult TrySetOcPowerLimit(double sustainedMw) =>
        Guard(() => IgclNative.ctlOverclockPowerLimitSet(_handle, sustainedMw));

    public CtlResult TryGetOcTemperatureLimitV2(out double limit)
    {
        double value = 0;
        var rc = Guard(() => IgclNative.ctlOverclockTemperatureLimitGetV2(_handle, out value));
        limit = value;
        return rc;
    }

    public CtlResult TrySetOcTemperatureLimitV2(double limit) =>
        Guard(() => IgclNative.ctlOverclockTemperatureLimitSetV2(_handle, limit));

    public CtlResult TryGetOcTemperatureLimit(out double limitC)
    {
        double value = 0;
        var rc = Guard(() => IgclNative.ctlOverclockTemperatureLimitGet(_handle, out value));
        limitC = value;
        return rc;
    }

    public CtlResult TrySetOcTemperatureLimit(double limitC) =>
        Guard(() => IgclNative.ctlOverclockTemperatureLimitSet(_handle, limitC));

    /// <summary>Resets frequency/voltage offsets, power/temp limits, and the GPU lock (not fans).</summary>
    public CtlResult TryResetOverclockToDefault() =>
        Guard(() => IgclNative.ctlOverclockResetToDefault(_handle));

    // --- V/F curve (newer drivers only; probe, never assume) ------------------

    public CtlResult TryReadVfCurve(CtlVfCurveType type, CtlVfCurveDetails detail, out CtlVoltageFrequencyPoint[] points)
    {
        points = [];
        uint count = 0;
        var rc = Guard(() => IgclNative.ctlOverclockReadVFCurve(_handle, type, detail, ref count, null));
        if (rc != CtlResult.Success || count == 0)
        {
            return rc;
        }

        var buffer = new CtlVoltageFrequencyPoint[count];
        rc = Guard(() => IgclNative.ctlOverclockReadVFCurve(_handle, type, detail, ref count, buffer));
        if (rc == CtlResult.Success)
        {
            points = buffer;
        }

        return rc;
    }

    public CtlResult TryWriteVfCurve(CtlVoltageFrequencyPoint[] points) =>
        Guard(() => IgclNative.ctlOverclockWriteCustomVFCurve(_handle, (uint)points.Length, points));

    internal static unsafe string FromAnsi(byte* buffer, int maxLength)
    {
        int length = 0;
        while (length < maxLength && buffer[length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(buffer, length);
    }
}
