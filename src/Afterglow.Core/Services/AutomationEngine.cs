using Afterglow.Core.Settings;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Services;

public sealed record AutomationEvent(AutomationRule Rule, double Value, uint DeviceIndex);

/// <summary>
/// Evaluates sustained-condition rules against telemetry: a rule fires only
/// after its metric stays at/above the threshold for the configured duration,
/// then re-arms after a cooldown so a hovering value can't machine-gun
/// actions. Rules watch every GPU independently — breach time and cooldown
/// are tracked per (rule, device), so a hot secondary card fires the rule
/// even while the primary is cool, and one card's cooldown never masks the
/// other's breach. Pure against injected time, so the rules are unit-testable.
/// </summary>
public sealed class AutomationEngine
{
    private const int CooldownSeconds = 300;

    private sealed class Entry
    {
        public required AutomationRule Rule { get; init; }

        public Dictionary<uint, DateTimeOffset> BreachStart { get; } = [];

        public Dictionary<uint, DateTimeOffset> CooldownUntil { get; } = [];
    }

    private Entry[] _entries = [];

    /// <summary>
    /// Replaces the rule set. Every settings save hands the rules over again —
    /// a polling, overlay or alert edit included — so an unchanged rule keeps
    /// its entry, breach timers and cooldowns with it; rebuilding them all let
    /// a rule fire again seconds into its five-minute cooldown, and postponed
    /// a breach that was about to act (R-38). Rules match by value, so the
    /// fresh instances read back from saved settings still count as unchanged;
    /// each old entry is claimed at most once, so two identical rules keep two
    /// histories. A changed or removed rule loses its state.
    /// </summary>
    public void UpdateRules(IReadOnlyList<AutomationRule> rules)
    {
        // Entries are shared with an Evaluate that may be running on the
        // telemetry thread; only Evaluate touches their dictionaries.
        var unclaimed = _entries.ToList();
        var entries = new Entry[rules.Count];
        for (int i = 0; i < rules.Count; i++)
        {
            int match = unclaimed.FindIndex(entry => entry.Rule == rules[i]);
            if (match >= 0)
            {
                entries[i] = unclaimed[match];
                unclaimed.RemoveAt(match);
            }
            else
            {
                entries[i] = new Entry { Rule = rules[i] };
            }
        }

        _entries = entries;
    }

    public IReadOnlyList<AutomationEvent> Evaluate(GpuSnapshot snapshot, DateTimeOffset now)
    {
        uint device = snapshot.DeviceIndex;
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
                entry.BreachStart.Remove(device);
                continue;
            }

            if (entry.CooldownUntil.TryGetValue(device, out var until) && now < until)
            {
                continue;
            }

            if (value >= entry.Rule.Threshold)
            {
                if (!entry.BreachStart.TryGetValue(device, out var since))
                {
                    since = now;
                    entry.BreachStart[device] = now;
                }

                if ((now - since).TotalSeconds >= entry.Rule.ForSeconds)
                {
                    (fired ??= []).Add(new AutomationEvent(entry.Rule, value, device));
                    entry.BreachStart.Remove(device);
                    entry.CooldownUntil[device] = now.AddSeconds(CooldownSeconds);
                }
            }
            else
            {
                entry.BreachStart.Remove(device);
            }
        }

        return fired is null ? [] : fired;
    }
}
