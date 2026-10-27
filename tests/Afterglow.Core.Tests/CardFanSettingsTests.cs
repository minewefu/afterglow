using Afterglow.Core.Fans;
using Afterglow.Core.Profiles;
using Afterglow.Core.Settings;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// A fan configuration applied to a card is persisted for THAT card (R-23): a
/// profile landing on a GPU other than the selected one used to persist
/// nothing, so its saved settings kept the previous curve, which the next
/// game's pre-state capture and exit restore then put back.
/// </summary>
public sealed class CardFanSettingsTests
{
    private const string Gpu0 = "GPU-aaaa1111-2222-3333-4444-555566667777";
    private const string Gpu1 = "GPU-bbbb1111-2222-3333-4444-555566667777";

    private static readonly FanCurveConfig CurveA = new()
    {
        Points = [new FanPoint(30, 30), new FanPoint(60, 45), new FanPoint(80, 70), new FanPoint(90, 100)],
        ZeroRpmBelowC = 0,
    };

    private static readonly FanCurveConfig CurveB = new()
    {
        Points = [new FanPoint(30, 35), new FanPoint(50, 55), new FanPoint(70, 85), new FanPoint(85, 100)],
        HysteresisC = 2,
        ZeroRpmBelowC = 0,
    };

    private static AppSettings TwoCards()
    {
        var gpu0 = new FanSettings { Mode = "fixed", FixedDutyPct = 40 };
        return new AppSettings
        {
            Fans = gpu0,
            FansByGpu = new Dictionary<string, FanSettings>
            {
                [Gpu0] = gpu0,
                [Gpu1] = new FanSettings { Mode = "curve", FixedDutyPct = 55, Curve = CurveA },
            },
        };
    }

    [Fact]
    public void A_curve_applied_to_the_unselected_card_is_what_its_next_game_capture_restores()
    {
        // The review's trigger: curve B applied to GPU 1 while GPU 0 is
        // selected; the next game's pre-state capture read GPU 1's saved
        // settings and reconstructed curve A.
        var settings = TwoCards();
        var applied = CardFanSettings.Applied(settings.FansByGpu[Gpu1], FanMode.Curve, 50, CurveB);
        var updated = CardFanSettings.WithCard(settings, Gpu1, isPrimary: false, applied);

        var captured = GameSessionRestore.CaptureFans(FanControlMode.Curve, null, updated.FansByGpu[Gpu1]);
        Assert.NotNull(captured);
        Assert.Equal(FanMode.Curve, captured.Mode);
        Assert.Same(CurveB, captured.Curve);
    }

    [Fact]
    public void Writing_one_card_leaves_the_other_card_and_the_legacy_field_alone()
    {
        var settings = TwoCards();
        var applied = CardFanSettings.Applied(settings.FansByGpu[Gpu1], FanMode.Fixed, 70, null);
        var updated = CardFanSettings.WithCard(settings, Gpu1, isPrimary: false, applied);

        Assert.Same(settings.FansByGpu[Gpu0], updated.FansByGpu[Gpu0]);
        Assert.Same(settings.Fans, updated.Fans);
        Assert.Equal("fixed", updated.FansByGpu[Gpu1].Mode);
        Assert.Equal(70u, updated.FansByGpu[Gpu1].FixedDutyPct);

        // The input is a snapshot of the settings; it is never mutated.
        Assert.Equal("curve", settings.FansByGpu[Gpu1].Mode);
    }

    [Fact]
    public void The_primary_card_keeps_the_legacy_single_gpu_field_in_sync()
    {
        var settings = TwoCards();
        var applied = CardFanSettings.Applied(settings.FansByGpu[Gpu0], FanMode.Curve, 50, CurveB);
        var updated = CardFanSettings.WithCard(settings, Gpu0, isPrimary: true, applied);

        Assert.Same(applied, updated.Fans);
        Assert.Same(applied, updated.FansByGpu[Gpu0]);
    }

    [Fact]
    public void The_same_rules_as_the_editor_decide_what_changes()
    {
        // FansViewModel.SyncFromApplied: the mode always follows, the duty only
        // for Fixed, the curve whenever one is supplied.
        var saved = new FanSettings { Mode = "curve", FixedDutyPct = 55, Curve = CurveA };

        var auto = CardFanSettings.Applied(saved, FanMode.Auto, 90, null);
        Assert.Equal("auto", auto.Mode);
        Assert.Equal(55u, auto.FixedDutyPct);
        Assert.Same(CurveA, auto.Curve);

        var fixedNoCurve = CardFanSettings.Applied(saved, FanMode.Fixed, 65, null);
        Assert.Equal("fixed", fixedNoCurve.Mode);
        Assert.Equal(65u, fixedNoCurve.FixedDutyPct);
        Assert.Same(CurveA, fixedNoCurve.Curve);

        var fixedWithCurve = CardFanSettings.Applied(saved, FanMode.Fixed, 65, CurveB);
        Assert.Same(CurveB, fixedWithCurve.Curve);

        var curve = CardFanSettings.Applied(saved, FanMode.Curve, 90, CurveB);
        Assert.Equal("curve", curve.Mode);
        Assert.Equal(55u, curve.FixedDutyPct);
        Assert.Same(CurveB, curve.Curve);
    }

    [Fact]
    public void A_card_with_no_entry_yet_gets_one()
    {
        var settings = new AppSettings();
        var applied = CardFanSettings.Applied(new FanSettings(), FanMode.Fixed, 60, null);
        var updated = CardFanSettings.WithCard(settings, Gpu1, isPrimary: false, applied);

        Assert.Equal("fixed", updated.FansByGpu[Gpu1].Mode);
        Assert.Equal("auto", updated.Fans.Mode);
    }
}
