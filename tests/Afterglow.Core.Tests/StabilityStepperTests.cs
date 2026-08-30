using System.Diagnostics;
using Afterglow.Core.Stress;

namespace Afterglow.Core.Tests;

/// <summary>
/// The stepper's per-step burn wait. Stop has to end the step while it is
/// burning, not when the step's timer finally runs out — otherwise the GPU
/// keeps burning at the offset the user just asked to abandon.
/// </summary>
public class StabilityStepperWaitTests
{
    [Fact]
    public void Cancel_ends_the_step_instead_of_burning_out_its_duration()
    {
        using var done = new ManualResetEventSlim(false);
        var clock = Stopwatch.StartNew();
        bool Cancelled() => clock.Elapsed > TimeSpan.FromMilliseconds(100);

        // A five-minute step: the pre-fix wait blocked here for the whole 300 s.
        bool terminal = StabilityStepper.WaitForBurn(done, TimeSpan.FromMinutes(5), Cancelled);

        Assert.False(terminal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the cancelled step did not end promptly");
    }

    [Fact]
    public void An_uneventful_step_still_runs_for_its_full_duration()
    {
        using var done = new ManualResetEventSlim(false);
        var clock = Stopwatch.StartNew();

        bool terminal = StabilityStepper.WaitForBurn(done, TimeSpan.FromSeconds(1), () => false);

        Assert.False(terminal);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1), "the step ended before its duration");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the step overran its duration");
    }

    [Fact]
    public void A_terminal_burn_state_ends_the_step_and_reports_terminal()
    {
        using var done = new ManualResetEventSlim(false);
        var burn = new Thread(() =>
        {
            Thread.Sleep(100);
            done.Set();                 // artifact / device lost / failed
        })
        {
            IsBackground = true,
        };

        var clock = Stopwatch.StartNew();
        burn.Start();
        bool terminal = StabilityStepper.WaitForBurn(done, TimeSpan.FromMinutes(5), () => false);
        Assert.True(burn.Join(TimeSpan.FromSeconds(5)));

        Assert.True(terminal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the failed step did not end promptly");
    }

    [Fact]
    public void A_burn_that_already_failed_is_not_waited_on()
    {
        using var done = new ManualResetEventSlim(true);
        var clock = Stopwatch.StartNew();

        bool terminal = StabilityStepper.WaitForBurn(done, TimeSpan.FromMinutes(5), () => false);

        Assert.True(terminal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "an already-terminal burn should return at once");
    }
}