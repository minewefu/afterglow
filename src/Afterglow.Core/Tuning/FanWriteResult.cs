using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Core.Tuning;

/// <summary>
/// What an all-fans manual write left on the card: the driver's answer, and
/// whether any fan may still hold the duty it set. After a success every fan
/// does. After a refusal, none does when the write is all-or-nothing; only a
/// write that moved some fans and could not hand them back, or a tuner that
/// cannot say per fan, leaves <see cref="FansMayBeManual"/> true.
/// </summary>
/// <param name="Result">The driver's answer: Success only when every fan took the duty.</param>
/// <param name="FansMayBeManual">Whether a fan may be left at this write's duty.</param>
public readonly record struct FanWriteResult(NvmlReturn Result, bool FansMayBeManual)
{
    public bool Succeeded => Result == NvmlReturn.Success;

    /// <summary>
    /// The answer of a tuner that reports nothing per fan (<see cref="IGpuTuner.SetAllFansRaw"/>).
    /// NotSupported means no fan interface, so nothing moved (Arc answers every
    /// write that way without touching the card); any other refusal may have
    /// come after some fans took the duty, so it counts as possibly manual.
    /// </summary>
    public static FanWriteResult FromRaw(NvmlReturn rc) => new(rc, rc != NvmlReturn.NotSupported);

    /// <summary>An all-fans write through <see cref="IGpuTuner.SetAllFans"/>, whichever tuner it is.</summary>
    public static FanWriteResult SetAllFans(IGpuTuner tuner, uint dutyPct)
    {
        ArgumentNullException.ThrowIfNull(tuner);
        return tuner.SetAllFans(dutyPct);
    }
}
