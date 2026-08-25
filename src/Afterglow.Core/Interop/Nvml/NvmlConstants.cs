namespace Afterglow.Core.Interop.Nvml;

/// <summary>
/// NVML return codes. Values are part of NVIDIA's stable public ABI
/// (see nvml.h, nvmlReturn_t).
/// </summary>
public enum NvmlReturn
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    NoPermission = 4,
    AlreadyInitialized = 5,
    NotFound = 6,
    InsufficientSize = 7,
    InsufficientPower = 8,
    DriverNotLoaded = 9,
    Timeout = 10,
    IrqIssue = 11,
    LibraryNotFound = 12,
    FunctionNotFound = 13,
    CorruptedInforom = 14,
    GpuIsLost = 15,
    ResetRequired = 16,
    OperatingSystem = 17,
    LibRmVersionMismatch = 18,
    InUse = 19,
    Memory = 20,
    NoData = 21,
    VgpuEccNotSupported = 22,
    InsufficientResources = 23,
    FreqNotSupported = 24,
    ArgumentVersionMismatch = 25,
    Deprecated = 26,
    NotReady = 27,
    GpuNotFound = 28,
    InvalidState = 29,
    Unknown = 999,
}

/// <summary>nvmlClockType_t</summary>
public enum NvmlClockType : uint
{
    Graphics = 0,
    Sm = 1,
    Mem = 2,
    Video = 3,
}

/// <summary>nvmlClockId_t</summary>
public enum NvmlClockId : uint
{
    Current = 0,
    AppClockTarget = 1,
    AppClockDefault = 2,
    CustomerBoostMax = 3,
}

/// <summary>nvmlTemperatureSensors_t</summary>
public enum NvmlTemperatureSensor : uint
{
    Gpu = 0,
}

/// <summary>nvmlTemperatureThresholds_t</summary>
public enum NvmlTemperatureThreshold : uint
{
    Shutdown = 0,
    Slowdown = 1,
    MemMax = 2,
    GpuMax = 3,
    AcousticMin = 4,
    AcousticCurrent = 5,
    AcousticMax = 6,
}

/// <summary>nvmlPcieUtilCounter_t</summary>
public enum NvmlPcieUtilCounter : uint
{
    TxBytes = 0,
    RxBytes = 1,
}

/// <summary>nvmlFanControlPolicy_t</summary>
public enum NvmlFanControlPolicy : uint
{
    /// <summary>Firmware/temperature controlled (auto).</summary>
    TemperatureContinuous = 0,

    /// <summary>Manual fan speed set by software.</summary>
    Manual = 1,
}

/// <summary>
/// Bits of the clocks-event (formerly "clocks throttle") reasons bitmask.
/// Documented, stable values from nvml.h.
/// </summary>
[Flags]
public enum NvmlClocksEventReasons : ulong
{
    None = 0,
    GpuIdle = 0x0000000000000001,
    ApplicationsClocksSetting = 0x0000000000000002,
    SwPowerCap = 0x0000000000000004,
    HwSlowdown = 0x0000000000000008,
    SyncBoost = 0x0000000000000010,
    SwThermalSlowdown = 0x0000000000000020,
    HwThermalSlowdown = 0x0000000000000040,
    HwPowerBrakeSlowdown = 0x0000000000000080,
    DisplayClockSetting = 0x0000000000000100,
}
