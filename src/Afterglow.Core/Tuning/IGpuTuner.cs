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

    /// <summary>Tracked clock lock (no driver getter exists on any vendor path).</summary>
    uint? AppliedLockMHz { get; }

    /// <summary>
    /// The currently applied values. PowerLimitW is null when the device has
    /// no readable power limit (the NVIDIA tuner always reads one back).
    /// </summary>
    (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz) ReadCurrent();

    ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true);

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
