using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// An in-memory NVIDIA-shaped tuner: offsets, a clock lock and a V/F table it
/// reads back as written. Every write raises <see cref="StateWritten"/> through
/// the production <see cref="WriteNotification"/>, as <see cref="GpuTuner"/>
/// does; <see cref="SetStateSilently"/> changes the card the way another
/// process would — with no notification in this one.
/// </summary>
internal sealed class FakeTuner : IGpuTuner
{
    public const string Uuid = "GPU-FAKE-0000-0000-0001";

    private readonly Lock _gate = new();
    private int _core;
    private int _mem;
    private uint? _lock;

    /// <summary>The V/F table as each slot's offset; null = the driver refuses to read it.</summary>
    private int[]? _vfOffsets;

    public FakeTuner(bool vfPoints = false, int vfSlots = 6)
    {
        Capabilities = new TuningCapabilities
        {
            SupportsCoreOffset = true,
            CoreOffsetMinMHz = -1000,
            CoreOffsetMaxMHz = 1000,
            SupportsMemOffset = true,
            MemOffsetMinMHz = -1000,
            MemOffsetMaxMHz = 3000,
            SupportsLockedCoreClock = true,
            MaxCoreClockMHz = 3000,
            SupportsVfPoints = vfPoints,
            VfPointCount = vfPoints ? vfSlots : 0,
        };

        _vfOffsets = vfPoints ? new int[vfSlots] : null;
    }

    public TuningCapabilities Capabilities { get; }

    public string? GpuUuid => Uuid;

    public string RecordKey => Uuid;

    /// <summary>A lock from before the last sleep that may or may not still be in force (IGpuTuner.UnconfirmedLockMHz).</summary>
    public uint? UnconfirmedLock { get; set; }

    public uint? UnconfirmedLockMHz => UnconfirmedLock;

    public uint? AppliedLockMHz
    {
        get
        {
            lock (_gate)
            {
                return _lock;
            }
        }
    }

    public event Action<IGpuTuner>? StateWritten;

    /// <summary>Calls to <see cref="ReadCurrent"/> so far.</summary>
    public int ReadCount => Volatile.Read(ref _readCount);

    private int _readCount;

    /// <summary>
    /// Runs inside <see cref="ReadCurrent"/> AFTER the values were taken and
    /// before they are returned — a read that has seen the card and not yet
    /// reported it, which is where a slow driver call leaves a capture.
    /// </summary>
    public Action? AfterRead { get; set; }

    /// <summary>When true, the V/F table read fails like a driver refusing it.</summary>
    public bool VfReadFails { get; set; }

    public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz) ReadCurrent()
    {
        Interlocked.Increment(ref _readCount);
        (int, int, double?, uint?, uint?) values;
        lock (_gate)
        {
            values = (_core, _mem, 300.0, null, _lock);
        }

        AfterRead?.Invoke();
        return values;
    }

    public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false)
    {
        lock (_gate)
        {
            // A global core offset levels every V/F slot to it (measured on
            // RTX 5090 / driver 616.56; see VfPointPlanner.HasPerPointShape).
            _core = profile.CoreOffsetMHz;
            _mem = profile.MemOffsetMHz;
            _lock = profile.LockedCoreClockMHz;
            if (_vfOffsets is not null)
            {
                Array.Fill(_vfOffsets, _core);
            }
        }

        return Notify(new ApplyResult(true, [KnobResult.Ok("core offset"), KnobResult.Ok("memory offset")]));
    }

    public ApplyResult ResetToDefaults() => Apply(new TuningProfile { Name = "defaults" });

    public KnobResult ForceUnlock()
    {
        lock (_gate)
        {
            _lock = null;
        }

        return Notify(KnobResult.Ok("clock lock", "released (explicit)"));
    }

    public NvmlReturn RestoreTuningLock(uint lockMHz)
    {
        lock (_gate)
        {
            _lock = lockMHz;
        }

        return Notify(NvmlReturn.Success);
    }

    public NvmlReturn LockClockForProbe(uint clockMHz) => Notify(NvmlReturn.Success);

    public NvmlReturn SetAllFansRaw(uint dutyPct) => NvmlReturn.NotSupported;

    public NvmlReturn SetFanRaw(uint coolerId, uint dutyPct) => NvmlReturn.NotSupported;

    public NvmlReturn RestoreAutoFansRaw() => NvmlReturn.NotSupported;

    public NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points)
    {
        points = [];
        lock (_gate)
        {
            if (_vfOffsets is null)
            {
                return NvapiStatus.NotSupported;
            }

            if (VfReadFails)
            {
                return NvapiStatus.Error;
            }

            points = _vfOffsets
                .Select((offset, i) => new NvapiGpu.VfpTablePoint(i, 700 + (i * 50), 1500 + (i * 150), offset))
                .ToList();
            return NvapiStatus.Ok;
        }
    }

    public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex)
    {
        lock (_gate)
        {
            foreach (var (slot, offset) in offsetsMHzByIndex)
            {
                _vfOffsets![slot] = offset;
            }
        }

        return Notify(KnobResult.Ok("V/F points"));
    }

    public KnobResult ClearVfPointOffsets()
    {
        lock (_gate)
        {
            if (_vfOffsets is not null)
            {
                Array.Fill(_vfOffsets, _core);
            }
        }

        return Notify(KnobResult.Ok("V/F points"));
    }

    /// <summary>Changes the card as another process would: no notification here.</summary>
    public void SetStateSilently(int core, int mem, uint? lockMHz = null)
    {
        lock (_gate)
        {
            _core = core;
            _mem = mem;
            _lock = lockMHz;
            if (_vfOffsets is not null)
            {
                Array.Fill(_vfOffsets, core);
            }
        }
    }

    private T Notify<T>(T result)
    {
        WriteNotification.Raise(StateWritten, this);
        return result;
    }
}
