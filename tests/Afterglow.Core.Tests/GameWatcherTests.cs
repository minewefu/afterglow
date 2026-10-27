using Afterglow.Core.Services;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Tests;

public sealed class GameWatcherTests
{
    private static GameRule Rule(string exe) => new() { ExecutableName = exe, ProfileName = "p", RevertOnExit = true };

    [Fact]
    public void No_rules_means_no_polling_timer()
    {
        using var watcher = new GameWatcher { RunningProcessNames = () => [] };
        watcher.Start();
        Assert.False(watcher.IsPolling);

        watcher.UpdateRules([Rule("game.exe")]);
        Assert.True(watcher.IsPolling);

        watcher.UpdateRules([]);
        Assert.False(watcher.IsPolling);
    }

    [Fact]
    public void Deleting_the_rule_mid_game_still_restores_on_the_next_scan()
    {
        var running = new List<string> { "explorer", "game" };
        using var watcher = new GameWatcher { RunningProcessNames = () => running };
        GameRule? started = null, exited = null;
        watcher.GameStarted += r => started = r;
        watcher.GameExited += r => exited = r;
        watcher.Start();
        watcher.UpdateRules([Rule("game.exe")]);

        watcher.Scan();
        Assert.Equal("game.exe", started?.ExecutableName);

        // The user deletes the rule while the game is still running: the poll
        // must stay armed and the next scan must hand back the pre-game state.
        watcher.UpdateRules([]);
        Assert.True(watcher.IsPolling);
        watcher.Scan();

        Assert.Equal("game.exe", exited?.ExecutableName);
        Assert.False(watcher.IsPolling);
    }

    [Fact]
    public void A_game_exit_is_reported_once_its_process_is_gone()
    {
        var running = new List<string> { "game" };
        using var watcher = new GameWatcher { RunningProcessNames = () => running };
        int exits = 0;
        watcher.GameExited += _ => exits++;
        watcher.Start();
        watcher.UpdateRules([Rule("game.exe")]);

        watcher.Scan();
        running.Clear();
        watcher.Scan();
        watcher.Scan();

        Assert.Equal(1, exits);
        Assert.True(watcher.IsPolling);   // rules remain, so it keeps watching for the next launch
    }

    [Fact]
    public void A_game_that_replaces_another_between_scans_gets_its_own_session()
    {
        // R-22: A closes and B opens inside one poll interval. The scan used to
        // add B while A was still counted (starts before exits), so B raised no
        // start, A's profile stayed on through B, and A's restore came only when
        // B closed. A's session must end first, and its exit must be raised
        // before B's start: the app's handlers consume the pre-game snapshot in
        // the order the events arrive.
        var running = new List<string> { "explorer", "a" };
        using var watcher = new GameWatcher { RunningProcessNames = () => running };
        var events = new List<string>();
        watcher.GameStarted += r => events.Add("start " + r.ExecutableName);
        watcher.GameExited += r => events.Add("exit " + r.ExecutableName);
        watcher.Start();
        watcher.UpdateRules([Rule("a.exe"), Rule("b.exe")]);

        watcher.Scan();
        running[1] = "b";
        watcher.Scan();
        watcher.Scan();

        string[] handoff = ["start a.exe", "exit a.exe", "start b.exe"];
        Assert.Equal(handoff, events);
        Assert.Equal("b.exe", watcher.ActiveRule?.ExecutableName);

        running.Remove("b");
        watcher.Scan();

        string[] ended = [.. handoff, "exit b.exe"];
        Assert.Equal(ended, events);
        Assert.Null(watcher.ActiveRule);
    }

    [Fact]
    public void A_replacement_game_starts_even_when_the_ended_one_keeps_its_profile()
    {
        // R-22 without a revert: A's end raises no exit event, but it still ends
        // A's session, so B (which does want a revert) gets one of its own.
        var running = new List<string> { "a" };
        using var watcher = new GameWatcher { RunningProcessNames = () => running };
        var started = new List<string>();
        var exited = new List<string>();
        watcher.GameStarted += r => started.Add(r.ExecutableName);
        watcher.GameExited += r => exited.Add(r.ExecutableName);
        watcher.Start();
        watcher.UpdateRules([Rule("a.exe") with { RevertOnExit = false }, Rule("b.exe")]);

        watcher.Scan();
        running[0] = "b";
        watcher.Scan();
        running.Clear();
        watcher.Scan();

        string[] expectedStarts = ["a.exe", "b.exe"];
        string[] expectedExits = ["b.exe"];
        Assert.Equal(expectedStarts, started);
        Assert.Equal(expectedExits, exited);
    }

    // ---- R1: overlapping games ----

    /// <summary>A watcher over <paramref name="running"/> that records every event as text.</summary>
    private static GameWatcher Recording(List<string> running, List<string> events, params GameRule[] rules)
    {
        var watcher = new GameWatcher { RunningProcessNames = () => running };
        watcher.GameStarted += r => events.Add("start " + r.ExecutableName);
        watcher.GameHandedOff += (from, to) => events.Add($"handoff {from.ExecutableName} -> {to.ExecutableName}");
        watcher.GameExited += r => events.Add("exit " + r.ExecutableName);
        watcher.Start();
        watcher.UpdateRules(rules);
        return watcher;
    }

    [Fact]
    public void A_game_started_during_another_joins_its_session_and_the_first_profile_stays()
    {
        var running = new List<string> { "a" };
        var events = new List<string>();
        using var watcher = Recording(running, events, Rule("a.exe"), Rule("b.exe"));

        watcher.Scan();
        running.Add("b");
        watcher.Scan();

        Assert.Equal(["start a.exe"], events);
        Assert.Equal("a.exe", watcher.ActiveRule?.ExecutableName);
    }

    [Fact]
    public void When_the_active_game_exits_the_game_still_running_takes_over()
    {
        // The user's report: A's profile stayed applied while only B ran, and
        // nothing changed until B closed too.
        var running = new List<string> { "a" };
        var events = new List<string>();
        using var watcher = Recording(running, events, Rule("a.exe"), Rule("b.exe"));
        watcher.Scan();
        running.Add("b");
        watcher.Scan();

        running.Remove("a");
        watcher.Scan();

        Assert.Equal(["start a.exe", "handoff a.exe -> b.exe"], events);
        Assert.Equal("b.exe", watcher.ActiveRule?.ExecutableName);

        // One session, one restore: the end comes when the last game closes.
        running.Remove("b");
        watcher.Scan();

        Assert.Equal(["start a.exe", "handoff a.exe -> b.exe", "exit b.exe"], events);
        Assert.Null(watcher.ActiveRule);
    }

    [Fact]
    public void A_joined_game_that_exits_first_changes_nothing()
    {
        var running = new List<string> { "a", "b" };
        var events = new List<string>();
        using var watcher = Recording(running, events, Rule("a.exe"), Rule("b.exe"));
        watcher.Scan();

        running.Remove("b");
        watcher.Scan();
        Assert.Equal(["start a.exe"], events);
        Assert.Equal("a.exe", watcher.ActiveRule?.ExecutableName);

        running.Remove("a");
        watcher.Scan();
        Assert.Equal(["start a.exe", "exit a.exe"], events);
    }

    [Fact]
    public void The_earliest_started_game_still_running_takes_over()
    {
        var running = new List<string> { "a" };
        var events = new List<string>();
        using var watcher = Recording(running, events, Rule("a.exe"), Rule("b.exe"), Rule("c.exe"));
        watcher.Scan();
        running.Add("b");
        watcher.Scan();
        running.Add("c");
        watcher.Scan();

        running.Remove("a");
        watcher.Scan();
        running.Remove("b");
        watcher.Scan();

        Assert.Equal(["start a.exe", "handoff a.exe -> b.exe", "handoff b.exe -> c.exe"], events);
        Assert.Equal("c.exe", watcher.ActiveRule?.ExecutableName);
    }

    [Fact]
    public void Games_that_all_exit_in_one_scan_end_the_session_without_a_handoff()
    {
        var running = new List<string> { "a" };
        var events = new List<string>();
        using var watcher = Recording(running, events, Rule("a.exe"), Rule("b.exe"));
        watcher.Scan();
        running.Add("b");
        watcher.Scan();

        running.Clear();
        watcher.Scan();

        Assert.Equal(["start a.exe", "exit a.exe"], events);
    }

    [Fact]
    public void A_game_starting_as_the_active_one_exits_joins_the_game_taking_over()
    {
        // A exits and C starts inside one poll while B runs: B (already running)
        // takes over, and C joins B's session rather than opening its own.
        var running = new List<string> { "a" };
        var events = new List<string>();
        using var watcher = Recording(running, events, Rule("a.exe"), Rule("b.exe"), Rule("c.exe"));
        watcher.Scan();
        running.Add("b");
        watcher.Scan();

        running[0] = "c";
        watcher.Scan();

        Assert.Equal(["start a.exe", "handoff a.exe -> b.exe"], events);
        Assert.Equal("b.exe", watcher.ActiveRule?.ExecutableName);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_rule_active_when_the_last_game_exits_decides_the_revert(bool firstReverts, bool lastReverts)
    {
        // The exited rule's RevertOnExit plays no part in a hand-off: its profile
        // is replaced either way, and nothing is restored while a game runs.
        var running = new List<string> { "a" };
        var events = new List<string>();
        using var watcher = Recording(running, events,
            Rule("a.exe") with { RevertOnExit = firstReverts }, Rule("b.exe") with { RevertOnExit = lastReverts });
        watcher.Scan();
        running.Add("b");
        watcher.Scan();
        running.Remove("a");
        watcher.Scan();

        running.Clear();
        watcher.Scan();

        string[] expected = lastReverts
            ? ["start a.exe", "handoff a.exe -> b.exe", "exit b.exe"]
            : ["start a.exe", "handoff a.exe -> b.exe"];
        Assert.Equal(expected, events);
    }

    [Fact]
    public void Deleting_the_active_games_rule_hands_off_to_the_other_running_game()
    {
        var running = new List<string> { "a", "b" };
        var events = new List<string>();
        using var watcher = Recording(running, events, Rule("a.exe"), Rule("b.exe"));
        watcher.Scan();

        watcher.UpdateRules([Rule("b.exe")]);
        watcher.Scan();

        Assert.Equal(["start a.exe", "handoff a.exe -> b.exe"], events);
    }
}
