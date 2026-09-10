using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tuning;

/// <summary>
/// What a V/F sweep starts from (see <see cref="IGpuTuner.BeginProbe"/>).
/// </summary>
/// <param name="MaxClockMHz">The highest clock the sweep may pin: the domain maximum, or the released ceiling once one has been verified.</param>
/// <param name="LockToRestoreMHz">The range lock this process had applied, which <see cref="IGpuTuner.EndProbe"/> puts back.</param>
/// <param name="ForeignClampNote">Set when a clamp this process had not applied was released before the sweep, for the outcome text.</param>
/// <param name="Refusal">Non-null when the card could not be prepared (a clamp that would not release); nothing was pinned.</param>
public sealed record ProbeStart(uint MaxClockMHz, uint? LockToRestoreMHz, string? ForeignClampNote, string? Refusal = null);

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
    /// and there an observed factory ceiling reads as null here), and never the
    /// V/F probe's exact pin: while a sweep runs, and after a pin the sweep
    /// could not release, this is still the range lock the pin displaced.
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
    /// verdict, reported as the "clock lock" knob. A profile that carries a
    /// lock AND asks for the release is refused as a "profile" failure before
    /// anything is written: two answers to one question are not resolved
    /// silently by any front-end.
    /// </summary>
    ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false);

    ApplyResult ResetToDefaults();

    KnobResult ForceUnlock();

    /// <summary>Range lock (idle downclock still allowed) — the form profiles apply.</summary>
    NvmlReturn RestoreTuningLock(uint lockMHz);

    /// <summary>Exact pin, required for V/F probing; only meaningful between <see cref="BeginProbe"/> and <see cref="EndProbe"/>.</summary>
    NvmlReturn LockClockForProbe(uint clockMHz);

    /// <summary>
    /// Prepares a V/F sweep: remembers the range lock this process applied (so
    /// <see cref="AppliedLockMHz"/> keeps answering with it while pins stand),
    /// releases whatever clamp is on the card so the sweep runs against the
    /// true ceiling, and reports the ceiling to sweep up to. The tuner owns
    /// this because only it can capture the displaced lock BEFORE the release
    /// empties its tracking — the probe capturing it from outside lost it.
    /// </summary>
    ProbeStart BeginProbe() => new(Capabilities.MaxCoreClockMHz, AppliedLockMHz, null);

    /// <summary>
    /// Ends a V/F sweep: puts back the lock <see cref="BeginProbe"/> remembered,
    /// or releases the pin when there was none (and does nothing when no pin
    /// ever landed). A failed verdict is the restore failure the probe reports.
    /// </summary>
    KnobResult EndProbe() => ForceUnlock();

    NvmlReturn SetAllFansRaw(uint dutyPct);

    NvmlReturn SetFanRaw(uint coolerId, uint dutyPct);

    NvmlReturn RestoreAutoFansRaw();

    NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points);

    KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex);

    KnobResult ClearVfPointOffsets();
}
