namespace Afterglow.Core.Interop.LevelZero;

/// <summary>
/// Managed wrapper around Level Zero Sysman (zes*), Afterglow's second Intel
/// telemetry source. Read-only by design: Afterglow routes all hardware writes
/// through IGCL, and zes_api.h itself deprecates its overclock block outright
/// and the legacy power-limit calls in favor of zesPower*LimitsExt.
/// There is no shutdown call in the Sysman API; the loader lives for the process.
/// </summary>
public sealed class ZesApi
{
    private ZesApi(IReadOnlyList<ZesDevice> devices)
    {
        Devices = devices;
    }

    /// <summary>All Sysman devices across all Sysman drivers, enumerated at init.</summary>
    public IReadOnlyList<ZesDevice> Devices { get; }

    /// <summary>
    /// Initializes Sysman via zesInit and enumerates its devices. Returns null
    /// with the failure code when ze_loader.dll is absent or no Sysman driver
    /// responds.
    /// </summary>
    public static ZesApi? TryCreate(out ZeResult status)
    {
        try
        {
            status = ZesNative.zesInit(0);
        }
        catch (DllNotFoundException)
        {
            status = ZeResult.LibraryNotFound;
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            status = ZeResult.FunctionNotFound;
            return null;
        }

        return status == ZeResult.Success ? new ZesApi(EnumerateDevices()) : null;
    }

    private static List<ZesDevice> EnumerateDevices()
    {
        var devices = new List<ZesDevice>();

        uint driverCount = 0;
        if (ZesNative.zesDriverGet(ref driverCount, null) != ZeResult.Success || driverCount == 0)
        {
            return devices;
        }

        var drivers = new nint[driverCount];
        if (ZesNative.zesDriverGet(ref driverCount, drivers) != ZeResult.Success)
        {
            return devices;
        }

        foreach (nint driver in drivers)
        {
            uint deviceCount = 0;
            if (ZesNative.zesDeviceGet(driver, ref deviceCount, null) != ZeResult.Success || deviceCount == 0)
            {
                continue;
            }

            var handles = new nint[deviceCount];
            if (ZesNative.zesDeviceGet(driver, ref deviceCount, handles) != ZeResult.Success)
            {
                continue;
            }

            foreach (nint handle in handles)
            {
                devices.Add(new ZesDevice(handle));
            }
        }

        return devices;
    }
}

/// <summary>
/// One Sysman device. Every method reports the raw <see cref="ZeResult"/>;
/// UnsupportedFeature from a component is the normal "not on this device"
/// answer, especially on Windows iGPUs where Sysman coverage is the weak end
/// of Intel's support matrix.
/// </summary>
public sealed class ZesDevice
{
    private readonly nint _handle;

    internal ZesDevice(nint handle)
    {
        _handle = handle;
    }

    private static ZeResult Guard(Func<ZeResult> call)
    {
        try
        {
            return call();
        }
        catch (EntryPointNotFoundException)
        {
            return ZeResult.FunctionNotFound;
        }
    }

    /// <summary>PCI BDF — the key used to pair this Sysman device with its IGCL adapter.</summary>
    public ZeResult TryGetPciProperties(out ZesPciProperties properties)
    {
        var data = new ZesPciProperties { SType = (int)ZesStructureType.PciProperties };
        var rc = Guard(() => ZesNative.zesDevicePciGetProperties(_handle, ref data));
        properties = data;
        return rc;
    }

    private delegate ZeResult EnumCall(nint device, ref uint count, nint[]? handles);

    private nint[] EnumHandles(EnumCall call)
    {
        uint count = 0;
        if (Guard(() => call(_handle, ref count, null)) != ZeResult.Success || count == 0)
        {
            return [];
        }

        var handles = new nint[count];
        return Guard(() => call(_handle, ref count, handles)) == ZeResult.Success ? handles : [];
    }

    public IReadOnlyList<(nint Handle, ZesPowerProperties Properties)> GetPowerDomains()
    {
        var result = new List<(nint, ZesPowerProperties)>();
        foreach (nint h in EnumHandles(ZesNative.zesDeviceEnumPowerDomains))
        {
            var props = new ZesPowerProperties { SType = (int)ZesStructureType.PowerProperties };
            if (Guard(() => ZesNative.zesPowerGetProperties(h, ref props)) == ZeResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static ZeResult TryGetEnergyCounter(nint power, out ZesPowerEnergyCounter energy)
    {
        ZesPowerEnergyCounter data = default;
        var rc = Guard(() => ZesNative.zesPowerGetEnergyCounter(power, ref data));
        energy = data;
        return rc;
    }

    public IReadOnlyList<(nint Handle, ZesFreqProperties Properties)> GetFrequencyDomains()
    {
        var result = new List<(nint, ZesFreqProperties)>();
        foreach (nint h in EnumHandles(ZesNative.zesDeviceEnumFrequencyDomains))
        {
            var props = new ZesFreqProperties { SType = (int)ZesStructureType.FreqProperties };
            if (Guard(() => ZesNative.zesFrequencyGetProperties(h, ref props)) == ZeResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static ZeResult TryGetFrequencyState(nint frequency, out ZesFreqState state)
    {
        var data = new ZesFreqState { SType = (int)ZesStructureType.FreqState };
        var rc = Guard(() => ZesNative.zesFrequencyGetState(frequency, ref data));
        state = data;
        return rc;
    }

    public IReadOnlyList<(nint Handle, ZesTempProperties Properties)> GetTemperatureSensors()
    {
        var result = new List<(nint, ZesTempProperties)>();
        foreach (nint h in EnumHandles(ZesNative.zesDeviceEnumTemperatureSensors))
        {
            var props = new ZesTempProperties { SType = (int)ZesStructureType.TempProperties };
            if (Guard(() => ZesNative.zesTemperatureGetProperties(h, ref props)) == ZeResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static ZeResult TryGetTemperature(nint sensor, out double temperatureC)
    {
        double value = 0;
        var rc = Guard(() => ZesNative.zesTemperatureGetState(sensor, out value));
        temperatureC = value;
        return rc;
    }

    public IReadOnlyList<(nint Handle, ZesMemProperties Properties)> GetMemoryModules()
    {
        var result = new List<(nint, ZesMemProperties)>();
        foreach (nint h in EnumHandles(ZesNative.zesDeviceEnumMemoryModules))
        {
            var props = new ZesMemProperties { SType = (int)ZesStructureType.MemProperties };
            if (Guard(() => ZesNative.zesMemoryGetProperties(h, ref props)) == ZeResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static ZeResult TryGetMemoryState(nint memory, out ZesMemState state)
    {
        var data = new ZesMemState { SType = (int)ZesStructureType.MemState };
        var rc = Guard(() => ZesNative.zesMemoryGetState(memory, ref data));
        state = data;
        return rc;
    }

    public static ZeResult TryGetMemoryBandwidth(nint memory, out ZesMemBandwidth bandwidth)
    {
        ZesMemBandwidth data = default;
        var rc = Guard(() => ZesNative.zesMemoryGetBandwidth(memory, ref data));
        bandwidth = data;
        return rc;
    }

    public IReadOnlyList<(nint Handle, ZesEngineProperties Properties)> GetEngineGroups()
    {
        var result = new List<(nint, ZesEngineProperties)>();
        foreach (nint h in EnumHandles(ZesNative.zesDeviceEnumEngineGroups))
        {
            var props = new ZesEngineProperties { SType = (int)ZesStructureType.EngineProperties };
            if (Guard(() => ZesNative.zesEngineGetProperties(h, ref props)) == ZeResult.Success)
            {
                result.Add((h, props));
            }
        }

        return result;
    }

    public static ZeResult TryGetEngineActivity(nint engine, out ZesEngineStats stats)
    {
        ZesEngineStats data = default;
        var rc = Guard(() => ZesNative.zesEngineGetActivity(engine, ref data));
        stats = data;
        return rc;
    }

    /// <summary>Fan handle count only — expected 0 on handheld iGPUs (EC-controlled fans).</summary>
    public int GetFanCount()
    {
        return EnumHandles(ZesNative.zesDeviceEnumFans).Length;
    }
}
