using Afterglow.Core.Hotkeys;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Tests;

/// <summary>
/// The shortcut rules against layouts described by table. The characters are
/// what Windows' own layouts type (docs/research/keyboard-layouts.md); the real
/// layouts are exercised in <see cref="KeyboardLayoutsTests"/>.
/// </summary>
public sealed class HotkeyPlannerTests
{
    private static readonly Func<HotkeyChord, TypedCharacter?> UnitedStates = _ => null;

    private static readonly Func<HotkeyChord, TypedCharacter?> Spanish = Layout("Spanish",
        ("Ctrl+Alt+1", "|"), ("Ctrl+Alt+2", "@"), ("Ctrl+Alt+3", "#"), ("Ctrl+Alt+4", "~ dead"),
        ("Ctrl+Alt+5", "€"), ("Ctrl+Alt+6", "¬"), ("Ctrl+Alt+E", "€"));

    private static readonly Func<HotkeyChord, TypedCharacter?> UnitedKingdom = Layout("United Kingdom",
        ("Ctrl+Alt+O", "ó"), ("Ctrl+Alt+4", "€"), ("Ctrl+Alt+E", "é"));

    private static readonly Func<HotkeyChord, TypedCharacter?> UsInternational = Layout("United States-International",
        ("Ctrl+Alt+O", "ó"), ("Ctrl+Alt+R", "®"), ("Ctrl+Alt+1", "¡"), ("Ctrl+Alt+2", "²"),
        ("Ctrl+Alt+3", "³"), ("Ctrl+Alt+4", "¤"), ("Ctrl+Alt+5", "€"));

    private static Func<HotkeyChord, TypedCharacter?> Layout(string name, params (string Chord, string Text)[] typed)
    {
        var table = typed.ToDictionary(t => Chord(t.Chord), t => t.Text);
        return chord => table.TryGetValue(chord, out string? text)
            ? new TypedCharacter(text.Replace(" dead", string.Empty, StringComparison.Ordinal), name, text.EndsWith(" dead", StringComparison.Ordinal))
            : null;
    }

    private static HotkeyChord Chord(string text)
    {
        Assert.True(HotkeyChord.TryParse(text, out var chord), text);
        return chord;
    }

    private static Dictionary<HotkeyAction, HotkeyAssignment> Plan(HotkeySettings settings, Func<HotkeyChord, TypedCharacter?> layout) =>
        HotkeyPlanner.Plan(settings, layout).ToDictionary(a => a.Action);

    private static string[] Candidates(HotkeyAssignment assignment) =>
        assignment.Candidates.Select(c => c.ToString()).ToArray();

    [Fact]
    public void Without_AltGr_characters_the_defaults_are_what_earlier_versions_registered()
    {
        var plan = Plan(new HotkeySettings(), UnitedStates);

        Assert.Equal(["Ctrl+Alt+O", "Ctrl+Alt+F10"], Candidates(plan[HotkeyAction.ToggleOverlay]));
        Assert.Equal(["Ctrl+Alt+R", "Ctrl+Alt+F11"], Candidates(plan[HotkeyAction.PanicReset]));
        for (int slot = 0; slot < HotkeyActions.ProfileSlotCount; slot++)
        {
            var assignment = plan[HotkeyActions.ProfileSlot(slot)];
            Assert.Equal([$"Ctrl+Alt+{slot + 1}", $"Ctrl+Alt+F{slot + 1}"], Candidates(assignment));
            Assert.Null(assignment.Reason);
            Assert.True(assignment.IsDefault);
        }
    }

    [Fact]
    public void Issue_6_on_Spanish_the_profile_slots_leave_the_digits_to_typing()
    {
        var plan = Plan(new HotkeySettings(), Spanish);

        // O and R type nothing on Spanish, so they stay.
        Assert.Equal("Ctrl+Alt+O", Candidates(plan[HotkeyAction.ToggleOverlay])[0]);
        Assert.Equal("Ctrl+Alt+R", Candidates(plan[HotkeyAction.PanicReset])[0]);
        for (int slot = 0; slot < HotkeyActions.ProfileSlotCount; slot++)
        {
            var assignment = plan[HotkeyActions.ProfileSlot(slot)];
            Assert.Equal([$"Ctrl+Alt+F{slot + 1}"], Candidates(assignment));
            Assert.Equal(Chord($"Ctrl+Alt+{slot + 1}"), assignment.Requested);
        }

        Assert.Equal("Ctrl+Alt+2 types \"@\" on your Spanish keyboard layout", plan[HotkeyAction.ApplyProfile2].Reason);
        Assert.Equal("Ctrl+Alt+4 starts the \"~\" accent on your Spanish keyboard layout", plan[HotkeyAction.ApplyProfile4].Reason);
    }

    [Fact]
    public void On_UK_the_overlay_falls_back_and_all_five_slots_move_together()
    {
        var plan = Plan(new HotkeySettings(), UnitedKingdom);

        Assert.Equal(["Ctrl+Alt+F10"], Candidates(plan[HotkeyAction.ToggleOverlay]));
        Assert.Contains("\"ó\"", plan[HotkeyAction.ToggleOverlay].Reason, StringComparison.Ordinal);
        Assert.Equal("Ctrl+Alt+R", Candidates(plan[HotkeyAction.PanicReset])[0]);

        // Only Ctrl+Alt+4 types (€), but a row of 1, 2, 3, F4, 5 would be a trap.
        for (int slot = 0; slot < HotkeyActions.ProfileSlotCount; slot++)
        {
            var assignment = plan[HotkeyActions.ProfileSlot(slot)];
            Assert.Equal([$"Ctrl+Alt+F{slot + 1}"], Candidates(assignment));
            Assert.Contains("Ctrl+Alt+4 types \"€\"", assignment.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void On_US_International_every_default_uses_its_F_key()
    {
        var plan = Plan(new HotkeySettings(), UsInternational);

        Assert.Equal(["Ctrl+Alt+F10"], Candidates(plan[HotkeyAction.ToggleOverlay]));
        Assert.Equal(["Ctrl+Alt+F11"], Candidates(plan[HotkeyAction.PanicReset]));
        Assert.Contains("\"®\"", plan[HotkeyAction.PanicReset].Reason, StringComparison.Ordinal);
        Assert.Equal(["Ctrl+Alt+F3"], Candidates(plan[HotkeyAction.ApplyProfile3]));
    }

    [Fact]
    public void No_plan_ever_registers_a_chord_that_types()
    {
        // Every candidate on every layout here — and with layouts combined, as a
        // user with two keyboards has — must type nothing on any of them.
        Func<HotkeyChord, TypedCharacter?>[] layouts = [UnitedStates, Spanish, UnitedKingdom, UsInternational];
        var settingsCases = new[]
        {
            new HotkeySettings(),
            new HotkeySettings().With(HotkeyAction.ToggleOverlay, "Ctrl+Alt+2"),
            new HotkeySettings().With(HotkeyAction.ApplyProfile1, "Ctrl+Alt+E").With(HotkeyAction.PanicReset, "Ctrl+Alt+F1"),
        };
        for (int mask = 1; mask < 1 << layouts.Length; mask++)
        {
            var active = layouts.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            TypedCharacter? Any(HotkeyChord chord) => active.Select(l => l(chord)).FirstOrDefault(t => t is not null);
            foreach (var settings in settingsCases)
            {
                var plan = HotkeyPlanner.Plan(settings, Any);
                var registered = plan.SelectMany(a => a.Candidates).ToArray();
                Assert.All(registered, chord => Assert.Null(Any(chord)));
                Assert.Equal(registered.Length, registered.Distinct().Count());
            }
        }
    }

    [Fact]
    public void A_chosen_shortcut_that_types_is_refused_not_replaced()
    {
        var settings = new HotkeySettings().With(HotkeyAction.ToggleOverlay, "Ctrl+Alt+2");
        var overlay = Plan(settings, Spanish)[HotkeyAction.ToggleOverlay];

        Assert.Empty(overlay.Candidates);
        Assert.False(overlay.IsDefault);
        Assert.Equal(Chord("Ctrl+Alt+2"), overlay.Requested);
        Assert.Equal("Ctrl+Alt+2 types \"@\" on your Spanish keyboard layout", overlay.Reason);
    }

    [Fact]
    public void A_chosen_shortcut_wins_over_a_default_that_wanted_it()
    {
        // Panic on Ctrl+Alt+1: slot 1's default moves to its F key rather than collide.
        var settings = new HotkeySettings().With(HotkeyAction.PanicReset, "Ctrl+Alt+1");
        var plan = Plan(settings, UnitedStates);

        Assert.Equal(["Ctrl+Alt+1"], Candidates(plan[HotkeyAction.PanicReset]));
        Assert.Equal(["Ctrl+Alt+F1"], Candidates(plan[HotkeyAction.ApplyProfile1]));
        Assert.Contains("Panic", plan[HotkeyAction.ApplyProfile1].Reason, StringComparison.Ordinal);

        // ...and a default never takes a chosen chord as its fallback either.
        var takesFallback = Plan(new HotkeySettings().With(HotkeyAction.PanicReset, "Ctrl+Alt+F10"), UnitedStates);
        Assert.Equal(["Ctrl+Alt+O"], Candidates(takesFallback[HotkeyAction.ToggleOverlay]));
    }

    [Fact]
    public void The_same_chosen_shortcut_twice_keeps_the_first()
    {
        var settings = new HotkeySettings()
            .With(HotkeyAction.ToggleOverlay, "Ctrl+Shift+F9")
            .With(HotkeyAction.PanicReset, "ctrl+shift+f9");
        var plan = Plan(settings, UnitedStates);

        Assert.Equal(["Ctrl+Shift+F9"], Candidates(plan[HotkeyAction.ToggleOverlay]));
        Assert.Empty(plan[HotkeyAction.PanicReset].Candidates);
        Assert.Equal("Ctrl+Shift+F9 is also set for \"Show or hide the overlay\"", plan[HotkeyAction.PanicReset].Reason);
    }

    [Fact]
    public void None_garbage_and_off_register_nothing()
    {
        var plan = Plan(new HotkeySettings().With(HotkeyAction.ToggleOverlay, "").With(HotkeyAction.PanicReset, "Ctrl+Alt+Banana"),
            UnitedStates);
        Assert.Empty(plan[HotkeyAction.ToggleOverlay].Candidates);
        Assert.Null(plan[HotkeyAction.ToggleOverlay].Reason);
        Assert.Null(plan[HotkeyAction.ToggleOverlay].Requested);
        Assert.Empty(plan[HotkeyAction.PanicReset].Candidates);
        Assert.Equal("\"Ctrl+Alt+Banana\" isn't a shortcut Afterglow can use", plan[HotkeyAction.PanicReset].Reason);

        var off = HotkeyPlanner.Plan(new HotkeySettings { Enabled = false }.With(HotkeyAction.PanicReset, "Ctrl+Shift+F9"), UnitedStates);
        Assert.All(off, a =>
        {
            Assert.Empty(a.Candidates);
            Assert.Equal(HotkeyPlanner.TurnedOff, a.Reason);
        });
    }

    [Theory]
    [InlineData("Shift+F5", "A shortcut needs Ctrl or Alt")]
    [InlineData("Ctrl+C", "would take an everyday app shortcut")]
    [InlineData("Alt+O", "would take an everyday app shortcut")]
    [InlineData("Ctrl+Home", "would take an everyday app shortcut")]
    [InlineData("Alt+F4", "belongs to Windows")]
    [InlineData("Ctrl+F4", "belongs to Windows")]
    [InlineData("Ctrl+Alt+Delete", "belongs to Windows")]
    [InlineData("Ctrl+Alt+F12", "F12 for debuggers")]
    public void Check_refuses_chords_that_belong_to_someone_else(string chord, string because)
    {
        Assert.Contains(because, HotkeyPlanner.Check(Chord(chord), UnitedStates), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+Alt+O")]
    [InlineData("Ctrl+Shift+R")]
    [InlineData("Alt+Shift+2")]
    [InlineData("Ctrl+F9")]
    [InlineData("Alt+F10")]
    [InlineData("Ctrl+Alt+Num5")]
    public void Check_accepts_ordinary_chords(string chord)
    {
        Assert.Null(HotkeyPlanner.Check(Chord(chord), UnitedStates));
    }
}
