using System.Numerics;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Hotkeys;

/// <summary>What a global shortcut does. The profile slots apply the Nth saved profile (alphabetical).</summary>
public enum HotkeyAction
{
    ToggleOverlay,
    PanicReset,
    ApplyProfile1,
    ApplyProfile2,
    ApplyProfile3,
    ApplyProfile4,
    ApplyProfile5,
}

public static class HotkeyActions
{
    public const int ProfileSlotCount = 5;

    public static IReadOnlyList<HotkeyAction> All { get; } = Enum.GetValues<HotkeyAction>();

    public static HotkeyAction ProfileSlot(int slot) => HotkeyAction.ApplyProfile1 + slot;

    /// <summary>The zero-based profile slot, or null for the overlay and panic actions.</summary>
    public static int? SlotOf(HotkeyAction action) =>
        action >= HotkeyAction.ApplyProfile1 ? action - HotkeyAction.ApplyProfile1 : null;

    public static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleOverlay => "Show or hide the overlay",
        HotkeyAction.PanicReset => "Panic: reset the GPU to driver defaults",
        _ => $"Apply saved profile {SlotOf(action) + 1}",
    };
}

/// <summary>
/// The default shortcuts. The primary is what Afterglow registered before
/// shortcuts were configurable; the fallback is an F-key stand-in used where the
/// primary would type a character on one of the user's keyboard layouts. Of the
/// 218 layouts Windows ships, every Ctrl+Alt letter and digit types something on
/// at least 31, and no Ctrl+Alt+F key types anything on any of them
/// (docs/research/keyboard-layouts.md).
/// </summary>
public static class HotkeyDefaults
{
    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private const ushort VkF1 = 0x70;

    public static HotkeyChord Primary(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleOverlay => new(CtrlAlt, 'O'),
        HotkeyAction.PanicReset => new(CtrlAlt, 'R'),
        _ => new(CtrlAlt, (ushort)('1' + HotkeyActions.SlotOf(action)!.Value)),
    };

    public static HotkeyChord Fallback(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleOverlay => new(CtrlAlt, VkF1 + 9),    // F10
        HotkeyAction.PanicReset => new(CtrlAlt, VkF1 + 10),      // F11 (F12 is the debugger's)
        _ => new(CtrlAlt, (ushort)(VkF1 + HotkeyActions.SlotOf(action)!.Value)),
    };
}

/// <summary>One action's plan.</summary>
/// <param name="Requested">The user's shortcut, or the primary default; null when there is none or it didn't parse.</param>
/// <param name="Candidates">What to register, tried in order: the first Windows accepts wins. Empty: nothing.</param>
/// <param name="Reason">Why <paramref name="Requested"/> isn't the first candidate; null when it is (or there is none).</param>
/// <param name="IsDefault">The settings leave this action at its default.</param>
public sealed record HotkeyAssignment(
    HotkeyAction Action,
    HotkeyChord? Requested,
    IReadOnlyList<HotkeyChord> Candidates,
    string? Reason,
    bool IsDefault);

/// <summary>
/// Decides which shortcuts may be registered. A global shortcut takes its keys
/// away from every application, so a shortcut is refused when it types a
/// character on one of the user's keyboard layouts (AltGr is Ctrl+Alt to Windows:
/// Ctrl+Alt+2 is "@" on the Spanish layout, issue #6) or would take an everyday
/// shortcut such as Ctrl+C or Alt+F4. A refused default falls back to its F-key
/// stand-in; a refused shortcut the user chose is left unregistered, with the
/// reason. Pure apart from the injected layout probe, so the rules are tested
/// without touching the session's keyboard layouts.
/// </summary>
public static class HotkeyPlanner
{
    public const string TurnedOff = "Keyboard shortcuts are turned off";

    private const ushort VkF12 = 0x7B;

    /// <summary>Single-modifier F-key chords and the secure attention sequence, which belong to Windows and apps.</summary>
    private static readonly HashSet<HotkeyChord> Reserved =
    [
        new(HotkeyModifiers.Alt, 0x73),                                   // Alt+F4: close the window
        new(HotkeyModifiers.Control, 0x73),                               // Ctrl+F4: close the tab or document
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x2E),         // Ctrl+Alt+Delete
    ];

    /// <param name="typedOn">What a chord types on the user's layouts, e.g. <see cref="KeyboardLayouts.FirstTyped"/>.</param>
    public static IReadOnlyList<HotkeyAssignment> Plan(HotkeySettings settings, Func<HotkeyChord, TypedCharacter?> typedOn)
    {
        var plan = new HotkeyAssignment?[HotkeyActions.All.Count];
        var taken = new Dictionary<HotkeyChord, HotkeyAction>();

        // The user's own shortcuts first, so a default never takes a chord they chose.
        foreach (var action in HotkeyActions.All)
        {
            if (settings.For(action) is { } text)
            {
                plan[(int)action] = PlanChosen(action, text, settings.Enabled, typedOn, taken);
            }
        }

        // The profile slots move to F1–F5 together when any of their digits is
        // refused, so the five stay one row of keys instead of a mix.
        string? slotsReason = null;
        foreach (var action in HotkeyActions.All)
        {
            if (plan[(int)action] is null && HotkeyActions.SlotOf(action) is not null &&
                Check(HotkeyDefaults.Primary(action), typedOn) is { } refused)
            {
                slotsReason = refused;
                break;
            }
        }

        foreach (var action in HotkeyActions.All)
        {
            plan[(int)action] ??= PlanDefault(action, settings.Enabled, slotsReason, typedOn, taken);
        }

        return plan!;
    }

    private static HotkeyAssignment PlanChosen(
        HotkeyAction action, string text, bool enabled, Func<HotkeyChord, TypedCharacter?> typedOn,
        Dictionary<HotkeyChord, HotkeyAction> taken)
    {
        if (text.Length == 0)
        {
            return new HotkeyAssignment(action, null, [], null, IsDefault: false);
        }

        if (!HotkeyChord.TryParse(text, out var chord))
        {
            return new HotkeyAssignment(action, null, [], $"\"{text}\" isn't a shortcut Afterglow can use", IsDefault: false);
        }

        string? problem = !enabled ? TurnedOff : Check(chord, typedOn);
        if (problem is null && taken.TryGetValue(chord, out var first))
        {
            problem = $"{chord} is also set for \"{HotkeyActions.Describe(first)}\"";
        }

        if (problem is not null)
        {
            return new HotkeyAssignment(action, chord, [], problem, IsDefault: false);
        }

        taken[chord] = action;
        return new HotkeyAssignment(action, chord, [chord], null, IsDefault: false);
    }

    private static HotkeyAssignment PlanDefault(
        HotkeyAction action, bool enabled, string? slotsReason, Func<HotkeyChord, TypedCharacter?> typedOn,
        Dictionary<HotkeyChord, HotkeyAction> taken)
    {
        var primary = HotkeyDefaults.Primary(action);
        var fallback = HotkeyDefaults.Fallback(action);
        if (!enabled)
        {
            return new HotkeyAssignment(action, primary, [], TurnedOff, IsDefault: true);
        }

        string? reason = Check(primary, typedOn);
        if (reason is null && HotkeyActions.SlotOf(action) is not null && slotsReason is not null)
        {
            reason = "Profile shortcuts use F1–F5 here: " + LowerFirst(slotsReason);
        }

        if (reason is null && taken.TryGetValue(primary, out var owner))
        {
            reason = $"{primary} is set for \"{HotkeyActions.Describe(owner)}\"";
        }

        // The fallback stays behind a usable primary as well, for when another
        // app already holds the primary and Windows refuses it.
        var candidates = new List<HotkeyChord>(2);
        foreach (var chord in reason is null ? [primary, fallback] : new[] { fallback })
        {
            if (Check(chord, typedOn) is null && !taken.ContainsKey(chord))
            {
                candidates.Add(chord);
                taken[chord] = action;
            }
        }

        return new HotkeyAssignment(action, primary, candidates, reason, IsDefault: true);
    }

    /// <summary>Why <paramref name="chord"/> can't be a global shortcut, or null when it can.</summary>
    public static string? Check(HotkeyChord chord, Func<HotkeyChord, TypedCharacter?> typedOn)
    {
        if (!chord.Modifiers.HasFlag(HotkeyModifiers.Control) && !chord.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            return "A shortcut needs Ctrl or Alt";
        }

        if (!chord.IsFunctionKey && BitOperations.PopCount((uint)chord.Modifiers) < 2)
        {
            return $"{chord} would take an everyday app shortcut; use two modifiers (Ctrl+Alt, Ctrl+Shift, Alt+Shift) or an F key";
        }

        if (Reserved.Contains(chord))
        {
            return $"{chord} belongs to Windows";
        }

        if (chord.VirtualKey == VkF12)
        {
            // RegisterHotKey's documentation: F12 is reserved for debuggers at all times.
            return "Windows keeps F12 for debuggers";
        }

        if (typedOn(chord) is { } typed)
        {
            string what = typed.IsDeadKey ? $"starts the \"{typed.Text}\" accent" : $"types \"{typed.Text}\"";
            return $"{chord} {what} on your {typed.LayoutName} keyboard layout";
        }

        return null;
    }

    private static string LowerFirst(string text) =>
        text.Length > 0 && char.IsUpper(text[0]) && !text.StartsWith("Ctrl", StringComparison.Ordinal) &&
        !text.StartsWith("Alt", StringComparison.Ordinal)
            ? char.ToLowerInvariant(text[0]) + text[1..]
            : text;
}
