using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// A tuner that holds core/memory offsets in memory and announces every write
/// the way the real tuners do: <see cref="StateWritten"/> on the writing thread,
/// after the write. Writes made from "elsewhere" are modelled two ways — a
/// write through this tuner from another thread (a page, a hotkey, a game rule),
/// which raises the event, and <see cref="ChangeSilently"/>, a change no event
/// reports (another process, a driver-side reset).
/// </summary>
internal sealed class WriteTrackingTuner : IGpuTuner
{
    private readonly Lock _lock = new();
    private readonly List<(TuningProfile Profile, int ThreadId)> _applies = [];
    private int _core;
    private int _mem;

    public TuningCapabilities Capabilities { get; init; } = new()
    {
        SupportsCoreOffset = true,
        CoreOffsetMinMHz = -500,
        CoreOffsetMaxMHz = 500,
        SupportsMemOffset = true,
        MemOffsetMinMHz = -1000,
        MemOffsetMaxMHz = 3000,
    };

    public string? GpuUuid { get; init; } = "GPU-TEST-0000";

    public uint? AppliedLockMHz => null;

    public event Action<IGpuTuner>? StateWritten;

    /// <summary>Every profile applied, in order, with the thread that applied it.</summary>
    public IReadOnlyList<(TuningProfile Profile, int ThreadId)> Applies
    {
        get
        {
            lock (_lock)
            {
                return [.. _applies];
            }
        }
    }

    public int ResetCalls { get; private set; }

    public int CoreOffsetMHz
    {
        get
        {
            lock (_lock)
            {
                return _core;
            }
        }
    }

    public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz)
        ReadCurrent()
    {
        lock (_lock)
        {
            return (_core, _mem, 250, null, null);
        }
    }

    public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false)
    {
        lock (_lock)
        {
            _core = Math.Clamp(profile.CoreOffsetMHz, Capabilities.CoreOffsetMinMHz, Capabilities.CoreOffsetMaxMHz);
            _mem = Math.Clamp(profile.MemOffsetMHz, Capabilities.MemOffsetMinMHz, Capabilities.MemOffsetMaxMHz);
            _applies.Add((profile, Environment.CurrentManagedThreadId));
        }

        StateWritten?.Invoke(this);
        return new ApplyResult(true, [KnobResult.Ok("core offset"), KnobResult.Ok("memory offset")]);
    }

    public ApplyResult ResetToDefaults()
    {
        lock (_lock)
        {
            _core = 0;
            _mem = 0;
            ResetCalls++;
        }

        StateWritten?.Invoke(this);
        return new ApplyResult(true, [KnobResult.Ok("core offset"), KnobResult.Ok("memory offset")]);
    }

    /// <summary>
    /// Another Afterglow surface writing: runs <paramref name="write"/> on a
    /// thread of its own and waits for it, so the event arrives on that thread.
    /// </summary>
    public void WriteFromElsewhere(Action<WriteTrackingTuner> write)
    {
        var other = new Thread(() => write(this)) { IsBackground = true };
        other.Start();
        other.Join();
    }

    /// <summary>A change no in-process event reports: another process, or the driver itself.</summary>
    public void ChangeSilently(int coreOffsetMHz)
    {
        lock (_lock)
        {
            _core = coreOffsetMHz;
        }
    }

    public KnobResult ForceUnlock() => KnobResult.Ok("clock lock");

    public NvmlReturn RestoreTuningLock(uint lockMHz) => NvmlReturn.NotSupported;

    public NvmlReturn LockClockForProbe(uint clockMHz) => NvmlReturn.NotSupported;

    public NvmlReturn SetAllFansRaw(uint dutyPct) => NvmlReturn.NotSupported;

    public NvmlReturn SetFanRaw(uint coolerId, uint dutyPct) => NvmlReturn.NotSupported;

    public NvmlReturn RestoreAutoFansRaw() => NvmlReturn.NotSupported;

    public NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points)
    {
        points = [];
        return NvapiStatus.NotSupported;
    }

    public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex) =>
        KnobResult.Fail("V/F points", "not supported by the fake");

    public KnobResult ClearVfPointOffsets() => KnobResult.Fail("V/F points", "not supported by the fake");
}
