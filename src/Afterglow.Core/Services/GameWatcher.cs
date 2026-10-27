using System.Diagnostics;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Services;

/// <summary>
/// Watches running processes and fires when a configured game starts or exits,
/// so per-game profiles apply automatically (something MSI Afterburner never had).
/// Polling-based (3 s) — robust, needs no WMI permissions, negligible cost —
/// and the poll only runs while there is something to watch: with no rules and
/// no game in flight there is no timer at all, so a resident Afterglow without
/// game rules never wakes up for this.
/// <para>
/// Configured games that overlap share one session, so there is one pre-game
/// state to restore (R1):
/// <list type="bullet">
/// <item>the first configured game to start opens the session (<see cref="GameStarted"/>);</item>
/// <item>a game that starts while another configured game runs joins it, and
/// the first game's profile stays — one card can run one profile, and the
/// first game is the one that asked;</item>
/// <item>when the game whose profile is active exits while another configured
/// game still runs, the earliest-started of those takes over
/// (<see cref="GameHandedOff"/>): its profile is applied, and the session's
/// pre-game state stays the restore target. It used to keep the exited game's
/// profile until the last game closed;</item>
/// <item>when the last one exits, the session ends (<see cref="GameExited"/>),
/// and the rule active at that moment decides by its RevertOnExit whether
/// the pre-game state comes back.</item>
/// </list>
/// </para>
/// </summary>
public sealed class GameWatcher : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, GameRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Configured games running in this session, in the order they were seen to start.</summary>
    private readonly List<string> _activeGames = [];

    /// <summary>The game whose rule is <see cref="ActiveRule"/>.</summary>
    private string? _activeGame;
    private Timer? _timer;
    private bool _started;

    /// <summary>Test seam: the running process names (no extension). Defaults to the real process list.</summary>
    internal Func<IEnumerable<string>> RunningProcessNames { get; init; } = EnumerateProcessNames;

    /// <summary>True while the 3 s poll is armed.</summary>
    internal bool IsPolling
    {
        get
        {
            lock (_lock)
            {
                return _timer is not null;
            }
        }
    }

    private static IEnumerable<string> EnumerateProcessNames()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                yield return process.ProcessName;
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Raised when a configured game starts with no other configured game
    /// running: a session opens. Capture the pre-game state, apply its profile.
    /// </summary>
    public event Action<GameRule>? GameStarted;

    /// <summary>
    /// Raised when the game whose profile is active exits while other configured
    /// games still run: (the exited game's rule, the rule taking over). Apply the
    /// new rule's profile; keep the pre-game state captured at
    /// <see cref="GameStarted"/> as the restore target, since it is what the card
    /// ran before any of these games. The exited rule's RevertOnExit does not
    /// apply here: its profile is replaced, not kept, and nothing is restored
    /// while a configured game still runs.
    /// </summary>
    public event Action<GameRule, GameRule>? GameHandedOff;

    /// <summary>
    /// Raised when the last configured game exits and the session's active rule
    /// (the one that started it, or the last to take over) wants a revert.
    /// </summary>
    public event Action<GameRule>? GameExited;

    /// <summary>The rule whose profile is currently active, if any.</summary>
    public GameRule? ActiveRule { get; private set; }

    public void UpdateRules(IEnumerable<GameRule> rules)
    {
        lock (_lock)
        {
            _rules.Clear();
            foreach (var rule in rules)
            {
                string key = Path.GetFileNameWithoutExtension(rule.ExecutableName);
                if (key.Length > 0)
                {
                    _rules[key] = rule;
                }
            }

            UpdateTimer();
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _started = true;
            UpdateTimer();
        }
    }

    /// <summary>Caller holds the lock. Polls while a rule exists or a matched game is still running.</summary>
    private void UpdateTimer()
    {
        bool needed = _started && (_rules.Count > 0 || _activeGames.Count > 0);
        if (needed && _timer is null)
        {
            _timer = new Timer(_ => Scan(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        }
        else if (!needed && _timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }
    }

    internal void Scan()
    {
        try
        {
            lock (_lock)
            {
                // With every rule deleted mid-game, nothing counts as running any
                // more, so the active game's exit (and its restore) still happens —
                // it used to return here and never restore the pre-game state.
                var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_rules.Count > 0)
                {
                    foreach (string name in RunningProcessNames())
                    {
                        if (_rules.ContainsKey(name))
                        {
                            running.Add(name);
                        }
                    }
                }

                // Exited games first (R-22): when one game closes and another
                // opens inside one poll interval, the ended session must close
                // before the new game is counted — otherwise the newcomer joined
                // the old session silently, kept the old profile, and the old
                // restore waited for the newcomer's exit. The exit is raised
                // before the start, so the handlers restore the old pre-game
                // state before capturing the new one.
                int before = _activeGames.Count;
                _activeGames.RemoveAll(g => !running.Contains(g));
                if (_activeGames.Count < before && ActiveRule is { } rule && _activeGame is { } activeGame &&
                    !_activeGames.Contains(activeGame, StringComparer.OrdinalIgnoreCase))
                {
                    if (_activeGames.Count > 0)
                    {
                        // R1: the active game left while another configured game
                        // runs. The earliest-started one still running takes over
                        // — the same "first game's profile" rule, applied to the
                        // games that remain — rather than the exited game's
                        // profile staying on until the last one closes. Games that
                        // exited in this same scan are already gone, so the
                        // hand-off never goes to one of them.
                        _activeGame = _activeGames[0];
                        ActiveRule = _rules[_activeGame];
                        GameHandedOff?.Invoke(rule, ActiveRule);
                    }
                    else
                    {
                        _activeGame = null;
                        ActiveRule = null;
                        if (rule.RevertOnExit)
                        {
                            GameExited?.Invoke(rule);
                        }
                    }
                }

                // Newly started games. Two seen in the same scan have no start
                // order to go by; the first one enumerated opens the session.
                foreach (string name in running)
                {
                    if (_activeGames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _activeGames.Add(name);
                    if (ActiveRule is null)
                    {
                        // No session: this game opens one. Any later game joins it.
                        _activeGame = name;
                        ActiveRule = _rules[name];
                        GameStarted?.Invoke(ActiveRule);
                    }
                }

                UpdateTimer();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process enumeration hiccups are non-fatal.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _started = false;
            UpdateTimer();
        }
    }
}
