using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// An NVIDIA card for <see cref="Tuning.GpuTuner"/>: NVML and NVAPI in one
/// object, behaving as docs/research/driver-apis.md (Blackwell section)
/// records for RTX 5090 / 616.56 —
/// <list type="number">
/// <item>the global core offset lives in the per-point table: a core-offset
/// write sets every live slot to the new offset, levelling any per-point shape;</item>
/// <item>a per-point write changes only the slots it names, and the global
/// offset NVML reports stays what it was;</item>
/// <item>clearing the point offsets leaves the global offset in place, so
/// every slot reads it afterwards.</item>
/// </list>
/// Clock locks have no getter, as in NVML: the fake records the calls and the
/// range in force so a test can see what the tuner wrote. Fans follow the two
/// interfaces' shapes: NVAPI writes every cooler in one call, NVML one fan per
/// call, so only NVML's fallback can leave some fans manual and others not
/// (<see cref="ManualFans"/>). Every failure a test needs is a settable return code.
/// </summary>
internal sealed class FakeNvidiaDriver : INvmlTuningDevice, INvapiTuningGpu
{
    public const int OffsetMinMHz = -1000;
    public const int OffsetMaxMHz = 1000;

    private readonly SortedDictionary<int, int> _table = [];

    public FakeNvidiaDriver(string uuid = "GPU-fake0000-1111-2222-3333-444455556666", int liveSlots = 5)
    {
        Uuid = uuid;
        for (int slot = 30; slot < 30 + liveSlots; slot++)
        {
            _table[slot] = 0;
        }
    }

    public string Uuid { get; }

    public int CoreOffsetMHz { get; private set; }

    public int MemOffsetMHz { get; private set; }

    public uint PowerLimitMw { get; private set; } = 300_000;

    /// <summary>The range lock in force, or null (NVML cannot read this back; the fake can).</summary>
    public (uint Min, uint Max)? LockedClocks { get; private set; }

    public List<(uint Min, uint Max)> LockWrites { get; } = [];

    public int CoreOffsetWrites { get; private set; }

    public int VfpWrites { get; private set; }

    /// <summary>Runs inside the per-point write, before it lands — to see what is on record at that moment.</summary>
    public Action? OnVfpWrite { get; set; }

    public NvmlReturn CoreOffsetReadRc { get; set; } = NvmlReturn.Success;

    public NvapiStatus VfpWriteRc { get; set; } = NvapiStatus.Ok;

    public NvmlReturn NumFansRc { get; set; } = NvmlReturn.Success;

    public uint NumFans { get; set; } = 2;

    public NvmlReturn DefaultFanSpeedRc { get; set; } = NvmlReturn.Success;

    public NvapiStatus RestoreAutoFansRc { get; set; } = NvapiStatus.Ok;

    public int NvmlFanReleases { get; private set; }

    /// <summary>NVAPI's one-call all-fans write; anything but Ok sends the tuner to NVML's per-fan fallback.</summary>
    public NvapiStatus SetAllFansRc { get; set; } = NvapiStatus.Ok;

    /// <summary>NVML per-fan answers to a manual duty, by fan index; a fan not listed accepts it.</summary>
    public Dictionary<uint, NvmlReturn> FanSetRcs { get; } = [];

    /// <summary>
    /// Each fan under manual control and its duty; a fan not listed is under
    /// firmware (auto) control. A refused write leaves its fan as it was, and
    /// does not undo the fans written before it — NVML commands one fan per call.
    /// </summary>
    public Dictionary<uint, uint> ManualFans { get; } = [];

    /// <summary>Every NVML per-fan manual write, accepted or not, in order.</summary>
    public List<(uint Fan, uint Percent)> FanSetCalls { get; } = [];

    /// <summary>The table's offsets in slot order.</summary>
    public IReadOnlyList<int> TableOffsets => [.. _table.Values];

    // --- NVML -----------------------------------------------------------------

    public string? GetUuid() => Uuid;

    public NvmlReturn TryGetClockOffset(NvmlClockType type, out NvmlClockOffset offset)
    {
        offset = new NvmlClockOffset
        {
            Type = type,
            ClockOffsetMHz = type == NvmlClockType.Graphics ? CoreOffsetMHz : MemOffsetMHz,
            MinClockOffsetMHz = OffsetMinMHz,
            MaxClockOffsetMHz = OffsetMaxMHz,
        };
        return type == NvmlClockType.Graphics ? CoreOffsetReadRc : NvmlReturn.Success;
    }

    public NvmlReturn TrySetClockOffset(NvmlClockType type, int offsetMHz)
    {
        if (type == NvmlClockType.Graphics)
        {
            CoreOffsetWrites++;
            CoreOffsetMHz = offsetMHz;
            foreach (int slot in _table.Keys.ToArray())
            {
                _table[slot] = offsetMHz; // levels every live slot (behaviour 1)
            }
        }
        else
        {
            MemOffsetMHz = offsetMHz;
        }

        return NvmlReturn.Success;
    }

    public NvmlReturn TryGetMaxClock(NvmlClockType type, out uint mhz)
    {
        mhz = 3000;
        return NvmlReturn.Success;
    }

    public NvmlReturn TryGetEnforcedPowerLimit(out uint milliwatts)
    {
        milliwatts = PowerLimitMw;
        return NvmlReturn.Success;
    }

    public NvmlReturn TryGetPowerLimitConstraints(out uint minMw, out uint maxMw)
    {
        minMw = 100_000;
        maxMw = 450_000;
        return NvmlReturn.Success;
    }

    public NvmlReturn TryGetDefaultPowerLimit(out uint milliwatts)
    {
        milliwatts = 300_000;
        return NvmlReturn.Success;
    }

    public NvmlReturn TrySetPowerLimit(uint milliwatts)
    {
        PowerLimitMw = milliwatts;
        return NvmlReturn.Success;
    }

    public NvmlReturn TryGetNumFans(out uint numFans)
    {
        numFans = NumFansRc == NvmlReturn.Success ? NumFans : 0;
        return NumFansRc;
    }

    public NvmlReturn TryGetMinMaxFanSpeed(out uint minPercent, out uint maxPercent)
    {
        minPercent = 30;
        maxPercent = 100;
        return NvmlReturn.Success;
    }

    public NvmlReturn TrySetFanSpeed(uint fan, uint percent)
    {
        FanSetCalls.Add((fan, percent));
        var rc = FanSetRcs.GetValueOrDefault(fan, NvmlReturn.Success);
        if (rc == NvmlReturn.Success)
        {
            ManualFans[fan] = percent;
        }

        return rc;
    }

    public NvmlReturn TrySetDefaultFanSpeed(uint fan)
    {
        NvmlFanReleases++;
        if (DefaultFanSpeedRc == NvmlReturn.Success)
        {
            ManualFans.Remove(fan);
        }

        return DefaultFanSpeedRc;
    }

    public NvmlReturn TrySetGpuLockedClocks(uint minMhz, uint maxMhz)
    {
        LockWrites.Add((minMhz, maxMhz));
        LockedClocks = (minMhz, maxMhz);
        return NvmlReturn.Success;
    }

    public NvmlReturn TryResetGpuLockedClocks()
    {
        LockedClocks = null;
        return NvmlReturn.Success;
    }

    // --- NVAPI ----------------------------------------------------------------

    public NvapiStatus TryGetVoltageBoostPercent(out uint percent)
    {
        percent = 0;
        return NvapiStatus.NotSupported;
    }

    public NvapiStatus TrySetVoltageBoostPercent(uint percent) => NvapiStatus.NotSupported;

    public NvapiStatus TryGetTempLimitRange(out int minC, out int defaultC, out int maxC)
    {
        minC = defaultC = maxC = 0;
        return NvapiStatus.NotSupported;
    }

    public NvapiStatus TryGetTempLimit(out int currentC)
    {
        currentC = 0;
        return NvapiStatus.NotSupported;
    }

    public NvapiStatus TrySetTempLimit(int targetC) => NvapiStatus.NotSupported;

    public NvapiStatus TryGetVfpPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points)
    {
        points = [.. _table.Select(p => new NvapiGpu.VfpTablePoint(p.Key, 700 + (p.Key * 5), 1500 + (p.Key * 10), p.Value))];
        return NvapiStatus.Ok;
    }

    public NvapiStatus TrySetVfpPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex)
    {
        OnVfpWrite?.Invoke();
        if (VfpWriteRc != NvapiStatus.Ok)
        {
            return VfpWriteRc;
        }

        VfpWrites++;
        foreach (var (slot, offset) in offsetsMHzByIndex)
        {
            if (_table.ContainsKey(slot))
            {
                _table[slot] = offset; // the global offset stays (behaviour 2)
            }
        }

        return NvapiStatus.Ok;
    }

    public NvapiStatus TryClearVfpPointOffsets()
    {
        foreach (int slot in _table.Keys.ToArray())
        {
            _table[slot] = CoreOffsetMHz; // the global offset survives the clear (behaviour 3)
        }

        return NvapiStatus.Ok;
    }

    public NvapiStatus TrySetAllFans(uint dutyPercent)
    {
        // One driver call for every cooler: it lands on all of them or on none.
        if (SetAllFansRc == NvapiStatus.Ok)
        {
            for (uint fan = 0; fan < NumFans; fan++)
            {
                ManualFans[fan] = dutyPercent;
            }
        }

        return SetAllFansRc;
    }

    public NvapiStatus TrySetFan(uint coolerId, uint dutyPercent) => NvapiStatus.Ok;

    public NvapiStatus TryRestoreAutoFans()
    {
        if (RestoreAutoFansRc == NvapiStatus.Ok)
        {
            ManualFans.Clear();
        }

        return RestoreAutoFansRc;
    }
}
