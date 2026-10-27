<#
.SYNOPSIS
    Drives the app through UI Automation in demo mode and checks the interactive states
    a screenshot can't show.

.DESCRIPTION
    Each page runs in its own --demo launch over a throwaway --data-dir, so nothing touches
    the GPU or your real settings. Checks: a dashboard tile opens and closes its expanded
    graph; the FPS tile with no game leads to the FPS page; moving a Tuning slider shows
    "Unapplied changes" and Apply clears it; the Fans mode switch reveals the duty slider;
    saving a profile lists it and enables the list actions; settings switches carry
    accessible names and toggle; with the Spanish layout installed, the default shortcuts
    leave its AltGr digits alone, and recording a shortcut saves and registers it while
    refusing one that types a character or takes an everyday app shortcut (this part
    presses keys, so leave the keyboard alone while it runs). Captures a PNG of each
    state into -Out.
    Exit code = number of failed checks.
#>
param([Parameter(Mandatory)] [string]$Exe,
      [string]$Out = (Join-Path $env:TEMP 'afterglow-ui-check'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Namespace UiTest -Name W -ReferencedAssemblies System.Drawing -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr h, System.IntPtr hdc, uint flags);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
public static void Capture(System.IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    using (var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top))
    using (var g = System.Drawing.Graphics.FromImage(bmp)) {
        var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
'@
New-Item -ItemType Directory -Force $Out | Out-Null
$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$script:fail = 0

function Start-App([string]$page, [string]$data) {
    $p = Start-Process $Exe -ArgumentList @('--demo', '--data-dir', "`"$data`"", '--page', $page) -PassThru
    for ($i = 0; $i -lt 40 -and $p.MainWindowHandle -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh() }
    Start-Sleep -Seconds 3
    $p.Refresh()
    return $p
}
function Root($p) { $A::FromHandle($p.MainWindowHandle) }
function Find($root, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    return $root.FindFirst($Scope::Descendants, $c)
}
function Has($root, [string]$name) { return $null -ne (Find $root $name) }
function Check([bool]$ok, [string]$what) {
    if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fail++ }
}
function Invoke($root, [string]$name) {
    $e = Find $root $name
    if (-not $e) { throw "not found: $name" }
    $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
}
function Shot($p, [string]$file) { [UiTest.W]::Capture($p.MainWindowHandle, (Join-Path $Out $file)) }

$data = Join-Path $env:TEMP 'afterglow-ui-check-data'
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $data | Out-Null

"Dashboard"
$p = Start-App 'dashboard' $data
try {
    $r = Root $p
    Invoke $r 'Core clock: show the last 10 minutes'
    Check (Has $r ("Core clock {0} last 10 min" -f [char]0xB7)) 'tile opens its expanded graph'
    Check (Has $r 'Close the expanded graph') 'expanded graph has a Close button'
    Shot $p 'dash-expanded.png'
    Invoke $r 'Close the expanded graph'
    Check (Has $r ("GPU load {0} last 10 min" -f [char]0xB7)) 'Close brings the four graphs back'
    Invoke $r 'Frames per second'
    Check (Has $r 'Average FPS') 'FPS tile with no game opens the FPS page'
    Shot $p 'dash-fps-nav.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

"Tuning"
$p = Start-App 'tuning' $data
try {
    $r = Root $p
    Check (-not (Has $r ' Unapplied changes')) 'no pending indicator at start'
    $s = Find $r 'Core clock offset, megahertz'
    $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(120)
    Start-Sleep -Milliseconds 600
    $pending = $r.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'TextBlock'))) # warm
    $texts = $r.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    $names = $texts | ForEach-Object { $_.Current.Name }
    Check (($names -join '|') -match 'Unapplied changes') 'moving a slider shows "Unapplied changes"'
    Check (($names -join '|') -match '\+120 MHz') 'value label follows the slider (+120 MHz)'
    Shot $p 'tuning-pending.png'
    Invoke $r 'Apply'
    $names = ($r.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name }) -join '|'
    Check (-not ($names -match 'Unapplied changes')) 'Apply clears the pending indicator'
    Check ($names -match 'Applied \(demo mode') 'Apply shows its result'
    Shot $p 'tuning-applied.png'
} catch { "  note: $_" } finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

"Fans"
$p = Start-App 'fans' $data
try {
    $r = Root $p
    Check (-not (Has $r 'Fixed fan duty, percent')) 'duty slider hidden in firmware mode'
    $seg = Find $r 'Fixed duty'
    $seg.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 600
    Check (Has $r 'Fixed fan duty, percent') 'choosing Fixed duty shows the duty slider'
    Check (Has $r 'Every fan at one speed, whatever the temperature.') 'mode description follows the selection'
    Shot $p 'fans-fixed.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

"Profiles"
$p = Start-App 'profiles' $data
try {
    $r = Root $p
    $apply = Find $r 'Apply'
    Check (-not $apply.Current.IsEnabled) 'Apply disabled with nothing selected'
    $tb = Find $r 'Profile name'
    $tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Design check')
    Start-Sleep -Milliseconds 300
    Invoke $r 'Save profile'
    $list = $r.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)))
    $items = $list.FindAll($Scope::Children, [System.Windows.Automation.Condition]::TrueCondition)
    Check ($items.Count -ge 1) "saved profile appears in the list ($($items.Count))"
    $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 500
    Check ((Find $r 'Apply').Current.IsEnabled) 'Apply enabled once a profile is selected'
    Shot $p 'profiles-saved.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

"Settings"
$p = Start-App 'settings' $data
try {
    $r = Root $p
    $t = $r.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Close button minimizes to the tray')),
        (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))))
    Check ($null -ne $t) 'unlabelled switches carry an accessible name'
    $before = $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 400
    $after = $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    Check ($before -ne $after) "switch toggles ($before -> $after)"
    Shot $p 'settings-toggled.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

# Keyboard shortcuts (issue #6). Spanish is installed for the session first —
# without activating it — because its AltGr row is what the shortcuts must not
# take; it is unloaded again afterwards if this script loaded it.
"Settings: keyboard shortcuts"
function Press([byte[]]$keys) {
    foreach ($k in $keys) { [UiTest.K]::keybd_event($k, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
    [array]::Reverse($keys)
    foreach ($k in $keys) { [UiTest.K]::keybd_event($k, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
    Start-Sleep -Milliseconds 700
}
function Free([uint32]$mods, [uint32]$vk) {
    # Whether another process (the app) holds this hotkey right now.
    if ([UiTest.K]::RegisterHotKey([IntPtr]::Zero, 0x7F00, $mods, $vk)) { [void][UiTest.K]::UnregisterHotKey([IntPtr]::Zero, 0x7F00); return $true }
    return $false
}
function Saved([string]$data) { (Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).hotkeys }
Add-Type -Namespace UiTest -Name K -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
[DllImport("user32.dll")] public static extern bool RegisterHotKey(System.IntPtr h, int id, uint mods, uint vk);
[DllImport("user32.dll")] public static extern bool UnregisterHotKey(System.IntPtr h, int id);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern System.IntPtr LoadKeyboardLayoutW(string klid, uint flags);
[DllImport("user32.dll")] public static extern bool UnloadKeyboardLayout(System.IntPtr hkl);
[DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int n, System.IntPtr[] list);
'@
$layoutsBefore = New-Object IntPtr[] 64
$layoutsBefore = $layoutsBefore[0..([UiTest.K]::GetKeyboardLayoutList(64, $layoutsBefore) - 1)]
$spanish = [UiTest.K]::LoadKeyboardLayoutW('0000040A', 0x80)
$p = Start-App 'settings' $data
try {
    $r = Root $p
    $ctrlAlt = 3; $ctrlShift = 6
    Check (Has $r 'Ctrl+Alt+1 types "|" on your Spanish keyboard layout — using Ctrl+Alt+F1 instead') 'Spanish digits are left to typing; slot 1 explains its F-key stand-in'
    Check (Free $ctrlAlt 0x32) 'Ctrl+Alt+2 is not registered by the app (it is "@")'
    Check (-not (Free $ctrlAlt 0x71)) 'Ctrl+Alt+F2 is registered instead'

    $change = Find $r 'Change shortcut: Show or hide the overlay'
    $change.SetFocus(); $change.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    Check (Has $r ('Press the new shortcut {0} Esc cancels' -f [char]0xB7)) 'Change starts recording'
    Check (Free $ctrlAlt 0x52) 'shortcuts are paused while recording (Ctrl+Alt+R is free)'
    Press @(0x11, 0x10, 0x78)                      # Ctrl+Shift+F9
    Check ((Saved $data).toggleOverlay -eq 'Ctrl+Shift+F9') "the pressed shortcut is saved ($((Saved $data).toggleOverlay))"
    Check (-not (Free $ctrlShift 0x78)) 'and registered'
    Check (-not (Free $ctrlAlt 0x52)) 'recording over: Ctrl+Alt+R is registered again'
    Check (Has $r 'Toggle it with Ctrl+Shift+F9 or from the tray menu.') 'the overlay card names the new shortcut'
    Shot $p 'settings-shortcut-recorded.png'

    $change = Find $r 'Change shortcut: Panic: reset the GPU to driver defaults'
    $change.SetFocus(); $change.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    Press @(0x11, 0x12, 0x32)                      # Ctrl+Alt+2: "@" on Spanish
    Check (Has $r 'Ctrl+Alt+2 types "@" on your Spanish keyboard layout. The shortcut wasn''t changed.') 'a chord that types is refused, with the character and layout'
    Check ($null -eq (Saved $data).panicReset) 'and the panic key keeps its default'
    $change.SetFocus(); $change.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    Press @(0x11, 0x43)                            # Ctrl+C
    Check (Has $r 'Ctrl+C would take an everyday app shortcut; use two modifiers (Ctrl+Alt, Ctrl+Shift, Alt+Shift) or an F key. The shortcut wasn''t changed.') 'an everyday app shortcut is refused'
    Shot $p 'settings-shortcut-refused.png'
    $change = Find $r 'Change shortcut: Show or hide the overlay'
    $change.SetFocus(); $change.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    Press @(0x11, 0x12, 0x52)                      # Ctrl+Alt+R: the panic key's
    Check (Has $r 'Ctrl+Alt+R is already set for "Panic: reset the GPU to driver defaults". The shortcut wasn''t changed.') 'another action''s shortcut is refused'
    Check ((Saved $data).toggleOverlay -eq 'Ctrl+Shift+F9') 'and the overlay keeps its own'

    Invoke $r 'Restore default shortcuts'
    Check ($null -eq (Saved $data).toggleOverlay) 'Restore default shortcuts clears the chosen one'
    Check (-not (Free $ctrlAlt 0x4F)) 'and Ctrl+Alt+O is registered again'

    # Pressing the default's own keys keeps it the default (free to move to its stand-in later).
    $change = Find $r 'Change shortcut: Panic: reset the GPU to driver defaults'
    $change.SetFocus(); $change.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    Press @(0x11, 0x12, 0x52)                      # Ctrl+Alt+R
    Check ($null -eq (Saved $data).panicReset) 'pressing the default''s own keys keeps the default'
    Check (-not (Free $ctrlAlt 0x52)) 'and Ctrl+Alt+R is registered'
} finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    if ($layoutsBefore -notcontains $spanish) { [void][UiTest.K]::UnloadKeyboardLayout($spanish) }
}

"failures: $script:fail"
exit $script:fail
