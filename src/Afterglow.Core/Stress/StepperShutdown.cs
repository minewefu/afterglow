namespace Afterglow.Core.Stress;

/// <summary>
/// Whether a stepper run that was in flight at app exit left the card where it
/// meant to — the condition for recording the exit as a clean shutdown (F-27).
///
/// Exit cancels the run and waits, bounded, for it to unwind; the unwind puts
/// back the core offset the run started from. Two outcomes still leave the
/// offset under test on the card: the wait runs out (the run thread is a
/// background thread and dies with the process mid-unwind), or the restoring
/// apply fails (the driver refusing calls after a TDR — the stepper ignores
/// that result and still reports "cancelled"). Marking either one clean
/// would hide an unvalidated overclock from the next launch's crash banner,
/// so the exit is only clean when the card reads back the offset the run
/// says it left — and the run did not itself report a refused restore. Since
/// 1.3 the stepper publishes where the card actually is when a restore is
/// refused (the offset under test, with StartOffsetRestored false), so the
/// readback alone would then agree with the status and pass.
/// <para>
/// A run interrupted by another writer (R-02) writes nothing afterwards, so it
/// restores nothing either. It publishes the offset it found on the card, with
/// StartOffsetRestored false when that is still the run's own offset — the same
/// two rules then decide, with no case of their own.
/// </para>
/// </summary>
public static class StepperShutdown
{
    /// <param name="wasRunning">A run was in flight (or unwinding) when exit began.</param>
    /// <param name="unwound">The run thread finished within the shutdown wait.</param>
    /// <param name="finalStatus">The run's last published status.</param>
    /// <param name="liveCoreOffsetMHz">The card's core offset read back after the wait; null when unreadable.</param>
    public static bool LeftSettled(bool wasRunning, bool unwound, StepperStatus finalStatus, int? liveCoreOffsetMHz)
    {
        ArgumentNullException.ThrowIfNull(finalStatus);
        if (!wasRunning)
        {
            return true;
        }

        if (!unwound || finalStatus.Running)
        {
            return false;
        }

        // A run that ended anywhere but "done" was meant to put the starting
        // offset back; if it says that did not land, the offset under test is
        // still on the card however consistent the readback looks.
        if (finalStatus.StartOffsetRestored == false && finalStatus.Phase != "done")
        {
            return false;
        }

        return liveCoreOffsetMHz is int live && live == finalStatus.CurrentOffsetMHz;
    }
}
