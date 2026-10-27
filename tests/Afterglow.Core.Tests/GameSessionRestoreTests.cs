using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Settings;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// The game-rule exit restore: it must put back what the game profile could
/// have changed — including the temperature limit and the per-point curve,
/// which ReadCurrent cannot see (F-21) — and the fans of the card the profile
/// landed on, from that card's own state (F-16). Fan services run through
/// their driver-free test seam; AppPaths is redirected because they record state.
/// </summary>
[Collection("AppPaths")]
public sealed class GameSessionRestoreTests : IDisposable
{
    private const string UuidA = "GPU-aaaa1111-2222-3333-4444-555566667777";
    private const string UuidB = "GPU-bbbb1111-2222-3333-4444-555566667777";

    private readonly string _root;

    public GameSessionRestoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"afterglow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot = _root;
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeDriver
    {
        public List<uint> SetCalls { get; } = [];

        public int RestoreCalls { get; private set; }

        public NvmlReturn SetAllFans(uint duty)
        {
            SetCalls.Add(duty);
            return NvmlReturn.Success;
        }

        public NvmlReturn RestoreAuto()
        {
            RestoreCalls++;
            return NvmlReturn.Success;
        }
    }

    private static FanControlService Service(FakeDriver driver, string uuid) =>
        new(driver.SetAllFans, driver.RestoreAuto, uuid, 30);

    private static TuningProfile Game(Func<TuningProfile, TuningProfile>? shape = null)
    {
        var profile = new TuningProfile { Name = "Cyberpunk", GpuUuid = UuidB, CoreOffsetMHz = 150, MemOffsetMHz = 1000 };
        return shape is null ? profile : shape(profile);
    }

    private static PreGameSnapshot Before(TuningProfile game, Func<PreGameSnapshot, PreGameSnapshot>? shape = null)
    {
        var snapshot = new PreGameSnapshot
        {
            Executable = "cyberpunk2077.exe",
            GpuIndex = 1,
            GpuUuid = UuidB,
            GameProfile = game,
            CoreOffsetMHz = 0,
            MemOffsetMHz = 0,
            PowerLimitW = 575,
            TempLimitSupported = true,
            TempLimitC = 83,
            VfPointsSupported = true,
            VfPointOffsetsMHz = new Dictionary<int, int> { [10] = 0, [11] = 0, [12] = 0 },
            VfHadShape = false,
            Fans = new FanRestoreState(FanMode.Auto, 50, null),
        };
        return shape is null ? snapshot : shape(snapshot);
    }

    // --- F-21: what ReadCurrent cannot see -----------------------------------

    [Fact]
    public void A_game_profiles_temperature_limit_is_put_back()
    {
        var plan = GameSessionRestore.Plan(Before(Game(p => p with { TempLimitC = 65 })));

        Assert.Equal(83u, plan.Profile.TempLimitC);
        Assert.Empty(plan.NotRestored);
        Assert.Null(plan.Profile.Validate());
    }

    [Fact]
    public void A_game_profiles_point_offsets_are_replaced_by_the_whole_pre_game_table()
    {
        var game = Game(p => p with { VfPointOffsetsMHz = new Dictionary<int, int> { [11] = 50, [12] = -30 } });

        var plan = GameSessionRestore.Plan(Before(game));

        // Every live slot, zeros included: writing only the non-zero points
        // would leave the game's +50/-30 in the slots that were 0 before.
        Assert.Equal(
            new Dictionary<int, int> { [10] = 0, [11] = 0, [12] = 0 },
            plan.Profile.VfPointOffsetsMHz);
        Assert.Null(plan.Profile.Validate());
    }

    [Fact]
    public void A_shaped_curve_levelled_by_the_games_core_offset_is_put_back()
    {
        // The game profile carries no points, but its core-offset write levels
        // the shared table — the user's curve is gone unless it is written back.
        var shaped = new Dictionary<int, int> { [10] = 0, [11] = 60, [12] = 60 };

        var plan = GameSessionRestore.Plan(Before(Game(), s => s with { VfPointOffsetsMHz = shaped, VfHadShape = true }));

        Assert.Equal(shaped, plan.Profile.VfPointOffsetsMHz);
    }

    [Fact]
    public void Knobs_the_game_did_not_touch_are_left_alone()
    {
        var plan = GameSessionRestore.Plan(Before(Game()));

        Assert.Null(plan.Profile.TempLimitC);
        Assert.Null(plan.Profile.VfPointOffsetsMHz);
        Assert.Empty(plan.NotRestored);
        Assert.Equal(0, plan.Profile.CoreOffsetMHz);
        Assert.Equal((double?)575, plan.Profile.PowerLimitW);
    }

    [Fact]
    public void What_could_not_be_read_is_reported_never_claimed_restored()
    {
        var game = Game(p => p with { TempLimitC = 65, VfPointOffsetsMHz = new Dictionary<int, int> { [11] = 50 } });

        var plan = GameSessionRestore.Plan(Before(game, s => s with { TempLimitC = null, VfPointOffsetsMHz = null }));
        string message = GameSessionRestore.Describe(
            "cyberpunk2077.exe", new ApplyResult(true, [KnobResult.Ok("core offset")]), fansRestored: null, plan.NotRestored);

        Assert.Equal(2, plan.NotRestored.Count);
        Assert.Contains("incomplete", message, StringComparison.Ordinal);
        Assert.Contains("temperature limit", message, StringComparison.Ordinal);
        Assert.Contains("V/F", message, StringComparison.Ordinal);
        Assert.DoesNotContain("restored (", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_knob_the_card_does_not_have_is_not_reported_missing()
    {
        // The game's temp-limit and point writes failed on such a card, so
        // nothing changed and there is nothing to put back or apologise for.
        var game = Game(p => p with { TempLimitC = 65, VfPointOffsetsMHz = new Dictionary<int, int> { [11] = 50 } });

        var plan = GameSessionRestore.Plan(Before(game, s => s with
        {
            TempLimitSupported = false,
            TempLimitC = null,
            VfPointsSupported = false,
            VfPointOffsetsMHz = null,
        }));

        Assert.Empty(plan.NotRestored);
        Assert.Null(plan.Profile.TempLimitC);
        Assert.Null(plan.Profile.VfPointOffsetsMHz);
    }

    [Fact]
    public void A_value_the_validator_rejects_does_not_sink_the_whole_restore()
    {
        // A 40 W card: the profile validator's 50 W floor used to get the
        // entire restore refused — offsets, lock and all.
        var plan = GameSessionRestore.Plan(Before(Game(), s => s with { PowerLimitW = 40 }));

        Assert.Null(plan.Profile.Validate());
        Assert.Null(plan.Profile.PowerLimitW);
        Assert.Contains(plan.NotRestored, n => n.StartsWith("power limit", StringComparison.Ordinal));
    }

    [Fact]
    public void The_restore_is_stamped_for_the_card_it_was_read_from()
    {
        var plan = GameSessionRestore.Plan(Before(Game()));

        Assert.True(plan.Profile.AppliesToGpu(UuidB));
        Assert.False(plan.Profile.AppliesToGpu(UuidA));
    }

    // --- F-16: the fans of the card the profile landed on ---------------------

    [Fact]
    public void An_auto_fan_game_profile_leaves_the_fans_alone_on_exit()
    {
        // In game context an Auto-fan profile has no fan opinion and never
        // touched the fans; its exit must not seize them either.
        var plan = GameSessionRestore.Plan(Before(Game(), s => s with { Fans = new FanRestoreState(FanMode.Fixed, 70, null) }));

        Assert.Null(plan.Fans);
        Assert.Empty(plan.NotRestored);
    }

    [Fact]
    public void Two_cards_the_exit_restores_only_the_profiles_card_to_its_own_state()
    {
        // Card A is selected and runs a curve with zero-RPM below 45 C; card B,
        // which the "Cyberpunk" profile is stamped for, is on firmware control.
        var driverA = new FakeDriver();
        var driverB = new FakeDriver();
        using var fansA = Service(driverA, UuidA);
        using var fansB = Service(driverB, UuidB);
        fansA.SetCurve(new FanCurveConfig { ZeroRpmBelowC = 45 });
        var settingsB = new FanSettings { Mode = "auto" };

        // Game start: card B's own state is captured, then the profile pins B.
        var game = Game(p => p with { FanMode = FanMode.Fixed, FixedFanPct = 90 });
        var captured = GameSessionRestore.CaptureFans(fansB.Mode, fansB.CommandedDuty, settingsB);
        Assert.True(fansB.SetFixed(90));

        // Game exit.
        var plan = GameSessionRestore.Plan(Before(game, s => s with { Fans = captured }));
        Assert.True(GameSessionRestore.ApplyFans(fansB, plan.Fans!));

        // B is handed back to firmware, as before the game — not given A's curve.
        Assert.Equal(FanControlMode.Auto, fansB.Mode);
        Assert.Equal(1, driverB.RestoreCalls);

        // A was never part of it.
        Assert.Equal(FanControlMode.Curve, fansA.Mode);
        Assert.Empty(driverA.SetCalls);
        Assert.Equal(0, driverA.RestoreCalls);
    }

    [Fact]
    public void A_cards_fan_state_comes_from_its_own_service_and_settings()
    {
        var curve = new FanCurveConfig { ZeroRpmBelowC = 0 };

        // Fixed: the duty the service actually commanded wins over the settings.
        Assert.Equal(
            new FanRestoreState(FanMode.Fixed, 55, null),
            GameSessionRestore.CaptureFans(FanControlMode.Fixed, 55.0, new FanSettings { Mode = "fixed", FixedDutyPct = 70 }));

        // Fixed but the command never landed: the card's saved fixed duty.
        Assert.Equal(
            new FanRestoreState(FanMode.Fixed, 70, null),
            GameSessionRestore.CaptureFans(FanControlMode.Fixed, null, new FanSettings { Mode = "fixed", FixedDutyPct = 70 }));

        // Curve: the service doesn't expose its curve, so the card's saved one.
        Assert.Equal(
            new FanRestoreState(FanMode.Curve, 50, curve),
            GameSessionRestore.CaptureFans(FanControlMode.Curve, 40.0, new FanSettings { Mode = "curve", Curve = curve }));

        // Running a curve whose shape this card never saved: unknown, so no guess.
        Assert.Null(GameSessionRestore.CaptureFans(FanControlMode.Curve, 40.0, new FanSettings { Mode = "auto" }));
    }

    [Fact]
    public void Unknown_pre_game_fans_are_left_as_the_game_set_them_and_said_so()
    {
        var game = Game(p => p with { FanMode = FanMode.Fixed, FixedFanPct = 90 });

        var plan = GameSessionRestore.Plan(Before(game, s => s with { Fans = null }));

        Assert.Null(plan.Fans);
        Assert.Contains(plan.NotRestored, n => n.StartsWith("fans", StringComparison.Ordinal));
    }

    [Fact]
    public void The_message_reports_what_happened()
    {
        var ok = new ApplyResult(true, [KnobResult.Ok("core offset")]);
        var partial = new ApplyResult(false, [KnobResult.Ok("core offset"), KnobResult.Fail("temp limit", "NotSupported")]);

        Assert.Equal(
            "game.exe exited — tuning from before the game restored (including fans).",
            GameSessionRestore.Describe("game.exe", ok, fansRestored: true, []));
        Assert.Equal(
            "game.exe exited — tuning from before the game restored.",
            GameSessionRestore.Describe("game.exe", ok, fansRestored: null, []));

        string failed = GameSessionRestore.Describe("game.exe", partial, fansRestored: false, []);
        Assert.Contains("incomplete", failed, StringComparison.Ordinal);
        Assert.Contains("temp limit", failed, StringComparison.Ordinal);
        Assert.Contains("fans", failed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_power_only_game_profile_keeps_the_curve_for_the_whole_session()
    {
        // R-12, through the real tuner over a fake driver that levels the table
        // on every core-offset write, as the measured RTX 5090 does.
        var driver = new Fakes.FakeNvidiaDriver(UuidA);
        var tuner = new GpuTuner(driver, driver, UuidA);
        Assert.True(tuner.Apply(new TuningProfile { Name = "Daily", CoreOffsetMHz = 100 }).AllSucceeded);
        Assert.True(tuner.SetVfPointOffsets(new Dictionary<int, int> { [30] = 200, [31] = -100 }).Applied);
        int[] curve = [200, -100, 100, 100, 100];

        // A game profile with no curve opinion and the offset already in force.
        var game = new TuningProfile { Name = "Quiet", CoreOffsetMHz = 100, PowerLimitW = 250 };
        var pre = GameSessionRestore.Capture(tuner, 0, "game.exe", game, fans: null);
        Assert.True(tuner.Apply(game).AllSucceeded);
        Assert.Equal(curve, driver.TableOffsets);

        var plan = GameSessionRestore.Plan(pre);
        Assert.True(tuner.Apply(plan.Profile, reconcileVfPoints: false).AllSucceeded);
        Assert.Equal(curve, driver.TableOffsets);
        Assert.Equal(300_000u, driver.PowerLimitMw);
    }

    // --- R1: a game exits while another configured game still runs ----------

    [Fact]
    public void A_handoff_without_a_snapshot_starts_the_remaining_game_fresh()
    {
        // The first game's profile was missing or refused: nothing is on the
        // card, so there is no earlier state to keep.
        Assert.Equal(GameHandoff.StartFresh, GameSessionRestore.PlanHandoff(null, Game(), 1, UuidB));
    }

    [Fact]
    public void A_handoff_to_a_profile_for_the_same_card_swaps_and_keeps_the_snapshot()
    {
        var next = Game(p => p with { Name = "Quiet", GpuUuid = null, CoreOffsetMHz = 50 });

        Assert.Equal(GameHandoff.Swap, GameSessionRestore.PlanHandoff(Before(Game()), next, 1, UuidB));
    }

    [Theory]
    [InlineData(0u, UuidB)]   // another card index
    [InlineData(1u, UuidA)]   // the index now belongs to another card
    public void A_handoff_to_another_card_ends_the_first_part_and_starts_fresh(uint index, string uuid)
    {
        // That card's state before the game was never captured; ending the
        // first card's part and capturing the other is the only correct restore.
        var next = Game(p => p with { GpuUuid = null });

        Assert.Equal(GameHandoff.EndThenStart, GameSessionRestore.PlanHandoff(Before(Game()), next, index, uuid));
    }

    [Fact]
    public void A_handoff_to_a_profile_that_cannot_land_ends_the_first_part()
    {
        var pre = Before(Game());

        // Missing, stamped for another GPU, and invalid: none is applied, so the
        // pre-game state must not be put back on the promise of one.
        Assert.Equal(GameHandoff.EndThenStart, GameSessionRestore.PlanHandoff(pre, null, null, null));
        Assert.Equal(GameHandoff.EndThenStart,
            GameSessionRestore.PlanHandoff(pre, Game(p => p with { GpuUuid = UuidA }), 1, UuidB));
        Assert.Equal(GameHandoff.EndThenStart,
            GameSessionRestore.PlanHandoff(pre, Game(p => p with { PowerLimitW = 5 }), 1, UuidB));
    }

    [Fact]
    public void After_a_swap_the_captured_state_stays_and_the_new_profile_decides_the_restore()
    {
        // Captured before the first game; never re-read. The first game set no
        // fans, the second sets a fixed duty: the final restore puts the fans
        // back because of the second, to what they were before the first.
        var first = Game();
        var second = Game(p => p with { Name = "Loud", FanMode = FanMode.Fixed, FixedFanPct = 80, TempLimitC = 70 });
        var pre = Before(first);

        var session = GameSessionRestore.HandedOff(pre, "second.exe", second);

        Assert.Equal(pre with { Executable = "second.exe", GameProfile = second }, session);
        var plan = GameSessionRestore.Plan(session);
        Assert.Equal(new FanRestoreState(FanMode.Auto, 50, null), plan.Fans);
        Assert.Equal(83u, plan.Profile.TempLimitC);
        Assert.Null(GameSessionRestore.Plan(pre).Fans);
    }

    [Fact]
    public void A_swap_leaves_nothing_of_the_first_profile_and_the_end_restores_the_original_state()
    {
        // The sequence MainViewModel runs, on the real tuner over a fake driver.
        // The first game's 250 W stayed under a second profile with no power
        // opinion when the second was applied straight over it.
        var driver = new Fakes.FakeNvidiaDriver(UuidA);
        var tuner = new GpuTuner(driver, driver, UuidA);
        var first = new TuningProfile { Name = "First", CoreOffsetMHz = 100, PowerLimitW = 250 };
        var second = new TuningProfile { Name = "Second", MemOffsetMHz = 500 };
        var pre = GameSessionRestore.Capture(tuner, 0, "first.exe", first, fans: null);
        Assert.True(tuner.Apply(first).AllSucceeded);

        Assert.Equal(GameHandoff.Swap, GameSessionRestore.PlanHandoff(pre, second, 0, tuner.GpuUuid));
        Assert.True(tuner.Apply(GameSessionRestore.Plan(pre).Profile, reconcileVfPoints: false).AllSucceeded);
        Assert.True(tuner.Apply(second).AllSucceeded);
        var session = GameSessionRestore.HandedOff(pre, "second.exe", second);

        Assert.Equal(300_000u, driver.PowerLimitMw);
        Assert.Equal(0, driver.CoreOffsetMHz);
        Assert.Equal(500, driver.MemOffsetMHz);

        Assert.True(tuner.Apply(GameSessionRestore.Plan(session).Profile, reconcileVfPoints: false).AllSucceeded);
        Assert.Equal((0, 0, 300_000u), (driver.CoreOffsetMHz, driver.MemOffsetMHz, driver.PowerLimitMw));
    }

    [Fact]
    public void The_handoff_message_says_what_fell_short()
    {
        var next = new GameRule { ExecutableName = "second.exe", ProfileName = "Loud" };
        var ok = new ApplyResult(true, [KnobResult.Ok("core offset")]);
        var refused = new ApplyResult(false, [KnobResult.Fail("power limit", "NotSupported")]);

        Assert.Equal(
            "first.exe exited — 'Loud' for second.exe, still running, took over.",
            GameSessionRestore.DescribeHandoff("first.exe", next, ok, fansRestored: true, [], ok));
        Assert.Equal(
            "first.exe exited — 'Loud' for second.exe, still running, took over, but putting back the pre-game " +
            "power limit, fans failed; not put back: temperature limit (x); some of its knobs failed.",
            GameSessionRestore.DescribeHandoff("first.exe", next, refused, fansRestored: false, ["temperature limit (x)"], refused));
    }
}
