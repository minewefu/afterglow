# Keyboard shortcuts and AltGr

Why Afterglow checks each global shortcut against the user's keyboard layouts
before registering it ([issue #6](https://github.com/minewefu/afterglow/issues/6)),
where each fact came from, and what was measured. Sources were read on
2026-10-27; measurements are from Windows 11 build 26300 (an Insider build, so
layout tables may differ slightly on other releases).

## The defect

Up to 1.5.0, Afterglow registered fixed global shortcuts with `RegisterHotKey`:
Ctrl+Alt+O (overlay), Ctrl+Alt+R (panic reset) and Ctrl+Alt+1–5 (profile
slots). To Windows, AltGr *is* Ctrl+Alt, so on a keyboard with an AltGr key those
shortcuts fire when the user types AltGr+key, and the character never reaches
any application.

How that comes about:

- **AltGr presses a left Ctrl.** A layout that declares `KLLF_ALTGR` (Microsoft's
  own German layout sample does:
  [kbdgr.c](https://github.com/microsoft/Windows-driver-samples/blob/main/input/layout/all_kbds/kbdgr/kbdgr.c))
  makes the right Alt key also press a left Ctrl. Microsoft documents no more than
  the flag; the left-Ctrl behaviour is described by Gecko's maintainers
  ([Mozilla bug 900750](https://bugzilla.mozilla.org/show_bug.cgi?id=900750)) and
  by AutoHotkey, which writes AltGr as LCtrl+RAlt
  ([Hotkeys](https://www.autohotkey.com/docs/v2/Hotkeys.htm)). *Measured:* with the
  Spanish layout active, injecting only Right Alt leaves Left Ctrl down.
- **Ctrl+Alt is the AltGr column.** A layout table maps its AltGr characters to
  the Ctrl+Alt shift state (kbdgr.c: Q under "Control + Menu" is `@`); Raymond
  Chen states AltGr+Q and Ctrl+Alt+Q are the same thing
  ([The Old New Thing](https://devblogs.microsoft.com/oldnewthing/20040329-00/?p=40003)).
  *Measured:* physical Left Ctrl+Left Alt+2 types `@` on Spanish, and across all
  218 layouts left Alt with Ctrl gives the same result as AltGr in every one of
  26,596 comparisons.
- **RegisterHotKey can't tell them apart.** `MOD_CONTROL` and `MOD_ALT` match
  either Ctrl and either Alt key
  ([RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)).
  The documentation doesn't say the keystroke is consumed. *Measured:* it is.

### Reproduction (Afterglow 1.5.0 as released, `--demo`)

A test window switched to the layout and injected the keys with `SendInput`.

| Layout | Keys | Without Afterglow | 1.5.0 running | Fixed build |
|---|---|---|---|---|
| Spanish | AltGr+1 / 2 / 3 / 5 | `\|` `@` `#` `€` | nothing | `\|` `@` `#` `€` |
| Spanish | AltGr+4, space | `~` | a bare space | `~` |
| Spanish | Left Ctrl+Left Alt+2 | `@` | nothing | `@` |
| Spanish | AltGr+6 | `¬` | `¬` (only 1–5 were registered) | `¬` |
| Polish (Programmers) | AltGr+O | `ó` | nothing, and the overlay switched on | `ó` |
| German | AltGr+Q | `@` | `@` | `@` |

Swallowing was not the only effect. Each swallowed key ran its action: Polish
AltGr+O turned the overlay on (and saved that), and on Spanish typing `|` or `@`
applied saved profile 1 or 2 to the GPU.

## Guidance and precedent

- Microsoft's Windows UX guidelines say not to use Ctrl+Alt combinations for
  shortcuts, precisely because some languages read them as AltGr. The same page
  recommends letting users customize shortcuts and says not to register
  system-wide ones; that guide was written for Windows 7 and is marked as not
  updated
  ([Keyboard](https://learn.microsoft.com/en-us/windows/win32/uxguide/inter-keyboard)).
  Afterglow's shortcuts are system-wide by design (the overlay is toggled from
  inside games, the panic reset from anywhere), so it keeps them, makes them
  configurable, and makes sure they never take a key that types.
- `RegisterHotKey`'s documentation reserves Windows-key shortcuts for the OS and
  F12 for debuggers, and offers `MOD_NOREPEAT` so holding the keys fires once.
- VS Code ships no Ctrl+Alt+key defaults on Windows because they may produce
  characters ([Keybinding issues](https://github.com/microsoft/vscode/wiki/Keybinding-Issues)).
- PowerToys has an open, unmerged pull request for ZoomIt
  ([#51383](https://github.com/microsoft/PowerToys/pull/51383), opened 2026-10-18)
  that checks each Ctrl+Alt hotkey with `ToUnicodeEx` against every installed
  layout and skips the ones that type: the same check as here. It reports a
  skipped hotkey as registered; Afterglow says why and falls back instead.

## Survey: what each shortcut types, on every layout Windows ships

Method: `dotnet run tools/keyboard-layout-survey.cs` loads each of the 218
non-IME layouts under `HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layouts`
without activating it, asks Afterglow's own probe (`KeyboardLayouts.Typed`:
`ToUnicodeEx` with flag `0x4`, so a dead key isn't left pending) what each
chord types, and unloads it again. The probe was checked against an
independently written scanner on all 218 layouts: 6,771 (layout, chord) pairs
with Ctrl, no differences.

| Shortcut | Layouts it types on | Shortcut | Layouts it types on |
|---|---:|---|---:|
| Ctrl+Alt+O (overlay) | 53 | Ctrl+Alt+F10 (its stand-in) | 0 |
| Ctrl+Alt+R (panic) | 49 | Ctrl+Alt+F11 (its stand-in) | 0 |
| Ctrl+Alt+1 | 82 | Ctrl+Alt+F1 | 0 |
| Ctrl+Alt+2 | 111 | Ctrl+Alt+F2 | 0 |
| Ctrl+Alt+3 | 113 | Ctrl+Alt+F3 | 0 |
| Ctrl+Alt+4 | 120 | Ctrl+Alt+F4 | 0 |
| Ctrl+Alt+5 | 108 | Ctrl+Alt+F5 | 0 |

Every Ctrl+Alt letter and digit types something on at least 31 layouts (the
fewest: Ctrl+Alt+J), and no Ctrl+Alt+F-key types anything on any layout. So no
Ctrl+Alt letter or digit can be a default that is safe everywhere. What the old
defaults take on common layouts:

| Layout | Ctrl+Alt+O | Ctrl+Alt+R | Ctrl+Alt+1–5 |
|---|---|---|---|
| United States, Russian | — | — | — |
| United Kingdom | `ó` | — | 4 `€` |
| United States-International | `ó` | `®` | `¡ ² ³ ¤ €` |
| Spanish | — | — | `\| @ # ~ €` (`~` a dead key) |
| German | — | — | 2 `²`, 3 `³` |
| French (Legacy, AZERTY) | — | — | 2–5 `~ # { [` (`~` a dead key) |
| Italian | — | — | 5 `€` |
| Polish (Programmers) | `ó` | — | — |
| Greek | — | `®` | 2–5 `² ³ £ §` |
| Dutch | — | `¶` | `¹ ² ³ ¼ ½` |

Caps Lock is asked both ways, because on keys a layout marks `CAPLOKALTGR` it
turns AltGr into AltGr+Shift while a hotkey ignores it. Measured: it adds no
character to any Ctrl chord on these 218 layouts, but the probe keeps the pass
for layouts that might.

Limits of the survey: punctuation keys aren't surveyed (shortcuts can't use
them); layout-specific extra modifiers (Canadian Multilingual's right Ctrl,
Kana) aren't modelled; ADLaM and Osage return private-use code points for some
AltGr keys, counted as Windows returns them.

Ctrl+Shift+digit was considered as a replacement and rejected: it types on some
layouts too (zero-width joiners on 21 Arabic, Persian and Indic layouts, `₹` on
18), and Excel, Google Sheets, File Explorer and Windows Terminal use Ctrl+Shift
with digits (Terminal also uses Ctrl+Alt+1–9, which the old defaults took from
it; those can now be changed).

## What Afterglow does

- **The rule.** No global shortcut is registered while it types a character —
  or starts a dead key — on any layout installed for the user. Alt without Ctrl
  is exempt: Windows delivers it as a menu key (`WM_SYSCHAR`), not text, and
  AltGr can't press it because AltGr adds a Ctrl and a hotkey fires only on its
  exact modifiers.
- **Defaults.** Unchanged where they are harmless (US layouts keep Ctrl+Alt+O, R
  and 1–5). Where one would type, it falls back to its Ctrl+Alt+F-key stand-in:
  F10 for the overlay, F11 for the panic reset (not F12, the debugger's), F1–F5
  for the profile slots, which move together so they stay one row of keys. A
  stand-in also takes over when another app already holds the default.
- **The user's own shortcuts** are refused with the reason (the character and the
  layout) rather than replaced. Settings records them by key press, pausing the
  registered shortcuts meanwhile; also refused are chords without Ctrl or Alt,
  one-modifier letters, digits and navigation keys (Ctrl+C, Alt+F), Alt+F4,
  Ctrl+F4, Ctrl+Alt+Delete, F12 and the Windows key.
- **Layouts added later.** Windows sends no message when one is installed, so
  the app compares `GetKeyboardLayoutList` on its existing 3-second tray timer
  and re-plans when the list changes.
- **Not chosen: a low-level keyboard hook** to tell AltGr from Ctrl+Alt. It
  wouldn't help (physical Ctrl+Alt types the AltGr characters too), Windows
  silently removes a hook that misses `LowLevelHooksTimeout`
  ([LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)),
  and how anti-cheat software treats a global keyboard hook is undocumented.
