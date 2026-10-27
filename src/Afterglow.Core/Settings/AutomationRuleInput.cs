using System.Globalization;

namespace Afterglow.Core.Settings;

/// <summary>Reads the Settings page's "add automation rule" boxes.</summary>
public static class AutomationRuleInput
{
    /// <summary>
    /// A threshold as typed: invariant culture, and finite. double.TryParse also
    /// accepts "NaN" and "Infinity", and turns an overflow such as "1e999" into
    /// infinity rather than failing. JSON has no such numbers, so a rule holding
    /// one could never be saved, and the failed save took every later settings
    /// save down with it (R-37).
    /// </summary>
    public static bool TryParseThreshold(string? text, out double threshold) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold) &&
        double.IsFinite(threshold);
}
