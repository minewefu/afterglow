using System.Diagnostics;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Services;

/// <summary>
/// Watches running processes and fires when a configured game starts or exits,
/// so per-game profiles apply automatically (something MSI Afterburner never had).
/// Polling-based (3 s) — robust, needs no WMI permissions, negligible cost.
/// </summary>
public sealed class GameWatcher : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, GameRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeGames = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;

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
        }
    }

    public void Start()
    {
        _timer ??= new Timer(_ => Scan(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    private void Scan()
    {
        try
        {
            lock (_lock)
            {
                if (_rules.Count == 0)
                {
                    return;
                }

                var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (_rules.ContainsKey(process.ProcessName))
                        {
                            running.Add(process.ProcessName);
                        }
                    }
                    finally
                    {
                        process.Dispose();
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

                // Exited games.
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
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process enumeration hiccups are non-fatal.
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
