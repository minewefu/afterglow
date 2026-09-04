using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Stress;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// The rule these guard: Afterglow never reports a stability result it did not
/// actually observe. Each test covers a path that used to produce a verdict —
/// or a "stable offset" — from evidence that was never gathered.
/// </summary>
public class NoVerdictWithoutEvidenceTests
{
    /// <summary>A tuner that answers everything as unsupported, with configurable capabilities.</summary>
    private sealed class StubTuner(TuningCapabilities caps) : IGpuTuner
    {
        public TuningCapabilities Capabilities { get; } = caps;

        public string? GpuUuid => "STUB-0000:00:00.0-0000";

        public uint? AppliedLockMHz => null;

        public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz)
            ReadCurrent() => (0, 0, null, null, null);

        /// <summary>How many times a tune was attempted — 0 proves the guard fired first.</summary>
        public int ApplyCallCount { get; private set; }

        public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true)
        {
            ApplyCallCount++;
            return new ApplyResult(true, [KnobResult.Ok("stub")]);
        }

        public ApplyResult ResetToDefaults() => new(true, [KnobResult.Ok("stub")]);

        public KnobResult ForceUnlock() => KnobResult.Ok("clock lock");

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
            KnobResult.Fail("V/F points", "stub");

        public KnobResult ClearVfPointOffsets() => KnobResult.Fail("V/F points", "stub");
    }

    [Fact]
    public void The_stepper_refuses_a_gpu_with_no_core_offset_knob()
    {
        // It walks the core offset. With no such knob every "step" stays at 0,
        // and the sweep used to burn a full cycle and then report +0 MHz as a
        // confirmed stable offset — a verdict about a control that does not exist.
        var tuner = new StubTuner(new TuningCapabilities { SupportsCoreOffset = false });
        var stepper = new StabilityStepper(tuner);

        CertifierLikeStatus? final = null;
        using var done = new ManualResetEventSlim(false);
        stepper.StatusChanged += status =>
        {
            if (!status.Running)
            {
                final = new CertifierLikeStatus(status.Phase, status.ResultOffsetMHz, status.Log);
                done.Set();
            }
        };

        stepper.Start(new StepperOptions());

        Assert.True(done.Wait(TimeSpan.FromSeconds(20)), "the stepper never reported a terminal status");
        Assert.Equal("failed", final!.Phase);
        Assert.Null(final.ResultOffsetMHz);

        // Discriminating assertions: without the capability guard the sweep
        // reaches Burn() and only fails later for want of an adapter, which on a
        // machine with no matching GPU produces the same phase and null offset.
        // These two can only hold if it refused BEFORE trying to tune or burn.
        Assert.Contains(final.Log, line => line.Contains("no core-clock offset", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, tuner.ApplyCallCount);
    }

    private sealed record CertifierLikeStatus(string Phase, int? ResultOffsetMHz, IReadOnlyList<string> Log);

    /// <summary>A tuner whose every offset write is refused, like a driver right after a TDR.</summary>
    private sealed class RefusingTuner(TuningCapabilities caps) : IGpuTuner
    {
        public TuningCapabilities Capabilities { get; } = caps;

        public string? GpuUuid => "STUB-0000:00:00.0-0000";

        public uint? AppliedLockMHz => null;

        public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz)
            ReadCurrent() => (0, 0, null, null, null);

        public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true) =>
            new(false, [KnobResult.Fail("core offset", "driver refused")]);

        public ApplyResult ResetToDefaults() => new(true, [KnobResult.Ok("stub")]);

        public KnobResult ForceUnlock() => KnobResult.Ok("clock lock");

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
            KnobResult.Fail("V/F points", "stub");

        public KnobResult ClearVfPointOffsets() => KnobResult.Fail("V/F points", "stub");
    }

    [Fact]
    public void A_stepper_run_whose_offset_restore_failed_says_so_instead_of_staying_silent()
    {
        // The restore paths used to discard ApplyOffset's result, so a driver
        // refusing the write — the ordinary state right after the reset that
        // ended the run — left the card at an untested offset with nothing
        // logged and nothing on the status for a caller to act on. MCP's
        // find_stable_offset then reported a clean finish over it.
        var tuner = new RefusingTuner(new TuningCapabilities
        {
            SupportsCoreOffset = true,
            CoreOffsetMinMHz = -200,
            CoreOffsetMaxMHz = 300,
        });
        var stepper = new StabilityStepper(tuner);

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

        stepper.Start(new StepperOptions());

        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "the stepper never reported a terminal status");
        Assert.Equal("failed", final!.Phase);
        Assert.Null(final.ResultOffsetMHz);

        // The load-bearing part: the outcome is RECORDED truthfully, not merely
        // implied by the phase. Both surfaces (the app's log panel and MCP's
        // payload) read these two. Here the refusal is the very FIRST write, of
        // the offset the card already holds — nothing changed, so the card IS
        // at the start offset and the log says so. (Re-issuing the same write
        // "as a restore" used to fail the same way and report the GPU "may
        // still be at" an offset that was never written.)
        Assert.True(final.StartOffsetRestored);
        Assert.Contains(final.Log, line => line.Contains("nothing was changed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_probe_that_cannot_lock_clocks_reports_aborted_not_running()
    {
        // This early return was the one probe exit that never carried an outcome,
        // so the CLI's abort check missed it: `vfcurve --probe` printed a
        // previously stored curve and exited 0 having locked and measured nothing.
        var probe = new VfCurveProbe(
            new StubTuner(new TuningCapabilities { MaxCoreClockMHz = 0 }),
            () => new Telemetry.GpuSnapshot { Timestamp = DateTimeOffset.Now, DeviceIndex = 0 });

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
        Assert.Equal(VfProbeOutcome.Aborted, terminal!.Outcome);
        Assert.NotEqual(VfProbeOutcome.Running, terminal.Outcome);
    }

    [Fact]
    public void Stopping_a_worker_that_never_started_is_a_clean_stop()
    {
        // The bool contract: true means "the worker is finished and its progress
        // can be read as a result". A test that never ran has nothing outstanding.
        using var stress = new GpuStressTest();
        using var vram = new VramTest();

        Assert.True(stress.StopAndWait(TimeSpan.FromMilliseconds(50)));
        Assert.True(vram.StopAndWait(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void A_run_that_never_entered_its_load_loop_is_not_a_clean_pass()
    {
        // TotalDispatches counts a one-off reference pass, so it is 1 even when
        // the load loop never ran — and that run's only verification compares
        // the reference buffer against itself, matching by construction. Every
        // verdict site therefore asks IsCleanPass, not State alone.
        var noWork = new StressProgress(
            StressState.Stopped, TimeSpan.FromSeconds(7), 0,
            TotalDispatches: 1, ErrorCount: 0, Detail: null, BurnDispatches: 0);

        Assert.Equal(StressState.Stopped, noWork.State);
        Assert.False(noWork.IsCleanPass);
    }

    [Fact]
    public void A_run_that_burned_and_stopped_cleanly_is_a_clean_pass()
    {
        var real = new StressProgress(
            StressState.Stopped, TimeSpan.FromMinutes(2), 4,
            TotalDispatches: 481, ErrorCount: 0, Detail: null, BurnDispatches: 480);

        Assert.True(real.IsCleanPass);
    }

    [Fact]
    public void A_terminal_failure_record_carries_the_work_it_actually_did()
    {
        // The engine's Report takes the burn count as a REQUIRED argument, so a
        // terminal record cannot silently publish 0 while thousands of load
        // dispatches ran. It used to default to 0 at the DeviceLost and Failed
        // sites, which made consumers describe a real TDR as "nothing was tested
        // — retry with more seconds": advice to re-run the offset that had just
        // reset the GPU.
        var tdr = new StressProgress(
            StressState.DeviceLost, TimeSpan.FromMinutes(3), 0,
            TotalDispatches: 901, ErrorCount: 0,
            Detail: "The GPU device was removed/reset (0x887A0006) — driver TDR.",
            BurnDispatches: 900);

        Assert.False(tdr.IsCleanPass);
        Assert.True(tdr.BurnDispatches > 0, "a TDR after real work must not report zero work");
        Assert.Contains("TDR", tdr.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_detected_artifact_is_never_a_clean_pass_however_much_work_ran()
    {
        var artifact = new StressProgress(
            StressState.ArtifactDetected, TimeSpan.FromMinutes(2), 4,
            TotalDispatches: 481, ErrorCount: 1, Detail: "mismatch", BurnDispatches: 480);

        Assert.False(artifact.IsCleanPass);
    }

    [Fact]
    public void Refusing_a_guessed_adapter_is_the_default_for_both_engines()
    {
        // As an opt-IN this was set on the certifier, stepper and V/F probe and
        // forgotten on both MCP engines and the Stability page — so an agent
        // could still be handed a verdict for a card the run never bound to.
        // Safe has to be the default; only a deliberately unbound CLI run opts
        // out, which is where the historical largest-VRAM fallback is documented.
        Assert.False(new GpuStressTest().AllowUnboundGuess);
        Assert.False(new VramTest().AllowUnboundGuess);
    }

    [Fact]
    public void A_guessed_adapter_is_flagged_so_callers_that_must_not_guess_can_refuse()
    {
        // Certification names a card, so it leaves AllowUnboundGuess false and
        // refuses a guessed pick rather than burning one GPU and stamping another.
        Assert.True(StressAdapter.IsUnboundGuess(
            $"Some GPU ({StressAdapter.UnboundGuessMarker}: 2 NVIDIA adapters, no PCI bus to bind to)"));
        Assert.False(StressAdapter.IsUnboundGuess("Some GPU (PCI bus 1)"));
    }
}
