using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;
using static Afterglow.Core.Tests.Fakes.ArcScenario;

namespace Afterglow.Core.Tests;

/// <summary>
/// The Arc clock-lock state machine, run against the real <see cref="ArcGpuTuner"/>
/// on an in-memory frequency domain. Each test is one scenario from the review
/// passes that used to be argued about in prose; a change to the tuner now has
/// to keep every one of them true at once.
/// </summary>
[Collection("AppPaths")]
public sealed class ArcClockLockScenarioTests : IDisposable
{
    private readonly StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void A_range_lock_is_written_verified_and_persisted()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);

        var result = tuner.Apply(Lock(1500));

        Assert.True(result.AllSucceeded, Describe(result));
        Assert.Equal((100d, 1500d), (dev.Min, dev.Max));
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.Equal(1500u, tuner.AppliedLockMHz);
        Assert.Equal(1500u, AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);
    }

    [Fact]
    public void A_lockless_apply_releases_the_tracked_clamp_verified()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1500)).AllSucceeded);

        var release = tuner.Apply(NoLock());

        Assert.True(release.AllSucceeded, Describe(release));
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.Null(tuner.AppliedLockMHz);
        Assert.Null(AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);
    }

    [Fact]
    public void A_factory_ceiling_below_the_domain_max_is_observed_never_applied()
    {
        var dev = new FakeArcDevice { FactoryMax = 2200 };
        var tuner = Tuner(dev);

        // Honest about a limit it cannot yet explain…
        Assert.Equal(2200u, tuner.ReadCurrent().LockedCoreClockMHz);
        // …but it is not a lock Afterglow applied.
        Assert.Null(tuner.AppliedLockMHz);

        var release = tuner.ForceUnlock();

        Assert.True(release.Applied, release.Detail);
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.Equal(2200u, tuner.MaxLockableClockMHz);
    }

    [Fact]
    public void A_lockless_apply_on_a_factory_ceiling_card_persists_no_phantom_lock()
    {
        var dev = new FakeArcDevice { FactoryMax = 2200 };
        var tuner = Tuner(dev);

        var first = tuner.Apply(NoLock());

        Assert.True(first.AllSucceeded, Describe(first));
        Assert.Null(AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);

        // The next process inherits nothing and applies cleanly too.
        var next = Tuner(dev);
        Assert.Null(next.AppliedLockMHz);
        var again = next.Apply(NoLock());
        Assert.True(again.AllSucceeded, Describe(again));
    }

    [Fact]
    public void A_record_inherited_from_a_crashed_session_is_released_by_a_lockless_apply()
    {
        var dev = new FakeArcDevice { Max = 1800 };
        AppliedStateStore.Record(Lock(1800), true, 1800, Uuid);
        var tuner = Tuner(dev);

        Assert.Equal(1800u, tuner.AppliedLockMHz);

        var release = tuner.Apply(NoLock());

        Assert.True(release.AllSucceeded, Describe(release));
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.AppliedLockMHz);
    }

    [Fact]
    public void A_record_whose_clamp_a_reboot_cleared_is_dropped_not_restored()
    {
        var dev = new FakeArcDevice(); // at factory: the reboot cleared it
        AppliedStateStore.Record(Lock(1800), true, 1800, Uuid);
        var tuner = Tuner(dev);

        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.Null(tuner.AppliedLockMHz);

        var apply = tuner.Apply(NoLock());

        Assert.True(apply.AllSucceeded, Describe(apply));
        Assert.Null(AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);
        Assert.DoesNotContain(dev.Writes, w => w.Max == 1800);
    }

    [Fact]
    public void A_written_lock_whose_release_the_driver_refuses_stays_tracked()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1500)).AllSucceeded);
        dev.RestoreResult = CtlResult.ErrorUnknown;

        var release = tuner.ForceUnlock();

        Assert.False(release.Applied, release.Detail);
        Assert.Equal(1500u, tuner.AppliedLockMHz);
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_range_lock_written_at_the_factory_ceiling_is_released_verified()
    {
        // Measured: a request above the ceiling settles at it. A lock written
        // there can never show a rising ceiling on release; the ceiling being
        // back at the highest one seen is the proof.
        var dev = new FakeArcDevice { FactoryMax = 2250 };
        var tuner = Tuner(dev);
        var applied = tuner.Apply(Lock(2250));
        Assert.True(applied.AllSucceeded, Describe(applied));

        var release = tuner.ForceUnlock();

        Assert.True(release.Applied, release.Detail);
        Assert.Contains("(verified)", release.Detail, StringComparison.Ordinal);
        Assert.Equal((100d, 2250d), (dev.Min, dev.Max));
        Assert.Null(tuner.AppliedLockMHz);
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_release_whose_readback_falls_short_of_the_highest_ceiling_seen_fails()
    {
        // The one shape the single rule refuses: this process has seen the
        // ceiling higher than the restore left it, so a cap stayed.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1500)).AllSucceeded); // the pre-write read saw 2300
        dev.ReadbackOffset = -900; // every readback now lands well below that

        var release = tuner.ForceUnlock();

        Assert.False(release.Applied, release.Detail);
        Assert.Equal(1500u, tuner.AppliedLockMHz);
    }

    [Fact]
    public void A_probe_pin_is_reported_but_is_not_the_lock_Afterglow_applied()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);

        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));

        Assert.Equal((1500d, 1500d), (dev.Min, dev.Max));
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);
        // The probe restores "the lock Afterglow applied" after a sweep; its own
        // pin must never be that, or a failed release turns into a range lock
        // the user never asked for on the next sweep and on every stepper step.
        Assert.Null(tuner.AppliedLockMHz);
    }

    [Fact]
    public void A_pin_at_the_factory_ceiling_is_released_verified_by_the_floor()
    {
        var dev = new FakeArcDevice { FactoryMax = 2200 };
        var tuner = Tuner(dev);
        _ = tuner.ReadCurrent();
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(2200));

        var release = tuner.ForceUnlock();

        Assert.True(release.Applied, release.Detail);
        Assert.Equal((100d, 2200d), (dev.Min, dev.Max));
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_pin_whose_release_the_driver_refuses_is_a_failed_release()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        _ = tuner.ReadCurrent();
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        dev.RestoreResult = CtlResult.ErrorUnknown;

        var release = tuner.ForceUnlock();

        Assert.False(release.Applied, release.Detail);
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_readback_getter_failure_after_a_pin_write_leaves_the_pin_tracked_and_on_record()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        _ = tuner.ReadCurrent();
        dev.ReadResult = CtlResult.ErrorUnknown;

        Assert.Equal(NvmlReturn.Unknown, tuner.LockClockForProbe(1500));
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);

        dev.ReadResult = CtlResult.Success;
        var release = tuner.ForceUnlock();
        Assert.True(release.Applied, release.Detail);
        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void The_users_lock_stays_the_applied_lock_while_a_pin_stands_and_after_its_release_fails()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);

        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        Assert.Equal(1800u, tuner.AppliedLockMHz);

        dev.RestoreResult = CtlResult.ErrorUnknown;
        Assert.False(tuner.ForceUnlock().Applied);
        Assert.Equal(1800u, tuner.AppliedLockMHz);

        dev.RestoreResult = CtlResult.Success;
        Assert.Equal(NvmlReturn.Success, tuner.RestoreTuningLock(1800));
        Assert.Equal(1800u, tuner.AppliedLockMHz);
        Assert.Equal((100d, 1800d), (dev.Min, dev.Max));
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_leftover_pin_released_cleanly_is_verified_and_forgotten()
    {
        var dev = new FakeArcDevice { Min = 1500, Max = 1500 };
        var tuner = Tuner(dev);
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);

        var release = tuner.ForceUnlock();

        Assert.True(release.Applied, release.Detail);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_clamp_changed_by_another_process_loses_this_sessions_provenance()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1500)).AllSucceeded);

        dev.Max = 1800; // afterglow-cli set --lock-clock 1800 in another terminal

        Assert.Equal(1800u, tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.Null(tuner.AppliedLockMHz);
    }

    [Fact]
    public void Reset_releases_the_clamp_and_clears_the_record()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1500)).AllSucceeded);

        var reset = tuner.ResetToDefaults();

        Assert.True(reset.AllSucceeded, Describe(reset));
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void A_verified_release_resolves_the_probes_pending_record()
    {
        var dev = new FakeArcDevice { Min = 1500, Max = 1500 };
        var tuner = Tuner(dev);
        AppliedStateStore.RecordProbeLockPending(Uuid);
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);
        _ = tuner.ReadCurrent();

        Assert.True(tuner.ForceUnlock().Applied);

        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_pin_is_on_record_before_it_lands_and_a_failed_release_keeps_it()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        _ = tuner.ReadCurrent();

        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);

        dev.RestoreResult = CtlResult.ErrorUnknown;
        Assert.False(tuner.ForceUnlock().Applied);
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_refused_pin_does_not_erase_an_older_sessions_pin_record()
    {
        AppliedStateStore.RecordProbeLockPending(Uuid); // a session that never released its pin
        var dev = new FakeArcDevice { WriteResult = CtlResult.ErrorInsufficientPermissions };
        var tuner = Tuner(dev);

        Assert.NotEqual(NvmlReturn.Success, tuner.LockClockForProbe(1500));

        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void An_explicit_release_on_a_card_without_the_clamp_is_one_failed_knob()
    {
        var dev = new FakeArcDevice { HwMax = 0 }; // the domain reports no controllable range
        var tuner = Tuner(dev);
        Assert.False(tuner.Capabilities.SupportsLockedCoreClock);

        var result = tuner.Apply(NoLock(), releaseLock: true);

        Assert.False(result.AllSucceeded);
        Assert.Single(result.Results, k => k.Knob == "clock lock" && !k.Applied);
    }

    [Fact]
    public void A_refused_pin_leaves_no_record()
    {
        var dev = new FakeArcDevice { WriteResult = CtlResult.ErrorInsufficientPermissions };
        var tuner = Tuner(dev);

        Assert.NotEqual(NvmlReturn.Success, tuner.LockClockForProbe(1500));

        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void A_restored_range_lock_resolves_the_pin_record()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);

        Assert.Equal(NvmlReturn.Success, tuner.RestoreTuningLock(1800));

        var state = AppliedStateStore.Load(Uuid);
        Assert.NotNull(state);
        Assert.False(state!.ProbeLockPending);
        Assert.Equal(1800u, state.LockedCoreClockMHz);
    }

    [Fact]
    public void Refused_writes_leave_nothing_tracked_and_nothing_recorded()
    {
        var dev = new FakeArcDevice
        {
            WriteResult = CtlResult.ErrorInsufficientPermissions,
            RestoreResult = CtlResult.ErrorInsufficientPermissions,
        };
        var tuner = Tuner(dev);

        var apply = tuner.Apply(Lock(1500));

        Assert.False(apply.AllSucceeded);
        Assert.Null(tuner.AppliedLockMHz);
        Assert.Null(AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.False(tuner.ForceUnlock().Applied);
    }

    [Fact]
    public void A_driver_that_truncates_the_readback_keeps_the_written_provenance()
    {
        // Measured on the B390: a request of 1499.6 reads back 1499.0. A
        // readback a fraction below the request must pass the write's
        // verification and must not demote the lock to "observed" on the next
        // read — that demotion is what made a verified lock un-releasable.
        var dev = new FakeArcDevice { ReadbackOffset = -0.6 };
        var tuner = Tuner(dev);

        var apply = tuner.Apply(Lock(1500));

        Assert.True(apply.AllSucceeded, Describe(apply));
        Assert.NotNull(tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.Equal(1500u, tuner.AppliedLockMHz);

        // Refresh-then-restore cycles (a probe, a game rule) must not drift the
        // lock down a megahertz at a time on the truncated echo.
        for (int i = 0; i < 3; i++)
        {
            _ = tuner.ReadCurrent();
            Assert.Equal(NvmlReturn.Success, tuner.RestoreTuningLock(tuner.AppliedLockMHz!.Value));
        }

        Assert.Equal(1500u, tuner.AppliedLockMHz);
        Assert.Equal(1500d, dev.Max);

        var release = tuner.Apply(NoLock());
        Assert.True(release.AllSucceeded, Describe(release));
        Assert.Null(tuner.AppliedLockMHz);
    }

    [Fact]
    public void An_explicit_release_is_one_operation_with_one_verdict()
    {
        // `--lock-clock off` / MCP unlock:true on a clamp this session never
        // applied (another tool's): Apply releases it itself and reports one
        // "clock lock" knob — no front-end release-then-reconcile.
        var dev = new FakeArcDevice { Max = 1500 };
        var tuner = Tuner(dev);

        var result = tuner.Apply(NoLock(), releaseLock: true);

        Assert.True(result.AllSucceeded, Describe(result));
        Assert.Single(result.Results, k => k.Knob == "clock lock");
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void An_explicit_release_with_nothing_clamped_still_answers_with_one_verified_knob()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);

        var result = tuner.Apply(NoLock(), releaseLock: true);

        Assert.True(result.AllSucceeded, Describe(result));
        var knob = Assert.Single(result.Results, k => k.Knob == "clock lock");
        Assert.Contains("(verified)", knob.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Results, k => k.Knob == "profile");
    }

    [Fact]
    public void An_explicit_release_the_driver_refuses_is_one_failed_knob()
    {
        var dev = new FakeArcDevice { Max = 1500, RestoreResult = CtlResult.ErrorInsufficientPermissions };
        var tuner = Tuner(dev);

        var result = tuner.Apply(NoLock(), releaseLock: true);

        Assert.False(result.AllSucceeded);
        Assert.Single(result.Results, k => k.Knob == "clock lock" && !k.Applied);
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void Dashboard_reads_during_a_pin_do_not_make_it_unreleasable()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        _ = tuner.ReadCurrent();
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);

        var release = tuner.ForceUnlock();

        Assert.True(release.Applied, release.Detail);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
    }
}
