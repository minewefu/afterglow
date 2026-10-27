using Afterglow.Core.Telemetry;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;
using static Afterglow.Core.Tests.Fakes.ArcScenario;

namespace Afterglow.Core.Tests;

/// <summary>
/// The V/F probe against the real <see cref="ArcGpuTuner"/> on an in-memory
/// frequency domain: a write it did not make stops the sweep without another
/// pin (R-02), and a finished step has enough samples in its voltage bin for
/// PlanUndervolt — or says that it has not (R-47). Kept apart from
/// VfProbeScenarioTests, which covers the clock-lock choreography itself.
/// </summary>
[Collection("AppPaths")]
public sealed class VfProbeInterruptionAndCoverageTests : IDisposable
{
    private readonly StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    /// <summary>The voltage the fake GPU runs at each pinned clock: distinct bins per step.</summary>
    private static double VoltageAt(double clockMHz) => 700 + (clockMHz / 10);

    private static GpuSnapshot Reading(FakeArcDevice device, uint load = 95) => new()
    {
        Timestamp = DateTimeOffset.Now,
        DeviceIndex = 0,
        CoreClockMHz = (uint)device.Max,
        CoreVoltageMv = VoltageAt(device.Max),
        GpuUtilPct = load,
    };

    private static VfCurveProbe Probe(ArcGpuTuner tuner, Func<GpuSnapshot> sample) =>
        new(tuner, sample)
        {
            SettleSeconds = 0,
            SampleSeconds = 0.01,
            SampleIntervalMs = 2,
            StepMHz = 500, // 600, 1100, 1600, 2100, 2300 on the default fake
            LoadFactory = () => new FakeProbeLoad(),
        };

    private static VfProbeProgress RunToEnd(VfCurveProbe probe, VfCurveRecorder? recorder = null)
    {
        VfProbeProgress? terminal = null;
        probe.ProgressChanged += p =>
        {
            if (!p.Running)
            {
                terminal = p;
            }
        };
        probe.Run(recorder ?? new VfCurveRecorder());
        Assert.NotNull(terminal);
        return terminal!;
    }

    // --- R-02: a write the probe did not make ------------------------------------

    [Fact]
    public void A_reset_mid_sweep_stops_it_without_pinning_the_clock_again()
    {
        // The Tuning-page Reset, synchronously from inside the sweep: before
        // the fix the probe went on to pin 1100, 1600, 2100 and 2300 MHz.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var probe = Probe(tuner, () => Reading(dev));
        bool reset = false;
        probe.ProgressChanged += p =>
        {
            if (p.Running && p.Phase == "measured" && !reset)
            {
                reset = true;
                Assert.True(tuner.ResetToDefaults().AllSucceeded);
            }
        };

        var final = RunToEnd(probe);

        Assert.Equal(VfProbeOutcome.Interrupted, final.Outcome);
        Assert.Equal(600d, Assert.Single(dev.PinnedTargets));
        Assert.Equal(1, final.StepIndex);
        Assert.Contains("did not pin the clock again", final.Phase, StringComparison.Ordinal);
        Assert.False(final.RestoreFailed, final.Phase);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.AppliedLockMHz);
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_reset_from_another_thread_stops_the_sweep_on_its_own()
    {
        // The app's shape: the probe on its worker, Reset on the UI thread. The
        // step holds for 5 s; the sweep must end promptly WITHOUT a Cancel.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var probe = Probe(tuner, () => Reading(dev));
        probe.SampleSeconds = 5;
        using var finished = new ManualResetEventSlim();
        VfProbeProgress? terminal = null;
        probe.ProgressChanged += p =>
        {
            if (!p.Running)
            {
                terminal = p;
                finished.Set();
            }
        };

        probe.Start(new VfCurveRecorder());
        try
        {
            Assert.True(dev.PinLanded.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the probe never pinned");
            Assert.True(tuner.ResetToDefaults().AllSucceeded);
            Assert.True(finished.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken),
                "the probe kept sweeping after the reset");
        }
        finally
        {
            Assert.True(probe.CancelAndWait(TimeSpan.FromSeconds(10)), "the worker did not stop");
        }

        Assert.Equal(VfProbeOutcome.Interrupted, terminal!.Outcome);
        Assert.Equal(600d, Assert.Single(dev.PinnedTargets));
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_reset_is_final_the_lock_the_sweep_displaced_is_not_put_back_over_it()
    {
        // The user had an 1800 MHz lock, probed, then pressed Reset mid-sweep.
        // Reset means stock: neither another pin nor the pre-probe lock.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        var probe = Probe(tuner, () => Reading(dev));
        bool reset = false;
        probe.ProgressChanged += p =>
        {
            if (p.Running && p.Phase == "measured" && !reset)
            {
                reset = true;
                Assert.True(tuner.ResetToDefaults().AllSucceeded);
            }
        };

        var final = RunToEnd(probe);

        Assert.Equal(VfProbeOutcome.Interrupted, final.Outcome);
        Assert.Single(dev.PinnedTargets);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.AppliedLockMHz);
    }

    [Fact]
    public void The_probes_own_writes_never_count_as_an_interruption()
    {
        // Every pin, the pre-sweep release and the restore raise StateWritten
        // on the probe's thread; a full sweep must still be Completed.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        int notifications = 0;
        tuner.StateWritten += _ => Interlocked.Increment(ref notifications);

        var final = RunToEnd(Probe(tuner, () => Reading(dev)));

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.Equal(5, dev.PinnedTargets.Count());
        Assert.True(notifications >= 7, $"expected BeginProbe + 5 pins + EndProbe to notify, saw {notifications}");
    }

    [Fact]
    public void A_write_to_another_card_does_not_interrupt_this_cards_sweep()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var otherDev = new FakeArcDevice();
        var other = new ArcGpuTuner(otherDev, "INTEL-00:03.0-E20B-0001");
        var probe = Probe(tuner, () => Reading(dev));
        probe.ProgressChanged += p =>
        {
            if (p.Running && p.Phase == "measured")
            {
                Assert.True(other.ResetToDefaults().AllSucceeded);
            }
        };

        var final = RunToEnd(probe);

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.Equal(5, dev.PinnedTargets.Count());
    }

    // --- R-47: measured must also be plannable ------------------------------------

    [Fact]
    public void A_fresh_probe_collects_enough_samples_to_plan_at_every_step()
    {
        // The default window is 1.5 s at 150 ms: ten polls against a plan
        // minimum of twenty. Scaled tenfold here (0.15 s at 15 ms) — before the
        // fix every step ended with ~10 samples and PlanUndervolt refused them all.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var probe = Probe(tuner, () => Reading(dev));
        probe.SampleSeconds = 0.15;
        probe.SampleIntervalMs = 15;
        var recorder = new VfCurveRecorder();

        var final = RunToEnd(probe, recorder);

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.Equal(0, final.StepsBelowPlanCoverage);
        var tuning = VfTuningState.Read(tuner);
        foreach (double target in dev.PinnedTargets)
        {
            double mv = VoltageAt(target);
            Assert.True(recorder.SamplesInBinAt(mv) >= VfCurveRecorder.PlanMinBinSamples,
                $"{target} MHz: only {recorder.SamplesInBinAt(mv)} samples at {mv} mV");
            Assert.NotNull(recorder.PlanUndervolt(mv, target, tuning));
        }
    }

    [Fact]
    public void A_step_straddling_two_bins_samples_until_one_of_them_can_plan()
    {
        // Alternating 761/763 mV split every step across bins 760 and 765, so
        // twenty polls give ten each — the count must be per bin, not per step.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        int polls = 0;
        var probe = Probe(tuner, () => Reading(dev) with
        {
            CoreVoltageMv = VoltageAt(dev.Max) + (Interlocked.Increment(ref polls) % 2 == 0 ? 1 : 3),
        });
        probe.SampleIntervalMs = 1;
        var recorder = new VfCurveRecorder();

        var final = RunToEnd(probe, recorder);

        Assert.Equal(0, final.StepsBelowPlanCoverage);
        var tuning = VfTuningState.Read(tuner);
        foreach (double target in dev.PinnedTargets)
        {
            Assert.NotNull(recorder.PlanUndervolt(VoltageAt(target), target, tuning));
        }
    }

    [Fact]
    public void A_step_that_cannot_reach_plan_coverage_is_reported_and_bounded()
    {
        // Idle-load readings are never binned: no step can be planned from.
        // The probe must still finish in bounded time and say so.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        int polls = 0;
        var probe = Probe(tuner, () =>
        {
            Interlocked.Increment(ref polls);
            return Reading(dev, load: 5);
        });
        probe.SampleSeconds = 0;
        probe.SampleIntervalMs = 1;
        probe.MaxSamplesPerStep = 8;

        var final = RunToEnd(probe);

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome); // every target was visited
        Assert.Equal(final.StepCount, final.StepsBelowPlanCoverage);
        Assert.Contains("cannot be planned", final.Phase, StringComparison.Ordinal);
        Assert.Equal(final.StepCount * 8, polls);
    }

    [Fact]
    public void The_poll_budget_can_always_reach_the_plan_minimum()
    {
        // Arithmetic, kept honest: the default window alone is 1.5 s / 150 ms =
        // 10 polls, which is why R-47 happened; the budget must cover at least
        // the plan minimum twice over, for steps that straddle two bins.
        var probe = new VfCurveProbe(Tuner(new FakeArcDevice()), () => throw new InvalidOperationException());

        Assert.Equal(10, (int)Math.Round(probe.SampleSeconds * 1000 / probe.SampleIntervalMs));
        Assert.True(probe.MaxSamplesPerStep >= 2 * VfCurveRecorder.PlanMinBinSamples);
    }

    [Fact]
    public void A_sweep_sets_aside_samples_measured_under_other_tuning_before_it_starts()
    {
        // A curve stamped +100 (another session, another tool) must not be
        // merged with a sweep run at stock (R-20).
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        var recorder = new VfCurveRecorder();
        recorder.SetTuningState(new VfTuningState(100));
        for (int i = 0; i < 25; i++)
        {
            recorder.Add(new GpuSnapshot
            {
                Timestamp = DateTimeOffset.Now,
                DeviceIndex = 0,
                CoreClockMHz = 2900,
                CoreVoltageMv = 1200,
                GpuUtilPct = 95,
            });
        }

        var final = RunToEnd(Probe(tuner, () => Reading(dev)), recorder);

        Assert.Equal(VfProbeOutcome.Completed, final.Outcome);
        Assert.Equal(VfTuningState.Stock, recorder.TuningState);
        Assert.Equal(0, recorder.SamplesInBinAt(1200));
    }
}
