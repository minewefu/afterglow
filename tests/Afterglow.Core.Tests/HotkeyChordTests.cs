using Afterglow.Core.Hotkeys;

namespace Afterglow.Core.Tests;

public sealed class HotkeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Alt+O", "Ctrl+Alt+O")]
    [InlineData("alt+ctrl+o", "Ctrl+Alt+O")]
    [InlineData(" Control + Shift + F9 ", "Ctrl+Shift+F9")]
    [InlineData("Ctrl+Alt+Shift+2", "Ctrl+Alt+Shift+2")]
    [InlineData("Ctrl+Alt+F24", "Ctrl+Alt+F24")]
    [InlineData("Ctrl+Alt+num5", "Ctrl+Alt+Num5")]
    [InlineData("ctrl+alt+pageup", "Ctrl+Alt+PageUp")]
    [InlineData("Ctrl+Alt+Delete", "Ctrl+Alt+Delete")]
    public void Parses_and_prints_canonically(string text, string canonical)
    {
        Assert.True(HotkeyChord.TryParse(text, out var chord));
        Assert.Equal(canonical, chord.ToString());

        // What is saved reads back to the same chord.
        Assert.True(HotkeyChord.TryParse(chord.ToString(), out var again));
        Assert.Equal(chord, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Alt")]               // no key
    [InlineData("Ctrl+Ctrl+O")]            // a modifier twice
    [InlineData("Win+Alt+O")]              // the Windows key is the OS's
    [InlineData("Ctrl+Alt+OO")]
    [InlineData("Ctrl+Alt+F25")]
    [InlineData("Ctrl+Alt+F0")]
    [InlineData("Ctrl+Alt+@")]             // punctuation: its key differs per layout
    [InlineData("Ctrl+Alt+Tab")]
    [InlineData("Ctrl+O+Alt")]             // the key must come last
    public void Rejects_what_it_cannot_name(string? text)
    {
        Assert.False(HotkeyChord.TryParse(text, out _));
    }

    [Fact]
    public void Modifiers_are_RegisterHotKeys_own_values()
    {
        Assert.Equal(0x1, (int)HotkeyModifiers.Alt);
        Assert.Equal(0x2, (int)HotkeyModifiers.Control);
        Assert.Equal(0x4, (int)HotkeyModifiers.Shift);
        Assert.True(HotkeyChord.TryParse("Ctrl+Alt+O", out var chord));
        Assert.Equal('O', chord.VirtualKey);
        Assert.True(HotkeyChord.TryParse("Ctrl+Alt+F1", out var f1));
        Assert.Equal(0x70, f1.VirtualKey);
        Assert.True(HotkeyChord.TryParse("Ctrl+Alt+Num0", out var num0));
        Assert.Equal(0x60, num0.VirtualKey);
    }

    [Fact]
    public void Parts_are_the_keycaps_in_order()
    {
        Assert.True(HotkeyChord.TryParse("Shift+Alt+Ctrl+R", out var chord));
        Assert.Equal(["Ctrl", "Alt", "Shift", "R"], chord.Parts);
    }

    [Fact]
    public void Key_names_cover_exactly_the_supported_keys()
    {
        Assert.Equal("Q", HotkeyChord.KeyName('Q'));
        Assert.Equal("7", HotkeyChord.KeyName('7'));
        Assert.Equal("F12", HotkeyChord.KeyName(0x7B));
        Assert.Equal("Num9", HotkeyChord.KeyName(0x69));
        Assert.Equal("Home", HotkeyChord.KeyName(0x24));
        Assert.Null(HotkeyChord.KeyName(0xC0));   // VK_OEM_3: ` in the US, ñ in Spain
        Assert.Null(HotkeyChord.KeyName(0x09));   // Tab
        Assert.Null(HotkeyChord.KeyName(0x1B));   // Esc
    }
}
