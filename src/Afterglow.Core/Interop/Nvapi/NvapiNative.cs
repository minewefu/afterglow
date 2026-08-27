using System.Runtime.InteropServices;

namespace Afterglow.Core.Interop.Nvapi;

/// <summary>NvAPI_Status (subset; full negative-value space is driver-defined).</summary>
public enum NvapiStatus
{
    Ok = 0,
    Error = -1,
    LibraryNotFound = -2,
    NoImplementation = -3,
    ApiNotInitialized = -4,
    InvalidArgument = -5,
    NvidiaDeviceNotFound = -6,
    EndEnumeration = -7,
    InvalidHandle = -8,
    IncompatibleStructVersion = -9,
    HandleInvalidated = -10,
    NotSupported = -104,
    DataNotFound = -121,
    FunctionNotFound = -136,
    SettingNotFound = -160,
    ProfileNotFound = -163,
    ProfileNameInUse = -164,
    ExecutableNotFound = -166,
}

/// <summary>
/// Interface IDs used by Afterglow. Every value is verified against published
/// open-source implementations (LibreHardwareMonitor, NvAPIWrapper) — see
/// docs/research/driver-apis.md for the provenance table.
/// </summary>
internal static class NvapiIds
{
    public const uint Initialize = 0x0150E828;
    public const uint EnumPhysicalGpus = 0xE5AC921F;
    public const uint GpuGetFullName = 0xCEEE8E9F;
    public const uint GpuGetBusId = 0x1BE0B8E5;
    public const uint GpuGetTachReading = 0x5F608315;
    public const uint GpuGetThermalSensors = 0x65FE3AAD;
    public const uint GpuGetDynamicPstatesInfoEx = 0x60DED2ED;
    public const uint GpuClientFanCoolersGetStatus = 0x35AED5E8;
    public const uint GpuClientFanCoolersGetControl = 0x814B209F;
    public const uint GpuClientFanCoolersSetControl = 0xA58971A5;
    public const uint GpuRestoreCoolerSettings = 0x8F6ED0FB;
    public const uint GpuClientVoltRailsGetStatus = 0x465F9BCF;
    public const uint GpuClientThermalPoliciesGetInfo = 0x0D258BB5;
    public const uint GpuClientThermalPoliciesGetStatus = 0xE9C425A1;
    public const uint GpuClientThermalPoliciesSetStatus = 0x34C0B13D;
    public const uint GpuGetCoreVoltageBoostPercent = 0x9DF23CA1;
    public const uint GpuSetCoreVoltageBoostPercent = 0xB9306D9B;
    public const uint GpuGetVfpCurve = 0x21537AD4;
}

#pragma warning disable CS0649 // fields assigned by native code

/// <summary>Private thermal sensor block: temperatures are fixed-point °C × 256.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvThermalSensors
{
    public uint Version;
    public uint Mask;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public int[] Reserved;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public int[] Temperatures;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvFanCoolersStatusItem
{
    public uint CoolerId;
    public uint CurrentRpm;
    public uint CurrentMinLevel;
    public uint CurrentMaxLevel;
    public uint CurrentLevel;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public uint[] Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvFanCoolersStatus
{
    public uint Version;
    public uint Count;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
    public ulong Reserved4;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public NvFanCoolersStatusItem[] Items;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvFanCoolerControlItem
{
    public uint CoolerId;
    public uint Level;

    /// <summary>0 = auto (firmware), 1 = manual.</summary>
    public uint ControlMode;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public uint[] Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvFanCoolerControl
{
    public uint Version;
    public uint Reserved;
    public uint Count;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public uint[] Reserved2;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public NvFanCoolerControlItem[] Items;
}

/// <summary>Core voltage rail status; explicit layout, size 0x4C, core µV at 0x28.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x4C)]
internal struct NvVoltRailsStatus
{
    [FieldOffset(0x00)]
    public uint Version;

    [FieldOffset(0x28)]
    public uint CoreMicrovolts;

    /// <summary>
    /// Reserved in published layouts; kept only to document the byte at 0x2C.
    /// Nothing reads it — do not use without independent verification.
    /// </summary>
    [FieldOffset(0x2C)]
    public uint ReservedAfterCoreMicrovolts;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvDynamicPstate
{
    public uint IsPresent;
    public int Percentage;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvDynamicPstatesInfo
{
    public uint Version;
    public uint Flags;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public NvDynamicPstate[] Utilizations;
}

/// <summary>Thermal policy limits; temperatures fixed-point °C × 256.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvThermalPoliciesInfoEntry
{
    public int Controller;
    public uint Unknown1;
    public int MinimumTemp;
    public int DefaultTemp;
    public int MaximumTemp;
    public uint Unknown2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvThermalPoliciesInfo
{
    public uint Version;
    public byte Count;
    public byte Unknown;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public NvThermalPoliciesInfoEntry[] Entries;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvThermalPoliciesStatusEntry
{
    public int Controller;

    /// <summary>Target temperature, °C × 256.</summary>
    public int TargetTemp;

    public uint PstateId;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvThermalPoliciesStatus
{
    public uint Version;
    public uint Count;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public NvThermalPoliciesStatusEntry[] Entries;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvVoltageBoostPercent
{
    public uint Version;
    public uint Percent;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public uint[] Reserved;
}

/// <summary>One point of the driver's voltage/frequency curve.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvVfpCurveEntry
{
    public uint Unknown1;
    public uint FrequencyKHz;
    public uint VoltageMicroV;
    public uint Unknown2;
    public uint Unknown3;
    public uint Unknown4;
    public uint Unknown5;
}

/// <summary>
/// GPU boost (V/F) curve: 80 core points and 23 memory points.
/// Read-only in Afterglow — the driver rejects curve writes on Blackwell.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvVfpCurve
{
    public uint Version;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public uint[] Masks;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
    public uint[] Unknown1;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)]
    public NvVfpCurveEntry[] GpuCurveEntries;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 23)]
    public NvVfpCurveEntry[] MemoryCurveEntries;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1064)]
    public uint[] Unknown2;
}

#pragma warning restore CS0649

/// <summary>
/// nvapi64.dll plumbing: resolves interfaces through the exported
/// nvapi_QueryInterface and exposes them as delegates. A null delegate means the
/// driver does not provide that interface.
/// </summary>
internal static class NvapiNative
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint QueryInterface(uint interfaceId);

    internal static T? GetDelegate<T>(uint id) where T : Delegate
    {
        nint ptr;
        try
        {
            ptr = QueryInterface(id);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        return ptr == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    internal static uint MakeVersion<T>(int version) where T : struct =>
        (uint)(Marshal.SizeOf<T>() | (version << 16));

    // Delegate shapes -----------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus EnumPhysicalGpusDelegate([Out] nint[] handles, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate NvapiStatus GetFullNameDelegate(nint gpu, byte* name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus GetBusIdDelegate(nint gpu, out uint busId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus GetTachReadingDelegate(nint gpu, out int rpm);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus GetThermalSensorsDelegate(nint gpu, ref NvThermalSensors sensors);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus GetDynamicPstatesDelegate(nint gpu, ref NvDynamicPstatesInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus FanCoolersGetStatusDelegate(nint gpu, ref NvFanCoolersStatus status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus FanCoolersControlDelegate(nint gpu, ref NvFanCoolerControl control);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus RestoreCoolerSettingsDelegate(nint gpu, nint coolerIndexes, uint indexCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus VoltRailsGetStatusDelegate(nint gpu, ref NvVoltRailsStatus status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus ThermalPoliciesGetInfoDelegate(nint gpu, ref NvThermalPoliciesInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus ThermalPoliciesStatusDelegate(nint gpu, ref NvThermalPoliciesStatus status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus VoltageBoostDelegate(nint gpu, ref NvVoltageBoostPercent boost);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate NvapiStatus GetVfpCurveDelegate(nint gpu, ref NvVfpCurve curve);
}
