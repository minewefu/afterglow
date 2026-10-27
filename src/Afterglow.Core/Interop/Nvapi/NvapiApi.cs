using System.Text;

namespace Afterglow.Core.Interop.Nvapi;

/// <summary>
/// Managed entry point for NVAPI. All interfaces are optional: a null/missing
/// interface degrades the corresponding feature instead of failing the app.
/// </summary>
public sealed unsafe class NvapiApi
{
    /// <summary>NVAPI_MAX_PHYSICAL_GPUS.</summary>
    private const int MaxPhysicalGpus = 64;

    private readonly NvapiGpuFunctions _fn;

    private NvapiApi()
    {
        _fn = NvapiGpuFunctions.Resolved;
    }

    public static NvapiApi? TryCreate(out NvapiStatus status)
    {
        status = NvapiNative.EnsureInitialized();
        return status == NvapiStatus.Ok ? new NvapiApi() : null;
    }

    public IReadOnlyList<NvapiGpu> GetGpus()
    {
        if (_fn.EnumPhysicalGpus == null)
        {
            return [];
        }

        nint* handles = stackalloc nint[MaxPhysicalGpus];
        int count = 0;
        if (_fn.EnumPhysicalGpus(handles, &count) != NvapiStatus.Ok)
        {
            return [];
        }

        count = Math.Clamp(count, 0, MaxPhysicalGpus);
        var gpus = new List<NvapiGpu>(count);
        for (int i = 0; i < count; i++)
        {
            gpus.Add(new NvapiGpu(handles[i], _fn));
        }

        return gpus;
    }
}

/// <summary>
/// One physical GPU seen through NVAPI. Wraps the verified interface set with
/// capability-tolerant methods; see docs/research/driver-apis.md for provenance.
/// Every driver struct lives on the stack for the duration of one call, so the
/// sensor reads (thermals, voltage, fan status) allocate nothing beyond their
/// managed results.
/// </summary>
public sealed unsafe class NvapiGpu : INvapiTuningGpu
{
    private readonly nint _handle;
    private readonly NvapiGpuFunctions _fn;

    private uint _thermalSensorsMask;
    private bool _thermalMaskProbed;

    /// <summary>
    /// NVML architecture value used to map private thermal channels (10 =
    /// Blackwell, 8 = Ada). Must be assigned before thermal reads;
    /// <see cref="GetPrivateThermals"/> returns nothing while it is 0 rather
    /// than guessing a channel map.
    /// </summary>
    public uint Architecture { get; set; }

    internal NvapiGpu(nint handle, NvapiGpuFunctions functions)
    {
        _handle = handle;
        _fn = functions;
    }

    public string? GetName()
    {
        if (_fn.GetFullName == null)
        {
            return null;
        }

        byte* buffer = stackalloc byte[64];
        if (_fn.GetFullName(_handle, buffer) != NvapiStatus.Ok)
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
        if (_fn.GetBusId == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        uint value = 0;
        var rc = _fn.GetBusId(_handle, &value);
        busId = value;
        return rc;
    }

    public NvapiStatus TryGetTachRpm(out int rpm)
    {
        rpm = 0;
        if (_fn.GetTachReading == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        int value = 0;
        var rc = _fn.GetTachReading(_handle, &value);
        rpm = value;
        return rc;
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

        if (_fn.GetThermalSensors == null)
        {
            return 0;
        }

        bool allSupported = true;
        for (int bit = 0; bit < 32; bit++)
        {
            uint mask = 1u << bit;
            var sensors = NewThermalSensors(mask);
            if (_fn.GetThermalSensors(_handle, &sensors) != NvapiStatus.Ok)
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

    private static NvThermalSensors NewThermalSensors(uint mask)
    {
        var sensors = default(NvThermalSensors);
        sensors.Version = NvapiNative.MakeVersion<NvThermalSensors>(2);
        sensors.Mask = mask;
        return sensors;
    }

    /// <summary>
    /// Reads hot spot / memory-junction temperatures where the driver exposes them.
    /// Channel mapping follows LibreHardwareMonitor's production mapping:
    /// Blackwell → memory junction on channel 2 (hot spot is blocked by NVIDIA);
    /// Ada → hot spot 1, memory 7; earlier → hot spot 1, memory 9.
    /// </summary>
    public (double? HotSpotC, double? MemJunctionC) GetPrivateThermals()
    {
        // The channel map depends on Architecture; until a caller has set it
        // (GpuManager does, from NVML), refusing is safer than silently using
        // the pre-Ada mapping and reporting the wrong sensor as hot spot.
        if (Architecture == 0)
        {
            return (null, null);
        }

        uint mask = ProbeThermalSensorMask();
        if (mask == 0 || _fn.GetThermalSensors == null)
        {
            return (null, null);
        }

        var sensors = NewThermalSensors(mask);
        if (_fn.GetThermalSensors(_handle, &sensors) != NvapiStatus.Ok)
        {
            return (null, null);
        }

        (int? hotSpotChannel, int? memChannel) = Architecture switch
        {
            >= 10 => ((int?)null, (int?)2),
            8 => (1, 7),
            _ => (1, 9),
        };

        return (ReadChannel(&sensors, hotSpotChannel), ReadChannel(&sensors, memChannel));
    }

    private static double? ReadChannel(NvThermalSensors* sensors, int? channel)
    {
        if (channel is not int c || c >= NvThermalSensors.Channels)
        {
            return null;
        }

        double value = sensors->Temperatures[c] / 256.0;
        return value is > 0 and < 150 ? Math.Round(value, 1) : null;
    }

    // --- Fans ------------------------------------------------------------------

    public readonly record struct FanInfo(uint CoolerId, uint Rpm, uint MinLevel, uint MaxLevel, uint Level);

    public NvapiStatus TryGetFanStatus(out IReadOnlyList<FanInfo> fans)
    {
        fans = [];
        if (_fn.FanCoolersGetStatus == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var status = default(NvFanCoolersStatus);
        status.Version = NvapiNative.MakeVersion<NvFanCoolersStatus>(1);
        var rc = _fn.FanCoolersGetStatus(_handle, &status);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        var result = new FanInfo[Math.Min(status.Count, NvFanCoolersStatus.MaxCoolers)];
        for (int i = 0; i < result.Length; i++)
        {
            ref readonly var item = ref status.Items[i];
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

        if (rc != NvapiStatus.Ok && _fn.RestoreCoolerSettings != null)
        {
            rc = _fn.RestoreCoolerSettings(_handle, null, 0);
        }

        return rc;
    }

    private NvapiStatus MutateFanControl(Func<NvFanCoolerControlItem, NvFanCoolerControlItem> mutate)
    {
        if (_fn.FanCoolersGetControl == null || _fn.FanCoolersSetControl == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var control = default(NvFanCoolerControl);
        control.Version = NvapiNative.MakeVersion<NvFanCoolerControl>(1);
        var rc = _fn.FanCoolersGetControl(_handle, &control);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        for (int i = 0; i < Math.Min(control.Count, NvFanCoolerControl.MaxCoolers); i++)
        {
            control.Items[i] = mutate(control.Items[i]);
        }

        return _fn.FanCoolersSetControl(_handle, &control);
    }

    // --- Voltage ----------------------------------------------------------------

    public NvapiStatus TryGetCoreVoltageMv(out double millivolts)
    {
        millivolts = 0;
        if (_fn.VoltRailsGetStatus == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var status = default(NvVoltRailsStatus);
        status.Version = NvapiNative.MakeVersion<NvVoltRailsStatus>(1);
        var rc = _fn.VoltRailsGetStatus(_handle, &status);
        if (rc == NvapiStatus.Ok)
        {
            millivolts = status.CoreMicrovolts / 1000.0;
        }

        return rc;
    }

    public NvapiStatus TryGetVoltageBoostPercent(out uint percent)
    {
        percent = 0;
        if (_fn.GetVoltageBoost == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var boost = default(NvVoltageBoostPercent);
        boost.Version = NvapiNative.MakeVersion<NvVoltageBoostPercent>(1);
        var rc = _fn.GetVoltageBoost(_handle, &boost);
        percent = boost.Percent;
        return rc;
    }

    public NvapiStatus TrySetVoltageBoostPercent(uint percent)
    {
        if (_fn.SetVoltageBoost == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var boost = default(NvVoltageBoostPercent);
        boost.Version = NvapiNative.MakeVersion<NvVoltageBoostPercent>(1);
        boost.Percent = Math.Min(percent, 100);
        return _fn.SetVoltageBoost(_handle, &boost);
    }

    // --- Per-point voltage/frequency curve (clock-boost table) -------------------

    /// <summary>One point of the GPU's voltage/frequency curve.</summary>
    public readonly record struct VfPoint(double VoltageMv, double ClockMHz);

    /// <summary>
    /// One live slot of the driver's core V/F table: the stored point plus the
    /// per-point offset currently applied to it. <see cref="Index"/> is the raw
    /// table slot (0–254) used by <see cref="TrySetVfpPointOffsets"/>.
    /// </summary>
    public readonly record struct VfpTablePoint(int Index, double VoltageMv, double ClockMHz, int OffsetMHz);

    // Delta scale, calibrated live rather than taken on faith: with a known
    // global +100 MHz core offset applied, the table reads raw 100000 per
    // point on RTX 5090 / driver 616.56 — plain kHz. (nvapioc halves these
    // values, i.e. kHz × 2, on the generations it was built against; if a
    // 20/30/40-series tester sees offsets reading at half/double the applied
    // global offset, this constant is where the generations diverge.)
    private const int TableDeltaPerMHz = 1000;

    /// <summary>Static bound for a single point's offset; the driver additionally validates.</summary>
    public const int VfpOffsetLimitMHz = 1500;

    // The three tables are 6–9 KB each; callers keep them in stack locals and
    // pass their addresses, so a full table read allocates nothing.

    private NvapiStatus ReadMasks(NvClockMasks* masks)
    {
        *masks = default;
        masks->Version = NvapiNative.MakeVersion<NvClockMasks>(1);
        return _fn.GetClockBoostMask == null ? NvapiStatus.NoImplementation : _fn.GetClockBoostMask(_handle, masks);
    }

    private NvapiStatus ReadCurve(NvClockMasks* mask, NvVfpCurve* curve)
    {
        *curve = default;
        curve->Version = NvapiNative.MakeVersion<NvVfpCurve>(1);
        CopyMask(mask->Mask, curve->Mask);
        return _fn.GetVfpCurve == null ? NvapiStatus.NoImplementation : _fn.GetVfpCurve(_handle, curve);
    }

    private NvapiStatus ReadTable(NvClockMasks* mask, NvClockTable* table)
    {
        *table = default;
        table->Version = NvapiNative.MakeVersion<NvClockTable>(1);
        CopyMask(mask->Mask, table->Mask);
        return _fn.GetClockBoostTable == null ? NvapiStatus.NoImplementation : _fn.GetClockBoostTable(_handle, table);
    }

    private static void CopyMask(byte* source, byte* destination) =>
        new ReadOnlySpan<byte>(source, NvClockBoost.MaskBytes).CopyTo(new Span<byte>(destination, NvClockBoost.MaskBytes));

    /// <summary>
    /// Reads the driver's stored core V/F table with the per-point offsets
    /// currently applied. Read and write are verified on RTX 5090 (Blackwell,
    /// driver 616.56) and expected on Pascal→Ada through the same interfaces;
    /// support is probed live, never assumed by generation, and any driver
    /// refusal is passed through unchanged (F-04/F-47: an older version of this
    /// comment claimed Blackwell rejects the interfaces — that was a broken
    /// struct layout of ours, not the driver).
    /// </summary>
    public NvapiStatus TryGetVfpPoints(out IReadOnlyList<VfpTablePoint> points)
    {
        points = [];
        NvClockMasks masks;
        var rc = ReadMasks(&masks);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        NvVfpCurve curve;
        rc = ReadCurve(&masks, &curve);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        NvClockTable table;
        rc = ReadTable(&masks, &table);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        var result = new List<VfpTablePoint>();
        for (int i = 0; i < NvClockBoost.Slots; i++)
        {
            if (masks.Clocks[i].Enabled != 1 || curve.Clocks[i].ClockType != 0)
            {
                continue;
            }

            double mv = curve.Clocks[i].VoltageMicroV / 1000.0;
            double mhz = curve.Clocks[i].FrequencyKHz / 1000.0;
            if (mv is <= 300 or >= 1600 || mhz is <= 100 or >= 4500)
            {
                continue;
            }

            result.Add(new VfpTablePoint(
                i, Math.Round(mv, 1), Math.Round(mhz, 1),
                table.Clocks[i].FrequencyDeltaKHz / TableDeltaPerMHz));
        }

        result.Sort((a, b) => a.VoltageMv.CompareTo(b.VoltageMv));
        points = result;
        return NvapiStatus.Ok;
    }

    /// <summary>
    /// Writes per-point core offsets (MHz, keyed by raw table slot) into the
    /// clock-boost table. Slots not in the dictionary keep their current
    /// delta; only live core-domain slots are touched, and each offset is
    /// clamped to ±<see cref="VfpOffsetLimitMHz"/>.
    /// </summary>
    public NvapiStatus TrySetVfpPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex)
    {
        if (_fn.SetClockBoostTable == null)
        {
            return NvapiStatus.NoImplementation;
        }

        NvClockMasks masks;
        var rc = ReadMasks(&masks);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        NvClockTable table;
        rc = ReadTable(&masks, &table);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        foreach (var (index, offsetMHz) in offsetsMHzByIndex)
        {
            if (index is < 0 or > 254 || masks.Clocks[index].Enabled != 1 || table.Clocks[index].ClockType != 0)
            {
                continue;
            }

            int clamped = Math.Clamp(offsetMHz, -VfpOffsetLimitMHz, VfpOffsetLimitMHz);
            table.Clocks[index].FrequencyDeltaKHz = clamped * TableDeltaPerMHz;
        }

        return _fn.SetClockBoostTable(_handle, &table);
    }

    /// <summary>
    /// Zeroes the per-point offset of every live core-domain slot. A
    /// read-modify-write, like <see cref="TrySetVfpPointOffsets"/>: slots of
    /// other clock domains and every opaque per-entry byte go back exactly as
    /// the driver returned them. It used to write a fabricated all-zero table,
    /// relabelling non-core slots as core and blanking their deltas — state the
    /// set path deliberately leaves alone (F-03/F-23).
    /// </summary>
    public NvapiStatus TryClearVfpPointOffsets()
    {
        if (_fn.SetClockBoostTable == null)
        {
            return NvapiStatus.NoImplementation;
        }

        NvClockMasks masks;
        var rc = ReadMasks(&masks);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        NvClockTable table;
        rc = ReadTable(&masks, &table);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        ClearCoreDeltas(&masks, &table);
        return _fn.SetClockBoostTable(_handle, &table);
    }

    /// <summary>Zeroes the delta of live core-domain slots only (separated so the rule is unit-testable).</summary>
    internal static void ClearCoreDeltas(NvClockMasks* masks, NvClockTable* table)
    {
        for (int i = 0; i < NvClockBoost.Slots; i++)
        {
            if (masks->Clocks[i].Enabled == 1 && table->Clocks[i].ClockType == 0)
            {
                table->Clocks[i].FrequencyDeltaKHz = 0;
            }
        }
    }

    /// <summary>
    /// Reads the driver's boost (V/F) curve for the core domain (points only,
    /// no offsets). Returns an empty list when the interface is unavailable.
    /// </summary>
    public IReadOnlyList<VfPoint> GetVfCurve()
    {
        NvClockMasks masks;
        if (ReadMasks(&masks) != NvapiStatus.Ok)
        {
            return [];
        }

        NvVfpCurve curve;
        if (ReadCurve(&masks, &curve) != NvapiStatus.Ok)
        {
            return [];
        }

        var points = new List<VfPoint>();
        for (int i = 0; i < NvClockBoost.Slots; i++)
        {
            if (masks.Clocks[i].Enabled != 1 || curve.Clocks[i].ClockType != 0)
            {
                continue;
            }

            double mv = curve.Clocks[i].VoltageMicroV / 1000.0;
            double mhz = curve.Clocks[i].FrequencyKHz / 1000.0;
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
        if (_fn.ThermalPoliciesGetInfo == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var info = default(NvThermalPoliciesInfo);
        info.Version = NvapiNative.MakeVersion<NvThermalPoliciesInfo>(2);
        var rc = _fn.ThermalPoliciesGetInfo(_handle, &info);
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
        NvThermalPoliciesStatus status;
        var rc = ReadThermalStatus(&status);
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
        if (_fn.ThermalPoliciesSetStatus == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        NvThermalPoliciesStatus status;
        var rc = ReadThermalStatus(&status);
        if (rc != NvapiStatus.Ok || status.Count == 0)
        {
            return rc == NvapiStatus.Ok ? NvapiStatus.DataNotFound : rc;
        }

        for (int i = 0; i < Math.Min(status.Count, NvThermalPoliciesStatus.MaxEntries); i++)
        {
            status.Entries[i].TargetTemp = targetC << 8;
        }

        return _fn.ThermalPoliciesSetStatus(_handle, &status);
    }

    private NvapiStatus ReadThermalStatus(NvThermalPoliciesStatus* status)
    {
        *status = default;
        status->Version = NvapiNative.MakeVersion<NvThermalPoliciesStatus>(2);
        return _fn.ThermalPoliciesGetStatus == null
            ? NvapiStatus.FunctionNotFound
            : _fn.ThermalPoliciesGetStatus(_handle, status);
    }

    // --- Utilization domains -----------------------------------------------------

    /// <summary>GPU / framebuffer / video-engine / bus utilization percentages.</summary>
    public NvapiStatus TryGetUtilizationDomains(out int gpu, out int framebuffer, out int video, out int bus)
    {
        gpu = framebuffer = video = bus = 0;
        if (_fn.GetDynamicPstates == null)
        {
            return NvapiStatus.FunctionNotFound;
        }

        var info = default(NvDynamicPstatesInfo);
        info.Version = NvapiNative.MakeVersion<NvDynamicPstatesInfo>(1);
        var rc = _fn.GetDynamicPstates(_handle, &info);
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
