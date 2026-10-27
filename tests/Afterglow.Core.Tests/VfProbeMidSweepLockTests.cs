using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;
using static Afterglow.Core.Tests.Fakes.ArcScenario;

namespace Afterglow.Core.Tests;

/// <summary>
/// A lock decision made outside a V/F sweep while it runs — a profile's lock
/// from a hotkey or a game rule, a Reset, an unlock, a profile without a lock —
/// against the lock the sweep displaced. The probe stops on such a write
/// (R-02), and what its EndProbe then leaves on the card must be what that
/// write left, not the older lock BeginProbe remembered (R-16). Run on the real
/// tuners over the in-memory Arc frequency domain and the fake NVIDIA driver:
/// tuner-level sequences for the exact orderings, including a pin that lands
/// between the foreign write and its notification, and the probe end to end
/// with the write coming from another thread.
/// </summary>
[Collection("AppPaths")]
public sealed class VfProbeMidSweepLockTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    /// <summary>A write that leaves the card with no clock lock.</summary>
    public enum StockWrite
    {
        Reset,
        ForceUnlock,

        /// <summary>`--lock-clock off`, MCP unlock: Apply with releaseLock.</summary>
        ExplicitUnlock,

        /// <summary>A saved profile with no lock (a hotkey's, a game rule's): Apply releases any lock.</summary>
        LocklessProfile,
    }

    private static bool Make(IGpuTuner tuner, StockWrite write) => write switch
    {
        StockWrite.Reset => tuner.ResetToDefaults().AllSucceeded,
        StockWrite.ForceUnlock => tuner.ForceUnlock().Applied,
        StockWrite.ExplicitUnlock => tuner.Apply(NoLock(), releaseLock: true).AllSucceeded,
        StockWrite.LocklessProfile => tuner.Apply(NoLock()).AllSucceeded,
        _ => throw new ArgumentOutOfRangeException(nameof(write)),
    };

    private static void ApplyUserLock(IGpuTuner tuner, uint? userLock)
    {
        if (userLock is uint mhz)
        {
            var applied = tuner.Apply(Lock(mhz));
            Assert.True(applied.AllSucceeded, applied.Summary);
        }
    }

    // --- Arc: a lock applied mid-sweep ---------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(1800u)]
    public void Arc_a_lock_applied_mid_sweep_is_the_lock_the_sweep_leaves(uint? userLock)
    {
        // Before the fix EndProbe put the displaced 1800 back over the 2000
        // (100..1800), or, with no lock before the sweep, released the 2000 as
        // if it were the probe's pin (100..2300).
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        ApplyUserLock(tuner, userLock);
        Assert.Null(tuner.BeginProbe().Refusal);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(600));

        var apply = tuner.Apply(Lock(2000));
        Assert.True(apply.AllSucceeded, apply.Summary);
        var end = tuner.EndProbe();

        Assert.True(end.Applied, end.Detail);
        Assert.Equal((100d, 2000d), (dev.Min, dev.Max));
        Assert.Equal(2000u, tuner.AppliedLockMHz);
        Assert.Equal(2000u, tuner.ReadCurrent().LockedCoreClockMHz);
        var record = AppliedStateStore.Load(Uuid);
        Assert.Equal(2000u, record?.LockedCoreClockMHz);
        Assert.NotEqual(true, record?.ProbeLockPending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1800u)]
    public void Arc_a_pin_that_lands_after_a_mid_sweep_lock_is_replaced_by_that_lock(uint? userLock)
    {
        // The probe checks for a foreign write before each pin, but the check
        // and the pin are two steps: a profile's lock can land in between, the
        // pin (waiting on the tuner's lock) lands right after it, and only then
        // does the profile's notification stop the sweep. The pin now covers
        // the newer lock, which is the one to put back.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        ApplyUserLock(tuner, userLock);
        Assert.Null(tuner.BeginProbe().Refusal);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(600));
        Assert.True(tuner.Apply(Lock(2000)).AllSucceeded);

        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1100)); // the racing pin
        Assert.Equal(2000u, tuner.AppliedLockMHz); // a pin is never the applied lock; what it covers is
        var end = tuner.EndProbe();

        Assert.True(end.Applied, end.Detail);
        Assert.Equal((100d, 2000d), (dev.Min, dev.Max));
        Assert.Equal(2000u, tuner.AppliedLockMHz);
        var record = AppliedStateStore.Load(Uuid);
        Assert.Equal(2000u, record?.LockedCoreClockMHz);
        Assert.NotEqual(true, record?.ProbeLockPending);
    }

    [Fact]
    public void Arc_a_mid_sweep_lock_the_driver_refuses_leaves_the_displaced_lock_to_be_put_back()
    {
        // Nothing landed, so nothing changed: the pin is still the sweep's and
        // the lock it displaced is still the lock Afterglow applied — on record
        // too. The NVIDIA tuner answers the same way.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        Assert.Null(tuner.BeginProbe().Refusal);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(600));
        dev.WriteResult = CtlResult.ErrorInsufficientPermissions;

        var apply = tuner.Apply(Lock(2000));

        Assert.False(apply.AllSucceeded);
        Assert.Equal(1800u, tuner.AppliedLockMHz);
        Assert.Equal(1800u, AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);
        dev.WriteResult = CtlResult.Success;
        Assert.True(tuner.EndProbe().Applied);
        Assert.Equal((100d, 1800d), (dev.Min, dev.Max));
    }

    [Fact]
    public void Arc_a_change_that_carries_the_lock_forward_mid_sweep_keeps_it()
    {
        // `set` without --lock-clock, MCP apply_tuning and the Tuning page build
        // their profile with LockedCoreClockMHz = AppliedLockMHz, which while a
        // pin stands is the displaced lock. That is a lock apply: the lock stays.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        Assert.Null(tuner.BeginProbe().Refusal);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(600));

        var carried = new TuningProfile { Name = "cli", LockedCoreClockMHz = tuner.AppliedLockMHz };
        Assert.Equal(1800u, carried.LockedCoreClockMHz);
        Assert.True(tuner.Apply(carried).AllSucceeded);

        Assert.True(tuner.EndProbe().Applied);
        Assert.Equal((100d, 1800d), (dev.Min, dev.Max));
        Assert.Equal(1800u, tuner.AppliedLockMHz);
        Assert.Equal(1800u, AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);
    }

    // --- Arc: a write to no lock mid-sweep -----------------------------------------

    [Theory]
    [InlineData(StockWrite.Reset, false)]
    [InlineData(StockWrite.Reset, true)]
    [InlineData(StockWrite.ForceUnlock, false)]
    [InlineData(StockWrite.ForceUnlock, true)]
    [InlineData(StockWrite.ExplicitUnlock, false)]
    [InlineData(StockWrite.ExplicitUnlock, true)]
    [InlineData(StockWrite.LocklessProfile, false)]
    [InlineData(StockWrite.LocklessProfile, true)]
    public void Arc_a_mid_sweep_write_to_no_lock_is_not_undone_by_the_sweeps_restore(StockWrite write, bool afterAPin)
    {
        // Every one of these ends with no lock outside a sweep, and must inside
        // one, whether it lands before the first pin or after one, and whether
        // or not a racing pin follows it. Before the fix the lock-less profile
        // that landed before the first pin left the displaced 1800 to be put back.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        Assert.Null(tuner.BeginProbe().Refusal);
        if (afterAPin)
        {
            Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(600));
        }

        Assert.True(Make(tuner, write), write.ToString());
        Assert.Null(tuner.AppliedLockMHz);

        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1100)); // the racing pin
        var end = tuner.EndProbe();

        Assert.True(end.Applied, end.Detail);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
        Assert.Null(tuner.AppliedLockMHz);
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Arc_a_lockless_profile_mid_sweep_removes_the_displaced_lock_and_says_so(bool afterAPin)
    {
        // A profile without a lock means "no lock": outside a sweep it releases
        // the lock (A_lockless_apply_releases_the_tracked_clamp_verified). The
        // lock the sweep displaced is that lock, so the profile removes it —
        // on record and in its own result — and EndProbe does not put it back.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        Assert.Null(tuner.BeginProbe().Refusal);
        if (afterAPin)
        {
            Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(600));
        }

        var apply = tuner.Apply(NoLock());

        Assert.True(apply.AllSucceeded, apply.Summary);
        var knob = Assert.Single(apply.Results, k => k.Knob == "clock lock");
        Assert.Contains("1800 MHz lock", knob.Detail, StringComparison.Ordinal);
        Assert.Null(tuner.AppliedLockMHz);
        Assert.Null(AppliedStateStore.Load(Uuid)?.LockedCoreClockMHz);

        var end = tuner.EndProbe();
        Assert.True(end.Applied, end.Detail);
        Assert.Equal((100d, 2300d), (dev.Min, dev.Max));
    }

    // --- Arc: the probe end to end -------------------------------------------------

    private static double VoltageAt(double clockMHz) => 700 + (clockMHz / 10);

    private static VfCurveProbe ArcProbe(ArcGpuTuner tuner, FakeArcDevice device) =>
        new(tuner, () => new GpuSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            DeviceIndex = 0,
            CoreClockMHz = (uint)device.Max,
            CoreVoltageMv = VoltageAt(device.Max),
            GpuUtilPct = 95,
        })
        {
            SettleSeconds = 0,
            SampleSeconds = 0.01,
            SampleIntervalMs = 2,
            StepMHz = 100, // 18 targets: the sweep is far from its end when the write lands
            LoadFactory = () => new FakeProbeLoad(),
        };

    /// <summary>Starts the probe on its own worker, as the app does; the returned event is set by its terminal report.</summary>
    private static ManualResetEventSlim StartProbe(VfCurveProbe probe, Action<VfProbeProgress> terminal)
    {
        var finished = new ManualResetEventSlim();
        probe.ProgressChanged += p =>
        {
            if (!p.Running)
            {
                terminal(p);
                finished.Set();
            }
        };
        probe.Start(new VfCurveRecorder());
        return finished;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1800u)]
    public void Arc_a_profile_lock_applied_from_another_thread_mid_sweep_is_what_stays(uint? userLock)
    {
        // The app's shape: the sweep on its worker, a hotkey's profile with a
        // 2000 MHz lock applied on another thread while a step is sampling.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        ApplyUserLock(tuner, userLock);
        var probe = ArcProbe(tuner, dev);
        probe.SampleSeconds = 5; // the write lands while the first step samples
        VfProbeProgress? final = null;
        using var finished = StartProbe(probe, p => final = p);
        try
        {
            Assert.True(dev.PinLanded.Wait(Wait, TestContext.Current.CancellationToken), "the probe never pinned");
            var apply = tuner.Apply(Lock(2000));
            Assert.True(apply.AllSucceeded, apply.Summary);
            Assert.True(finished.Wait(Wait, TestContext.Current.CancellationToken), "the probe kept sweeping");
        }
        finally
        {
            Assert.True(probe.CancelAndWait(Wait), "the worker did not stop");
        }

        Assert.Equal(VfProbeOutcome.Interrupted, final!.Outcome);
        Assert.False(final.RestoreFailed, final.Phase);
        Assert.Equal(600d, Assert.Single(dev.PinnedTargets));
        Assert.Equal((100d, 2000d), (dev.Min, dev.Max));
        Assert.Equal(2000u, tuner.AppliedLockMHz);
        var record = AppliedStateStore.Load(Uuid);
        Assert.Equal(2000u, record?.LockedCoreClockMHz);
        Assert.NotEqual(true, record?.ProbeLockPending);
    }

    [Fact]
    public void Arc_a_pin_racing_a_lock_applied_from_another_thread_is_replaced_by_that_lock()
    {
        // The race the probe's own comment concedes, forced: a subscriber ahead
        // of the probe's holds the applying thread inside its notification until
        // the sweep has landed another pin over the new lock. Only then does the
        // probe hear of the write and stop.
        var dev = new FakeArcDevice();
        var tuner = Tuner(dev);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        var probe = ArcProbe(tuner, dev);
        using var firstPin = new ManualResetEventSlim();
        using var racingPin = new ManualResetEventSlim();
        int applyThread = -1;
        bool applyLanded = false;
        tuner.StateWritten += _ => // subscribed before the probe's handler, so it runs first
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref applyThread))
            {
                // No assert here: the tuner logs and swallows a subscriber's
                // exception. The test checks racingPin once Apply returns.
                Volatile.Write(ref applyLanded, true);
                racingPin.Wait(Wait);
            }
            else if (Volatile.Read(ref applyLanded))
            {
                racingPin.Set();
            }
            else if (dev.PinLanded.IsSet)
            {
                firstPin.Set();
            }
        };
        VfProbeProgress? final = null;
        using var finished = StartProbe(probe, p => final = p);
        try
        {
            Assert.True(firstPin.Wait(Wait, TestContext.Current.CancellationToken), "the probe never pinned");
            Volatile.Write(ref applyThread, Environment.CurrentManagedThreadId);
            Assert.True(tuner.Apply(Lock(2000)).AllSucceeded);
            Assert.True(racingPin.IsSet, "the race was not forced");
            Assert.True(finished.Wait(Wait, TestContext.Current.CancellationToken), "the probe kept sweeping");
        }
        finally
        {
            Assert.True(probe.CancelAndWait(Wait), "the worker did not stop");
        }

        Assert.Equal(VfProbeOutcome.Interrupted, final!.Outcome);
        Assert.False(final.RestoreFailed, final.Phase);
        int lockWrite = dev.Writes.IndexOf((100d, 2000d));
        Assert.True(lockWrite >= 0, "the 2000 MHz lock was never written");
        Assert.Contains(dev.Writes.Skip(lockWrite + 1), w => w.Min > 0 && w.Min == w.Max); // a pin landed over it
        Assert.Equal((100d, 2000d), (dev.Min, dev.Max));
        Assert.Equal(2000u, tuner.AppliedLockMHz);
        var record = AppliedStateStore.Load(Uuid);
        Assert.Equal(2000u, record?.LockedCoreClockMHz);
        Assert.NotEqual(true, record?.ProbeLockPending);
    }

    // --- NVIDIA: the same sequences ------------------------------------------------
    // The NVIDIA tuner keeps the probe's pin apart from the applied lock and
    // EndProbe restores the applied lock as it stands at the end, so a write
    // made mid-sweep is what it restores. These pass before and after the Arc
    // fix; they hold the two vendors to one answer.

    private const uint NvidiaFloorMHz = 210; // GpuTuner's range-lock floor

    private static GpuTuner Nvidia(FakeNvidiaDriver driver) => new(driver, driver, driver.Uuid);

    [Theory]
    [InlineData(null)]
    [InlineData(1800u)]
    public void Nvidia_a_lock_applied_mid_sweep_is_the_lock_the_sweep_leaves_even_under_a_racing_pin(uint? userLock)
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Nvidia(driver);
        ApplyUserLock(tuner, userLock);
        Assert.Null(tuner.BeginProbe().Refusal);
        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(600));
        Assert.True(tuner.Apply(Lock(2000)).AllSucceeded);

        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1100)); // the racing pin
        Assert.Equal(2000u, tuner.AppliedLockMHz);
        var end = tuner.EndProbe();

        Assert.True(end.Applied, end.Detail);
        Assert.Equal((NvidiaFloorMHz, 2000u), driver.LockedClocks);
        Assert.Equal(2000u, tuner.AppliedLockMHz);
        var record = AppliedStateStore.Load(driver.Uuid);
        Assert.Equal(2000u, record?.LockedCoreClockMHz);
        Assert.NotEqual(true, record?.ProbeLockPending);
    }

    [Theory]
    [InlineData(StockWrite.Reset, false)]
    [InlineData(StockWrite.Reset, true)]
    [InlineData(StockWrite.ForceUnlock, false)]
    [InlineData(StockWrite.ForceUnlock, true)]
    [InlineData(StockWrite.ExplicitUnlock, false)]
    [InlineData(StockWrite.ExplicitUnlock, true)]
    [InlineData(StockWrite.LocklessProfile, false)]
    [InlineData(StockWrite.LocklessProfile, true)]
    public void Nvidia_a_mid_sweep_write_to_no_lock_is_not_undone_by_the_sweeps_restore(StockWrite write, bool afterAPin)
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Nvidia(driver);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        Assert.Null(tuner.BeginProbe().Refusal);
        if (afterAPin)
        {
            Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(600));
        }

        Assert.True(Make(tuner, write), write.ToString());
        Assert.Null(tuner.AppliedLockMHz);

        Assert.Equal(NvmlReturn.Success, tuner.LockClockForProbe(1100)); // the racing pin
        var end = tuner.EndProbe();

        Assert.True(end.Applied, end.Detail);
        Assert.Null(driver.LockedClocks);
        Assert.Null(tuner.AppliedLockMHz);
        Assert.NotEqual(true, AppliedStateStore.Load(driver.Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void Nvidia_a_profile_lock_applied_from_another_thread_mid_sweep_is_what_stays()
    {
        var driver = new FakeNvidiaDriver();
        var tuner = Nvidia(driver);
        Assert.True(tuner.Apply(Lock(1800)).AllSucceeded);
        var probe = new VfCurveProbe(tuner, () =>
        {
            uint clock = driver.LockedClocks?.Max ?? 1500;
            return new GpuSnapshot
            {
                Timestamp = DateTimeOffset.Now,
                DeviceIndex = 0,
                CoreClockMHz = clock,
                CoreVoltageMv = VoltageAt(clock),
                GpuUtilPct = 95,
            };
        })
        {
            SettleSeconds = 0,
            SampleSeconds = 5, // the write lands while the first step samples
            SampleIntervalMs = 2,
            StepMHz = 500,
            LoadFactory = () => new FakeProbeLoad(),
        };
        using var pinned = new ManualResetEventSlim();
        tuner.StateWritten += _ =>
        {
            if (driver.LockedClocks is (uint min, uint max) && min == max)
            {
                pinned.Set();
            }
        };
        VfProbeProgress? final = null;
        using var finished = StartProbe(probe, p => final = p);
        try
        {
            Assert.True(pinned.Wait(Wait, TestContext.Current.CancellationToken), "the probe never pinned");
            Assert.True(tuner.Apply(Lock(2000)).AllSucceeded);
            Assert.True(finished.Wait(Wait, TestContext.Current.CancellationToken), "the probe kept sweeping");
        }
        finally
        {
            Assert.True(probe.CancelAndWait(Wait), "the worker did not stop");
        }

        Assert.Equal(VfProbeOutcome.Interrupted, final!.Outcome);
        Assert.False(final.RestoreFailed, final.Phase);
        Assert.Equal((NvidiaFloorMHz, 2000u), driver.LockedClocks);
        Assert.Equal(2000u, tuner.AppliedLockMHz);
        var record = AppliedStateStore.Load(driver.Uuid);
        Assert.Equal(2000u, record?.LockedCoreClockMHz);
        Assert.NotEqual(true, record?.ProbeLockPending);
    }
}
