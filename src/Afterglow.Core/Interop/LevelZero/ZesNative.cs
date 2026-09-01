using System.Runtime.InteropServices;

namespace Afterglow.Core.Interop.LevelZero;

/// <summary>
/// ze_result_t. Success is 0. Per-component "not supported here" is
/// <see cref="ErrorUnsupportedFeature"/>; treat every channel as optional.
/// </summary>
public enum ZeResult : uint
{
    Success = 0,
    NotReady = 1,
    ErrorDeviceLost = 0x70000001,
    ErrorOutOfHostMemory = 0x70000002,
    ErrorOutOfDeviceMemory = 0x70000003,
    ErrorInsufficientPermissions = 0x70010000,
    ErrorNotAvailable = 0x70010001,
    ErrorDependencyUnavailable = 0x70020000,
    ErrorUninitialized = 0x78000001,
    ErrorUnsupportedVersion = 0x78000002,
    ErrorUnsupportedFeature = 0x78000003,
    ErrorInvalidArgument = 0x78000004,
    ErrorInvalidNullHandle = 0x78000005,
    ErrorInvalidNullPointer = 0x78000007,
    ErrorInvalidEnumeration = 0x7800000c,
    ErrorUnknown = 0x7ffffffe,

    // Afterglow-synthesized codes for load failures (outside the ZE ranges).
    LibraryNotFound = 0xffff0001,
    FunctionNotFound = 0xffff0002,
}

/// <summary>zes_structure_type_t values for the structs Afterglow passes.</summary>
internal enum ZesStructureType
{
    PciProperties = 0x2,
    EngineProperties = 0x5,
    FreqProperties = 0x9,
    MemProperties = 0xb,
    PowerProperties = 0xd,
    TempProperties = 0x14,
    FreqState = 0x1b,
    MemState = 0x1e,
}

/// <summary>zes_freq_domain_t.</summary>
public enum ZesFreqDomain
{
    Gpu = 0,
    Memory = 1,
    Media = 2,
}

/// <summary>zes_temp_sensors_t.</summary>
public enum ZesTempSensor
{
    Global = 0,
    Gpu = 1,
    Memory = 2,
    GlobalMin = 3,
    GpuMin = 4,
    MemoryMin = 5,
}

/// <summary>zes_engine_group_t (the *_ALL groups Afterglow reads).</summary>
public enum ZesEngineGroup
{
    All = 0,
    ComputeAll = 1,
    MediaAll = 2,
    CopyAll = 3,
    RenderAll = 12,
}

/// <summary>zes_mem_loc_t.</summary>
public enum ZesMemLocation
{
    System = 0,
    Device = 1,
}

/// <summary>
/// zes_freq_throttle_reason_flags_t: why the hardware is limiting frequency.
/// </summary>
[Flags]
public enum ZesFreqThrottleReasons : uint
{
    None = 0,
    AveragePowerCap = 1 << 0, // PL1
    BurstPowerCap = 1 << 1,   // PL2
    CurrentLimit = 1 << 2,    // PL4
    ThermalLimit = 1 << 3,
    PsuAlert = 1 << 4,
    SoftwareRange = 1 << 5,
    HardwareRange = 1 << 6,
    Voltage = 1 << 7,
    Thermal = 1 << 8,
    Power = 1 << 9,
}

/// <summary>zes_pci_address_t (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesPciAddress
{
    public uint Domain;
    public uint Bus;
    public uint Device;
    public uint Function;
}

/// <summary>zes_pci_speed_t (16 bytes). -1 = unknown.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesPciSpeed
{
    public int Gen;
    public int Width;
    public long MaxBandwidth; // bytes/sec
}

/// <summary>zes_pci_properties_t (56 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesPciProperties
{
    public int SType;
    public nint PNext;
    public ZesPciAddress Address;
    public ZesPciSpeed MaxSpeed;
    public byte HaveBandwidthCounters; // ze_bool_t: 1 byte
    public byte HavePacketCounters;
    public byte HaveReplayCounters;
}

/// <summary>zes_power_properties_t (40 bytes). Limit fields are deprecated but still filled by drivers.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesPowerProperties
{
    public int SType;
    public nint PNext;
    public byte OnSubdevice;
    public uint SubdeviceId;
    public byte CanControl;
    public byte IsEnergyThresholdSupported;
    public int DefaultLimitMw; // -1 = unknown
    public int MinLimitMw;
    public int MaxLimitMw;
}

/// <summary>zes_power_energy_counter_t (16 bytes): µJ + µs (structure-local base).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesPowerEnergyCounter
{
    public ulong EnergyUj;
    public ulong TimestampUs;
}

/// <summary>zes_freq_properties_t (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesFreqProperties
{
    public int SType;
    public nint PNext;
    public ZesFreqDomain Type;
    public byte OnSubdevice;
    public uint SubdeviceId;
    public byte CanControl;
    public byte IsThrottleEventSupported;
    public double Min; // MHz
    public double Max; // MHz
}

/// <summary>zes_freq_state_t (64 bytes). Negative values mean "not known".</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesFreqState
{
    public int SType;
    public nint PNext;
    public double CurrentVoltage; // Volts
    public double Request;        // MHz
    public double Tdp;            // MHz
    public double Efficient;      // MHz
    public double Actual;         // MHz
    public ZesFreqThrottleReasons ThrottleReasons;
}

/// <summary>zes_temp_properties_t (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesTempProperties
{
    public int SType;
    public nint PNext;
    public ZesTempSensor Type;
    public byte OnSubdevice;
    public uint SubdeviceId;
    public double MaxTemperature;
    public byte IsCriticalTempSupported;
    public byte IsThreshold1Supported;
    public byte IsThreshold2Supported;
}

/// <summary>zes_mem_properties_t (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesMemProperties
{
    public int SType;
    public nint PNext;
    public int Type;                // zes_mem_type_t
    public byte OnSubdevice;
    public uint SubdeviceId;
    public ZesMemLocation Location; // System = shared, Device = dedicated
    public ulong PhysicalSize;      // bytes; 0 = unknown
    public int BusWidth;            // -1 = unknown
    public int NumChannels;         // -1 = unknown
}

/// <summary>zes_mem_state_t (40 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesMemState
{
    public int SType;
    public nint PNext;
    public int Health; // zes_mem_health_t
    public ulong Free; // bytes
    public ulong Total; // bytes ("size" in the header; deprecated as unreliable)
}

/// <summary>zes_mem_bandwidth_t (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesMemBandwidth
{
    public ulong ReadCounter;  // total bytes
    public ulong WriteCounter; // total bytes
    public ulong MaxBandwidth; // bytes/sec
    public ulong TimestampUs;  // structure-local base
}

/// <summary>zes_engine_properties_t (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesEngineProperties
{
    public int SType;
    public nint PNext;
    public ZesEngineGroup Type;
    public byte OnSubdevice;
    public uint SubdeviceId;
}

/// <summary>zes_engine_stats_t (16 bytes): %util = deltaActive / deltaTimestamp.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZesEngineStats
{
    public ulong ActiveTime;  // µs
    public ulong TimestampUs; // µs
}

/// <summary>
/// Raw P/Invoke surface of ze_loader.dll (the Level Zero loader installed with
/// the Intel graphics driver). Signatures follow the official zes_api.h
/// (github.com/oneapi-src/level-zero); struct layouts are pinned by
/// ZesStructLayoutTests against sizes compiled from that header. Sysman here is
/// telemetry-only by Afterglow policy (IGCL is the write path); the header
/// deprecates the Sysman OC block outright and the legacy zesPowerGet/SetLimits
/// in favor of the *Ext variants.
/// </summary>
internal static class ZesNative
{
    private const string Lib = "ze_loader.dll";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesInit(uint flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesDriverGet(ref uint count, [In, Out] nint[]? drivers);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesDeviceGet(nint driver, ref uint count, [In, Out] nint[]? devices);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesDevicePciGetProperties(nint device, ref ZesPciProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesDeviceEnumPowerDomains(nint device, ref uint count, [In, Out] nint[]? domains);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesPowerGetProperties(nint power, ref ZesPowerProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesPowerGetEnergyCounter(nint power, ref ZesPowerEnergyCounter energy);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesDeviceEnumFrequencyDomains(nint device, ref uint count, [In, Out] nint[]? domains);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesFrequencyGetProperties(nint frequency, ref ZesFreqProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesFrequencyGetState(nint frequency, ref ZesFreqState state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesDeviceEnumTemperatureSensors(nint device, ref uint count, [In, Out] nint[]? sensors);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesTemperatureGetProperties(nint sensor, ref ZesTempProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesTemperatureGetState(nint sensor, out double temperatureC);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesDeviceEnumMemoryModules(nint device, ref uint count, [In, Out] nint[]? modules);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesMemoryGetProperties(nint memory, ref ZesMemProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesMemoryGetState(nint memory, ref ZesMemState state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesMemoryGetBandwidth(nint memory, ref ZesMemBandwidth bandwidth);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesDeviceEnumEngineGroups(nint device, ref uint count, [In, Out] nint[]? engines);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesEngineGetProperties(nint engine, ref ZesEngineProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesEngineGetActivity(nint engine, ref ZesEngineStats stats);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ZeResult zesDeviceEnumFans(nint device, ref uint count, [In, Out] nint[]? fans);
}
