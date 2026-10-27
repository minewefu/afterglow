using Afterglow.Core.Profiles;
using Afterglow.Core.Stress;
using Afterglow.Core.Tests.Fakes;

namespace Afterglow.Core.Tests;

/// <summary>
/// The stepper against a fake tuner and scripted burns (R-02): a step whose
/// configuration was changed underneath it is never scored, and once another
/// writer has acted the stepper writes nothing more.
/// </summary>
public class StepperOwnershipTests
{
    /// <summary>Zero-second steps: each burn ends on the stepper's first 500 ms poll.</summary>
    private static readonly StepperOptions Sweep = new()
    {
        StepMHz = 30,
        SecondsPerStep = 0,
        MaxOffsetMHz = 90,
        ConfirmSeconds = 0,
    };

    private static StepperStatus Run(StabilityStepper stepper)
    {
        StepperStatus? final = null;
        using var done = new ManualResetEventSlim(false);
        stepper.StatusChanged += status =>
        {
            if (!status.Running)
            {
                final = status;
                done.Set();
            }
        };

        stepper.Start(Sweep);
        Assert.True(done.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken),
            "the stepper never reported a terminal status");
        Assert.True(stepper.CancelAndWait(TimeSpan.FromSeconds(30)), "the run thread did not end");
        return final!;
    }

    /// <summary>A stepper whose burn number <paramref name="burn"/> (0-based) runs <paramref name="during"/>.</summary>
    private static StabilityStepper Stepper(WriteTrackingTuner tuner, int burn = -1, Action? during = null)
    {
        int count = 0;
        return new StabilityStepper(tuner)
        {
            LoadFactory = () => new ScriptedBurn { OnStart = count++ == burn ? during : null },
        };
    }

    [Fact]
    public void The_stepper_does_not_mistake_its_own_writes_for_another_writer()
    {
        var tuner = new WriteTrackingTuner();

        var final = Run(Stepper(tuner));

        Assert.Equal("done", final.Phase);
        Assert.Equal(90, final.ResultOffsetMHz);
        Assert.Equal(90, tuner.CoreOffsetMHz);
    }

    [Fact]
    public void A_reset_from_elsewhere_mid_burn_ends_the_sweep_as_interrupted_and_writes_nothing_more()
    {
        // The review's harness: the device is reset while +30 burns. The
        // stepper scored +30 as passing on evidence gathered at stock, walked
        // on, and finally re-applied an offset over the user's reset.
        var tuner = new WriteTrackingTuner();
        int appliesBefore = -1;
        var stepper = Stepper(tuner, burn: 1, during: () =>
        {
            appliesBefore = tuner.Applies.Count;
            tuner.WriteFromElsewhere(t => t.ResetToDefaults());
        });

        var final = Run(stepper);

        Assert.Equal("interrupted", final.Phase);
        Assert.Null(final.ResultOffsetMHz);

        // +30 was never scored, and nothing was written after the reset — not
        // a next step, not a closing re-write, not a "restore".
        Assert.DoesNotContain(final.Log, line => line.Contains("+30 MHz passed", StringComparison.Ordinal));
        Assert.Equal(appliesBefore, tuner.Applies.Count);
        Assert.Equal(0, tuner.CoreOffsetMHz);
        Assert.Equal(0, final.CurrentOffsetMHz);

        // The card is where the other writer left it, not at an offset under
        // test, so nothing claims a failed restore.
        Assert.NotEqual(false, final.StartOffsetRestored);
    }

    [Fact]
    public void A_change_no_event_reports_is_caught_by_the_readback_and_not_scored()
    {
        // Another process moves the offset to +5 while +30 burns. Nothing in this
        // process hears about it; the readback against what the step applied does.
        var tuner = new WriteTrackingTuner();
        int appliesBefore = -1;
        var stepper = Stepper(tuner, burn: 1, during: () =>
        {
            appliesBefore = tuner.Applies.Count;
            tuner.ChangeSilently(5);
        });

        var final = Run(stepper);

        Assert.Equal("interrupted", final.Phase);
        Assert.Null(final.ResultOffsetMHz);
        Assert.DoesNotContain(final.Log, line => line.Contains("+30 MHz passed", StringComparison.Ordinal));
        Assert.Equal(appliesBefore, tuner.Applies.Count);
        Assert.Equal(5, final.CurrentOffsetMHz);
    }

    [Fact]
    public void A_write_from_elsewhere_that_leaves_the_offset_under_test_says_so()
    {
        // A write that did not move the core offset (a memory-only apply) still
        // ends the run — the configuration under test changed — and since the
        // card is left at the untested +30, the status must not read as settled.
        var tuner = new WriteTrackingTuner();
        var stepper = Stepper(tuner, burn: 1, during: () =>
            tuner.WriteFromElsewhere(t => t.Apply(new TuningProfile { Name = "mem", CoreOffsetMHz = 30, MemOffsetMHz = 800 })));

        var final = Run(stepper);

        Assert.Equal("interrupted", final.Phase);
        Assert.Equal(30, final.CurrentOffsetMHz);
        Assert.False(final.StartOffsetRestored);
        Assert.False(StepperShutdown.LeftSettled(wasRunning: true, unwound: true, final, liveCoreOffsetMHz: 30));
    }
}
