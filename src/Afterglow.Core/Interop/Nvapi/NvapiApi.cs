using System.Text;

namespace Afterglow.Core.Interop.Nvapi;

/// <summary>
/// Managed entry point for NVAPI. All interfaces are optional: a null/missing
/// interface degrades the corresponding feature instead of failing the app.
/// </summary>
public sealed class NvapiApi
{
    private readonly NvapiNative.EnumPhysicalGpusDelegate? _enumGpus;

    private NvapiApi()
    {
        _enumGpus = NvapiNative.GetDelegate<NvapiNative.EnumPhysicalGpusDelegate>(NvapiIds.EnumPhysicalGpus);
    }

    public static NvapiApi? TryCreate(out NvapiStatus status)
    {
        var initialize = NvapiNative.GetDelegate<NvapiNative.InitializeDelegate>(NvapiIds.Initialize);
        if (initialize is null)
        {
            status = NvapiStatus.LibraryNotFound;
            return null;
        }

        status = initialize();
        return status == NvapiStatus.Ok ? new NvapiApi() : null;
    }

    public IReadOnlyList<NvapiGpu> GetGpus()
    {
        if (_enumGpus is null)
        {
            return [];
        }

        var handles = new nint[64];
        if (_enumGpus(handles, out int count) != NvapiStatus.Ok)
        {
            return [];
        }

        var gpus = new List<NvapiGpu>(count);
        for (int i = 0; i < count; i++)
        {
            gpus.Add(new NvapiGpu(handles[i]));
        }

        return gpus;
    }
}

/// <summary>
/// One physical GPU seen through NVAPI. Wraps the verified interface set with
/// capability-tolerant methods; see docs/research/driver-apis.md for provenance.
/// </summary>
public sealed class NvapiGpu
{
    private readonly nint _handle;

    private readonly NvapiNative.GetFullNameDelegate? _getFullName;
    private readonly NvapiNative.GetBusIdDelegate? _getBusId;
    private readonly NvapiNative.GetTachReadingDelegate? _getTach;
    private readonly NvapiNative.GetThermalSensorsDelegate? _getThermalSensors;
    private readonly NvapiNative.GetDynamicPstatesDelegate? _getDynamicPstates;
    private readonly NvapiNative.FanCoolersGetStatusDelegate? _fanStatus;
    private readonly NvapiNative.FanCoolersControlDelegate? _fanGetControl;
    private readonly NvapiNative.FanCoolersControlDelegate? _fanSetControl;
    private readonly NvapiNative.RestoreCoolerSettingsDelegate? _restoreCoolers;
    private readonly NvapiNative.VoltRailsGetStatusDelegate? _voltRails;
    private readonly NvapiNative.ThermalPoliciesGetInfoDelegate? _thermalPoliciesInfo;
    private readonly NvapiNative.ThermalPoliciesStatusDelegate? _thermalPoliciesGetStatus;
    private readonly NvapiNative.ThermalPoliciesStatusDelegate? _thermalPoliciesSetStatus;
    private readonly NvapiNative.VoltageBoostDelegate? _getVoltageBoost;
    private readonly NvapiNative.VoltageBoostDelegate? _setVoltageBoost;
    private readonly NvapiNative.GetVfpCurveDelegate? _getVfpCurve;

    private uint _thermalSensorsMask;
    private bool _thermalMaskProbed;

    /// <summary>NVML architecture value used to map private thermal channels (10 = Blackwell, 8 = Ada).</summary>
    public uint Architecture { get; set; }

    internal NvapiGpu(nint handle)
    {
        _handle = handle;
        _getFullName = NvapiNative.GetDelegate<NvapiNative.GetFullNameDelegate>(NvapiIds.GpuGetFullName);
        _getBusId = NvapiNative.GetDelegate<NvapiNative.GetBusIdDelegate>(NvapiIds.GpuGetBusId);
        _getTach = NvapiNative.GetDelegate<NvapiNative.GetTachReadingDelegate>(NvapiIds.GpuGetTachReading);
        _getThermalSensors = NvapiNative.GetDelegate<NvapiNative.GetThermalSensorsDelegate>(NvapiIds.GpuGetThermalSensors);
        _getDynamicPstates = NvapiNative.GetDelegate<NvapiNative.GetDynamicPstatesDelegate>(NvapiIds.GpuGetDynamicPstatesInfoEx);
        _fanStatus = NvapiNative.GetDelegate<NvapiNative.FanCoolersGetStatusDelegate>(NvapiIds.GpuClientFanCoolersGetStatus);
        _fanGetControl = NvapiNative.GetDelegate<NvapiNative.FanCoolersControlDelegate>(NvapiIds.GpuClientFanCoolersGetControl);
        _fanSetControl = NvapiNative.GetDelegate<NvapiNative.FanCoolersControlDelegate>(NvapiIds.GpuClientFanCoolersSetControl);
        _restoreCoolers = NvapiNative.GetDelegate<NvapiNative.RestoreCoolerSettingsDelegate>(NvapiIds.GpuRestoreCoolerSettings);
        _voltRails = NvapiNative.GetDelegate<NvapiNative.VoltRailsGetStatusDelegate>(NvapiIds.GpuClientVoltRailsGetStatus);
        _thermalPoliciesInfo = NvapiNative.GetDelegate<NvapiNative.ThermalPoliciesGetInfoDelegate>(NvapiIds.GpuClientThermalPoliciesGetInfo);
        _thermalPoliciesGetStatus = NvapiNative.GetDelegate<NvapiNative.ThermalPoliciesStatusDelegate>(NvapiIds.GpuClientThermalPoliciesGetStatus);
        _thermalPoliciesSetStatus = NvapiNative.GetDelegate<NvapiNative.ThermalPoliciesStatusDelegate>(NvapiIds.GpuClientThermalPoliciesSetStatus);
        _getVoltageBoost = NvapiNative.GetDelegate<NvapiNative.VoltageBoostDelegate>(NvapiIds.GpuGetCoreVoltageBoostPercent);
        _setVoltageBoost = NvapiNative.GetDelegate<NvapiNative.VoltageBoostDelegate>(NvapiIds.GpuSetCoreVoltageBoostPercent);
        _getVfpCurve = NvapiNative.GetDelegate<NvapiNative.GetVfpCurveDelegate>(NvapiIds.GpuGetVfpCurve);
    }

    public unsafe string? GetName()
    {
        if (_getFullName is null)
        {
            return null;
        }

        byte* buffer = stackalloc byte[64];
        if (_getFullName(_handle, buffer) != NvapiStatus.Ok)
        {
            return null;
        }

        int length = 0;
        while (length < 64 && buffer[length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(buffer, length);
    }

    public NvapiStatus TryGetBusId(out uint busId)
    {
        busId = 0;
        return _getBusId?.Invoke(_handle, out busId) ?? NvapiStatus.FunctionNotFound;
    }

    public NvapiStatus TryGetTachRpm(out int rpm)
    {
        rpm = 0;
        return _getTach?.Invoke(_handle, out rpm) ?? NvapiStatus.FunctionNotFound;
    }

    // --- Private thermal sensors (hot spot / memory junction) -----------------

    private uint ProbeThermalSensorMask()
    {
        if (_thermalMaskProbed)
        {
            return _thermalSensorsMask;
        }

        _thermalMaskProbed = true;
        _thermalSensorsMask = 0;

        if (_getThermalSensors is null)
        {
            return 0;
        }

        bool allSupported = true;
        for (int bit = 0; bit < 32; bit++)
        {
            uint mask = 1u << bit;
            var sensors = NewThermalSensors(mask);
            if (_getThermalSensors(_handle, ref sensors) != NvapiStatus.Ok)
            {
                _thermalSensorsMask = mask - 1;
                allSupported = false;
                break;
            }
        }

        if (allSupported)
        {
            _thermalSensorsMask = uint.MaxValue;
        }

        return _thermalSensorsMask;
    }

    private static NvThermalSensors NewThermalSensors(uint mask) => new()
    {
        Version = NvapiNative.MakeVersion<NvThermalSensors>(2),
        Mask = mask,
        Reserved = new int[8],
        Temperatures = new int[32],
    };

    /// <summary>
    /// Reads hot spot / memory-junction temperatures where the driver exposes them.
    /// Channel mapping follows LibreHardwareMonitor's production mapping:
    /// Blackwell → memory junction on channel 2 (hot spot is blocked by NVIDIA);
    /// Ada → hot spot 1, memory 7; earlier → hot spot 1, memory 9.
    /// </summary>
    public (double? HotSpotC, double? MemJunctionC) GetPrivateThermals()
    {
        uint mask = ProbeThermalSensorMask();
        if (mask == 0 || _getThermalSensors is null)
        {
            return (null, null);
        }

        var sensors = NewThermalSensors(mask);
        if (_getThermalSensors(_handle, ref sensors) != NvapiStatus.Ok)
        {
            return (null, null);
        }

        (int? hotSpotChannel, int? memChannel) = Architecture switch
        {
            >= 10 => ((int?)null, (int?)2),
            8 => (1, 7),
            _ => (1, 9),
        };

        double? ReadChannel(int? channel)
        {
            if (channel is not int c || c >= sensors.Temperatures.Length)
            {
                return null;
            }

            double value = sensors.Temperatures[c] / 256.0;
            return value is > 0 and < 150 ? Math.Round(value, 1) : null;
        }

        return (ReadChannel(hotSpotChannel), ReadChannel(memChannel));
    }

    // --- Fans ------------------------------------------------------------------

    public readonly record struct FanInfo(uint CoolerId, uint Rpm, uint MinLevel, uint MaxLevel, uint Level);

    public NvapiStatus TryGetFanStatus(out IReadOnlyList<FanInfo> fans)
    {
        fans = [];
        if (_fanStatus is null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var status = new NvFanCoolersStatus
        {
            Version = NvapiNative.MakeVersion<NvFanCoolersStatus>(1),
            Items = new NvFanCoolersStatusItem[32],
        };
        var rc = _fanStatus(_handle, ref status);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        var result = new FanInfo[Math.Min(status.Count, 32)];
        for (int i = 0; i < result.Length; i++)
        {
            var item = status.Items[i];
            result[i] = new FanInfo(item.CoolerId, item.CurrentRpm, item.CurrentMinLevel, item.CurrentMaxLevel, item.CurrentLevel);
        }

        fans = result;
        return NvapiStatus.Ok;
    }

    /// <summary>
    /// Sets every fan to a manual duty (0–100). 0 is allowed (zero-RPM) — the
    /// FanCoolers interface accepts it even though NVML's minimum is ~30 %.
    /// </summary>
    public NvapiStatus TrySetAllFans(uint dutyPercent)
    {
        return MutateFanControl(item =>
        {
            item.Level = Math.Min(dutyPercent, 100);
            item.ControlMode = 1;
            return item;
        });
    }

    /// <summary>Sets one fan (by cooler id) to a manual duty.</summary>
    public NvapiStatus TrySetFan(uint coolerId, uint dutyPercent)
    {
        return MutateFanControl(item =>
        {
            if (item.CoolerId == coolerId)
            {
                item.Level = Math.Min(dutyPercent, 100);
                item.ControlMode = 1;
            }

            return item;
        });
    }

    /// <summary>Returns all fans to firmware (auto) control.</summary>
    public NvapiStatus TryRestoreAutoFans()
    {
        var rc = MutateFanControl(item =>
        {
            item.ControlMode = 0;
            return item;
        });

        if (rc != NvapiStatus.Ok && _restoreCoolers is not null)
        {
            rc = _restoreCoolers(_handle, 0, 0);
        }

        return rc;
    }

    private NvapiStatus MutateFanControl(Func<NvFanCoolerControlItem, NvFanCoolerControlItem> mutate)
    {
        if (_fanGetControl is null || _fanSetControl is null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var control = new NvFanCoolerControl
        {
            Version = NvapiNative.MakeVersion<NvFanCoolerControl>(1),
            Reserved2 = new uint[8],
            Items = new NvFanCoolerControlItem[32],
        };
        var rc = _fanGetControl(_handle, ref control);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        for (int i = 0; i < Math.Min(control.Count, 32); i++)
        {
            control.Items[i] = mutate(control.Items[i]);
        }

        return _fanSetControl(_handle, ref control);
    }

    // --- Voltage ----------------------------------------------------------------

    public NvapiStatus TryGetCoreVoltageMv(out double millivolts)
    {
        millivolts = 0;
        if (_voltRails is null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var status = new NvVoltRailsStatus { Version = NvapiNative.MakeVersion<NvVoltRailsStatus>(1) };
        var rc = _voltRails(_handle, ref status);
        if (rc == NvapiStatus.Ok)
        {
            millivolts = status.CoreMicrovolts / 1000.0;
        }

        return rc;
    }

    public NvapiStatus TryGetVoltageBoostPercent(out uint percent)
    {
        percent = 0;
        if (_getVoltageBoost is null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var boost = new NvVoltageBoostPercent
        {
            Version = NvapiNative.MakeVersion<NvVoltageBoostPercent>(1),
            Reserved = new uint[8],
        };
        var rc = _getVoltageBoost(_handle, ref boost);
        percent = boost.Percent;
        return rc;
    }

    public NvapiStatus TrySetVoltageBoostPercent(uint percent)
    {
        if (_setVoltageBoost is null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var boost = new NvVoltageBoostPercent
        {
            Version = NvapiNative.MakeVersion<NvVoltageBoostPercent>(1),
            Percent = Math.Min(percent, 100),
            Reserved = new uint[8],
        };
        return _setVoltageBoost(_handle, ref boost);
    }

    // --- Voltage/frequency curve (read-only) -------------------------------------

    /// <summary>One point of the GPU's voltage/frequency curve.</summary>
    public readonly record struct VfPoint(double VoltageMv, double ClockMHz);

    /// <summary>
    /// Reads the driver's boost (V/F) curve for the core domain. Read-only:
    /// per-point curve writes are rejected by the driver on RTX 50, so Afterglow
    /// visualizes the curve and undervolts through the documented lock+offset APIs.
    /// Returns an empty list when the interface is unavailable.
    /// </summary>
    public IReadOnlyList<VfPoint> GetVfCurve()
    {
        if (_getVfpCurve is null)
        {
            return [];
        }

        var curve = new NvVfpCurve
        {
            Version = NvapiNative.MakeVersion<NvVfpCurve>(1),
            Masks = new uint[4],
            Unknown1 = new uint[12],
            GpuCurveEntries = new NvVfpCurveEntry[80],
            MemoryCurveEntries = new NvVfpCurveEntry[23],
            Unknown2 = new uint[1064],
        };

        if (_getVfpCurve(_handle, ref curve) != NvapiStatus.Ok)
        {
            return [];
        }

        var points = new List<VfPoint>();
        foreach (var entry in curve.GpuCurveEntries)
        {
            if (entry.FrequencyKHz == 0 || entry.VoltageMicroV == 0)
            {
                continue;
            }

            double mv = entry.VoltageMicroV / 1000.0;
            double mhz = entry.FrequencyKHz / 1000.0;
            if (mv is > 300 and < 1600 && mhz is > 100 and < 4500)
            {
                points.Add(new VfPoint(Math.Round(mv, 1), Math.Round(mhz, 1)));
            }
        }

        points.Sort((a, b) => a.VoltageMv.CompareTo(b.VoltageMv));
        return points;
    }

    // --- Temperature limit (thermal policies) -----------------------------------

    public NvapiStatus TryGetTempLimitRange(out int minC, out int defaultC, out int maxC)
    {
        minC = defaultC = maxC = 0;
        if (_thermalPoliciesInfo is null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var info = new NvThermalPoliciesInfo
        {
            Version = NvapiNative.MakeVersion<NvThermalPoliciesInfo>(2),
            Entries = new NvThermalPoliciesInfoEntry[4],
        };
        var rc = _thermalPoliciesInfo(_handle, ref info);
        if (rc != NvapiStatus.Ok || info.Count == 0)
        {
            return rc == NvapiStatus.Ok ? NvapiStatus.DataNotFound : rc;
        }

        minC = info.Entries[0].MinimumTemp >> 8;
        defaultC = info.Entries[0].DefaultTemp >> 8;
        maxC = info.Entries[0].MaximumTemp >> 8;
        return NvapiStatus.Ok;
    }

    public NvapiStatus TryGetTempLimit(out int currentC)
    {
        currentC = 0;
        var rc = ReadThermalStatus(out var status);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        if (status.Count == 0)
        {
            return NvapiStatus.DataNotFound;
        }

        currentC = status.Entries[0].TargetTemp >> 8;
        return NvapiStatus.Ok;
    }

    public NvapiStatus TrySetTempLimit(int targetC)
    {
        if (_thermalPoliciesSetStatus is null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var rc = ReadThermalStatus(out var status);
        if (rc != NvapiStatus.Ok || status.Count == 0)
        {
            return rc == NvapiStatus.Ok ? NvapiStatus.DataNotFound : rc;
        }

        for (int i = 0; i < Math.Min(status.Count, 4); i++)
        {
            status.Entries[i].TargetTemp = targetC << 8;
        }

        return _thermalPoliciesSetStatus(_handle, ref status);
    }

    private NvapiStatus ReadThermalStatus(out NvThermalPoliciesStatus status)
    {
        status = new NvThermalPoliciesStatus
        {
            Version = NvapiNative.MakeVersion<NvThermalPoliciesStatus>(2),
            Entries = new NvThermalPoliciesStatusEntry[4],
        };
        return _thermalPoliciesGetStatus?.Invoke(_handle, ref status) ?? NvapiStatus.FunctionNotFound;
    }

    // --- Utilization domains -----------------------------------------------------

    /// <summary>GPU / framebuffer / video-engine / bus utilization percentages.</summary>
    public NvapiStatus TryGetUtilizationDomains(out int gpu, out int framebuffer, out int video, out int bus)
    {
        gpu = framebuffer = video = bus = 0;
        if (_getDynamicPstates is null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var info = new NvDynamicPstatesInfo
        {
            Version = NvapiNative.MakeVersion<NvDynamicPstatesInfo>(1),
            Utilizations = new NvDynamicPstate[8],
        };
        var rc = _getDynamicPstates(_handle, ref info);
        if (rc == NvapiStatus.Ok)
        {
            gpu = info.Utilizations[0].IsPresent != 0 ? info.Utilizations[0].Percentage : 0;
            framebuffer = info.Utilizations[1].IsPresent != 0 ? info.Utilizations[1].Percentage : 0;
            video = info.Utilizations[2].IsPresent != 0 ? info.Utilizations[2].Percentage : 0;
            bus = info.Utilizations[3].IsPresent != 0 ? info.Utilizations[3].Percentage : 0;
        }

        return rc;
    }
}
