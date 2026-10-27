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
/// One NVML GPU. Thin, allocation-free wrappers over the native calls; every
/// method reports the raw <see cref="NvmlReturn"/> so callers can distinguish
/// "unsupported on this GPU" from real errors. Newer driver exports missing on
/// old drivers surface as <see cref="NvmlReturn.FunctionNotFound"/>.
/// </summary>
public sealed unsafe class NvmlDevice : INvmlTuningDevice
{
    private readonly nint _handle;

    internal NvmlDevice(nint handle, uint index)
    {
        _handle = handle;
        Index = index;
    }

    public uint Index { get; }

    internal nint Handle => _handle;

    // --- Missing-export guards -------------------------------------------------
    // Exports newer than the oldest supported driver go through these: an
    // EntryPointNotFoundException (thrown when the generated P/Invoke first
    // binds) becomes FunctionNotFound. They take the P/Invoke's address as a
    // managed function pointer rather than a lambda, so a guarded call costs no
    // closure and no delegate — the sensor poll calls several every tick. Out
    // values are zero when the export is missing.

    private NvmlReturn Call(delegate*<nint, NvmlReturn> call)
    {
        try
        {
            return call(_handle);
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }
    }

    private NvmlReturn Call<TA>(delegate*<nint, TA, NvmlReturn> call, TA a)
    {
        try
        {
            return call(_handle, a);
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }
    }

    private NvmlReturn Call<TA, TB>(delegate*<nint, TA, TB, NvmlReturn> call, TA a, TB b)
    {
        try
        {
            return call(_handle, a, b);
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }
    }

    private NvmlReturn Read<T>(delegate*<nint, out T, NvmlReturn> call, out T value)
        where T : unmanaged
    {
        value = default;
        try
        {
            return call(_handle, out value);
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }
    }

    private NvmlReturn Read<TA, T>(delegate*<nint, TA, out T, NvmlReturn> call, TA a, out T value)
        where T : unmanaged
    {
        value = default;
        try
        {
            return call(_handle, a, out value);
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }
    }

    private NvmlReturn ReadPair(delegate*<nint, out uint, out uint, NvmlReturn> call, out uint first, out uint second)
    {
        first = second = 0;
        try
        {
            return call(_handle, out first, out second);
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }
    }

    /// <summary>In/out struct calls (versioned NVML structs carry their request in the same buffer).</summary>
    private NvmlReturn Exchange<T>(delegate*<nint, ref T, NvmlReturn> call, ref T value)
        where T : unmanaged
    {
        try
        {
            return call(_handle, ref value);
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }
    }

    // --- Identity ------------------------------------------------------------

    public string? GetName()
    {
        byte* buffer = stackalloc byte[97];
        return NvmlNative.nvmlDeviceGetName(_handle, buffer, 96) == NvmlReturn.Success
            ? NvmlApi.FromUtf8(buffer, 96)
            : null;
    }

    public string? GetUuid()
    {
        byte* buffer = stackalloc byte[97];
        return NvmlNative.nvmlDeviceGetUUID(_handle, buffer, 96) == NvmlReturn.Success
            ? NvmlApi.FromUtf8(buffer, 96)
            : null;
    }

    public string? GetVbiosVersion()
    {
        byte* buffer = stackalloc byte[33];
        return NvmlNative.nvmlDeviceGetVbiosVersion(_handle, buffer, 32) == NvmlReturn.Success
            ? NvmlApi.FromUtf8(buffer, 32)
            : null;
    }

    public string? GetBoardPartNumber()
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

    public NvmlReturn TryGetArchitecture(out uint architecture) =>
        Read(&NvmlNative.nvmlDeviceGetArchitecture, out architecture);

    // --- Temperatures --------------------------------------------------------

    public NvmlReturn TryGetTemperature(NvmlTemperatureSensor sensor, out uint tempC) =>
        NvmlNative.nvmlDeviceGetTemperature(_handle, sensor, out tempC);

    public NvmlReturn TryGetTemperatureThreshold(NvmlTemperatureThreshold threshold, out uint tempC) =>
        Read(&NvmlNative.nvmlDeviceGetTemperatureThreshold, threshold, out tempC);

    // --- Clocks --------------------------------------------------------------

    public NvmlReturn TryGetClock(NvmlClockType type, out uint mhz) =>
        NvmlNative.nvmlDeviceGetClockInfo(_handle, type, out mhz);

    public NvmlReturn TryGetMaxClock(NvmlClockType type, out uint mhz) =>
        NvmlNative.nvmlDeviceGetMaxClockInfo(_handle, type, out mhz);

    public NvmlReturn TryGetClockById(NvmlClockType type, NvmlClockId id, out uint mhz)
    {
        mhz = 0;
        try
        {
            return NvmlNative.nvmlDeviceGetClock(_handle, type, id, out mhz);
        }
        catch (EntryPointNotFoundException)
        {
            return NvmlReturn.FunctionNotFound;
        }
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

    public NvmlReturn TryGetBar1MemoryInfo(out NvmlBar1Memory memory) =>
        Read(&NvmlNative.nvmlDeviceGetBAR1MemoryInfo, out memory);

    // --- Power ---------------------------------------------------------------

    public NvmlReturn TryGetPowerUsage(out uint milliwatts) =>
        NvmlNative.nvmlDeviceGetPowerUsage(_handle, out milliwatts);

    public NvmlReturn TryGetEnforcedPowerLimit(out uint milliwatts) =>
        NvmlNative.nvmlDeviceGetEnforcedPowerLimit(_handle, out milliwatts);

    public NvmlReturn TryGetPowerLimitConstraints(out uint minMw, out uint maxMw) =>
        ReadPair(&NvmlNative.nvmlDeviceGetPowerManagementLimitConstraints, out minMw, out maxMw);

    public NvmlReturn TryGetDefaultPowerLimit(out uint milliwatts) =>
        Read(&NvmlNative.nvmlDeviceGetPowerManagementDefaultLimit, out milliwatts);

    public NvmlReturn TrySetPowerLimit(uint milliwatts) =>
        Call(&NvmlNative.nvmlDeviceSetPowerManagementLimit, milliwatts);

    public NvmlReturn TryGetTotalEnergyConsumption(out ulong millijoules) =>
        Read(&NvmlNative.nvmlDeviceGetTotalEnergyConsumption, out millijoules);

    // --- Performance / throttling --------------------------------------------

    public NvmlReturn TryGetPerformanceState(out uint pstate) =>
        NvmlNative.nvmlDeviceGetPerformanceState(_handle, out pstate);

    public NvmlReturn TryGetClocksEventReasons(out NvmlClocksEventReasons reasons)
    {
        var rc = Read(&NvmlNative.nvmlDeviceGetCurrentClocksThrottleReasons, out ulong raw);
        reasons = (NvmlClocksEventReasons)raw;
        return rc;
    }

    // --- Fans ----------------------------------------------------------------

    public NvmlReturn TryGetNumFans(out uint numFans) =>
        Read(&NvmlNative.nvmlDeviceGetNumFans, out numFans);

    public NvmlReturn TryGetFanSpeed(out uint percent) =>
        NvmlNative.nvmlDeviceGetFanSpeed(_handle, out percent);

    public NvmlReturn TryGetFanSpeed(uint fan, out uint percent) =>
        Read(&NvmlNative.nvmlDeviceGetFanSpeed_v2, fan, out percent);

    public NvmlReturn TryGetMinMaxFanSpeed(out uint minPercent, out uint maxPercent) =>
        ReadPair(&NvmlNative.nvmlDeviceGetMinMaxFanSpeed, out minPercent, out maxPercent);

    public NvmlReturn TryGetFanControlPolicy(uint fan, out NvmlFanControlPolicy policy) =>
        Read(&NvmlNative.nvmlDeviceGetFanControlPolicy_v2, fan, out policy);

    public NvmlReturn TrySetFanControlPolicy(uint fan, NvmlFanControlPolicy policy) =>
        Call(&NvmlNative.nvmlDeviceSetFanControlPolicy, fan, policy);

    public NvmlReturn TrySetFanSpeed(uint fan, uint percent) =>
        Call(&NvmlNative.nvmlDeviceSetFanSpeed_v2, fan, percent);

    public NvmlReturn TrySetDefaultFanSpeed(uint fan) =>
        Call(&NvmlNative.nvmlDeviceSetDefaultFanSpeed_v2, fan);

    // --- Locked clocks -------------------------------------------------------

    public NvmlReturn TrySetGpuLockedClocks(uint minMhz, uint maxMhz) =>
        Call(&NvmlNative.nvmlDeviceSetGpuLockedClocks, minMhz, maxMhz);

    public NvmlReturn TryResetGpuLockedClocks() =>
        Call(&NvmlNative.nvmlDeviceResetGpuLockedClocks);

    public NvmlReturn TrySetMemoryLockedClocks(uint minMhz, uint maxMhz) =>
        Call(&NvmlNative.nvmlDeviceSetMemoryLockedClocks, minMhz, maxMhz);

    public NvmlReturn TryResetMemoryLockedClocks() =>
        Call(&NvmlNative.nvmlDeviceResetMemoryLockedClocks);

    // --- Clock offsets (modern OC API) ---------------------------------------

    /// <summary>Reads the current offset and the driver-reported legal range for a clock domain.</summary>
    public NvmlReturn TryGetClockOffset(NvmlClockType type, out NvmlClockOffset offset)
    {
        offset = new NvmlClockOffset
        {
            Version = NvmlClockOffset.Version1,
            Type = type,
            Pstate = 0,
        };
        return Exchange(&NvmlNative.nvmlDeviceGetClockOffsets, ref offset);
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
        return Exchange(&NvmlNative.nvmlDeviceSetClockOffsets, ref info);
    }

    // --- Newer fan / temperature reads ---------------------------------------

    public NvmlReturn TryGetFanRpm(uint fan, out uint rpm)
    {
        var info = new NvmlFanSpeedInfo { Version = NvmlFanSpeedInfo.Version1, Fan = fan };
        var rc = Exchange(&NvmlNative.nvmlDeviceGetFanSpeedRPM, ref info);
        rpm = info.SpeedRpm;
        return rc;
    }

    public NvmlReturn TryGetTargetFanSpeed(uint fan, out uint percent) =>
        Read(&NvmlNative.nvmlDeviceGetTargetFanSpeed, fan, out percent);

    /// <summary>Headroom in °C to the nearest slowdown threshold (driver R570+).</summary>
    public NvmlReturn TryGetThrottleMargin(out int marginC)
    {
        var info = new NvmlMarginTemperature { Version = NvmlMarginTemperature.Version1 };
        var rc = Exchange(&NvmlNative.nvmlDeviceGetMarginTemperature, ref info);
        marginC = info.MarginTemperatureC;
        return rc;
    }

    /// <summary>
    /// Batched field-value read, in place over the caller's buffer (an array
    /// converts implicitly). Each entry's Status must be checked individually;
    /// the call succeeds if any field was populated.
    /// </summary>
    public NvmlReturn TryGetFieldValues(Span<NvmlFieldValue> values)
    {
        fixed (NvmlFieldValue* buffer = values)
        {
            try
            {
                return NvmlNative.nvmlDeviceGetFieldValues(_handle, values.Length, buffer);
            }
            catch (EntryPointNotFoundException)
            {
                return NvmlReturn.FunctionNotFound;
            }
        }
    }

    public NvmlReturn TryGetPciInfo(out uint domain, out uint bus, out uint device, out string busId)
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
    public NvmlReturn TryGetPcieThroughput(NvmlPcieUtilCounter counter, out uint kilobytesPerSecond) =>
        Read(&NvmlNative.nvmlDeviceGetPcieThroughput, counter, out kilobytesPerSecond);
}
