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
/// </summary>
public sealed class GameWatcher : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, GameRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeGames = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Raised when a configured game starts (rule) — apply its profile.</summary>
    public event Action<GameRule>? GameStarted;

    /// <summary>Raised when the last configured game exits and its rule wants a revert.</summary>
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
                var exited = _activeGames.Where(g => !running.Contains(g)).ToList();
                foreach (string name in exited)
                {
                    _activeGames.Remove(name);
                }

                if (exited.Count > 0 && _activeGames.Count == 0 && ActiveRule is { } rule)
                {
                    ActiveRule = null;
                    if (rule.RevertOnExit)
                    {
                        GameExited?.Invoke(rule);
                    }
                }

                // Newly started games.
                foreach (string name in running)
                {
                    if (_activeGames.Add(name) && _activeGames.Count == 1)
                    {
                        ActiveRule = _rules[name];
                        GameStarted?.Invoke(_rules[name]);
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
