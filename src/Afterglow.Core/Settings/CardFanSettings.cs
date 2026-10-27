using Afterglow.Core.Fans;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Settings;

/// <summary>
/// One card's persisted fan configuration, updated for the card a fan
/// configuration actually landed on.
/// <para>
/// A profile or game rule that lands on a GPU other than the selected one used
/// to persist nothing: the only write went through the Fans page editor, which
/// shows the SELECTED card. That card's saved settings kept describing the
/// previous curve (or Auto), so the next game's pre-state capture reconstructed
/// the wrong curve, its exit restored it, and the next start re-armed it (R-23).
/// </para>
/// </summary>
public static class CardFanSettings
{
    /// <summary>The persisted name of a fan mode.</summary>
    public static string ModeName(FanMode mode) => mode switch
    {
        FanMode.Fixed => "fixed",
        FanMode.Curve => "curve",
        _ => "auto",
    };

    /// <summary>
    /// A card's saved configuration after a fan configuration was applied to
    /// it through a profile or game rule — the same rules the Fans page editor
    /// follows for the selected card (FansViewModel.SyncFromApplied): the mode
    /// always follows, the duty only for Fixed, the curve whenever one is given.
    /// </summary>
    public static FanSettings Applied(FanSettings saved, FanMode mode, uint fixedPct, FanCurveConfig? curve)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return saved with
        {
            Mode = ModeName(mode),
            FixedDutyPct = mode == FanMode.Fixed ? fixedPct : saved.FixedDutyPct,
            Curve = curve ?? saved.Curve,
        };
    }

    /// <summary>
    /// The settings with one card's fan configuration replaced. The primary GPU
    /// also keeps the legacy single-GPU field in sync so a downgrade reads it;
    /// any other card leaves it alone.
    /// </summary>
    public static AppSettings WithCard(AppSettings settings, string cardKey, bool isPrimary, FanSettings fans)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fans);
        var byGpu = new Dictionary<string, FanSettings>(settings.FansByGpu, StringComparer.Ordinal)
        {
            [cardKey] = fans,
        };
        return settings with { FansByGpu = byGpu, Fans = isPrimary ? fans : settings.Fans };
    }
}
