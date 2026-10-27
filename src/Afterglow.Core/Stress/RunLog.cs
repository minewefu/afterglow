using System.Collections;
using System.Globalization;

namespace Afterglow.Core.Stress;

/// <summary>
/// One run's log — a stepper sweep's or a certification's — kept to its newest
/// <see cref="Capacity"/> lines (#7).
/// <para>
/// The app used to show only the last 10–14 lines of a list that had no limit
/// at all (MCP replies carried all of it). Now the app shows the whole log too,
/// and the cap is what bounds the app, MCP replies and CLI output. A real run
/// writes far less: two lines a step, so a stepper sweep at the app's smallest
/// step (+10 MHz up to +600) is ~130 lines with its preamble and verdict, at
/// MCP's smallest (+5 MHz) ~250, and a certification about 30. 5,000 lines is
/// 20× the longest of those, so no real run is cut short, while at ~150
/// characters a line it bounds a log at roughly 1.5 MB of strings and an MCP
/// reply at about 0.75 MB of JSON. Past it, the oldest line goes and a single
/// marker line — "… N earlier lines not kept" — stands in for everything dropped.
/// </para>
/// <para>
/// <see cref="Snapshot"/> is what a status publishes. The burn reports progress
/// about once a second and every report republishes the status; the old
/// <c>_log.ToArray()</c> copied the whole log on each one. A snapshot is now
/// cached until a line is added, so a progress tick costs nothing more and a
/// copy happens once per new line.
/// </para>
/// Not thread-safe: the owner takes its own lock around every call, as it did
/// around the list this replaces.
/// </summary>
public sealed class RunLog
{
    /// <summary>The cap for a stepper or certification run; see the class remarks for why 5,000.</summary>
    public const int DefaultCapacity = 5000;

    private readonly Queue<string> _lines = new();
    private object _run = new();
    private long _dropped;
    private RunLogLines? _snapshot;

    public RunLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>How many of the newest lines are kept (the marker comes on top).</summary>
    public int Capacity { get; }

    public void Add(string line)
    {
        if (_lines.Count == Capacity)
        {
            _lines.Dequeue();
            _dropped++;
        }

        _lines.Enqueue(line);
        _snapshot = null;
    }

    /// <summary>Empties the log for a new run. Snapshots taken before belong to the old run.</summary>
    public void Clear()
    {
        _lines.Clear();
        _dropped = 0;
        _run = new object();
        _snapshot = null;
    }

    /// <summary>
    /// The log as it stands: the marker first if lines were dropped, then the
    /// kept lines, oldest first. The same instance comes back until the log changes.
    /// </summary>
    public RunLogLines Snapshot() => _snapshot ??= new RunLogLines(_run, _dropped, _lines.ToArray());
}

/// <summary>
/// An immutable snapshot of a <see cref="RunLog"/>: what a stepper or
/// certification status carries as its log. It reads as a plain list of lines;
/// <see cref="Dropped"/> and <see cref="Written"/> number them across the whole
/// run, so a viewer that has shown an earlier snapshot can add only the lines
/// that are new (<see cref="RunLogCursor"/>).
/// </summary>
public sealed class RunLogLines : IReadOnlyList<string>
{
    /// <summary>A log with nothing in it, belonging to no run.</summary>
    public static RunLogLines Empty { get; } = new(new object(), 0, []);

    private readonly object _run;
    private readonly string[] _kept;
    private readonly string? _marker;

    internal RunLogLines(object run, long dropped, string[] kept)
    {
        _run = run;
        _kept = kept;
        Dropped = dropped;
        _marker = dropped > 0 ? Marker(dropped) : null;
    }

    /// <summary>Lines this run logged that are no longer kept; the marker line stands for them.</summary>
    public long Dropped { get; }

    /// <summary>Every line this run has logged, kept or not (the marker is not one of them).</summary>
    public long Written => Dropped + _kept.Length;

    /// <summary>Lines in this snapshot, counting the marker.</summary>
    public int Count => _kept.Length + (_marker is null ? 0 : 1);

    public string this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _marker is null ? _kept[index] : index == 0 ? _marker : _kept[index - 1];
        }
    }

    /// <summary>The line that stands in for <paramref name="dropped"/> lines no longer kept.</summary>
    public static string Marker(long dropped) => string.Create(
        CultureInfo.InvariantCulture, $"… {dropped:N0} earlier {(dropped == 1 ? "line" : "lines")} not kept");

    /// <summary>True when both snapshots come from the same run of the same log.</summary>
    public bool IsSameRun(RunLogLines other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(_run, other._run);
    }

    public IEnumerator<string> GetEnumerator()
    {
        if (_marker is not null)
        {
            yield return _marker;
        }

        foreach (string line in _kept)
        {
            yield return line;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// What a viewer must do to go from what it shows to a newer snapshot:
/// <see cref="Reset"/> means clear and show the whole snapshot; otherwise add
/// the snapshot's lines from index <see cref="From"/> on (none when
/// <see cref="From"/> equals its count).
/// </summary>
public readonly record struct RunLogUpdate(bool Reset, int From);

/// <summary>
/// Remembers how much of a run's log a viewer has shown, so each new snapshot
/// costs only its new lines: the app's log boxes append them, and the CLI
/// prints them. Re-rendering the whole log for every status would be O(lines)
/// per update, and in a text box it would also reset the reader's selection.
/// </summary>
public sealed class RunLogCursor
{
    private readonly long _maxShown;
    private RunLogLines? _shown;
    private long _shownFrom;

    /// <param name="maxShown">
    /// The most lines the viewer may hold. A viewer that only appends keeps
    /// lines the log itself has since dropped; past this many, the next update
    /// is a <see cref="RunLogUpdate.Reset"/> to the snapshot, which brings the
    /// viewer back to the log's own cap. Unbounded by default (a console prints
    /// and forgets).
    /// </param>
    public RunLogCursor(long maxShown = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxShown, 1);
        _maxShown = maxShown;
    }

    /// <summary>Lines the viewer holds after the last update, counting a marker line.</summary>
    public long Shown => _shown is null ? 0 : _shown.Written - _shownFrom + (_shownFrom > 0 ? 1 : 0);

    /// <summary>
    /// Takes the next snapshot and says how to show it. Anything that is not a
    /// <see cref="RunLogLines"/> carries no numbering, so it is always a reset.
    /// </summary>
    public RunLogUpdate Advance(IReadOnlyList<string> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next is not RunLogLines lines)
        {
            _shown = null;
            _shownFrom = 0;
            return new RunLogUpdate(Reset: true, From: 0);
        }

        var shown = _shown;
        if (shown is not null && lines.IsSameRun(shown) && lines.Written <= shown.Written)
        {
            // Nothing new — or an older snapshot of this run arriving late. A
            // status is published from the run's thread and from the burn's
            // progress thread, so two can reach a handler out of order; the
            // viewer already holds everything this one has.
            return new RunLogUpdate(Reset: false, From: lines.Count);
        }

        _shown = lines;
        if (shown is null
            || !lines.IsSameRun(shown)

            // Lines were dropped that the viewer never saw. Appending the rest
            // would close the gap silently; the reset brings the marker.
            || lines.Dropped > shown.Written

            // Appending would take the viewer past its own bound.
            || lines.Written - _shownFrom + (_shownFrom > 0 ? 1 : 0) > _maxShown)
        {
            _shownFrom = lines.Dropped;
            return new RunLogUpdate(Reset: true, From: 0);
        }

        // The first line the viewer has not shown, as an index into this snapshot.
        long firstNew = shown.Written - lines.Dropped + (lines.Dropped > 0 ? 1 : 0);
        return new RunLogUpdate(Reset: false, From: (int)firstNew);
    }
}
