using Afterglow.Core.Stress;
using Afterglow.Core.Tests.Fakes;

namespace Afterglow.Core.Tests;

/// <summary>
/// The stepper's and the certifier's run log (#7): the app now shows all of it,
/// so it is capped, says how much it dropped, and numbers its lines so viewers
/// (the app's log boxes, the CLI) add only what is new.
/// </summary>
public class RunLogTests
{
    private static RunLog Filled(int capacity, int lines)
    {
        var log = new RunLog(capacity);
        for (int i = 0; i < lines; i++)
        {
            log.Add($"line {i}");
        }

        return log;
    }

    [Fact]
    public void Under_the_cap_every_line_is_kept_and_there_is_no_marker()
    {
        var lines = Filled(capacity: 5, lines: 5).Snapshot();

        Assert.Equal(["line 0", "line 1", "line 2", "line 3", "line 4"], lines);
        Assert.Equal(0, lines.Dropped);
        Assert.Equal(5, lines.Written);
    }

    [Fact]
    public void Past_the_cap_the_oldest_lines_go_and_one_marker_says_how_many()
    {
        var one = Filled(capacity: 5, lines: 6).Snapshot();
        Assert.Equal(["… 1 earlier line not kept", "line 1", "line 2", "line 3", "line 4", "line 5"], one);
        Assert.Equal(1, one.Dropped);
        Assert.Equal(6, one.Written);

        var many = Filled(capacity: 5, lines: 9).Snapshot();
        Assert.Equal(["… 4 earlier lines not kept", "line 4", "line 5", "line 6", "line 7", "line 8"], many);
        Assert.Single(many, line => line.StartsWith('…'));
    }

    [Fact]
    public void The_default_cap_is_5000_lines_plus_the_marker()
    {
        Assert.Equal(5000, RunLog.DefaultCapacity);
        Assert.Equal(5000, new RunLog().Capacity);

        var full = Filled(RunLog.DefaultCapacity, 5000).Snapshot();
        Assert.Equal(5000, full.Count);
        Assert.Equal("line 0", full[0]);

        var over = Filled(RunLog.DefaultCapacity, 12_345).Snapshot();
        Assert.Equal(5001, over.Count);
        Assert.Equal("… 7,345 earlier lines not kept", over[0]);
        Assert.Equal("line 7345", over[1]);
        Assert.Equal("line 12344", over[^1]);
        Assert.Equal(12_345, over.Written);
    }

    [Fact]
    public void The_marker_reads_the_same_in_any_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("… 7,345 earlier lines not kept", RunLogLines.Marker(7345));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    [Fact]
    public void A_snapshot_is_reused_until_a_line_is_added()
    {
        // Every burn progress tick republishes the status; that must not copy
        // the log again when no line was added.
        var log = Filled(capacity: 100, lines: 10);
        var first = log.Snapshot();

        Assert.Same(first, log.Snapshot());

        log.Add("line 10");
        var second = log.Snapshot();
        Assert.NotSame(first, second);
        Assert.Equal(10, first.Count);
        Assert.Equal(11, second.Count);
        Assert.True(second.IsSameRun(first));
    }

    [Fact]
    public void Clear_starts_a_new_run()
    {
        var log = Filled(capacity: 3, lines: 7);
        var before = log.Snapshot();

        log.Clear();
        var after = log.Snapshot();

        Assert.Empty(after);
        Assert.Equal(0, after.Dropped);
        Assert.False(after.IsSameRun(before));
        Assert.False(RunLogLines.Empty.IsSameRun(after));
    }

    [Fact]
    public void Indexing_outside_the_snapshot_throws()
    {
        var lines = Filled(capacity: 2, lines: 3).Snapshot();

        Assert.Equal(3, lines.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => lines[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => lines[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunLog(0));
    }

    [Fact]
    public void The_stepper_publishes_its_log_as_numbered_lines()
    {
        // The idle status already carries the type every run publishes, so the
        // app's log box and the CLI can number its lines.
        Assert.IsType<RunLogLines>(new StabilityStepper(new FakeTuner()).Status.Log);
    }

    [Fact]
    public void A_viewer_starts_with_everything_then_adds_only_new_lines()
    {
        var log = Filled(capacity: 100, lines: 3);
        var cursor = new RunLogCursor();

        Assert.Equal(new RunLogUpdate(Reset: true, From: 0), cursor.Advance(log.Snapshot()));

        log.Add("line 3");
        log.Add("line 4");
        var next = log.Snapshot();
        Assert.Equal(new RunLogUpdate(Reset: false, From: 3), cursor.Advance(next));

        // A progress tick: same snapshot, nothing to add.
        Assert.Equal(new RunLogUpdate(Reset: false, From: next.Count), cursor.Advance(next));
        Assert.Equal(5, cursor.Shown);
    }

    [Fact]
    public void An_older_snapshot_arriving_late_changes_nothing()
    {
        // The run thread and the burn's progress thread both publish, so two
        // statuses can reach a handler out of order.
        var log = Filled(capacity: 100, lines: 3);
        var older = log.Snapshot();
        log.Add("line 3");
        var newer = log.Snapshot();
        var cursor = new RunLogCursor();
        cursor.Advance(newer);

        Assert.Equal(new RunLogUpdate(Reset: false, From: older.Count), cursor.Advance(older));

        log.Add("line 4");
        Assert.Equal(new RunLogUpdate(Reset: false, From: 4), cursor.Advance(log.Snapshot()));
    }

    [Fact]
    public void A_new_run_or_a_plain_list_is_a_reset()
    {
        var log = Filled(capacity: 100, lines: 3);
        var cursor = new RunLogCursor();
        cursor.Advance(log.Snapshot());

        log.Clear();
        log.Add("new run");
        Assert.True(cursor.Advance(log.Snapshot()).Reset);

        Assert.True(cursor.Advance(["not numbered"]).Reset);
        Assert.True(cursor.Advance(RunLogLines.Empty).Reset);
    }

    [Fact]
    public void Past_the_cap_a_viewer_appends_after_the_marker()
    {
        var log = Filled(capacity: 5, lines: 5);
        var cursor = new RunLogCursor();
        cursor.Advance(log.Snapshot());

        log.Add("line 5");
        log.Add("line 6");
        var next = log.Snapshot(); // marker, line 2 .. line 6
        var update = cursor.Advance(next);

        Assert.False(update.Reset);
        Assert.Equal(["line 5", "line 6"], next.Skip(update.From));
    }

    [Fact]
    public void Lines_dropped_before_a_viewer_saw_them_force_a_reset_with_the_marker()
    {
        var log = Filled(capacity: 5, lines: 2);
        var cursor = new RunLogCursor();
        cursor.Advance(log.Snapshot());

        for (int i = 2; i < 10; i++)
        {
            log.Add($"line {i}"); // lines 2..4 are dropped unseen
        }

        Assert.Equal(new RunLogUpdate(Reset: true, From: 0), cursor.Advance(log.Snapshot()));
    }

    [Fact]
    public void A_bounded_viewer_resets_to_the_log_once_it_holds_too_much()
    {
        var log = Filled(capacity: 10, lines: 10);
        var cursor = new RunLogCursor(maxShown: 15);
        cursor.Advance(log.Snapshot());

        for (int i = 10; i < 15; i++)
        {
            log.Add($"line {i}");
            Assert.False(cursor.Advance(log.Snapshot()).Reset);
        }

        // Lines 0..14: the log itself dropped 0..4, the viewer still holds them.
        Assert.Equal(15, cursor.Shown);
        log.Add("line 15");
        Assert.True(cursor.Advance(log.Snapshot()).Reset);
        Assert.Equal(11, cursor.Shown); // marker + the 10 kept
    }

    /// <summary>
    /// Plays a run into a viewer the way the app's log box does — append or
    /// reset as told — and checks after every snapshot that it shows a true,
    /// contiguous tail of the run under at most one marker, within its bound.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void A_viewer_that_follows_the_updates_always_shows_a_true_tail_of_the_run(int seed)
    {
        const int Capacity = 50;
        const int MaxShown = 120;
        var random = new Random(seed);
        var log = new RunLog(Capacity);
        var cursor = new RunLogCursor(MaxShown);
        var screen = new List<string>();
        int written = 0;

        for (int round = 0; round < 400; round++)
        {
            // Mostly a line or two per status, sometimes a burst past the cap.
            int batch = random.Next(10) == 0 ? random.Next(Capacity, 2 * Capacity) : random.Next(0, 3);
            for (int i = 0; i < batch; i++)
            {
                log.Add($"line {written++}");
            }

            var snapshot = log.Snapshot();
            var update = cursor.Advance(snapshot);
            if (update.Reset)
            {
                screen.Clear();
                screen.AddRange(snapshot);
            }
            else
            {
                screen.AddRange(snapshot.Skip(update.From));
            }

            Assert.True(screen.Count <= MaxShown, $"round {round}: {screen.Count} lines on screen");
            Assert.Equal(cursor.Shown, screen.Count);

            int first = 0;
            if (screen.Count > 0 && screen[0].StartsWith('…'))
            {
                first = int.Parse(screen[0].Split(' ')[1].Replace(",", string.Empty, StringComparison.Ordinal),
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(RunLogLines.Marker(first), screen[0]);
                screen.RemoveAt(0);
            }

            Assert.Equal(Enumerable.Range(first, written - first).Select(n => $"line {n}"), screen);
            if (first > 0)
            {
                screen.Insert(0, RunLogLines.Marker(first));
            }
        }
    }

    [Fact]
    public void An_unbounded_viewer_like_the_CLI_prints_every_line_once()
    {
        // CertifyCommand printed lines by Count; at the cap the count stops
        // growing, and it would have printed nothing more.
        var log = new RunLog(capacity: 5);
        var cursor = new RunLogCursor();
        var printed = new List<string>();

        for (int i = 0; i < 23; i++)
        {
            log.Add($"line {i}");
            var snapshot = log.Snapshot();
            for (int j = cursor.Advance(snapshot).From; j < snapshot.Count; j++)
            {
                printed.Add(snapshot[j]);
            }
        }

        Assert.Equal(Enumerable.Range(0, 23).Select(n => $"line {n}"), printed);
    }
}
