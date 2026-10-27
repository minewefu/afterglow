// Surveys every keyboard layout installed with Windows for the characters a
// Ctrl+Alt shortcut would take from typing (issue #6), using the same probe the
// app checks its shortcuts with. Prints the Markdown tables in
// docs/research/keyboard-layouts.md.
//
//   dotnet run tools/keyboard-layout-survey.cs
//
// Each layout is loaded without being activated (KLF_NOTELLSHELL) and unloaded
// again unless it was already installed; the script checks that the user's
// layout list is unchanged at the end.
#:project ../src/Afterglow.Core/Afterglow.Core.csproj
#:property TargetFramework=net10.0-windows
#:property AllowUnsafeBlocks=true

using System.Globalization;
using System.Runtime.InteropServices;
using Afterglow.Core.Hotkeys;
using Microsoft.Win32;

const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;
nint[] before = KeyboardLayouts.Installed();
using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts")!;
string[] klids = [.. root.GetSubKeyNames().Where(k => !k.StartsWith("E0", StringComparison.OrdinalIgnoreCase)).Order()];

var chords = new List<HotkeyChord>();
for (char key = 'A'; key <= 'Z'; key++)
{
    chords.Add(new HotkeyChord(CtrlAlt, key));
}

for (char key = '0'; key <= '9'; key++)
{
    chords.Add(new HotkeyChord(CtrlAlt, key));
}

for (ushort f = 0x70; f <= 0x7B; f++)
{
    chords.Add(new HotkeyChord(CtrlAlt, f));
}

var hits = chords.ToDictionary(c => c, _ => new List<string>());
int surveyed = 0;
var notLoaded = new List<string>();
foreach (string klid in klids)
{
    nint layout = Native.LoadKeyboardLayout(klid, 0x80);
    try
    {
        // Windows answers a layout it can't load with another one; its language must match.
        if ((ushort)layout != ushort.Parse(klid.AsSpan(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
        {
            notLoaded.Add(klid);
            continue;
        }

        surveyed++;
        foreach (var chord in chords)
        {
            if (KeyboardLayouts.Typed(chord, layout) is { } typed)
            {
                hits[chord].Add($"{typed.LayoutName} `{typed.Text}`{(typed.IsDeadKey ? " (dead key)" : string.Empty)}");
            }
        }
    }
    finally
    {
        if (!before.Contains(layout))
        {
            _ = Native.UnloadKeyboardLayout(layout);
        }
    }
}

Console.WriteLine($"Windows {Environment.OSVersion.Version}: {surveyed} of {klids.Length} layouts surveyed" +
                  (notLoaded.Count > 0 ? $"; not loadable: {string.Join(", ", notLoaded)}" : string.Empty));
Console.WriteLine();
Console.WriteLine("| Shortcut | Layouts it types on | |");
Console.WriteLine("|---|---:|---|");
foreach (var action in HotkeyActions.All)
{
    var primary = HotkeyDefaults.Primary(action);
    var fallback = HotkeyDefaults.Fallback(action);
    Console.WriteLine($"| {primary} | {hits[primary].Count} | {HotkeyActions.Describe(action)} (default) |");
    Console.WriteLine($"| {fallback} | {hits[fallback].Count} | its stand-in |");
}

var least = chords.Where(c => !c.IsFunctionKey).OrderBy(c => hits[c].Count).First();
Console.WriteLine();
Console.WriteLine($"Every Ctrl+Alt letter and digit types on at least {hits[least].Count} layouts (fewest: {least}); " +
                  $"Ctrl+Alt+F1..F12 type on {chords.Where(c => c.IsFunctionKey).Sum(c => hits[c].Count)}.");
foreach (var action in new[] { HotkeyAction.ToggleOverlay, HotkeyAction.PanicReset, HotkeyAction.ApplyProfile2 })
{
    var primary = HotkeyDefaults.Primary(action);
    Console.WriteLine();
    Console.WriteLine($"{primary}: {string.Join("; ", hits[primary])}");
}

bool restored = KeyboardLayouts.Installed().SequenceEqual(before);
Console.WriteLine();
Console.WriteLine(restored ? "Your keyboard layouts are as they were." : "WARNING: the layout list changed during the survey.");
return restored ? 0 : 1;

internal static partial class Native
{
    [LibraryImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadKeyboardLayout(string klid, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnloadKeyboardLayout(nint layout);
}
