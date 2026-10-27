namespace Afterglow.Core.Interop.Nvml;

/// <summary>
/// The part of <see cref="NvmlDevice"/> the NVIDIA tuner drives, and nothing
/// more. <see cref="NvmlDevice"/> is the only production implementation; the
/// interface exists so <see cref="Tuning.GpuTuner"/> can be exercised end to
/// end against a fake driver in tests. The real device calls native exports,
/// which left the tuner's own sequencing (which knob is written, in what
/// order, and what is recorded around it) testable only on a live card.
/// Signatures match <see cref="NvmlDevice"/> exactly.
/// </summary>
internal interface INvmlTuningDevice
{
    string? GetUuid();

    NvmlReturn TryGetClockOffset(NvmlClockType type, out NvmlClockOffset offset);

    NvmlReturn TrySetClockOffset(NvmlClockType type, int offsetMHz);

    NvmlReturn TryGetMaxClock(NvmlClockType type, out uint mhz);

    NvmlReturn TryGetEnforcedPowerLimit(out uint milliwatts);

    NvmlReturn TryGetPowerLimitConstraints(out uint minMw, out uint maxMw);

    NvmlReturn TryGetDefaultPowerLimit(out uint milliwatts);

    NvmlReturn TrySetPowerLimit(uint milliwatts);

    NvmlReturn TryGetNumFans(out uint numFans);

    NvmlReturn TryGetMinMaxFanSpeed(out uint minPercent, out uint maxPercent);

    NvmlReturn TrySetFanSpeed(uint fan, uint percent);

    NvmlReturn TrySetDefaultFanSpeed(uint fan);

    NvmlReturn TrySetGpuLockedClocks(uint minMhz, uint maxMhz);

    NvmlReturn TryResetGpuLockedClocks();
}
