using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Stress;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;
using static Afterglow.Core.Tests.Fakes.ArcScenario;

namespace Afterglow.Core.Tests;

/// <summary>
/// The V/F probe's clock-lock choreography — release before, pin each step,
/// restore, record — run end to end against the real <see cref="ArcGpuTuner"/>
/// on an in-memory frequency domain with a load engine that runs nothing.
/// </summary>
[Collection("AppPaths")]
public sealed class VfProbeScenarioTests : IDisposable
{
    private readonly StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    private static VfCurveProbe Probe(ArcGpuTuner tuner, FakeArcDevice device, FakeProbeLoad? load = null) =>
        new(tuner, () => new GpuSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            DeviceIndex = 0,
            CoreClockMHz = (uint)device.Max,
            CoreVoltageMv = 700 + device.Max / 10,
            GpuUtilPct = 95,
        })
        {
            SettleSeconds = 0,
            SampleSeconds = 0.05,
            SampleIntervalMs = 5,
            StepMHz = 500,
            LoadFactory = () => load ?? new FakeProbeLoad(),
        };

    private static VfProbeProgress RunToEnd(VfCurveProbe probe)
    {
        VfProbeProgress? terminal = null;
        probe.ProgressChanged += p =>
        {
            if (!p.Running)
            {
                terminal = p;
            }
        };
        probe.Run(new VfCurveRecorder());
        Assert.NotNull(terminal);
        return terminal!;
    }

    [Fact]
    public void A_full_sweep_pins_every_target_and_leaves_the_card_at_factory()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);

        var final = RunToEnd(Probe(tuner, dev));

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.Equal(5, final.StepCount);
        Assert.Equal(final.StepCount, final.StepIndex);
        Assert.False(final.RestoreFailed, final.Phase);
        Assert.Null(final.LoadFailure);
        Assert.Equal(2300d, dev.PinnedTargets.Max());
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.AppliedLockMHz);
        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void A_factory_ceiling_card_sweeps_up_to_its_ceiling_and_restores_cleanly()
    {
        var dev = new FakeArcDevice { FactoryMax = 2200 };
        var tuner = Tuner(dev);

        var final = RunToEnd(Probe(tuner, dev));

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.False(final.RestoreFailed, final.Phase);
        Assert.Equal(2200d, dev.PinnedTargets.Max());
        Assert.Equal((100d, 2200d), (dev.Min, dev.Max));
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_users_range_lock_is_restored_after_the_sweep()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var applied = tuner.Apply(Lock(1800));
        Assert.True(applied.AllSucceeded, Describe(applied));

        var final = RunToEnd(Probe(tuner, dev));

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.False(final.RestoreFailed, final.Phase);
        Assert.Equal((100d, 1800d), (dev.Min, dev.Max));
        Assert.Equal(1800u, tuner.AppliedLockMHz);
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_hardware_verdict_during_teardown_is_reported_as_a_load_failure()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);

        var final = RunToEnd(Probe(tuner, dev, new FakeProbeLoad { TeardownState = StressState.ArtifactDetected }));

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.NotNull(final.LoadFailure);
        Assert.Contains("miscalculated", final.LoadFailure, StringComparison.Ordinal);
        Assert.False(final.RestoreFailed, final.Phase);
    }

    [Fact]
    public void A_clamp_this_process_did_not_apply_is_released_first_and_reported()
    {
        var dev = new FakeArcDevice { Max = 1500 }; // another tool's clamp
        var tuner = Tuner(dev);

        var final = RunToEnd(Probe(tuner, dev));

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.Contains("not applied", final.Phase, StringComparison.Ordinal);
        Assert.Equal(2300d, dev.PinnedTargets.Max());
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
    }

    [Fact]
    public void A_leftover_pin_from_a_dead_session_gets_a_full_sweep_not_a_two_point_curve()
    {
        var dev = new FakeArcDevice { Min = 1500, Max = 1500 };
        var tuner = Tuner(dev);

        var final = RunToEnd(Probe(tuner, dev));

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.Equal(5, final.StepCount);
        Assert.Equal(2300d, dev.PinnedTargets.Max());
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.False(final.RestoreFailed, final.Phase);
    }

    [Fact]
    public void A_failed_restore_is_recorded_and_a_later_verified_release_resolves_it()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var probe = Probe(tuner, dev);
        probe.ProgressChanged += p =>
        {
            if (p.Running && p.Phase == "measured")
            {
                dev.RestoreResult = CtlResult.ErrorUnknown; // the driver stops taking restores mid-sweep
            }
        };

        var final = RunToEnd(probe);

        Assert.True(final.RestoreFailed, final.Phase);
        Assert.Contains("WARNING", final.Phase, StringComparison.Ordinal);
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);

        dev.RestoreResult = CtlResult.Success;
        Assert.True(tuner.ForceUnlock().Applied);
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_load_that_cannot_run_aborts_before_anything_is_pinned()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);

        var final = RunToEnd(Probe(tuner, dev, new FakeProbeLoad { FailOnStart = true }));

        Assert.Equal(VfProbeOutcome.Aborted, final.Outcome);
        Assert.Contains("load could not run", final.Phase, StringComparison.Ordinal);
        Assert.Empty(dev.PinnedTargets);
        Assert.False(final.RestoreFailed);
        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void Cancelling_mid_sweep_restores_the_clock()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var probe = Probe(tuner, dev);
        probe.SampleSeconds = 5;
        VfProbeProgress? terminal = null;
        probe.ProgressChanged += p =>
        {
            if (!p.Running)
            {
                terminal = p;
            }
        };

        probe.Start(new VfCurveRecorder());
        Assert.True(dev.PinLanded.Wait(TimeSpan.FromSeconds(10)), "the probe never pinned");
        Assert.True(probe.CancelAndWait(TimeSpan.FromSeconds(10)), "the worker did not stop");

        Assert.NotNull(terminal);
        Assert.Equal(VfProbeOutcome.Cancelled, terminal!.Outcome);
        Assert.False(terminal.RestoreFailed, terminal.Phase);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void A_refused_pin_aborts_with_the_drivers_reason_and_no_record()
    {
        var dev = new FakeArcDevice
        {
            WriteResult = CtlResult.ErrorInsufficientPermissions,
            RestoreResult = CtlResult.ErrorInsufficientPermissions,
        };
        var tuner = Tuner(dev);

        var final = RunToEnd(Probe(tuner, dev));

        Assert.Equal(VfProbeOutcome.Aborted, final.Outcome);
        Assert.Contains("administrator", final.Phase, StringComparison.Ordinal);
        Assert.False(final.RestoreFailed, final.Phase);
        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void A_pin_left_by_a_failed_release_is_not_restored_as_a_lock_by_the_next_sweep()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var first = Probe(tuner, dev);
        first.ProgressChanged += p =>
        {
            if (p.Running && p.Phase == "measured")
            {
                dev.RestoreResult = CtlResult.ErrorUnknown;
            }
        };
        Assert.True(RunToEnd(first).RestoreFailed);
        Assert.Null(tuner.AppliedLockMHz);

        dev.RestoreResult = CtlResult.Success;
        var second = RunToEnd(Probe(tuner, dev));

        Assert.Equal(VfProbeOutcome.Completed, second.Outcome);
        Assert.False(second.RestoreFailed, second.Phase);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.AppliedLockMHz);
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_load_engine_that_will_not_stop_is_an_unknown_not_an_instability_verdict()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);

        var final = RunToEnd(Probe(tuner, dev, new FakeProbeLoad { HangOnStop = true }));

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.Null(final.LoadFailure);
        Assert.Contains("did not stop", final.Phase, StringComparison.Ordinal);
    }

    [Fact]
    public void A_written_lock_at_the_factory_ceiling_still_gets_a_sweep()
    {
        // The lock sits exactly at the ceiling a fresh process has never seen
        // released, so its release cannot be verified in this process. The
        // sweep must not be refused for that: it runs under the lock and puts
        // it back.
        var dev = new FakeArcDevice { FactoryMax = 2250 };
        var tuner = Tuner(dev);
        var applied = tuner.Apply(Lock(2250));
        Assert.True(applied.AllSucceeded, Describe(applied));

        var final = RunToEnd(Probe(tuner, dev));

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.False(final.RestoreFailed, final.Phase);
        Assert.Equal((100d, 2250d), (dev.Min, dev.Max));
        Assert.Equal(2250u, tuner.AppliedLockMHz);
    }

    [Fact]
    public void A_pin_is_on_record_from_the_moment_it_lands_until_the_release_verifies()
    {
        // A process killed anywhere in the sweep — or a shutdown whose join
        // timed out — leaves the truthful record behind without any front-end
        // composing it.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var probe = Probe(tuner, dev);
        probe.SampleSeconds = 5;
        Assert.Null(AppliedStateStore.Load(Uuid));

        probe.Start(new VfCurveRecorder());
        Assert.True(dev.PinLanded.Wait(TimeSpan.FromSeconds(10)), "the probe never pinned");
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);

        Assert.True(probe.CancelAndWait(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }
}
