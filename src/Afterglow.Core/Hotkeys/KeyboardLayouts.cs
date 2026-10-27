using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Afterglow.Core.Hotkeys;

/// <summary>A character a shortcut would type on one of the user's keyboard layouts.</summary>
/// <param name="Text">What the keys type; for a dead key, the accent it waits to place.</param>
/// <param name="LayoutName">The layout's name as Windows lists it ("Spanish", "Polish (Programmers)").</param>
public sealed record TypedCharacter(string Text, string LayoutName, bool IsDeadKey);

/// <summary>
/// What a key chord types on the keyboard layouts installed for this user.
/// To Windows, AltGr is Ctrl+Alt: on a layout with an AltGr key (KLLF_ALTGR) the
/// right Alt key also presses a left Ctrl, and Ctrl+Alt+key reads the same AltGr
/// column of the layout. So a RegisterHotKey on Ctrl+Alt+2 also takes AltGr+2 —
/// "@" on the Spanish layout — away from every application (issue #6). Asking
/// each layout through ToUnicodeEx is how a shortcut is checked before it is
/// registered.
/// </summary>
public static unsafe partial class KeyboardLayouts
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLShift = 0xA0;
    private const int VkLControl = 0xA2;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const byte KeyDown = 0x80;
    private const uint MapVkToScanCode = 0;

    /// <summary>
    /// ToUnicodeEx flag bit 2 (Windows 10 1607 and later): leave the kernel's
    /// keyboard state alone. Without it, probing a dead key (Spanish AltGr+4, "~")
    /// would leave the accent pending and land on the user's next keystroke.
    /// </summary>
    private const uint KeepKeyboardState = 0x4;

    private const string LayoutsKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

    [LibraryImport("user32.dll")]
    private static partial int GetKeyboardLayoutList(int count, nint* layouts);

    [LibraryImport("user32.dll")]
    private static partial int ToUnicodeEx(
        uint virtualKey, uint scanCode, byte* keyState, char* buffer, int bufferLength, uint flags, nint layout);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyExW")]
    private static partial uint MapVirtualKeyEx(uint code, uint mapType, nint layout);

    /// <summary>The input layouts loaded for this user (Settings › Time &amp; language › Keyboard).</summary>
    public static nint[] Installed()
    {
        int count = GetKeyboardLayoutList(0, null);
        var layouts = new nint[Math.Max(count, 0)];
        fixed (nint* list = layouts)
        {
            count = GetKeyboardLayoutList(layouts.Length, list);
        }

        return count == layouts.Length ? layouts : layouts[..Math.Max(count, 0)];
    }

    /// <summary>The first character <paramref name="chord"/> types on any of <paramref name="layouts"/>, or null.</summary>
    public static TypedCharacter? FirstTyped(HotkeyChord chord, IEnumerable<nint> layouts)
    {
        foreach (nint layout in layouts)
        {
            if (Typed(chord, layout) is { } typed)
            {
                return typed;
            }
        }

        return null;
    }

    /// <summary>
    /// What <paramref name="chord"/> types on one layout: printable text or a dead
    /// key counts; nothing, or only control characters (Ctrl+C is U+0003), doesn't.
    /// </summary>
    public static TypedCharacter? Typed(HotkeyChord chord, nint layout)
    {
        bool hasAlt = chord.Modifiers.HasFlag(HotkeyModifiers.Alt);
        if (hasAlt && !chord.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            // Alt without Ctrl is the menu key: Windows turns it into WM_SYSCHAR
            // (a mnemonic), never text, though ToUnicodeEx answers with the
            // un-Alted character. And AltGr can't press it — AltGr adds a Ctrl,
            // and a hotkey only fires on its exact modifiers.
            return null;
        }

        uint scanCode = MapVirtualKeyEx(chord.VirtualKey, MapVkToScanCode, layout);
        byte* state = stackalloc byte[256];
        char* buffer = stackalloc char[8];

        // Alt is asked as both keys: the left one (what Ctrl+Alt is pressed with)
        // and the right one (AltGr, together with the left Ctrl Windows adds).
        for (int pass = 0; pass < (hasAlt ? 2 : 1); pass++)
        {
            new Span<byte>(state, 256).Clear();
            if (chord.Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                state[VkControl] = KeyDown;
                state[VkLControl] = KeyDown;
            }

            if (hasAlt)
            {
                state[VkMenu] = KeyDown;
                state[pass == 0 ? VkLMenu : VkRMenu] = KeyDown;
            }

            if (chord.Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                state[VkShift] = KeyDown;
                state[VkLShift] = KeyDown;
            }

            int length = ToUnicodeEx(chord.VirtualKey, scanCode, state, buffer, 8, KeepKeyboardState, layout);
            if (length < 0)
            {
                return new TypedCharacter(new string(buffer[0], 1), DisplayName(layout), IsDeadKey: true);
            }

            var text = new ReadOnlySpan<char>(buffer, Math.Min(length, 8));
            foreach (char c in text)
            {
                if (c >= 0x20 && c != 0x7F)
                {
                    return new TypedCharacter(text.ToString(), DisplayName(layout), IsDeadKey: false);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The layout's name from the registry ("Spanish", "Polish (Programmers)").
    /// An HKL's high word is the layout: 0xFnnn is a variant found by its "Layout
    /// Id", anything else the low word of its KLID. Falls back to the language.
    /// </summary>
    public static string DisplayName(nint layout)
    {
        uint hkl = (uint)((ulong)layout & 0xFFFF_FFFF);
        int language = (int)(hkl & 0xFFFF);
        int device = (int)(hkl >> 16);
        try
        {
            string? klid = (device & 0xF000) switch
            {
                0xF000 => FindVariant(device & 0x0FFF),
                0xE000 => hkl.ToString("X8", CultureInfo.InvariantCulture),
                _ => "0000" + device.ToString("X4", CultureInfo.InvariantCulture),
            };
            if (klid is not null)
            {
                using var entry = Registry.LocalMachine.OpenSubKey($@"{LayoutsKey}\{klid}");
                if (entry?.GetValue("Layout Text") is string { Length: > 0 } name)
                {
                    return name;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        try
        {
            return CultureInfo.GetCultureInfo(language).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return $"layout {hkl:X8}";
        }
    }

    private static string? FindVariant(int layoutId)
    {
        using var layouts = Registry.LocalMachine.OpenSubKey(LayoutsKey);
        if (layouts is null)
        {
            return null;
        }

        foreach (string klid in layouts.GetSubKeyNames())
        {
            using var entry = layouts.OpenSubKey(klid);
            if (entry?.GetValue("Layout Id") is string id &&
                int.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) && value == layoutId)
            {
                return klid;
            }
        }

        return null;
    }
}
