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

        Assert.True(result.AllSucceeded, result.Summary);
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

        Assert.True(release.AllSucceeded, release.Summary);
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

        Assert.True(first.AllSucceeded, first.Summary);
        Assert.Null(AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);

        // The next process inherits nothing and applies cleanly too.
        var next = Tuner(dev);
        Assert.Null(next.AppliedLockMHz);
        var again = next.Apply(NoLock());
        Assert.True(again.AllSucceeded, again.Summary);
    }

    [Fact]
    public void A_record_inherited_from_a_crashed_session_is_released_by_a_lockless_apply()
    {
        var dev = new FakeArcDevice { Max = 1800 };
        AppliedStateStore.Record(Lock(1800), true, 1800, Uuid);
        var tuner = Tuner(dev);

        Assert.Equal(1800u, tuner.AppliedLockMHz);

        var release = tuner.Apply(NoLock());

        Assert.True(release.AllSucceeded, release.Summary);
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

        Assert.True(apply.AllSucceeded, apply.Summary);
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
        Assert.True(applied.AllSucceeded, applied.Summary);

        var release = tuner.ForceUnlock();

        Assert.True(release.Applied, release.Detail);
        Assert.Contains("factory ceiling", release.Detail, StringComparison.Ordinal); // adopted, honestly worded
        Assert.Equal((100d, 2250d), (dev.Min, dev.Max));
        Assert.Null(tuner.AppliedLockMHz);
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_release_whose_readback_falls_short_of_a_ceiling_this_process_verified_fails()
    {
        // The one shape the rule refuses: this process has VERIFIED the ceiling
        // higher than the restore left it, so a cap stayed.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.ForceUnlock().Applied); // verifies the 2300 ceiling
        Assert.True(tuner.Apply(Lock(1500)).AllSucceeded);
        dev.ReadbackOffset = -900; // every readback now lands well below that

        var release = tuner.ForceUnlock();

        Assert.False(release.Applied, release.Detail);
        Assert.Equal(1500u, tuner.AppliedLockMHz);
    }

    [Fact]
    public void A_lock_written_a_megahertz_above_the_factory_ceiling_stays_releasable_across_processes()
    {
        // The request settles at the ceiling (measured) and verifies within the
        // write tolerance; tracking the request instead of the settled value
        // left a shadow no release could ever reach.
        var dev = new FakeArcDevice { FactoryMax = 2200 };
        var applied = Tuner(dev).Apply(Lock(2201));
        Assert.True(applied.AllSucceeded, applied.Summary); // settled at 2200, within the write tolerance

        var next = Tuner(dev); // a fresh process inherits the 2201 record over a card at 100..2200
        Assert.Equal(2201u, next.AppliedLockMHz);

        var release = next.ForceUnlock();
        Assert.True(release.Applied, release.Detail);
        Assert.Equal((100d, 2200d), (dev.Min, dev.Max));
        Assert.Null(next.AppliedLockMHz);
    }

    [Fact]
    public void An_apply_that_both_locks_and_releases_is_refused_before_anything_is_written()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);

        var result = tuner.Apply(Lock(1500), releaseLock: true);

        Assert.False(result.AllSucceeded);
        Assert.Single(result.Results, k => k.Knob == "profile" && !k.Applied);
        Assert.Empty(dev.Writes);
        Assert.Null(AppliedStateStore.Load(Uuid));
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
        dev.ReadResult = CtlResult.ErrorUnknown;

        Assert.Equal(NvmlReturn.Unknown, tuner.LockClockForProbe(1500));
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);

        dev.ReadResult = CtlResult.Success;
        var release = tuner.ForceUnlock();
        Assert.True(release.Applied, release.Detail);
        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void The_users_lock_stays_the_applied_lock_through_a_sweep_and_after_its_restore_fails()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);

        var start = tuner.BeginProbe();
        Assert.Null(start.Refusal);
        Assert.Equal(1800u, start.LockToRestoreMHz);
        Assert.Equal(2300u, start.MaxClockMHz); // the sweep runs against the true ceiling, not under the lock
        Assert.Equal(1800u, tuner.AppliedLockMHz); // captured before the release emptied the tracking

        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        Assert.Equal(1800u, tuner.AppliedLockMHz);

        dev.WriteResult = CtlResult.ErrorUnknown; // the restore write is refused
        Assert.False(tuner.EndProbe().Applied);
        Assert.Equal(1800u, tuner.AppliedLockMHz);
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);

        dev.WriteResult = CtlResult.Success;
        var end = tuner.EndProbe();
        Assert.True(end.Applied, end.Detail);
        Assert.Equal(1800u, tuner.AppliedLockMHz);
        Assert.Equal((100d, 1800d), (dev.Min, dev.Max));
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_retried_sweep_carries_forward_the_lock_an_earlier_restore_could_not_put_back()
    {
        // R-16: user lock 1800 -> probe pin 1500 -> refused restore -> retry.
        // The retry's pre-sweep release empties the tracking; the lock the
        // first sweep displaced must survive it and be what the retry restores.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        Assert.Null(tuner.BeginProbe().Refusal);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        dev.WriteResult = CtlResult.ErrorUnknown;
        Assert.False(tuner.EndProbe().Applied);
        dev.WriteResult = CtlResult.Success;

        var retry = tuner.BeginProbe();

        Assert.Null(retry.Refusal);
        Assert.Equal(1800u, retry.LockToRestoreMHz);
        Assert.Null(retry.ForeignClampNote); // the pin it released was this process's own
        Assert.Equal(1800u, tuner.AppliedLockMHz);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(2000));

        var end = tuner.EndProbe();

        Assert.True(end.Applied, end.Detail);
        Assert.Equal((100d, 1800d), (dev.Min, dev.Max));
        Assert.Equal(1800u, tuner.AppliedLockMHz);
        var record = AppliedStateStore.Load(Uuid);
        Assert.Equal(1800u, record?.LockedCoreClockMHz);
        Assert.NotEqual(true, record?.ProbeLockPending);
    }

    [Fact]
    public void A_retry_whose_pre_sweep_release_is_refused_still_holds_the_displaced_lock()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        Assert.Null(tuner.BeginProbe().Refusal);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        dev.WriteResult = CtlResult.ErrorUnknown;
        Assert.False(tuner.EndProbe().Applied);
        dev.RestoreResult = CtlResult.ErrorUnknown;

        Assert.NotNull(tuner.BeginProbe().Refusal);
        Assert.Equal(1800u, tuner.AppliedLockMHz);

        dev.WriteResult = CtlResult.Success;
        dev.RestoreResult = CtlResult.Success;
        Assert.Equal(1800u, tuner.BeginProbe().LockToRestoreMHz);
        Assert.True(tuner.EndProbe().Applied);
        Assert.Equal((100d, 1800d), (dev.Min, dev.Max));
    }

    [Fact]
    public void A_lock_applied_after_a_failed_restore_replaces_the_displaced_one()
    {
        // Carrying the displaced lock forward must not outrank a lock the user
        // applied since: that one is on the card, is what the record persists,
        // and is what the next sweep puts back.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        Assert.Null(tuner.BeginProbe().Refusal);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        dev.WriteResult = CtlResult.ErrorUnknown;
        Assert.False(tuner.EndProbe().Applied);
        dev.WriteResult = CtlResult.Success;

        var apply = tuner.Apply(Lock(2000));

        Assert.True(apply.AllSucceeded, apply.Summary);
        Assert.Equal(2000u, tuner.AppliedLockMHz);
        Assert.Equal(2000u, AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);

        Assert.Equal(2000u, tuner.BeginProbe().LockToRestoreMHz);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        Assert.True(tuner.EndProbe().Applied);
        Assert.Equal((100d, 2000d), (dev.Min, dev.Max));
        Assert.Equal(2000u, tuner.AppliedLockMHz);
    }

    [Fact]
    public void A_factory_floor_above_the_hardware_minimum_is_a_released_state()
    {
        // R-17. Intel's ctl_freq_range_t: the -1 restore returns the minimum to
        // the factory value, which "can be larger than the hardware min".
        var dev = new FakeArcDevice { HwMin = 100, FactoryMin = 300 };
        var tuner = Tuner(dev);

        var unlock = tuner.ForceUnlock();

        Assert.True(unlock.Applied, unlock.Detail);
        Assert.Contains("factory floor", unlock.Detail, StringComparison.Ordinal); // adopted, honestly worded
        Assert.Equal((300d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.Null(tuner.AppliedLockMHz);

        // Remembered: the next release lands on the verified floor.
        var again = tuner.ForceUnlock();
        Assert.True(again.Applied, again.Detail);
        Assert.Contains("(verified)", again.Detail, StringComparison.Ordinal);

        var reset = tuner.ResetToDefaults();
        Assert.True(reset.AllSucceeded, reset.Summary);
        var plain = tuner.Apply(NoLock());
        Assert.True(plain.AllSucceeded, plain.Summary);
        Assert.Null(AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);
    }

    [Fact]
    public void A_fresh_process_on_a_raised_factory_floor_can_unlock_reset_apply_and_probe()
    {
        // The review's sequence, each step in a process that has verified nothing.
        static FakeArcDevice Card() => new() { HwMin = 100, FactoryMin = 300 };

        var unlock = Tuner(Card()).ForceUnlock();
        Assert.True(unlock.Applied, unlock.Detail);

        var reset = Tuner(Card()).ResetToDefaults();
        Assert.True(reset.AllSucceeded, reset.Summary);

        var plain = Tuner(Card()).Apply(NoLock());
        Assert.True(plain.AllSucceeded, plain.Summary);
        Assert.DoesNotContain("100..", plain.Summary, StringComparison.Ordinal); // the floor was 300, never quoted as 100

        var start = Tuner(Card()).BeginProbe();
        Assert.Null(start.Refusal);
        Assert.Equal(2300u, start.MaxClockMHz);
        Assert.Contains("300 MHz clock floor", start.ForeignClampNote, StringComparison.Ordinal); // not a "2300 MHz ceiling"
    }

    [Fact]
    public void A_range_lock_on_a_raised_factory_floor_card_releases_verified()
    {
        var dev = new FakeArcDevice { HwMin = 100, FactoryMin = 300 };
        var tuner = Tuner(dev);
        Assert.True(tuner.ForceUnlock().Applied); // verifies the 300 floor
        Assert.True(tuner.Apply(Lock(1500)).AllSucceeded);
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);

        var release = tuner.Apply(NoLock());

        Assert.True(release.AllSucceeded, release.Summary);
        Assert.Equal((300d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_pin_a_release_leaves_in_place_still_fails_on_a_raised_factory_floor_card()
    {
        // Adopting a factory floor must not adopt a pin: a range whose floor
        // meets its ceiling is still an exact pin, whatever the floor is.
        var inner = new FakeArcDevice { HwMin = 100, FactoryMin = 300 };
        var dev = new RestoreKeeps(inner) { KeepPin = true };
        var tuner = new ArcGpuTuner(dev, Uuid);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));

        var release = tuner.ForceUnlock();

        Assert.False(release.Applied, release.Detail);
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_release_whose_floor_stays_above_a_floor_this_process_verified_fails()
    {
        // The floor's twin of the ceiling rule: once a release has verified the
        // 300 floor, a release that leaves it at 1500 kept part of the pin.
        var inner = new FakeArcDevice { HwMin = 100, FactoryMin = 300 };
        var dev = new RestoreKeeps(inner);
        var tuner = new ArcGpuTuner(dev, Uuid);
        Assert.True(tuner.ForceUnlock().Applied); // verifies 300..2300
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        dev.KeepFloor = true;

        var release = tuner.ForceUnlock();

        Assert.False(release.Applied, release.Detail);
        Assert.Equal((1500d, 2300d), (inner.Min, inner.Max));
        Assert.NotNull(tuner.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_sweep_that_pinned_nothing_ends_with_nothing_to_release()
    {
        var dev = new FakeArcDevice { WriteResult = CtlResult.ErrorInsufficientPermissions };
        var tuner = Tuner(dev);

        Assert.Null(tuner.BeginProbe().Refusal);
        Assert.NotEqual(NvmlReturn.Success, tuner.LockClockForProbe(1500));

        var end = tuner.EndProbe();
        Assert.True(end.Applied, end.Detail);
        Assert.DoesNotContain(dev.Writes, w => w.Min < 0); // no release was even attempted
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

        Assert.True(reset.AllSucceeded, reset.Summary);
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

        Assert.True(tuner.ForceUnlock().Applied);

        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void A_pin_is_on_record_before_it_lands_and_a_failed_release_keeps_it()
    {
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);

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

        Assert.True(apply.AllSucceeded, apply.Summary);
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
        Assert.True(release.AllSucceeded, release.Summary);
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

        Assert.True(result.AllSucceeded, result.Summary);
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

        Assert.True(result.AllSucceeded, result.Summary);
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
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1500));
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);
        Assert.Equal(1500u, tuner.ReadCurrent().LockedCoreClockMHz);

        var release = tuner.ForceUnlock();

        Assert.True(release.Applied, release.Detail);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
    }

    /// <summary>
    /// A frequency domain whose factory restore reports success but keeps part
    /// of what was there: the floor (<see cref="KeepFloor"/>) or the whole
    /// range (<see cref="KeepPin"/>). No driver has been measured doing this;
    /// it is the shape the release's floor rule exists to refuse.
    /// </summary>
    private sealed class RestoreKeeps(FakeArcDevice inner) : IArcDevice
    {
        public bool KeepFloor { get; set; }

        public bool KeepPin { get; set; }

        public IReadOnlyList<(nint Handle, CtlFreqProperties Properties)> GetFrequencyDomains() =>
            inner.GetFrequencyDomains();

        public CtlResult TryGetFrequencyRange(nint domain, out CtlFreqRange range) =>
            inner.TryGetFrequencyRange(domain, out range);

        public CtlResult TrySetFrequencyRange(nint domain, double minMhz, double maxMhz)
        {
            var (min, max) = (inner.Min, inner.Max);
            var rc = inner.TrySetFrequencyRange(domain, minMhz, maxMhz);
            if (rc == CtlResult.Success && minMhz < 0 && maxMhz < 0)
            {
                inner.Min = KeepFloor || KeepPin ? min : inner.Min;
                inner.Max = KeepPin ? max : inner.Max;
            }

            return rc;
        }

        public CtlResult TryGetOcProperties(out CtlOcProperties properties) => inner.TryGetOcProperties(out properties);

        public CtlResult TrySetOverclockWaiver() => inner.TrySetOverclockWaiver();

        public CtlResult TryGetOcPowerLimitV2(out double limit) => inner.TryGetOcPowerLimitV2(out limit);

        public CtlResult TrySetOcPowerLimitV2(double limit) => inner.TrySetOcPowerLimitV2(limit);
    }
}
