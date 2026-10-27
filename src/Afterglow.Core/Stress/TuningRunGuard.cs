using System.Diagnostics;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Stress;

/// <summary>
/// The tuning knobs a run compares to decide whether the card still holds what
/// the run applied. Read back from the driver, so the values are the CLAMPED
/// ones the run actually put on the card, not the ones it asked for.
/// </summary>
internal readonly record struct TuningReadback(int CoreOffsetMHz, int MemOffsetMHz, uint? VoltageBoostPct, uint? LockedCoreClockMHz)
{
    public static TuningReadback Read(IGpuTuner tuner)
    {
        var (core, mem, _, boost, lockMHz) = tuner.ReadCurrent();
        return new TuningReadback(core, mem, boost, lockMHz);
    }
}

/// <summary>
/// Ownership of one card's tuning for the length of one certification or
/// stepper run (R-02).
/// <para>
/// A run's verdict is about the configuration it applied, so it has to know
/// whether that configuration stayed on the card while it burned. Ordinary
/// Apply and Reset, profile hotkeys, game rules, automation and the panic reset
/// all write the same tuner, and nothing stopped them mid-run: the certifier's
/// old check only recognised "every knob back at stock", so +100 MHz moved to
/// +25 passed as unchanged, and the stepper scored a step the device had been
/// reset under, then re-applied its offset over the user's reset.
/// </para>
/// <para>
/// Two witnesses, because neither covers everything. Every in-process write
/// raises <see cref="IGpuTuner.StateWritten"/> on the writing thread; the run
/// marks its own writes (<see cref="Own"/>), so any other notification is
/// someone else's — even one that wrote the same values back, or changed
/// something and changed it back before a readback could see it. A change from
/// outside the process (another tool, the CLI beside the app, the driver
/// itself) raises nothing, so <see cref="Drift"/> also compares the card's
/// readback with what the run applied.
/// </para>
/// </summary>
internal sealed class TuningRunGuard : IDisposable
{
    /// <summary>
    /// The Arc write paths accept a clamp readback within 1 MHz of the request
    /// and both sides round independently (see ArcGpuTuner), so 2 MHz.
    /// </summary>
    private const long LockToleranceMHz = 2;

    private readonly IGpuTuner _tuner;
    private readonly int _ownerThreadId;
    private int _ownWriteDepth;
    private int _foreignWrites;
    private int _foreignDuringOwnWrite;
    private TuningReadback? _applied;

    /// <summary>Starts watching; must be created on the run's own thread, which is the only one that may call <see cref="Own"/>.</summary>
    public TuningRunGuard(IGpuTuner tuner)
    {
        _tuner = tuner;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        tuner.StateWritten += OnStateWritten;
    }

    /// <summary>True once any write this run did not make has been reported.</summary>
    public bool Interrupted => Volatile.Read(ref _foreignWrites) > 0;

    /// <summary>
    /// A foreign write was reported while one of this run's own writes was in
    /// flight. The tuner serialises the writes but not their notifications, so
    /// which one landed last cannot be known — the card may hold either.
    /// </summary>
    public bool InterruptedDuringOwnWrite => Volatile.Read(ref _foreignDuringOwnWrite) > 0;

    /// <summary>Runs one of this run's own writes, so its notification is not mistaken for another writer's.</summary>
    public T Own<T>(Func<T> write)
    {
        Debug.Assert(Environment.CurrentManagedThreadId == _ownerThreadId, "a run's writes come from its own thread");
        Interlocked.Increment(ref _ownWriteDepth);
        try
        {
            return write();
        }
        finally
        {
            Interlocked.Decrement(ref _ownWriteDepth);
        }
    }

    private void OnStateWritten(IGpuTuner sender)
    {
        bool ownWriteInFlight = Volatile.Read(ref _ownWriteDepth) > 0;
        if (ownWriteInFlight && Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            return;
        }

        if (ownWriteInFlight)
        {
            Interlocked.Increment(ref _foreignDuringOwnWrite);
        }

        Interlocked.Increment(ref _foreignWrites);
    }

    /// <summary>
    /// Records what the card reads right after the run's own apply, as the
    /// configuration every later readback must still match. A failed getter
    /// reads 0/null, so two consecutive reads must agree before one is trusted
    /// (a third breaks a tie); if none agree the last read stands, and a later
    /// comparison that still disagrees ends the run rather than passing it.
    /// </summary>
    public TuningReadback CaptureApplied()
    {
        var previous = TuningReadback.Read(_tuner);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var next = TuningReadback.Read(_tuner);
            if (Describe(previous, next) is null)
            {
                _applied = next;
                return next;
            }

            previous = next;
        }

        _applied = previous;
        return previous;
    }

    /// <summary>
    /// Null while the card still reads the configuration this run applied;
    /// otherwise which knob moved. A single disagreeing read is corroborated
    /// first: a transient getter failure must not throw away a mode that just
    /// burned for 90 s, while a real change stays changed.
    /// </summary>
    public string? Drift()
    {
        if (_applied is not { } applied)
        {
            return null;
        }

        if (Describe(applied, TuningReadback.Read(_tuner)) is null)
        {
            return null;
        }

        return Describe(applied, TuningReadback.Read(_tuner));
    }

    /// <summary>
    /// Which knob differs between two readbacks, among the ones that are real
    /// driver readbacks of settings. The power limit is deliberately not one:
    /// the driver's ENFORCED limit can move under platform power management
    /// (laptop dynamic boost) with nobody writing anything, which would end
    /// runs that nothing interrupted.
    /// </summary>
    private string? Describe(TuningReadback was, TuningReadback now)
    {
        var caps = _tuner.Capabilities;
        if (caps.SupportsCoreOffset && now.CoreOffsetMHz != was.CoreOffsetMHz)
        {
            return $"the core offset reads {Fmt(now.CoreOffsetMHz)} instead of the {Fmt(was.CoreOffsetMHz)} this run applied";
        }

        if (caps.SupportsMemOffset && now.MemOffsetMHz != was.MemOffsetMHz)
        {
            return $"the memory offset reads {Fmt(now.MemOffsetMHz)} instead of the {Fmt(was.MemOffsetMHz)} this run applied";
        }

        // A failed voltage-boost read is null; only two real readings compare.
        if (was.VoltageBoostPct is uint wasBoost && now.VoltageBoostPct is uint nowBoost && wasBoost != nowBoost)
        {
            return $"the voltage boost reads {nowBoost}% instead of the {wasBoost}% this run applied";
        }

        // NVML has no locked-clock getter: there the "lock" is an in-process
        // shadow, which only in-process writes change — and those are already
        // reported through StateWritten.
        if (_tuner.LockIsDriverReadable)
        {
            switch (was.LockedCoreClockMHz, now.LockedCoreClockMHz)
            {
                case (null, uint appeared):
                    return $"the GPU is now clamped to {appeared} MHz, a cap this run did not apply";
                case (uint gone, null):
                    return $"the {gone} MHz clock clamp this run applied has been released";
                case (uint a, uint b) when Math.Abs((long)a - b) > LockToleranceMHz:
                    return $"the clock clamp reads {b} MHz instead of the {a} MHz this run applied";
                default:
                    break;
            }
        }

        return null;
    }

    public void Dispose() => _tuner.StateWritten -= OnStateWritten;

    private static string Fmt(int offset) => offset >= 0 ? $"+{offset} MHz" : $"{offset} MHz";
}
