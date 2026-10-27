namespace Afterglow.Core.Interop.Nvapi;

/// <summary>
/// The part of <see cref="NvapiGpu"/> the NVIDIA tuner drives (voltage boost,
/// temperature limit, the per-point V/F table, fan control). <see cref="NvapiGpu"/>
/// is the only production implementation; like <see cref="Nvml.INvmlTuningDevice"/>
/// the interface exists so <see cref="Tuning.GpuTuner"/> can run against a fake
/// driver in tests. Signatures match <see cref="NvapiGpu"/> exactly.
/// </summary>
internal interface INvapiTuningGpu
{
    NvapiStatus TryGetVoltageBoostPercent(out uint percent);

    NvapiStatus TrySetVoltageBoostPercent(uint percent);

    NvapiStatus TryGetTempLimitRange(out int minC, out int defaultC, out int maxC);

    NvapiStatus TryGetTempLimit(out int currentC);

    NvapiStatus TrySetTempLimit(int targetC);

    NvapiStatus TryGetVfpPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points);

    NvapiStatus TrySetVfpPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex);

    NvapiStatus TryClearVfpPointOffsets();

    NvapiStatus TrySetAllFans(uint dutyPercent);

    NvapiStatus TrySetFan(uint coolerId, uint dutyPercent);

    NvapiStatus TryRestoreAutoFans();
}
