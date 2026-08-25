using System.Text;

namespace Afterglow.Core.Interop.Nvml;

/// <summary>
/// Managed lifecycle wrapper around NVML. Create via <see cref="TryCreate"/>,
/// dispose to release the library. All device access flows through
/// <see cref="NvmlDevice"/> instances obtained from <see cref="GetDevices"/>.
/// </summary>
public sealed class NvmlApi : IDisposable
{
    private bool _disposed;

    private NvmlApi()
    {
    }

    /// <summary>
    /// Initializes NVML. Returns null (with the failure code) when the NVIDIA driver
    /// is not installed or nvml.dll cannot be loaded.
    /// </summary>
    public static NvmlApi? TryCreate(out NvmlReturn status)
    {
        NvmlNative.EnsureResolverInstalled();
        try
        {
            status = NvmlNative.nvmlInit_v2();
        }
        catch (DllNotFoundException)
        {
            status = NvmlReturn.LibraryNotFound;
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            status = NvmlReturn.FunctionNotFound;
            return null;
        }

        return status == NvmlReturn.Success ? new NvmlApi() : null;
    }

    public unsafe string? GetDriverVersion()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte* buffer = stackalloc byte[81];
        return NvmlNative.nvmlSystemGetDriverVersion(buffer, 80) == NvmlReturn.Success
            ? FromUtf8(buffer, 80)
            : null;
    }

    public unsafe string? GetNvmlVersion()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte* buffer = stackalloc byte[81];
        return NvmlNative.nvmlSystemGetNVMLVersion(buffer, 80) == NvmlReturn.Success
            ? FromUtf8(buffer, 80)
            : null;
    }

    public IReadOnlyList<NvmlDevice> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (NvmlNative.nvmlDeviceGetCount_v2(out uint count) != NvmlReturn.Success)
        {
            return [];
        }

        var devices = new List<NvmlDevice>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (NvmlNative.nvmlDeviceGetHandleByIndex_v2(i, out nint handle) == NvmlReturn.Success)
            {
                devices.Add(new NvmlDevice(handle, i));
            }
        }

        return devices;
    }

    internal static unsafe string FromUtf8(byte* buffer, int maxLength)
    {
        int length = 0;
        while (length < maxLength && buffer[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(buffer, length);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _ = NvmlNative.nvmlShutdown();
        }
    }
}

/// <summary>
/// One NVML GPU. Thin, allocation-light wrappers over the native calls; every
/// method reports the raw <see cref="NvmlReturn"/> so callers can distinguish
/// "unsupported on this GPU" from real errors. Newer driver exports missing on
/// old drivers surface as <see cref="NvmlReturn.FunctionNotFound"/>.
/// </summary>
public sealed class NvmlDevice
{
    private readonly nint _handle;

    internal NvmlDevice(nint handle, uint index)
    {
        _handle = handle;
        Index = index;
    }

    public uint Index { get; }

    internal nint Handle => _handle;

    private static NvmlReturn Guard(Func<NvmlReturn> call)
    {
        try
        {
            return call();
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }
    }

    // --- Identity ------------------------------------------------------------

    public unsafe string? GetName()
    {
        byte* buffer = stackalloc byte[97];
        return NvmlNative.nvmlDeviceGetName(_handle, buffer, 96) == NvmlReturn.Success
            ? NvmlApi.FromUtf8(buffer, 96)
            : null;
    }

    public unsafe string? GetUuid()
    {
        byte* buffer = stackalloc byte[97];
        return NvmlNative.nvmlDeviceGetUUID(_handle, buffer, 96) == NvmlReturn.Success
            ? NvmlApi.FromUtf8(buffer, 96)
            : null;
    }

    public unsafe string? GetVbiosVersion()
    {
        byte* buffer = stackalloc byte[33];
        return NvmlNative.nvmlDeviceGetVbiosVersion(_handle, buffer, 32) == NvmlReturn.Success
            ? NvmlApi.FromUtf8(buffer, 32)
            : null;
    }

    public unsafe string? GetBoardPartNumber()
    {
        byte* buffer = stackalloc byte[81];
        NvmlReturn rc;
        try
        {
            rc = NvmlNative.nvmlDeviceGetBoardPartNumber(_handle, buffer, 80);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        return rc == NvmlReturn.Success ? NvmlApi.FromUtf8(buffer, 80) : null;
    }

    public NvmlReturn TryGetArchitecture(out uint architecture)
    {
        uint value = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetArchitecture(_handle, out value));
        architecture = value;
        return rc;
    }

    // --- Temperatures --------------------------------------------------------

    public NvmlReturn TryGetTemperature(NvmlTemperatureSensor sensor, out uint tempC) =>
        NvmlNative.nvmlDeviceGetTemperature(_handle, sensor, out tempC);

    public NvmlReturn TryGetTemperatureThreshold(NvmlTemperatureThreshold threshold, out uint tempC)
    {
        uint value = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetTemperatureThreshold(_handle, threshold, out value));
        tempC = value;
        return rc;
    }

    // --- Clocks --------------------------------------------------------------

    public NvmlReturn TryGetClock(NvmlClockType type, out uint mhz) =>
        NvmlNative.nvmlDeviceGetClockInfo(_handle, type, out mhz);

    public NvmlReturn TryGetMaxClock(NvmlClockType type, out uint mhz) =>
        NvmlNative.nvmlDeviceGetMaxClockInfo(_handle, type, out mhz);

    public NvmlReturn TryGetClockById(NvmlClockType type, NvmlClockId id, out uint mhz)
    {
        uint value = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetClock(_handle, type, id, out value));
        mhz = value;
        return rc;
    }

    // --- Utilization / memory ------------------------------------------------

    public NvmlReturn TryGetUtilization(out NvmlUtilization utilization) =>
        NvmlNative.nvmlDeviceGetUtilizationRates(_handle, out utilization);

    public NvmlReturn TryGetEncoderUtilization(out uint percent)
    {
        var rc = NvmlNative.nvmlDeviceGetEncoderUtilization(_handle, out percent, out _);
        return rc;
    }

    public NvmlReturn TryGetDecoderUtilization(out uint percent)
    {
        var rc = NvmlNative.nvmlDeviceGetDecoderUtilization(_handle, out percent, out _);
        return rc;
    }

    public NvmlReturn TryGetMemoryInfo(out NvmlMemory memory) =>
        NvmlNative.nvmlDeviceGetMemoryInfo(_handle, out memory);

    public NvmlReturn TryGetBar1MemoryInfo(out NvmlBar1Memory memory)
    {
        var result = default(NvmlBar1Memory);
        var rc = Guard(() => NvmlNative.nvmlDeviceGetBAR1MemoryInfo(_handle, out result));
        memory = result;
        return rc;
    }

    // --- Power ---------------------------------------------------------------

    public NvmlReturn TryGetPowerUsage(out uint milliwatts) =>
        NvmlNative.nvmlDeviceGetPowerUsage(_handle, out milliwatts);

    public NvmlReturn TryGetEnforcedPowerLimit(out uint milliwatts) =>
        NvmlNative.nvmlDeviceGetEnforcedPowerLimit(_handle, out milliwatts);

    public NvmlReturn TryGetPowerLimitConstraints(out uint minMw, out uint maxMw)
    {
        uint min = 0, max = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetPowerManagementLimitConstraints(_handle, out min, out max));
        minMw = min;
        maxMw = max;
        return rc;
    }

    public NvmlReturn TryGetDefaultPowerLimit(out uint milliwatts)
    {
        uint value = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetPowerManagementDefaultLimit(_handle, out value));
        milliwatts = value;
        return rc;
    }

    public NvmlReturn TrySetPowerLimit(uint milliwatts) =>
        Guard(() => NvmlNative.nvmlDeviceSetPowerManagementLimit(_handle, milliwatts));

    public NvmlReturn TryGetTotalEnergyConsumption(out ulong millijoules)
    {
        ulong value = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetTotalEnergyConsumption(_handle, out value));
        millijoules = value;
        return rc;
    }

    // --- Performance / throttling --------------------------------------------

    public NvmlReturn TryGetPerformanceState(out uint pstate) =>
        NvmlNative.nvmlDeviceGetPerformanceState(_handle, out pstate);

    public NvmlReturn TryGetClocksEventReasons(out NvmlClocksEventReasons reasons)
    {
        ulong raw = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetCurrentClocksThrottleReasons(_handle, out raw));
        reasons = (NvmlClocksEventReasons)raw;
        return rc;
    }

    // --- Fans ----------------------------------------------------------------

    public NvmlReturn TryGetNumFans(out uint numFans)
    {
        uint value = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetNumFans(_handle, out value));
        numFans = value;
        return rc;
    }

    public NvmlReturn TryGetFanSpeed(out uint percent) =>
        NvmlNative.nvmlDeviceGetFanSpeed(_handle, out percent);

    public NvmlReturn TryGetFanSpeed(uint fan, out uint percent)
    {
        uint value = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetFanSpeed_v2(_handle, fan, out value));
        percent = value;
        return rc;
    }

    public NvmlReturn TryGetMinMaxFanSpeed(out uint minPercent, out uint maxPercent)
    {
        uint min = 0, max = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetMinMaxFanSpeed(_handle, out min, out max));
        minPercent = min;
        maxPercent = max;
        return rc;
    }

    public NvmlReturn TryGetFanControlPolicy(uint fan, out NvmlFanControlPolicy policy)
    {
        NvmlFanControlPolicy value = NvmlFanControlPolicy.TemperatureContinuous;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetFanControlPolicy_v2(_handle, fan, out value));
        policy = value;
        return rc;
    }

    public NvmlReturn TrySetFanControlPolicy(uint fan, NvmlFanControlPolicy policy) =>
        Guard(() => NvmlNative.nvmlDeviceSetFanControlPolicy(_handle, fan, policy));

    public NvmlReturn TrySetFanSpeed(uint fan, uint percent) =>
        Guard(() => NvmlNative.nvmlDeviceSetFanSpeed_v2(_handle, fan, percent));

    public NvmlReturn TrySetDefaultFanSpeed(uint fan) =>
        Guard(() => NvmlNative.nvmlDeviceSetDefaultFanSpeed_v2(_handle, fan));

    // --- Locked clocks -------------------------------------------------------

    public NvmlReturn TrySetGpuLockedClocks(uint minMhz, uint maxMhz) =>
        Guard(() => NvmlNative.nvmlDeviceSetGpuLockedClocks(_handle, minMhz, maxMhz));

    public NvmlReturn TryResetGpuLockedClocks() =>
        Guard(() => NvmlNative.nvmlDeviceResetGpuLockedClocks(_handle));

    public NvmlReturn TrySetMemoryLockedClocks(uint minMhz, uint maxMhz) =>
        Guard(() => NvmlNative.nvmlDeviceSetMemoryLockedClocks(_handle, minMhz, maxMhz));

    public NvmlReturn TryResetMemoryLockedClocks() =>
        Guard(() => NvmlNative.nvmlDeviceResetMemoryLockedClocks(_handle));

    // --- Clock offsets (modern OC API) ---------------------------------------

    /// <summary>Reads the current offset and the driver-reported legal range for a clock domain.</summary>
    public NvmlReturn TryGetClockOffset(NvmlClockType type, out NvmlClockOffset offset)
    {
        var info = new NvmlClockOffset
        {
            Version = NvmlClockOffset.Version1,
            Type = type,
            Pstate = 0,
        };
        var rc = Guard(() => NvmlNative.nvmlDeviceGetClockOffsets(_handle, ref info));
        offset = info;
        return rc;
    }

    /// <summary>Applies a clock offset (requires elevation). Caller must clamp to the reported range.</summary>
    public NvmlReturn TrySetClockOffset(NvmlClockType type, int offsetMHz)
    {
        var info = new NvmlClockOffset
        {
            Version = NvmlClockOffset.Version1,
            Type = type,
            Pstate = 0,
            ClockOffsetMHz = offsetMHz,
        };
        return Guard(() => NvmlNative.nvmlDeviceSetClockOffsets(_handle, ref info));
    }

    // --- Newer fan / temperature reads ---------------------------------------

    public NvmlReturn TryGetFanRpm(uint fan, out uint rpm)
    {
        var info = new NvmlFanSpeedInfo { Version = NvmlFanSpeedInfo.Version1, Fan = fan };
        var rc = Guard(() => NvmlNative.nvmlDeviceGetFanSpeedRPM(_handle, ref info));
        rpm = info.SpeedRpm;
        return rc;
    }

    public NvmlReturn TryGetTargetFanSpeed(uint fan, out uint percent)
    {
        uint value = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetTargetFanSpeed(_handle, fan, out value));
        percent = value;
        return rc;
    }

    /// <summary>Headroom in °C to the nearest slowdown threshold (driver R570+).</summary>
    public NvmlReturn TryGetThrottleMargin(out int marginC)
    {
        var info = new NvmlMarginTemperature { Version = NvmlMarginTemperature.Version1 };
        var rc = Guard(() => NvmlNative.nvmlDeviceGetMarginTemperature(_handle, ref info));
        marginC = info.MarginTemperatureC;
        return rc;
    }

    /// <summary>
    /// Batched field-value read. Each entry's Status must be checked individually;
    /// the call succeeds if any field was populated.
    /// </summary>
    public NvmlReturn TryGetFieldValues(NvmlFieldValue[] values)
    {
        return Guard(() => NvmlNative.nvmlDeviceGetFieldValues(_handle, values.Length, values));
    }

    public unsafe NvmlReturn TryGetPciInfo(out uint domain, out uint bus, out uint device, out string busId)
    {
        domain = bus = device = 0;
        busId = string.Empty;
        NvmlPciInfo info = default;
        NvmlReturn rc;
        try
        {
            rc = NvmlNative.nvmlDeviceGetPciInfo_v3(_handle, out info);
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }

        if (rc == NvmlReturn.Success)
        {
            domain = info.Domain;
            bus = info.Bus;
            device = info.Device;
            busId = NvmlApi.FromUtf8(info.BusId, 32);
        }

        return rc;
    }

    // --- PCIe ----------------------------------------------------------------

    public NvmlReturn TryGetPcieLink(out uint currentGen, out uint currentWidth, out uint maxGen, out uint maxWidth)
    {
        currentGen = currentWidth = maxGen = maxWidth = 0;
        var rc = NvmlNative.nvmlDeviceGetCurrPcieLinkGeneration(_handle, out currentGen);
        if (rc != NvmlReturn.Success)
        {
            return rc;
        }

        _ = NvmlNative.nvmlDeviceGetCurrPcieLinkWidth(_handle, out currentWidth);
        _ = NvmlNative.nvmlDeviceGetMaxPcieLinkGeneration(_handle, out maxGen);
        _ = NvmlNative.nvmlDeviceGetMaxPcieLinkWidth(_handle, out maxWidth);
        return NvmlReturn.Success;
    }

    /// <summary>Blocking ~20 ms sample inside the driver; call from a background thread only.</summary>
    public NvmlReturn TryGetPcieThroughput(NvmlPcieUtilCounter counter, out uint kilobytesPerSecond)
    {
        uint value = 0;
        var rc = Guard(() => NvmlNative.nvmlDeviceGetPcieThroughput(_handle, counter, out value));
        kilobytesPerSecond = value;
        return rc;
    }
}
