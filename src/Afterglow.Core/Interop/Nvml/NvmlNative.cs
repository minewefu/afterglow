using System.Runtime.InteropServices;

namespace Afterglow.Core.Interop.Nvml;

/// <summary>nvmlUtilization_t</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlUtilization
{
    public uint Gpu;
    public uint Memory;
}

/// <summary>nvmlMemory_t (v1)</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlMemory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

/// <summary>nvmlBAR1Memory_t</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlBar1Memory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

/// <summary>
/// nvmlClockOffset_v1_t (24 bytes). Version constant = sizeof | (1 &lt;&lt; 24).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlClockOffset
{
    public const uint Version1 = 24 | (1u << 24);

    public uint Version;
    public NvmlClockType Type;
    public uint Pstate;
    public int ClockOffsetMHz;
    public int MinClockOffsetMHz;
    public int MaxClockOffsetMHz;
}

/// <summary>nvmlFanSpeedInfo_v1_t (12 bytes): out RPM for one fan.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlFanSpeedInfo
{
    public const uint Version1 = 12 | (1u << 24);

    public uint Version;
    public uint Fan;
    public uint SpeedRpm;
}

/// <summary>nvmlMarginTemperature_v1_t (8 bytes): headroom to the nearest slowdown threshold.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlMarginTemperature
{
    public const uint Version1 = 8 | (1u << 24);

    public uint Version;
    public int MarginTemperatureC;
}

/// <summary>nvmlFieldValue_t (40 bytes). Set FieldId before calling GetFieldValues.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlFieldValue
{
    public uint FieldId;
    public uint ScopeId;
    public long Timestamp;
    public long LatencyUsec;
    public uint ValueType;
    public NvmlReturn Status;
    public ulong Value;

    public const uint FiPowerInstant = 186;
    public const uint FiTemperatureShutdownTLimit = 193;
    public const uint FiTemperatureSlowdownTLimit = 194;
    public const uint FiTemperatureMemMaxTLimit = 195;
    public const uint FiTemperatureGpuMaxTLimit = 196;
    public const uint FiPcieCountTxBytes = 197;
    public const uint FiPcieCountRxBytes = 198;
}

/// <summary>nvmlPciInfo_t (68 bytes, unversioned).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NvmlPciInfo
{
    public fixed byte BusIdLegacy[16];
    public uint Domain;
    public uint Bus;
    public uint Device;
    public uint PciDeviceId;
    public uint PciSubSystemId;
    public fixed byte BusId[32];
}

/// <summary>
/// Raw P/Invoke surface of nvml.dll (ships with the NVIDIA driver in System32).
/// Signatures follow nvml.h; all functions here are part of NVIDIA's documented,
/// ABI-stable NVML API. Newer exports may be missing on old drivers — callers must
/// treat <see cref="EntryPointNotFoundException"/> as "not supported".
/// </summary>
internal static unsafe class NvmlNative
{
    private const string Lib = "nvml.dll";

    private static int _resolverInstalled;

    /// <summary>
    /// Installs the DllImport resolver for this assembly exactly once. Must be called
    /// before the first NVML P/Invoke (done by <see cref="NvmlApi.TryCreate"/>).
    /// </summary>
    internal static void EnsureResolverInstalled()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NvmlNative).Assembly, static (name, _, _) =>
        {
            if (!name.Equals(Lib, StringComparison.OrdinalIgnoreCase))
            {
                return IntPtr.Zero;
            }

            // Normal location since driver R445+: System32. Fall back to the legacy
            // NVSMI folder used by very old drivers.
            if (NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, Lib), out nint handle))
            {
                return handle;
            }

            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", Lib);
            return NativeLibrary.TryLoad(legacy, out handle) ? handle : IntPtr.Zero;
        });
    }

    // --- Lifecycle -----------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlInit_v2();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlShutdown();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlSystemGetDriverVersion(byte* version, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlSystemGetNVMLVersion(byte* version, uint length);

    // --- Device enumeration / identity --------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetCount_v2(out uint deviceCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetName(nint device, byte* name, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetUUID(nint device, byte* uuid, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetVbiosVersion(nint device, byte* version, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetBoardPartNumber(nint device, byte* partNumber, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetArchitecture(nint device, out uint architecture);

    // --- Temperatures --------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetTemperature(nint device, NvmlTemperatureSensor sensor, out uint tempC);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetTemperatureThreshold(
        nint device, NvmlTemperatureThreshold threshold, out uint tempC);

    // --- Clocks --------------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetClockInfo(nint device, NvmlClockType type, out uint clockMHz);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetMaxClockInfo(nint device, NvmlClockType type, out uint clockMHz);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetClock(
        nint device, NvmlClockType type, NvmlClockId clockId, out uint clockMHz);

    // --- Utilization / memory ------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetUtilizationRates(nint device, out NvmlUtilization utilization);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetEncoderUtilization(
        nint device, out uint utilization, out uint samplingPeriodUs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetDecoderUtilization(
        nint device, out uint utilization, out uint samplingPeriodUs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetMemoryInfo(nint device, out NvmlMemory memory);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetBAR1MemoryInfo(nint device, out NvmlBar1Memory bar1Memory);

    // --- Power ---------------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetPowerUsage(nint device, out uint milliwatts);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetEnforcedPowerLimit(nint device, out uint milliwatts);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetPowerManagementLimitConstraints(
        nint device, out uint minMilliwatts, out uint maxMilliwatts);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetPowerManagementDefaultLimit(nint device, out uint milliwatts);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceSetPowerManagementLimit(nint device, uint milliwatts);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetTotalEnergyConsumption(nint device, out ulong millijoules);

    // --- Performance state / throttling --------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetPerformanceState(nint device, out uint pstate);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetCurrentClocksThrottleReasons(nint device, out ulong reasons);

    // --- Fans ----------------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetNumFans(nint device, out uint numFans);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetFanSpeed(nint device, out uint speedPercent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetFanSpeed_v2(nint device, uint fan, out uint speedPercent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetMinMaxFanSpeed(nint device, out uint minPercent, out uint maxPercent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetFanControlPolicy_v2(
        nint device, uint fan, out NvmlFanControlPolicy policy);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceSetFanControlPolicy(
        nint device, uint fan, NvmlFanControlPolicy policy);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceSetFanSpeed_v2(nint device, uint fan, uint speedPercent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceSetDefaultFanSpeed_v2(nint device, uint fan);

    // --- Locked clocks (documented overclock-adjacent controls) ---------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceSetGpuLockedClocks(nint device, uint minClockMHz, uint maxClockMHz);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceResetGpuLockedClocks(nint device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceSetMemoryLockedClocks(nint device, uint minClockMHz, uint maxClockMHz);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceResetMemoryLockedClocks(nint device);

    // --- PCIe ----------------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetCurrPcieLinkGeneration(nint device, out uint generation);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetCurrPcieLinkWidth(nint device, out uint width);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetMaxPcieLinkGeneration(nint device, out uint generation);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetMaxPcieLinkWidth(nint device, out uint width);

    /// <summary>Note: the driver samples for ~20 ms inside this call; do not call on a UI thread.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetPcieThroughput(
        nint device, NvmlPcieUtilCounter counter, out uint kilobytesPerSecond);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetPciInfo_v3(nint device, out NvmlPciInfo pciInfo);

    // --- Clock offsets (modern overclocking API, driver R555+) -----------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetClockOffsets(nint device, ref NvmlClockOffset info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceSetClockOffsets(nint device, ref NvmlClockOffset info);

    // --- Newer fan / temperature reads ----------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetFanSpeedRPM(nint device, ref NvmlFanSpeedInfo fanSpeed);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetTargetFanSpeed(nint device, uint fan, out uint targetSpeedPercent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetMarginTemperature(nint device, ref NvmlMarginTemperature margin);

    // --- Batched field values ---------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlDeviceGetFieldValues(
        nint device, int valuesCount, [In, Out] NvmlFieldValue[] values);
}
