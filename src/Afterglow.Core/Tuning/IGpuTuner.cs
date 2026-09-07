using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tuning;

/// <summary>
/// The tuning surface every vendor implements. Extracted verbatim from
/// <see cref="GpuTuner"/> so the NVIDIA implementation keeps its exact
/// signatures and semantics (they cannot be regression-tested on non-NVIDIA
/// hardware); consumers gate every knob on <see cref="Capabilities"/>, which
/// each implementation fills by probing its driver live — never by generation
/// or marketing name.
///
/// The lock/fan members return <see cref="NvmlReturn"/> and the V/F members
/// use NVAPI types because those are the signatures the entire app was built
/// against; non-NVIDIA tuners answer them with the honest
/// <see cref="NvmlReturn.NotSupported"/> / <see cref="NvapiStatus.NotSupported"/>
/// values, and the UI never reaches them when the corresponding capability
/// reads false.
/// </summary>
public interface IGpuTuner
{
    TuningCapabilities Capabilities { get; }

    /// <summary>Stable identity profiles and applied state are stamped with.</summary>
    string? GpuUuid { get; }

    /// <summary>
    /// The clock lock Afterglow APPLIED — never a ceiling merely observed from
    /// the driver (NVML has no getter; IGCL does, see <see cref="LockIsDriverReadable"/>,
    /// and there an observed factory ceiling reads as null here). The probe
    /// restores this value after a sweep, so it must be something Afterglow put on.
    /// </summary>
    uint? AppliedLockMHz { get; }

    /// <summary>
    /// True when <c>ReadCurrent().LockedCoreClockMHz</c> comes from a real driver
    /// readback rather than an in-process shadow. Only a driver-backed value can
    /// witness a change made from outside this process — a reset by an
    /// automation rule, a TDR recovery, or another tool — so callers that need
    /// to detect such a change must not rely on the lock unless this is true.
    /// False on NVML (no locked-clock getter exists); true on IGCL.
    /// </summary>
    bool LockIsDriverReadable => false;

    /// <summary>
    /// The key this card's applied-state record is filed under: its UUID, or
    /// the index fallback when the driver reports none. Every writer for the
    /// card — the tuner, its fan service, a probe pinning it — uses this one
    /// key, so the record has one home and a verified lock release from any
    /// process resolves the pin a probe recorded.
    /// </summary>
    string RecordKey => GpuUuid ?? throw new InvalidOperationException("this tuner has no record key");

    /// <summary>
    /// The highest clock a lock can be pinned at: the domain maximum, or on a
    /// driver that reads the range back, the released ceiling once observed —
    /// a factory ceiling below the domain maximum cannot be exceeded, and a
    /// sweep that tries is refused at its last target.
    /// </summary>
    uint MaxLockableClockMHz => Capabilities.MaxCoreClockMHz;

    /// <summary>
    /// The currently applied values. PowerLimitW is null when the device has
    /// no readable power limit (the NVIDIA tuner always reads one back).
    /// LockedCoreClockMHz is an OBSERVATION — on a driver with a readback
    /// getter it is whatever ceiling the driver reports, factory or foreign
    /// clamp included — and must never be fed back into a profile as the lock
    /// to preserve; that is <see cref="AppliedLockMHz"/>. Carrying the observed
    /// value forward wrote an Arc factory ceiling back as a clamp with written
    /// provenance, which no release could then adopt.
    /// </summary>
    (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz) ReadCurrent();

    /// <summary>
    /// Applies a profile. With <paramref name="releaseLock"/> the clock lock is
    /// released whether or not this session tracks one — an explicit
    /// <c>--lock-clock off</c> or MCP <c>unlock</c> is one operation with one
    /// verdict, reported as the "clock lock" knob, instead of a front-end
    /// releasing first and reconciling that against Apply's own release.
    /// </summary>
    ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false);

    ApplyResult ResetToDefaults();

    KnobResult ForceUnlock();

    /// <summary>Range lock (idle downclock still allowed) — the probe-restore path.</summary>
    NvmlReturn RestoreTuningLock(uint lockMHz);

    /// <summary>Exact pin, required for V/F probing.</summary>
    NvmlReturn LockClockForProbe(uint clockMHz);

    NvmlReturn SetAllFansRaw(uint dutyPct);

    NvmlReturn SetFanRaw(uint coolerId, uint dutyPct);

    NvmlReturn RestoreAutoFansRaw();

    NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points);

    KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex);

    KnobResult ClearVfPointOffsets();
}
