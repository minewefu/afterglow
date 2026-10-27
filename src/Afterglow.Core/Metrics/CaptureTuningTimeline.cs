using Afterglow.Core.Tuning;

namespace Afterglow.Core.Metrics;

/// <summary>
/// One reading of the tuning a card runs, as a session report labels it. A null
/// knob is one the card does not have (Intel Arc has no clock offsets) or did
/// not report.
/// </summary>
public readonly record struct TuningReading(
    int? CoreOffsetMHz,
    int? MemOffsetMHz,
    double? PowerLimitW,
    uint? VoltageBoostPct,
    uint? LockedCoreClockMHz)
{
    /// <summary>
    /// The card's applied values, offsets only where the card has them; null when
    /// the read fails. This runs on every refresh of a capture, so a failed read
    /// is "no reading this second", never an exception out of the UI timer.
    /// </summary>
    public static TuningReading? Read(IGpuTuner tuner)
    {
        ArgumentNullException.ThrowIfNull(tuner);
        try
        {
            var (core, mem, powerW, boostPct, lockMHz) = tuner.ReadCurrent();
            var caps = tuner.Capabilities;
            return new TuningReading(
                caps.SupportsCoreOffset ? core : null,
                caps.SupportsMemOffset ? mem : null,
                powerW,
                boostPct,
                lockMHz);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether two readings are the same tuning. The power limit is a read-back,
    /// so it is compared to within half a watt rather than bit for bit.
    /// </summary>
    public bool SameTuning(TuningReading other) =>
        CoreOffsetMHz == other.CoreOffsetMHz
        && MemOffsetMHz == other.MemOffsetMHz
        && VoltageBoostPct == other.VoltageBoostPct
        && LockedCoreClockMHz == other.LockedCoreClockMHz
        && (PowerLimitW, other.PowerLimitW) switch
        {
            (null, null) => true,
            ({ } a, { } b) => Math.Abs(a - b) < 0.5,
            _ => false,
        };
}

/// <summary>The tuning a session report is labelled with, and whether its measured window spans a change.</summary>
public readonly record struct TuningAttribution(TuningReading? Reading, bool ChangedDuringWindow);

/// <summary>
/// The tuning a capture ran under, recorded as the capture goes, so a session
/// report is labelled with what was applied while its frames were presented —
/// not with whatever the card runs at the moment Stop is clicked (R-28). In the
/// game-profile workflow the game's exit restores the baseline before the user
/// stops the capture, and the report read at Stop stamped the tuned run with
/// the untuned settings, inverting every before/after comparison built on it.
/// <para>
/// How a change is placed. The caller adds a reading of the card on every
/// refresh, then reads the target's stats, then checks that the target's
/// process is still running and, if it is, confirms it here. A reading added
/// before a confirmation was taken while the process was running, so the change
/// it shows happened while the process ran (or before it started) and can have
/// shaped its frames. A reading added after the process's last confirmation
/// cannot be placed that way and is not used for it. A game-exit restore is
/// applied only once the game's process is gone, so every reading that shows
/// it comes after the game's last confirmation: it can never relabel the run,
/// whatever the timing of the restore against the stats' freshness window or
/// PresentMon's output latency. The comparison is made on process liveness,
/// not on frame arrival times, precisely so that neither can blur it.
/// </para>
/// <para>
/// What it cannot see: a change made in the final refresh interval before the
/// process exits (about a second) labels nothing, so those last frames are
/// counted under the previous tuning. A change inside the measured window is
/// reported as <see cref="TuningAttribution.ChangedDuringWindow"/> rather than
/// hidden: the window's numbers then mix two configurations. The window's start
/// is placed early by the freshness limit, and a change is dated by the reading
/// that first showed it, so the flag errs toward "changed" by a second or two.
/// </para>
/// Not thread-safe: one owner (the Metrics page, on the UI thread).
/// </summary>
public sealed class CaptureTuningTimeline
{
    private readonly List<(long Sequence, long AtMs, TuningReading Reading)> _changes = [];
    private readonly Dictionary<int, long> _confirmedRunning = [];
    private long _sequence;

    /// <summary>
    /// Records a reading of the card taken at <paramref name="nowMs"/> (any
    /// monotonic millisecond clock, the same one passed to <see cref="AttributeTo"/>).
    /// A failed read (null) adds nothing; an unchanged reading only advances the order.
    /// </summary>
    public void AddReading(long nowMs, TuningReading? reading)
    {
        _sequence++;
        if (reading is not { } value)
        {
            return;
        }

        if (_changes.Count > 0 && _changes[^1].Reading.SameTuning(value))
        {
            return;
        }

        _changes.Add((_sequence, nowMs, value));
    }

    /// <summary>
    /// Records that the process <paramref name="pid"/> was seen running after
    /// every reading added so far.
    /// </summary>
    public void ConfirmRunning(int pid) => _confirmedRunning[pid] = ++_sequence;

    /// <summary>The tuning to label <paramref name="pid"/>'s frame statistics with.</summary>
    /// <param name="pid">The process the statistics belong to.</param>
    /// <param name="statsTakenAtMs">When the statistics were read, on the readings' clock.</param>
    /// <param name="stats">The statistics: their frame count and average frametime span the window.</param>
    public TuningAttribution AttributeTo(int pid, long statsTakenAtMs, FrameWindowStats stats)
    {
        if (_changes.Count == 0)
        {
            return default;
        }

        // The window's frames were presented from about here on: its span is the
        // sum of its frametimes, and its last frame may have arrived up to the
        // freshness limit before the statistics were read.
        double windowStartMs = statsTakenAtMs - (stats.FrameCount * stats.AverageFrametimeMs)
            - FrameMetricsService.StaleAfterMs;

        if (_confirmedRunning.TryGetValue(pid, out long confirmed) && _changes[0].Sequence < confirmed)
        {
            // The last change read while the process was still running.
            int index = _changes.FindLastIndex(c => c.Sequence < confirmed);
            bool changed = index > 0 && _changes[index].AtMs > windowStartMs;
            return new TuningAttribution(_changes[index].Reading, changed);
        }

        // Never seen running after a reading (its liveness could not be read):
        // the tuning at the start of its window, and any change between then and
        // the statistics counts as a mix.
        int atStart = Math.Max(0, _changes.FindLastIndex(c => c.AtMs <= windowStartMs));
        bool changedLater = _changes.Skip(atStart + 1).Any(c => c.AtMs <= statsTakenAtMs);
        return new TuningAttribution(_changes[atStart].Reading, changedLater);
    }
}
