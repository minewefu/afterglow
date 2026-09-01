using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tuning;

/// <summary>
/// Intel Arc tuner. In this beta it implements no write paths, so every
/// capability reads false and the UI renders its existing honest "not
/// supported" states — monitoring works, tuning does not claim to. This is
/// deliberate: a capability flag here means "Afterglow can drive this knob on
/// this device, verified by readback", not "the driver has an entry point".
/// The write paths (frequency clamps first — the one knob the verified
/// OneXPlayer 3 driver reports as controllable — then OC knobs where discrete
/// Arc drivers answer) arrive in a later milestone, each probed live via
/// <see cref="IgclDevice.TryGetOcProperties"/> and verified before its flag
/// flips true.
/// </summary>
public sealed class ArcGpuTuner : IGpuTuner
{
    private readonly IgclDevice _device;

    public ArcGpuTuner(IgclDevice device, string? gpuUuid)
    {
        _device = device;
        GpuUuid = gpuUuid;
        Capabilities = new TuningCapabilities
        {
            SupportsCoreOffset = false,
            SupportsMemOffset = false,
            SupportsPowerLimit = false,
            SupportsLockedCoreClock = false,
            SupportsFanControl = false,
            SupportsVoltageBoost = false,
            SupportsTempLimit = false,
            SupportsVfPoints = false,
        };
    }

    public TuningCapabilities Capabilities { get; }

    public string? GpuUuid { get; }

    public uint? AppliedLockMHz => null;

    /// <summary>The IGCL device, for the milestone that adds write paths.</summary>
    public IgclDevice Device => _device;

    public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz) ReadCurrent() =>
        (0, 0, null, null, null);

    public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true)
    {
        return new ApplyResult(false, [
            KnobResult.Fail("tuning", "not implemented for Intel GPUs in this beta — monitoring only"),
        ]);
    }

    public ApplyResult ResetToDefaults()
    {
        // Nothing can have been applied through Afterglow, so there is nothing
        // to reset — report success so the panic path doesn't alarm falsely.
        return new ApplyResult(true, [
            KnobResult.Ok("reset", "no tuning paths on this Intel GPU in this beta; nothing was applied"),
        ]);
    }

    public KnobResult ForceUnlock() =>
        KnobResult.Fail("clock lock", "not supported on Intel GPUs in this beta");

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
        KnobResult.Fail("V/F points", "not supported on Intel GPUs");

    public KnobResult ClearVfPointOffsets() =>
        KnobResult.Fail("V/F points", "not supported on Intel GPUs");
}
