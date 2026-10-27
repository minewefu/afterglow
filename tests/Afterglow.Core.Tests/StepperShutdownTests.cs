using Afterglow.Core.Stress;

namespace Afterglow.Core.Tests;

/// <summary>
/// Exiting while the stepper runs: the exit may only be recorded as a clean
/// shutdown once the card is back at the offset the run says it left, or the
/// next launch's crash banner never mentions an overclock nobody validated (F-27).
/// </summary>
public class StepperShutdownTests
{
    private static StepperStatus Final(string phase, int offset, bool running = false, bool? restored = null) =>
        new(running, phase, offset, LastGoodOffsetMHz: null, TimeSpan.Zero, TimeSpan.Zero, [], ResultOffsetMHz: null,
            StartOffsetRestored: restored);

    [Fact]
    public void No_run_in_flight_is_a_clean_exit()
    {
        Assert.True(StepperShutdown.LeftSettled(wasRunning: false, unwound: true, Final("idle", 0), liveCoreOffsetMHz: 180));
    }

    [Fact]
    public void A_cancelled_run_whose_restore_landed_is_a_clean_exit()
    {
        // Started at +0, cancelled while burning +180: the unwind put +0 back.
        Assert.True(StepperShutdown.LeftSettled(wasRunning: true, unwound: true, Final("cancelled", 0), liveCoreOffsetMHz: 0));
    }

    [Fact]
    public void A_run_still_unwinding_when_the_wait_ran_out_is_not()
    {
        // The background thread dies with the process; +180 may still be applied.
        Assert.False(StepperShutdown.LeftSettled(wasRunning: true, unwound: false, Final("stopping", 180, running: true), liveCoreOffsetMHz: 180));
    }

    [Fact]
    public void A_restore_the_driver_refused_is_not()
    {
        // After a TDR the driver can refuse the restoring apply; the run still
        // reports "cancelled" at the start offset, but the card reads +180.
        Assert.False(StepperShutdown.LeftSettled(wasRunning: true, unwound: true, Final("cancelled", 0), liveCoreOffsetMHz: 180));
    }

    [Fact]
    public void A_refused_restore_the_run_reports_honestly_is_not()
    {
        // Since 1.3 the stepper publishes where the card is left when the driver
        // refuses the restore: +180 with StartOffsetRestored false. The readback
        // agrees with that status, which must not make the exit clean.
        Assert.False(StepperShutdown.LeftSettled(
            wasRunning: true, unwound: true, Final("cancelled", 180, restored: false), liveCoreOffsetMHz: 180));
        Assert.False(StepperShutdown.LeftSettled(
            wasRunning: true, unwound: true, Final("failed", 240, restored: false), liveCoreOffsetMHz: 240));
    }

    [Fact]
    public void A_restore_the_run_reports_as_landed_and_the_card_confirms_is_clean()
    {
        Assert.True(StepperShutdown.LeftSettled(
            wasRunning: true, unwound: true, Final("cancelled", 0, restored: true), liveCoreOffsetMHz: 0));
    }

    [Fact]
    public void An_unreadable_offset_is_not_evidence_of_a_restore()
    {
        Assert.False(StepperShutdown.LeftSettled(wasRunning: true, unwound: true, Final("cancelled", 0), liveCoreOffsetMHz: null));
    }

    [Fact]
    public void An_interrupted_run_is_settled_only_when_the_other_writer_moved_the_card()
    {
        // R-02: another writer reset the card mid-run and the stepper wrote
        // nothing after it — the card is the other writer's, at +0.
        Assert.True(StepperShutdown.LeftSettled(
            wasRunning: true, unwound: true, Final("interrupted", 0), liveCoreOffsetMHz: 0));

        // The other write did not move the core offset: the run's own +180 is
        // still applied and nothing restored it.
        Assert.False(StepperShutdown.LeftSettled(
            wasRunning: true, unwound: true, Final("interrupted", 180, restored: false), liveCoreOffsetMHz: 180));
    }

    [Fact]
    public void A_run_that_finished_at_its_result_during_exit_is_a_clean_exit()
    {
        Assert.True(StepperShutdown.LeftSettled(wasRunning: true, unwound: true, Final("done", 120), liveCoreOffsetMHz: 120));
    }
}
