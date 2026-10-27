using System.Runtime.InteropServices;
using Afterglow.Core.Hotkeys;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Tests;

/// <summary>
/// The layout probe against Windows' own keyboard layouts. Each test loads the
/// layouts it needs without activating them (KLF_NOTELLSHELL) and unloads the
/// ones it loaded, so the user's input list is left as it was.
/// </summary>
public sealed partial class KeyboardLayoutsTests : IDisposable
{
    private const uint KlfNoTellShell = 0x80;

    [LibraryImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadKeyboardLayout(string klid, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnloadKeyboardLayout(nint layout);

    private readonly nint[] _before = KeyboardLayouts.Installed();
    private readonly List<nint> _loaded = [];

    private nint Load(string klid, uint expectedHkl)
    {
        nint layout = LoadKeyboardLayout(klid, KlfNoTellShell);
        if (!_before.Contains(layout) && layout != 0)
        {
            _loaded.Add(layout);
        }

        // Windows answers an unknown or unloadable layout with the default one.
        uint hkl = (uint)((ulong)layout & 0xFFFF_FFFF);
        Assert.SkipWhen(hkl != expectedHkl, $"Windows didn't load keyboard layout {klid} here (got {hkl:X8}).");
        return layout;
    }

    public void Dispose()
    {
        foreach (nint layout in _loaded)
        {
            _ = UnloadKeyboardLayout(layout);
        }

        _loaded.Clear();
    }

    private static HotkeyChord Chord(string text)
    {
        Assert.True(HotkeyChord.TryParse(text, out var chord), text);
        return chord;
    }

    [Fact]
    public void Issue_6_Spanish_AltGr_digits_are_characters()
    {
        nint spanish = Load("0000040A", 0x040A040A);

        Assert.Equal(new TypedCharacter("@", "Spanish", IsDeadKey: false), KeyboardLayouts.Typed(Chord("Ctrl+Alt+2"), spanish));
        Assert.Equal("|", KeyboardLayouts.Typed(Chord("Ctrl+Alt+1"), spanish)?.Text);
        Assert.Equal("€", KeyboardLayouts.Typed(Chord("Ctrl+Alt+5"), spanish)?.Text);
        Assert.Equal(new TypedCharacter("~", "Spanish", IsDeadKey: true), KeyboardLayouts.Typed(Chord("Ctrl+Alt+4"), spanish));

        Assert.Null(KeyboardLayouts.Typed(Chord("Ctrl+Alt+O"), spanish));
        Assert.Null(KeyboardLayouts.Typed(Chord("Ctrl+Alt+R"), spanish));
        Assert.Null(KeyboardLayouts.Typed(Chord("Ctrl+Alt+F5"), spanish));
        Assert.Null(KeyboardLayouts.Typed(Chord("Ctrl+Shift+C"), spanish));   // a control character
    }

    [Fact]
    public void Probing_a_dead_key_leaves_nothing_pending()
    {
        nint spanish = Load("0000040A", 0x040A040A);

        Assert.True(KeyboardLayouts.Typed(Chord("Ctrl+Alt+4"), spanish)?.IsDeadKey);

        // Had the probe armed the accent, Shift+1 would come back combined with it.
        Assert.Equal("!", KeyboardLayouts.Typed(new HotkeyChord(HotkeyModifiers.Shift, '1'), spanish)?.Text);
    }

    [Fact]
    public void Alt_without_Ctrl_is_a_menu_key_not_text()
    {
        nint spanish = Load("0000040A", 0x040A040A);

        // ToUnicodeEx alone would answer '"' (Shift+2); Windows sends WM_SYSCHAR.
        Assert.Null(KeyboardLayouts.Typed(Chord("Alt+Shift+2"), spanish));
    }

    [Theory]
    [InlineData("00000407", 0x04070407u, "Ctrl+Alt+Q", "@", "German")]
    [InlineData("00000407", 0x04070407u, "Ctrl+Alt+2", "²", "German")]
    [InlineData("00000809", 0x08090809u, "Ctrl+Alt+O", "ó", "United Kingdom")]
    [InlineData("00000809", 0x08090809u, "Ctrl+Alt+4", "€", "United Kingdom")]
    [InlineData("00000415", 0x04150415u, "Ctrl+Alt+O", "ó", "Polish (Programmers)")]
    [InlineData("00020409", 0xF0010409u, "Ctrl+Alt+R", "®", "United States-International")]
    public void Other_layouts_type_what_Windows_says(string klid, uint hkl, string chord, string text, string name)
    {
        nint layout = Load(klid, hkl);

        Assert.Equal(new TypedCharacter(text, name, IsDeadKey: false), KeyboardLayouts.Typed(Chord(chord), layout));
    }

    [Fact]
    public void US_types_nothing_with_any_default()
    {
        nint us = Load("00000409", 0x04090409);

        foreach (var action in HotkeyActions.All)
        {
            Assert.Null(KeyboardLayouts.Typed(HotkeyDefaults.Primary(action), us));
            Assert.Null(KeyboardLayouts.Typed(HotkeyDefaults.Fallback(action), us));
        }
    }

    [Fact]
    public void F_key_fallbacks_type_nothing_on_the_AltGr_layouts()
    {
        nint[] layouts =
        [
            Load("0000040A", 0x040A040A), Load("00000407", 0x04070407), Load("00000809", 0x08090809),
            Load("00000415", 0x04150415), Load("00020409", 0xF0010409),
        ];

        foreach (var action in HotkeyActions.All)
        {
            Assert.Null(KeyboardLayouts.FirstTyped(HotkeyDefaults.Fallback(action), layouts));
        }
    }

    [Fact]
    public void Planned_against_a_real_Spanish_layout_the_slots_use_F_keys()
    {
        nint[] layouts = [Load("00000409", 0x04090409), Load("0000040A", 0x040A040A)];

        var plan = HotkeyPlanner.Plan(new HotkeySettings(), chord => KeyboardLayouts.FirstTyped(chord, layouts));

        Assert.Equal(Chord("Ctrl+Alt+O"), plan[(int)HotkeyAction.ToggleOverlay].Candidates[0]);
        Assert.Equal(Chord("Ctrl+Alt+R"), plan[(int)HotkeyAction.PanicReset].Candidates[0]);
        Assert.Equal([Chord("Ctrl+Alt+F2")], plan[(int)HotkeyAction.ApplyProfile2].Candidates);
        Assert.Equal("Ctrl+Alt+2 types \"@\" on your Spanish keyboard layout", plan[(int)HotkeyAction.ApplyProfile2].Reason);
    }

    [Fact]
    public void The_users_layouts_are_left_as_they_were()
    {
        _ = Load("0000040A", 0x040A040A);
        _ = Load("00020409", 0xF0010409);
        Dispose();

        Assert.Equal(_before, KeyboardLayouts.Installed());
    }
}
