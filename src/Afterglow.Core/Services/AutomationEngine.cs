using Afterglow.Core.Settings;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Services;

public sealed record AutomationEvent(AutomationRule Rule, double Value);

/// <summary>
/// Evaluates sustained-condition rules against telemetry: a rule fires only
/// after its metric stays at/above the threshold for the configured duration,
/// then re-arms after a cooldown so a hovering value can't machine-gun
/// actions. Pure against injected time, so the rules are unit-testable.
/// </summary>
public sealed class AutomationEngine
{
    private const int CooldownSeconds = 300;

    private sealed class Entry
    {
        public required AutomationRule Rule { get; init; }

        public DateTimeOffset? BreachStart { get; set; }

        public DateTimeOffset CooldownUntil { get; set; } = DateTimeOffset.MinValue;
    }

    private Entry[] _entries = [];

    public void UpdateRules(IReadOnlyList<AutomationRule> rules)
    {
        _entries = [.. rules.Select(rule => new Entry { Rule = rule })];
    }

    public IReadOnlyList<AutomationEvent> Evaluate(GpuSnapshot snapshot, DateTimeOffset now)
    {
        List<AutomationEvent>? fired = null;
        foreach (var entry in _entries)
        {
            double? metric = entry.Rule.Metric switch
            {
                "gpu" => snapshot.GpuTempC,
                "memjunction" => snapshot.MemJunctionTempC,
                "power" => snapshot.PowerW,
                _ => null,
            };

            if (metric is not double value)
            {
                entry.BreachStart = null;
                continue;
            }

            if (now < entry.CooldownUntil)
            {
                continue;
            }

            if (value >= entry.Rule.Threshold)
            {
                entry.BreachStart ??= now;
                if ((now - entry.BreachStart.Value).TotalSeconds >= entry.Rule.ForSeconds)
                {
                    (fired ??= []).Add(new AutomationEvent(entry.Rule, value));
                    entry.BreachStart = null;
                    entry.CooldownUntil = now.AddSeconds(CooldownSeconds);
                }
            }
            else
            {
                entry.BreachStart = null;
            }
        }

        return fired is null ? [] : fired;
    }
}
