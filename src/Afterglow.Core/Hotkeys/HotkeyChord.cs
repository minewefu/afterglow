using System.Globalization;

namespace Afterglow.Core.Hotkeys;

/// <summary>
/// Modifier keys of a global shortcut. The values are RegisterHotKey's MOD_ALT,
/// MOD_CONTROL and MOD_SHIFT, so a chord's modifiers pass straight through. The
/// Windows key is left out on purpose: RegisterHotKey's documentation reserves
/// shortcuts with it for the operating system.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
}

/// <summary>
/// A global shortcut as RegisterHotKey takes it: modifiers plus one virtual key.
/// Persisted as text ("Ctrl+Alt+O") so settings.json stays readable and editable.
/// </summary>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, ushort VirtualKey)
{
    private const ushort VkF1 = 0x70;
    private const ushort VkF24 = 0x87;
    private const ushort VkNumPad0 = 0x60;
    private const ushort VkNumPad9 = 0x69;

    /// <summary>
    /// The non-alphanumeric keys a shortcut may use. Punctuation keys are left out:
    /// their virtual keys sit on different characters in every layout (VK_OEM_3 is
    /// ` in the US and ñ in Spain), so no name for them would be honest.
    /// </summary>
    private static readonly Dictionary<ushort, string> NamedKeys = new()
    {
        [0x21] = "PageUp",
        [0x22] = "PageDown",
        [0x23] = "End",
        [0x24] = "Home",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
    };

    public bool IsLetterOrDigit => VirtualKey is >= '0' and <= '9' or >= 'A' and <= 'Z';

    public bool IsFunctionKey => VirtualKey is >= VkF1 and <= VkF24;

    /// <summary>The key's name ("O", "2", "F9", "Num5", "PageUp"), or null when shortcuts can't use it.</summary>
    public static string? KeyName(ushort virtualKey) => virtualKey switch
    {
        >= '0' and <= '9' or >= 'A' and <= 'Z' => ((char)virtualKey).ToString(),
        >= VkF1 and <= VkF24 => "F" + (virtualKey - VkF1 + 1).ToString(CultureInfo.InvariantCulture),
        >= VkNumPad0 and <= VkNumPad9 => "Num" + (virtualKey - VkNumPad0).ToString(CultureInfo.InvariantCulture),
        _ => NamedKeys.GetValueOrDefault(virtualKey),
    };

    /// <summary>Modifier names then the key, in canonical order: what the Settings keycaps show.</summary>
    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                parts.Add("Shift");
            }

            parts.Add(KeyName(VirtualKey) ?? $"0x{VirtualKey:X2}");
            return parts;
        }
    }

    /// <summary>Canonical text, e.g. "Ctrl+Alt+O"; <see cref="TryParse"/> reads it back.</summary>
    public override string ToString() => string.Join('+', Parts);

    /// <summary>
    /// Reads "Ctrl+Alt+O"-style text: modifiers in any order and case, then exactly
    /// one key Afterglow can name. Anything else (including Win) is rejected.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] tokens = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            HotkeyModifiers modifier = tokens[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        ushort? key = ParseKey(tokens[^1]);
        if (key is null)
        {
            return false;
        }

        chord = new HotkeyChord(modifiers, key.Value);
        return true;
    }

    private static ushort? ParseKey(string token)
    {
        if (token.Length == 1 && char.ToUpperInvariant(token[0]) is var c and (>= '0' and <= '9' or >= 'A' and <= 'Z'))
        {
            return c;
        }

        if (token.Length is 2 or 3 && token[0] is 'F' or 'f' &&
            int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            return (ushort)(VkF1 + f - 1);
        }

        if (token.Length == 4 && token.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && token[3] is >= '0' and <= '9')
        {
            return (ushort)(VkNumPad0 + token[3] - '0');
        }

        foreach (var (virtualKey, name) in NamedKeys)
        {
            if (name.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return virtualKey;
            }
        }

        return null;
    }
}
