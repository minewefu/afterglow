using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Afterglow.Core.Interop.Igcl;

/// <summary>
/// ctl_result_t. Success is 0; 0x00000001 (StillOpenByAnotherCaller) is also a
/// success code returned by ctlClose. Values must be matched exactly — the
/// header's own range scheme has documented outliers, so never range-classify.
/// </summary>
public enum CtlResult : uint
{
    Success = 0x00000000,
    SuccessStillOpenByAnotherCaller = 0x00000001,
    ErrorNotInitialized = 0x40000001,
    ErrorAlreadyInitialized = 0x40000002,
    ErrorDeviceLost = 0x40000003,
    ErrorOutOfHostMemory = 0x40000004,
    ErrorOutOfDeviceMemory = 0x40000005,
    ErrorInsufficientPermissions = 0x40000006,
    ErrorNotAvailable = 0x40000007,
    ErrorUninitialized = 0x40000008,
    ErrorUnsupportedVersion = 0x40000009,
    ErrorUnsupportedFeature = 0x4000000a,
    ErrorInvalidArgument = 0x4000000b,
    ErrorInvalidApiHandle = 0x4000000c,
    ErrorInvalidNullHandle = 0x4000000d,
    ErrorInvalidNullPointer = 0x4000000e,
    ErrorInvalidSize = 0x4000000f,
    ErrorUnsupportedSize = 0x40000010,
    ErrorUnsupportedImageFormat = 0x40000011,
    ErrorDataRead = 0x40000012,
    ErrorDataWrite = 0x40000013,
    ErrorDataNotFound = 0x40000014,
    ErrorNotImplemented = 0x40000015,
    ErrorOsCall = 0x40000016,
    ErrorKmdCall = 0x40000017,
    ErrorUnload = 0x40000018,
    ErrorZeLoader = 0x40000019,
    ErrorInvalidOperationType = 0x4000001a,
    ErrorNullOsInterface = 0x4000001b,
    ErrorNullOsAdapterHandle = 0x4000001c,
    ErrorNullOsDisplayOutputHandle = 0x4000001d,
    ErrorWaitTimeout = 0x4000001e,
    ErrorPersistenceNotSupported = 0x4000001f,
    ErrorPlatformNotSupported = 0x40000020,
    ErrorUnknownApplicationUid = 0x40000021,
    ErrorInvalidEnumeration = 0x40000022,
    ErrorFileDelete = 0x40000023,
    ErrorResetDeviceRequired = 0x40000024,
    ErrorFullRebootRequired = 0x40000025,
    ErrorLoad = 0x40000026,
    ErrorDeviceUnavailable = 0x40000027,
    ErrorUnknown = 0x4000ffff,
    ErrorRetryOperation = 0x40010000,
    ErrorIgscLoader = 0x40010001,
    ErrorCoreOverclockNotSupported = 0x44000001,
    ErrorCoreOverclockWaiverNotSet = 0x44000008,
    ErrorCoreOverclockDeprecatedApi = 0x44000009,

    // Afterglow-synthesized codes for load failures (outside every documented range).
    LibraryNotFound = 0xffff0001,
    FunctionNotFound = 0xffff0002,
}

/// <summary>ctl_units_t.</summary>
public enum CtlUnits
{
    FrequencyMhz = 0,
    OperationsGts = 1,
    OperationsMts = 2,
    VoltageVolts = 3,
    PowerWatts = 4,
    TemperatureCelsius = 5,
    EnergyJoules = 6,
    TimeSeconds = 7,
    MemoryBytes = 8,
    AngularSpeedRpm = 9,
    PowerMilliwatts = 10,
    Percent = 11,
    MemSpeedGbps = 12,
    VoltageMillivolts = 13,
    BandwidthMbps = 14,
    Unknown = 0x4800FFFF,
}

/// <summary>ctl_data_type_t.</summary>
#pragma warning disable CA1720 // member names mirror the header's enumerators verbatim
public enum CtlDataType
{
    Int8 = 0,
    Uint8 = 1,
    Int16 = 2,
    Uint16 = 3,
    Int32 = 4,
    Uint32 = 5,
    Int64 = 6,
    Uint64 = 7,
    Float = 8,
    Double = 9,
    StringAscii = 10,
    StringUtf16 = 11,
    StringUtf132 = 12,
    Unknown = 0x4800FFFF,
}
#pragma warning restore CA1720

/// <summary>ctl_device_type_t.</summary>
public enum CtlDeviceType
{
    Graphics = 1,
    System = 2,
}

/// <summary>ctl_freq_domain_t.</summary>
public enum CtlFreqDomain
{
    Gpu = 0,
    Memory = 1,
    Media = 2,
}

/// <summary>ctl_temp_sensors_t.</summary>
public enum CtlTempSensor
{
    Global = 0,
    Gpu = 1,
    Memory = 2,
    GlobalMin = 3,
    GpuMin = 4,
    MemoryMin = 5,
}

/// <summary>ctl_mem_loc_t.</summary>
public enum CtlMemLocation
{
    System = 0,
    Device = 1,
}

/// <summary>ctl_engine_group_t.</summary>
public enum CtlEngineGroup
{
    Gt = 0,
    Render = 1,
    Media = 2,
}

/// <summary>ctl_fan_speed_mode_t.</summary>
public enum CtlFanSpeedMode
{
    Default = 0,
    Fixed = 1,
    Table = 2,
}

/// <summary>ctl_fan_speed_units_t.</summary>
public enum CtlFanSpeedUnits
{
    Rpm = 0,
    Percent = 1,
}

/// <summary>
/// ctl_freq_throttle_reason_flags_t: why the hardware is limiting frequency.
/// </summary>
[Flags]
public enum CtlFreqThrottleReasons : uint
{
    None = 0,
    AveragePowerCap = 1 << 0, // PL1
    BurstPowerCap = 1 << 1,   // PL2
    CurrentLimit = 1 << 2,    // PL4
    ThermalLimit = 1 << 3,    // T > TjMax
    PsuAlert = 1 << 4,
    SoftwareRange = 1 << 5,
    HardwareRange = 1 << 6,
}

/// <summary>
/// ctl_application_id_t (16 bytes) — GUID-shaped app id inside init args.
/// Data4 is uint8_t[8] in the header, split here into two uints so the struct
/// keeps the header's 4-byte alignment (a ulong would shift it to offset 24
/// inside <see cref="CtlInitArgs"/> and break the 36-byte layout).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlApplicationId
{
    public uint Data1;
    public ushort Data2;
    public ushort Data3;
    public uint Data4Low;
    public uint Data4High;
}

/// <summary>ctl_init_args_t (36 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlInitArgs
{
    public uint Size;
    public byte Version;
    public uint AppVersion;       // CTL_MAKE_VERSION(1, 1) = 0x00010001
    public uint Flags;            // 1 = CTL_INIT_FLAG_USE_LEVEL_ZERO (telemetry/frequency APIs)
    public uint SupportedVersion; // out: runtime's implementation version
    public CtlApplicationId ApplicationUid;

    public const uint ImplVersion = 0x00010001;
    public const uint FlagUseLevelZero = 1;
}

/// <summary>ctl_adapter_bdf_t (3 bytes, alignment 1).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlAdapterBdf
{
    public byte Bus;
    public byte Device;
    public byte Function;
}

/// <summary>ctl_firmware_version_t (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlFirmwareVersion
{
    public ulong MajorVersion;
    public ulong MinorVersion;
    public ulong BuildNumber;
}

/// <summary>
/// ctl_device_adapter_properties_t (320 bytes on x64). Caller must pre-set Size,
/// Version (2 for BDF/subsys ids, per Intel's own samples), and point PDeviceId
/// at a caller-allocated 8-byte buffer (the driver writes the Windows adapter
/// LUID there) with DeviceIdSize = 8.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct CtlDeviceAdapterProperties
{
    public uint Size;
    public byte Version;
    public IntPtr PDeviceId;
    public uint DeviceIdSize;
    public CtlDeviceType DeviceType;
    public uint SupportedSubfunctionFlags;
    public ulong DriverVersion;
    public CtlFirmwareVersion FirmwareVersion;
    public uint PciVendorId;
    public uint PciDeviceId;
    public uint RevId;
    public uint NumEusPerSubSlice;
    public uint NumSubSlicesPerSlice;
    public uint NumSlices;
    public fixed byte Name[100];              // CTL_MAX_DEVICE_NAME_LEN, ANSI
    public uint GraphicsAdapterProperties;    // bit0 = integrated adapter
    public uint Frequency;                    // Version > 0
    public ushort PciSubsysId;                // Version > 1
    public ushort PciSubsysVendorId;          // Version > 1
    public CtlAdapterBdf AdapterBdf;          // Version > 1
    public uint NumXeCores;                   // Version > 2
    public fixed byte Reserved[108];          // CTL_MAX_RESERVED_SIZE

    public const uint FlagIntegrated = 1;
}

/// <summary>ctl_data_value_t — 8-byte union; read the member matching CtlDataType.</summary>
[StructLayout(LayoutKind.Explicit)]
public struct CtlDataValue
{
    [FieldOffset(0)] public sbyte Data8;
    [FieldOffset(0)] public byte DataU8;
    [FieldOffset(0)] public short Data16;
    [FieldOffset(0)] public ushort DataU16;
    [FieldOffset(0)] public int Data32;
    [FieldOffset(0)] public uint DataU32;
    [FieldOffset(0)] public long Data64;
    [FieldOffset(0)] public ulong DataU64;
    [FieldOffset(0)] public float DataFloat;
    [FieldOffset(0)] public double DataDouble;
}

/// <summary>
/// ctl_oc_telemetry_item_t (24 bytes). Valid only when Supported is nonzero;
/// Units/Type describe the union member to read.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlTelemetryItem
{
    public byte Supported; // C++ bool: 1 byte
    public CtlUnits Units;
    public CtlDataType Type;
    public CtlDataValue Value;

    /// <summary>The value as a double regardless of the encoded integer width.</summary>
    public readonly double AsDouble() => Type switch
    {
        CtlDataType.Int8 => Value.Data8,
        CtlDataType.Uint8 => Value.DataU8,
        CtlDataType.Int16 => Value.Data16,
        CtlDataType.Uint16 => Value.DataU16,
        CtlDataType.Int32 => Value.Data32,
        CtlDataType.Uint32 => Value.DataU32,
        CtlDataType.Int64 => Value.Data64,
        CtlDataType.Uint64 => Value.DataU64,
        CtlDataType.Float => Value.DataFloat,
        _ => Value.DataDouble,
    };
}

/// <summary>ctl_psu_info_t (56 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPsuInfo
{
    public byte Supported;
    public int PsuType;
    public CtlTelemetryItem EnergyCounter;
    public CtlTelemetryItem Voltage;
}

/// <summary>Inline ctl_psu_info_t[CTL_PSU_COUNT = 5].</summary>
[InlineArray(5)]
public struct CtlPsuInfoArray5
{
    private CtlPsuInfo _element0;
}

/// <summary>Inline ctl_oc_telemetry_item_t[CTL_FAN_COUNT = 5].</summary>
[InlineArray(5)]
public struct CtlTelemetryItemArray5
{
    private CtlTelemetryItem _element0;
}

/// <summary>
/// ctl_power_telemetry_t (1024 bytes on x64). Version 1 unlocks the trailing
/// items from GpuVrTemp onward; drivers refresh at most every 50 ms.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPowerTelemetry
{
    public uint Size;
    public byte Version;
    public CtlTelemetryItem TimeStamp;             // seconds since Unix epoch (double)
    public CtlTelemetryItem GpuEnergyCounter;      // monotonic; delta-J / delta-s = W
    public CtlTelemetryItem GpuVoltage;
    public CtlTelemetryItem GpuCurrentClockFrequency;
    public CtlTelemetryItem GpuCurrentTemperature;
    public CtlTelemetryItem GlobalActivityCounter; // monotonic busy-seconds
    public CtlTelemetryItem RenderComputeActivityCounter;
    public CtlTelemetryItem MediaActivityCounter;
    public byte GpuPowerLimited;
    public byte GpuTemperatureLimited;
    public byte GpuCurrentLimited;
    public byte GpuVoltageLimited;
    public byte GpuUtilizationLimited;
    public CtlTelemetryItem VramEnergyCounter;
    public CtlTelemetryItem VramVoltage;
    public CtlTelemetryItem VramCurrentClockFrequency;
    public CtlTelemetryItem VramCurrentEffectiveFrequency;
    public CtlTelemetryItem VramReadBandwidthCounter;
    public CtlTelemetryItem VramWriteBandwidthCounter;
    public CtlTelemetryItem VramCurrentTemperature;
    public byte VramPowerLimited;        // deprecated, always false
    public byte VramTemperatureLimited;  // deprecated
    public byte VramCurrentLimited;      // deprecated
    public byte VramVoltageLimited;      // deprecated
    public byte VramUtilizationLimited;  // deprecated
    public CtlTelemetryItem TotalCardEnergyCounter;
    public CtlPsuInfoArray5 Psu;
    public CtlTelemetryItemArray5 FanSpeed;
    public CtlTelemetryItem GpuVrTemp;             // Version > 0
    public CtlTelemetryItem VramVrTemp;            // Version > 0
    public CtlTelemetryItem SaVrTemp;              // Version > 0
    public CtlTelemetryItem GpuEffectiveClock;     // Version > 0
    public CtlTelemetryItem GpuOverVoltagePercent; // Version > 0
    public CtlTelemetryItem GpuPowerPercent;       // Version > 0
    public CtlTelemetryItem GpuTemperaturePercent; // Version > 0
    public CtlTelemetryItem VramReadBandwidth;     // Version > 0
    public CtlTelemetryItem VramWriteBandwidth;    // Version > 0
}

/// <summary>ctl_temp_properties_t (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlTempProperties
{
    public uint Size;
    public byte Version;
    public CtlTempSensor Type;
    public double MaxTemperature;
}

/// <summary>ctl_freq_properties_t (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlFreqProperties
{
    public uint Size;
    public byte Version;
    public CtlFreqDomain Type;
    public byte CanControl; // C++ bool
    public double Min;      // MHz
    public double Max;      // MHz (non-overclock max)
}

/// <summary>ctl_freq_state_t (56 bytes). Negative values mean "not known".</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlFreqState
{
    public uint Size;
    public byte Version;
    public double CurrentVoltage; // Volts
    public double Request;        // MHz
    public double Tdp;            // MHz sustainable under current power/thermal limits
    public double Efficient;      // MHz
    public double Actual;         // MHz
    public CtlFreqThrottleReasons ThrottleReasons;
}

/// <summary>ctl_freq_range_t (24 bytes). 0 = hardware limit, -1 = factory value.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlFreqRange
{
    public uint Size;
    public byte Version;
    public double Min; // MHz
    public double Max; // MHz
}

/// <summary>ctl_mem_properties_t (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlMemProperties
{
    public uint Size;
    public byte Version;
    public int Type;                 // ctl_mem_type_t (LPDDR5 = 8, GDDR6 = 12, ...)
    public CtlMemLocation Location;  // System = shared, Device = dedicated
    public ulong PhysicalSize;       // bytes; 0 = unknown
    public int BusWidth;             // -1 = unknown
    public int NumChannels;          // -1 = unknown
}

/// <summary>ctl_mem_state_t (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlMemState
{
    public uint Size;
    public byte Version;
    public ulong Free; // bytes
    public ulong Total; // bytes ("size" in the header: total allocatable)
}

/// <summary>ctl_mem_bandwidth_t (40 bytes). Counters need Version > 0.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlMemBandwidth
{
    public uint Size;
    public byte Version;
    public ulong MaxBandwidth;  // bytes/sec
    public ulong Timestamp;     // µs, monotonic, structure-local base
    public ulong ReadCounter;   // total bytes, Version > 0
    public ulong WriteCounter;  // total bytes, Version > 0
}

/// <summary>ctl_engine_properties_t (12 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlEngineProperties
{
    public uint Size;
    public byte Version;
    public CtlEngineGroup Type;
}

/// <summary>ctl_engine_stats_t (24 bytes). %util = deltaActive / deltaTimestamp.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlEngineStats
{
    public uint Size;
    public byte Version;
    public ulong ActiveTime; // µs busy
    public ulong Timestamp;  // µs, monotonic, structure-local base
}

/// <summary>ctl_pci_address_t (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPciAddress
{
    public uint Size;
    public byte Version;
    public uint Domain;
    public uint Bus;
    public uint Device;
    public uint Function;
}

/// <summary>ctl_pci_speed_t (24 bytes). -1 = unknown.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPciSpeed
{
    public uint Size;
    public byte Version;
    public int Gen;
    public int Width;
    public long MaxBandwidth; // bytes/sec
}

/// <summary>ctl_pci_properties_t (64 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPciProperties
{
    public uint Size;
    public byte Version;
    public CtlPciAddress Address;
    public CtlPciSpeed MaxSpeed;
    public byte ResizableBarSupported; // C++ bool
    public byte ResizableBarEnabled;   // C++ bool
}

/// <summary>ctl_pci_state_t (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPciState
{
    public uint Size;
    public byte Version;
    public CtlPciSpeed Speed;
}

/// <summary>ctl_fan_properties_t (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlFanProperties
{
    public uint Size;
    public byte Version;
    public byte CanControl;      // C++ bool
    public uint SupportedModes;  // bitfield of 1 << CtlFanSpeedMode
    public uint SupportedUnits;  // bitfield of 1 << CtlFanSpeedUnits
    public int MaxRpm;           // -1 = unknown
    public int MaxPoints;        // -1 = no temp/speed table support
}

/// <summary>ctl_fan_speed_t (16 bytes). Speed -1 = no fixed setting.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlFanSpeed
{
    public uint Size;
    public byte Version;
    public int Speed;
    public CtlFanSpeedUnits Units;
}

/// <summary>ctl_fan_temp_speed_t (28 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlFanTempSpeed
{
    public uint Size;
    public byte Version;
    public uint TemperatureC;
    public CtlFanSpeed Speed;
}

/// <summary>Inline ctl_fan_temp_speed_t[CTL_FAN_TEMP_SPEED_PAIR_COUNT = 32].</summary>
[InlineArray(32)]
public struct CtlFanTempSpeedArray32
{
    private CtlFanTempSpeed _element0;
}

/// <summary>ctl_fan_speed_table_t (908 bytes). Points ordered by ascending temperature.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlFanSpeedTable
{
    public uint Size;
    public byte Version;
    public int NumPoints; // 0 = none configured, -1 = unsupported
    public CtlFanTempSpeedArray32 Table;
}

/// <summary>ctl_fan_config_t (936 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlFanConfig
{
    public uint Size;
    public byte Version;
    public CtlFanSpeedMode Mode;
    public CtlFanSpeed SpeedFixed;
    public CtlFanSpeedTable SpeedTable;
}

/// <summary>
/// ctl_oc_control_info_t (48 bytes): per-knob capability report. Supported == 0
/// means the control does not exist on this device; Relative controls are
/// offsets, absolute otherwise; Reference is valid only when HasReference.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlOcControlInfo
{
    public byte Supported;    // C++ bool
    public byte Relative;     // C++ bool
    public byte HasReference; // C++ bool ("bReference")
    public CtlUnits Units;
    public double Min;
    public double Max;
    public double Step;
    public double Default;
    public double Reference;
}

/// <summary>
/// ctl_oc_properties_t (440 bytes on x64). Version 1 unlocks VramMemSpeedLimit
/// and the two V/F-curve limit blocks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlOcProperties
{
    public uint Size;
    public byte Version;
    public byte Supported; // C++ bool: adapter supports overclocking at all
    public CtlOcControlInfo GpuFrequencyOffset;
    public CtlOcControlInfo GpuVoltageOffset;
    public CtlOcControlInfo VramFrequencyOffset; // deprecated
    public CtlOcControlInfo VramVoltageOffset;   // deprecated
    public CtlOcControlInfo PowerLimit;
    public CtlOcControlInfo TemperatureLimit;
    public CtlOcControlInfo VramMemSpeedLimit;       // Version > 0
    public CtlOcControlInfo GpuVfCurveVoltageLimit;  // Version > 0
    public CtlOcControlInfo GpuVfCurveFrequencyLimit; // Version > 0
}

/// <summary>ctl_oc_vf_pair_t (24 bytes). Voltage mV / Frequency MHz; 0/0 = unlocked.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlOcVfPair
{
    public uint Size;
    public byte Version;
    public double Voltage;   // mV
    public double Frequency; // MHz
}

/// <summary>ctl_voltage_frequency_point_t (8 bytes, no Size/Version header).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlVoltageFrequencyPoint
{
    public uint VoltageMv;
    public uint FrequencyMhz;
}

/// <summary>ctl_vf_curve_type_t.</summary>
public enum CtlVfCurveType
{
    Stock = 0,
    Live = 1,
}

/// <summary>ctl_vf_curve_details_t.</summary>
public enum CtlVfCurveDetails
{
    Simplified = 0,
    Medium = 1,
    Elaborate = 2,
}

/// <summary>ctl_power_sustained_limit_t (12 bytes): PL1.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPowerSustainedLimit
{
    public byte Enabled; // C++ bool
    public int PowerMw;
    public int IntervalMs; // averaging window (Tau)
}

/// <summary>ctl_power_burst_limit_t (8 bytes): PL2.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPowerBurstLimit
{
    public byte Enabled; // C++ bool
    public int PowerMw;
}

/// <summary>ctl_power_peak_limit_t (8 bytes): PL4. PowerDcMw is -1 with no battery.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPowerPeakLimit
{
    public int PowerAcMw;
    public int PowerDcMw;
}

/// <summary>ctl_power_limits_t (36 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPowerLimits
{
    public uint Size;
    public byte Version;
    public CtlPowerSustainedLimit Sustained;
    public CtlPowerBurstLimit Burst;
    public CtlPowerPeakLimit Peak;
}

/// <summary>ctl_power_properties_t (20 bytes). Limits in mW; -1 = unknown.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPowerDomainProperties
{
    public uint Size;
    public byte Version;
    public byte CanControl; // C++ bool
    public int DefaultLimitMw;
    public int MinLimitMw;
    public int MaxLimitMw;
}

/// <summary>ctl_power_energy_counter_t (24 bytes). Energy in µJ, timestamp in µs.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CtlPowerEnergyCounter
{
    public uint Size;
    public byte Version;
    public ulong EnergyUj;
    public ulong TimestampUs;
}

/// <summary>
/// Raw P/Invoke surface of ControlLib.dll — the IGCL (Intel Graphics Control
/// Library) runtime the Intel graphics driver installs into System32. Signatures
/// follow the official igcl_api.h (github.com/intel/drivers.gpu.control-library,
/// header v1-r1); struct layouts are pinned by IgclStructLayoutTests against
/// sizes compiled from that header. The header never names the DLL; ControlLib.dll
/// is the documented loader name from the IGCL distribution. Newer exports (V2
/// overclock calls, V/F curve read/write) may be missing on older runtimes —
/// callers must treat <see cref="EntryPointNotFoundException"/> as "not supported".
/// </summary>
internal static unsafe class IgclNative
{
    private const string Lib = "ControlLib.dll";

    // --- Lifecycle -----------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlInit(ref CtlInitArgs initDesc, out nint apiHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlClose(nint apiHandle);

    // --- Device enumeration --------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEnumerateDevices(nint apiHandle, ref uint count, [In, Out] nint[]? devices);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlGetDeviceProperties(nint device, ref CtlDeviceAdapterProperties properties);

    // --- Bulk power telemetry ------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlPowerTelemetryGet(nint device, ref CtlPowerTelemetry telemetry);

    // --- Temperature ---------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEnumTemperatureSensors(nint device, ref uint count, [In, Out] nint[]? sensors);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlTemperatureGetProperties(nint sensor, ref CtlTempProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlTemperatureGetState(nint sensor, out double temperatureC);

    // --- Frequency -----------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEnumFrequencyDomains(nint device, ref uint count, [In, Out] nint[]? domains);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFrequencyGetProperties(nint domain, ref CtlFreqProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFrequencyGetState(nint domain, ref CtlFreqState state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFrequencyGetRange(nint domain, ref CtlFreqRange range);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFrequencySetRange(nint domain, ref CtlFreqRange range);

    // --- Memory --------------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEnumMemoryModules(nint device, ref uint count, [In, Out] nint[]? modules);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlMemoryGetProperties(nint module, ref CtlMemProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlMemoryGetState(nint module, ref CtlMemState state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlMemoryGetBandwidth(nint module, ref CtlMemBandwidth bandwidth);

    // --- Engines -------------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEnumEngineGroups(nint device, ref uint count, [In, Out] nint[]? engines);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEngineGetProperties(nint engine, ref CtlEngineProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEngineGetActivity(nint engine, ref CtlEngineStats stats);

    // --- PCI -----------------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlPciGetProperties(nint device, ref CtlPciProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlPciGetState(nint device, ref CtlPciState state);

    // --- Fans ----------------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEnumFans(nint device, ref uint count, [In, Out] nint[]? fans);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFanGetProperties(nint fan, ref CtlFanProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFanGetConfig(nint fan, ref CtlFanConfig config);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFanGetState(nint fan, CtlFanSpeedUnits units, out int speed);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFanSetDefaultMode(nint fan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFanSetFixedSpeedMode(nint fan, ref CtlFanSpeed speed);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlFanSetSpeedTableMode(nint fan, ref CtlFanSpeedTable speedTable);

    // --- Power domains (PL1/PL2/PL4 limits) ----------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEnumPowerDomains(nint device, ref uint count, [In, Out] nint[]? domains);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlPowerGetProperties(nint power, ref CtlPowerDomainProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlPowerGetEnergyCounter(nint power, ref CtlPowerEnergyCounter energy);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlPowerGetLimits(nint power, ref CtlPowerLimits limits);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlPowerSetLimits(nint power, ref CtlPowerLimits limits);

    // --- Overclock -----------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGetProperties(nint device, ref CtlOcProperties properties);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockWaiverSet(nint device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuFrequencyOffsetGet(nint device, out double offsetMhz);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuFrequencyOffsetSet(nint device, double offsetMhz);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuVoltageOffsetGet(nint device, out double offsetMv);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuVoltageOffsetSet(nint device, double offsetMv);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuLockGet(nint device, out CtlOcVfPair vfPair);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuLockSet(nint device, CtlOcVfPair vfPair); // by value

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockPowerLimitGet(nint device, out double sustainedMw);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockPowerLimitSet(nint device, double sustainedMw);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockTemperatureLimitGet(nint device, out double limitC);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockTemperatureLimitSet(nint device, double limitC);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockResetToDefault(nint device);

    // V2 overclock (Arc-era drivers): units come from CtlOcProperties.<knob>.Units.

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuFrequencyOffsetGetV2(nint device, out double offset);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuFrequencyOffsetSetV2(nint device, double offset);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuMaxVoltageOffsetGetV2(nint device, out double offset);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockGpuMaxVoltageOffsetSetV2(nint device, double offset);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockPowerLimitGetV2(nint device, out double limit);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockPowerLimitSetV2(nint device, double limit);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockTemperatureLimitGetV2(nint device, out double limit);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockTemperatureLimitSetV2(nint device, double limit);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockVramMemSpeedLimitGetV2(nint device, out double limit);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockVramMemSpeedLimitSetV2(nint device, double limit);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockReadVFCurve(
        nint device, CtlVfCurveType curveType, CtlVfCurveDetails detail,
        ref uint numPoints, [In, Out] CtlVoltageFrequencyPoint[]? points);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlOverclockWriteCustomVFCurve(
        nint device, uint numPoints, [In] CtlVoltageFrequencyPoint[] points);
}
