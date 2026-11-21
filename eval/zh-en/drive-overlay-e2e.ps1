<#
.SYNOPSIS
  Verifies the floating translation overlay of the real GlyphLens app on this machine (UI Automation + real mouse and keyboard).

.DESCRIPTION
  Checks, one by one and reporting PASS/FAIL for each:
   opens independently of the main window - stays above an ordinary application (the ordinary app) - is topmost - can be dragged and
   resized with the mouse - can be hidden and shown (button and Ctrl+Alt+O) - interactive vs pass-through (a click goes to
   the ordinary app underneath) - the unlock handle - translations of a real Chinese window arrive in the cards - the app stays
   responsive during Live - the overlay is excluded from display capture (display capture + OCR never reads the overlay's
   own labels) - closing the main window keeps the overlay session alive - the position is remembered.

  The model folder must already contain the OCR models (ocr\PP-OCRv5_*_onnx) and the translate-zh_en package folder (zh_en), for
  example the folder used by drive-chinese-e2e.ps1. Multi-monitor placement cannot be tested on a one-monitor machine; it is
  covered by unit tests only.
#>
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$ModelsDir,
  [string]$Shot,
  [switch]$ControlWithoutProtection
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W6 {
  public struct POINT { public int X; public int Y; }
  public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr h, out uint a);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, StringBuilder l);
  public static string EditText(IntPtr form) {
    string result = "";
    EnumChildWindows(form, (c, p) => {
      var cls = new StringBuilder(256); GetClassName(c, cls, 256);
      if (cls.ToString().Contains("EDIT")) { var sb = new StringBuilder(4096); SendMessage(c, 0x000D, (IntPtr)4096, sb); result = sb.ToString(); return false; }
      return true; }, IntPtr.Zero);
    return result;
  }
  public static int[] ZOrder() { var l = new System.Collections.Generic.List<int>(); EnumWindows((h, p) => { if (IsWindowVisible(h)) l.Add(h.ToInt32()); return true; }, IntPtr.Zero); return l.ToArray(); }
}
"@
[void][W6]::SetProcessDPIAware()
$AE = [System.Windows.Automation.AutomationElement]
. (Join-Path $PSScriptRoot 'ui-pages.ps1')
function Log($s, $n, $d) { "{0,-6} {1} :: {2}" -f $s, $n, $d }
$script:pass = 0; $script:fail = 0
function Check($ok, $name, $detail) { if ($ok) { $script:pass++; Log PASS $name $detail } else { $script:fail++; Log FAIL $name $detail } }
function PropEq($id) { New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id) }
function Find($root, $id) { Enter-PageOf $root $id; $root.FindFirst('Descendants', (PropEq $id)) }
function Txt($root, $id) { $e = Find $root $id; if ($e) { $e.Current.Name } else { "<missing $id>" } }
function Click($root, $id) { (Find $root $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Until($sb, $sec = 30) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $sec) { if (& $sb) { return $true }; Start-Sleep -Milliseconds 150 }; return (& $sb) }
function Overlay($proc) { $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.AndCondition((PropEq 'GlyphLensOverlayWindow'), (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))))) }
function OverlayVisible($proc) { $o = Overlay $proc; $o -and (-not $o.Current.IsOffscreen) -and [W6]::IsWindowVisible([IntPtr]$o.Current.NativeWindowHandle) }
function Rect($h) { $r = New-Object W6+RECT; [void][W6]::GetWindowRect($h, [ref]$r); [pscustomobject]@{ X = $r.L; Y = $r.T; W = $r.R - $r.L; H = $r.B - $r.T; R = $r.R; B = $r.B } }
function Drag($x1, $y1, $x2, $y2) {
  [void][W6]::SetCursorPos($x1, $y1); Start-Sleep -Milliseconds 120
  [W6]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120
  $steps = 12
  for ($i = 1; $i -le $steps; $i++) { [void][W6]::SetCursorPos([int]($x1 + ($x2 - $x1) * $i / $steps), [int]($y1 + ($y2 - $y1) * $i / $steps)); Start-Sleep -Milliseconds 25 }
  Start-Sleep -Milliseconds 120; [W6]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
}
function Click-At($x, $y) { [void][W6]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 150; [W6]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W6]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300 }
function Shot($win, $path) {
  $h = [IntPtr]$win.Current.NativeWindowHandle; $r = Rect $h
  $bmp = New-Object System.Drawing.Bitmap($r.W, $r.H); $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.X, $r.Y, 0, 0, $bmp.Size); $g.Dispose(); $bmp.Save($path); Log INFO screenshot $path
}

$env:GLYPHLENS_MODELS_DIR = $ModelsDir
if ($ControlWithoutProtection) { $env:GLYPHLENS_DISABLE_CAPTURE_EXCLUSION = '1' }
$settingsFile = Join-Path $env:LOCALAPPDATA 'GlyphLens\overlay.json'
if (Test-Path $settingsFile) { Remove-Item $settingsFile -Force }
$renderer = Join-Path $PSScriptRoot 'render-scenes.ps1'
$target = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $renderer, '-Mode', 'Show', '-SceneName', 'chat-light', '-Title', 'GlyphLensChineseTarget' -PassThru
$ordinaryScript = Join-Path $PSScriptRoot 'ordinary-app.ps1'
$notepad = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $ordinaryScript -PassThru
$null = Until { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' }).Count -ge 1 } 30
$ordinaryHwnd = [IntPtr]::Zero
$null = Until { $e = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'GlyphLensOrdinaryApp'))); if ($e) { $script:ordinaryHwnd = [IntPtr]$e.Current.NativeWindowHandle }; $script:ordinaryHwnd -ne [IntPtr]::Zero } 30
Start-Sleep 2
$proc = Start-Process $Exe -PassThru
try {
  $win = $null
  $null = Until { $script:win = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); $null -ne $script:win } 25
  Start-Sleep 2
  $main = $win

  # ---- 1. the overlay is created hidden and opens on request, independently of the main window -------------------------
  Check (-not (OverlayVisible $proc)) overlay-starts-hidden 'the overlay is not shown until requested'
  Click $main 'ShowOverlayButton'
  $shown = Until { OverlayVisible $proc } 10
  Check $shown overlay-opens "overlay window present=$([bool](Overlay $proc))"
  $ov = Overlay $proc; $oh = [IntPtr]$ov.Current.NativeWindowHandle
  Check ($ov.Current.ProcessId -eq $proc.Id) overlay-is-a-separate-window 'a top-level window of its own'

  # ---- 2. above an ordinary application, topmost, no focus stealing -------------------------------------------------------
  [void][W6]::SetWindowPos($ordinaryHwnd, [IntPtr]::Zero, ((Rect $oh).X + 40), ((Rect $oh).Y + 120), 520, 380, 0x0004)
  [void][W6]::SetForegroundWindow($ordinaryHwnd); Start-Sleep -Milliseconds 600
  $z = [W6]::ZOrder()
  $oi = [Array]::IndexOf($z, $oh.ToInt32()); $ni = [Array]::IndexOf($z, $ordinaryHwnd.ToInt32())
  $exStyle = [W6]::GetWindowLong($oh, -20)
  Check (($oi -ge 0) -and ($oi -lt $ni)) above-ordinary-window "overlay z-index=$oi notepad z-index=$ni (lower is higher)"
  Check (($exStyle -band 0x8) -ne 0) topmost-style "WS_EX_TOPMOST set=$((($exStyle -band 0x8) -ne 0))"
  Check ((($exStyle -band 0x08000000) -ne 0) -and (($exStyle -band 0x80) -ne 0)) no-activate-tool-window "WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW (no focus stealing, no taskbar entry)"
  $fg = [System.Windows.Automation.AutomationElement]::FocusedElement
  Check ($fg.Current.ProcessId -ne $proc.Id) focus-not-stolen "focused element belongs to process $($fg.Current.ProcessId), not GlyphLens ($($proc.Id))"

  # ---- 3. move and resize with the real mouse --------------------------------------------------------------------------
  # Real mouse input on a shared desktop can be disturbed by a person moving the mouse, so each gesture is retried towards its
  # target (the check still compares the final result with the target).
  $r0 = Rect $oh
  $goalX = $r0.X - 220; $goalY = $r0.Y + 90
  for ($try = 1; $try -le 4; $try++) {
    $cur = Rect $oh; $dx = $goalX - $cur.X; $dy = $goalY - $cur.Y
    if (([Math]::Abs($dx) -le 4) -and ([Math]::Abs($dy) -le 4)) { break }
    Drag ($cur.X + 150) ($cur.Y + 28) ($cur.X + 150 + $dx) ($cur.Y + 28 + $dy)
  }
  $r1 = Rect $oh
  Check ((([Math]::Abs(($r1.X - $r0.X) + 220) -le 6) -and ([Math]::Abs(($r1.Y - $r0.Y) - 90) -le 6))) drag-moves "moved by ($($r1.X - $r0.X),$($r1.Y - $r0.Y)), expected (-220,90)"
  $goalW = $r1.W + 80; $goalH = $r1.H - 120
  for ($try = 1; $try -le 4; $try++) {
    $cur = Rect $oh; $dw = $goalW - $cur.W; $dh = $goalH - $cur.H
    if (([Math]::Abs($dw) -le 6) -and ([Math]::Abs($dh) -le 6)) { break }
    Drag ($cur.R - 12) ($cur.B - 12) ($cur.R - 12 + $dw) ($cur.B - 12 + $dh)
  }
  $r2 = Rect $oh
  Check ((([Math]::Abs($r2.W - $goalW) -le 8) -and ([Math]::Abs($r2.H - $goalH) -le 8))) resize "size $($r1.W)x$($r1.H) -> $($r2.W)x$($r2.H), expected ${goalW}x$goalH"
  $goalW = [int](300 * ($r0.W / 440.0))
  for ($try = 1; $try -le 4; $try++) {
    $cur = Rect $oh
    if ($cur.W -le [int](320 * ($r0.W / 400.0)) + 6) { break }
    Drag ($cur.R - 12) ($cur.B - 12) ($cur.R - 12 - 400) ($cur.B - 12)
  }
  $r3 = Rect $oh
  $dpi = [Math]::Max(1.0, ($r0.W / 400.0))
  Check (($r3.W -ge [int](320 * $dpi) - 4) -and ($r3.W -le [int](320 * $dpi) + 10)) minimum-width "dragged far narrower; the width stopped at $($r3.W) px (minimum $([int](320 * $dpi)) px)"
  Drag ($r3.R - 12) ($r3.B - 12) ($r3.R - 12 + 100) ($r3.B - 12 + 40)

  # ---- 4. translations arrive in the overlay cards (real capture -> OCR -> translation) ---------------------------------
  (Find $main 'TargetKindWindow').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
  Click $main 'RefreshButton'; Start-Sleep 1
  $found = Select-CaptureWindow $main '*GlyphLensChineseTarget*'
  Check $found select-chinese-window 'the Chinese test window is selected'
  if (-not $found) { throw 'the test window is not in the target list: not starting a capture of any other window' }
  Click $main 'StartCaptureButton'
  $cap = Until { (Txt $main 'CaptureStatusText') -eq 'Capturing' } 15
  Check $cap capture "status=$(Txt $main 'CaptureStatusText')"
  $lang = Find $main 'OcrLanguageSelector'
  $lexp = $lang.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $lexp.Expand()
  $litems = @(); $null = Until { $script:litems = @($lang.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))); $script:litems.Count -ge 3 } 10
  ($litems | Where-Object { $_.Current.Name -like '*Chinese*' } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); $lexp.Collapse(); Start-Sleep -Milliseconds 300
  ((Find $main 'TranslateToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
  $null = Until { (Txt $main 'WorkerStatusText') -eq 'Ready' } 90
  $null = Until { (Txt $main 'ModelsSummaryText') -like '*installed*' -and (Txt $main 'ModelsSummaryText') -notlike 'No *' } 90
  Click $main 'ReadTextButton'
  $ov = Overlay $proc
  $got = Until { @((Find $ov 'OverlayCardsList').FindAll('Descendants', (PropEq 'OverlayCardTranslation'))).Count -ge 3 } 120
  $cards = @((Find $ov 'OverlayCardsList').FindAll('Descendants', (PropEq 'OverlayCardTranslation')))
  $texts = @($cards | ForEach-Object { try { $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { $_.Current.Name } })
  Check $got translations-in-overlay "cards with a translation: $($cards.Count); first: '$($texts[0])'"
  $hanRegex = New-Object System.Text.RegularExpressions.Regex(('[' + [char]0x3400 + '-' + [char]0x9fff + ']'))
  Check (($texts | Where-Object { $hanRegex.IsMatch($_) }).Count -eq 0) translations-are-english 'no Chinese character in any translation'
  Check ((Txt $ov 'OverlayLanguages') -match 'English') header-languages "header='$(Txt $ov 'OverlayLanguages')'"
  if ($Shot) { Shot $ov "$Shot-overlay.png" }

  # ---- 5. responsiveness during Live ------------------------------------------------------------------------------------
  ((Find $main 'LiveOcrToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
  $worst = 0.0; $hung = 0
  for ($i = 0; $i -lt 25; $i++) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $null = (Find $main 'ReadTextButton').Current.IsEnabled; $sw.Stop()
    if ($sw.Elapsed.TotalMilliseconds -gt $worst) { $worst = $sw.Elapsed.TotalMilliseconds }
    $proc.Refresh(); if (-not $proc.Responding) { $hung++ }
    Start-Sleep -Milliseconds 200
  }
  Check (($worst -lt 1500) -and ($hung -eq 0)) responsive-during-live "slowest UI Automation round trip=$([int]$worst) ms, not-responding samples=$hung"
  ((Find $main 'LiveOcrToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle(); Start-Sleep 1

  # ---- 6. hide / show (button and Ctrl+Alt+O) -------------------------------------------------------------------------
  Click (Overlay $proc) 'OverlayHideButton'
  Check (Until { -not (OverlayVisible $proc) } 5) hide-button 'the overlay is hidden by its own button'
  Click $main 'ShowOverlayButton'
  Check (Until { OverlayVisible $proc } 5) show-button 'shown again from the main window'
  [System.Windows.Forms.SendKeys]::SendWait('^%o')
  Check (Until { -not (OverlayVisible $proc) } 5) hotkey-hides 'Ctrl+Alt+O hides the overlay'
  [System.Windows.Forms.SendKeys]::SendWait('^%o')
  Check (Until { OverlayVisible $proc } 5) hotkey-shows 'Ctrl+Alt+O shows it again'
  $ov = Overlay $proc; $oh = [IntPtr]$ov.Current.NativeWindowHandle

  # ---- 7. interactive vs pass-through ----------------------------------------------------------------------------------
  $cx = (Rect $oh).X + 200; $cy = (Rect $oh).Y + 300
  [void][W6]::SetWindowPos($ordinaryHwnd, [IntPtr]::Zero, ((Rect $oh).X + 30), ((Rect $oh).Y + 90), 520, 520, 0x0040)  # HWND_TOP: above the main window, below the topmost overlay
  [void][W6]::SetForegroundWindow($ordinaryHwnd); Start-Sleep -Milliseconds 700
  $pt = New-Object W6+POINT; $pt.X = $cx; $pt.Y = $cy
  $hitInteractive = [W6]::GetAncestor([W6]::WindowFromPoint($pt), 2)
  Check ($hitInteractive -eq $oh) interactive-receives-mouse "window under the cursor = overlay ($($hitInteractive -eq $oh))"
  # Switch with the shortcut (as a user in another application would): invoking the main window's button would raise that window.
  [System.Windows.Forms.SendKeys]::SendWait('^%i')
  $null = Until { (([W6]::GetWindowLong($oh, -20)) -band 0x20) -ne 0 } 5
  $exStyle = [W6]::GetWindowLong($oh, -20)
  Check (($exStyle -band 0x20) -ne 0) pass-through-style "WS_EX_TRANSPARENT set=$((($exStyle -band 0x20) -ne 0))"
  $null = Until { [W6]::GetAncestor([W6]::WindowFromPoint($pt), 2) -ne $oh } 5
  $hitPass = [W6]::GetAncestor([W6]::WindowFromPoint($pt), 2)
  $hitName = try { ($AE::FromHandle($hitPass)).Current.Name } catch { '?' }
  Check ($hitPass -eq $ordinaryHwnd) pass-through-hit-goes-below "window under the cursor is the ordinary app underneath ($($hitPass -eq $ordinaryHwnd)), not the overlay ($($hitPass -ne $oh)); it is '$hitName' hwnd=$hitPass ordinary=$ordinaryHwnd rect=$((Rect $ordinaryHwnd | ConvertTo-Json -Compress)) point=$cx,$cy"
  # A real click and typing reach the ordinary app through the overlay.
  Click-At $cx $cy
  Start-Sleep -Milliseconds 400
  $fgNow = [System.Windows.Automation.AutomationElement]::FocusedElement
  Log INFO click-through-focus "focused after the click: process $($fgNow.Current.ProcessId) '$($fgNow.Current.Name)' (ordinary app process $($notepad.Id), GlyphLens $($proc.Id))"
  [System.Windows.Forms.SendKeys]::SendWait('hello-through-overlay')
  Start-Sleep -Milliseconds 400
  $npText = [W6]::EditText($ordinaryHwnd)
  Log INFO ordinary-app-edit "text='$npText'"
  Check ($npText -like '*hello-through-overlay*') underlying-app-usable "the ordinary app received the click and typing; its text='$npText'"
  # The overlay can be left on screen and still be unlocked with the handle (the overlay itself ignores the mouse).
  $handle = $AE::RootElement.FindFirst('Children', (PropEq 'GlyphLensOverlayUnlockHandle'))
  Check ($null -ne $handle) unlock-handle-shown 'a small handle is shown while the overlay ignores the mouse'
  if ($handle) {
    $hr = Rect ([IntPtr]$handle.Current.NativeWindowHandle)
    $orr = Rect $oh
    Log INFO handle-geometry "handle ($($hr.X),$($hr.Y) $($hr.W)x$($hr.H)) overlay ($($orr.X),$($orr.Y) $($orr.W)x$($orr.H)) mode before='$(Txt (Overlay $proc) 'OverlayModeButton')'"
    for ($try = 1; $try -le 3; $try++) {
      Click-At ($hr.X + [int]($hr.W / 2)) ($hr.Y + [int]($hr.H / 2))
      if ($null -ne (Until { (([W6]::GetWindowLong($oh, -20)) -band 0x20) -eq 0 } 3)) { if ((([W6]::GetWindowLong($oh, -20)) -band 0x20) -eq 0) { break } }
    }
  }
  Start-Sleep -Milliseconds 800
  Log INFO mode-after-handle "main-window mode button='$(Txt $main 'OverlayModeMainButton')'"
  $exStyle = [W6]::GetWindowLong($oh, -20)
  Check (($exStyle -band 0x20) -eq 0) handle-restores-interactive "WS_EX_TRANSPARENT cleared=$((($exStyle -band 0x20) -eq 0))"
  [System.Windows.Forms.SendKeys]::SendWait('^%i'); Start-Sleep -Milliseconds 600
  $toPass = (([W6]::GetWindowLong($oh, -20)) -band 0x20) -ne 0
  [System.Windows.Forms.SendKeys]::SendWait('^%i'); Start-Sleep -Milliseconds 600
  Check ($toPass -and ((([W6]::GetWindowLong($oh, -20)) -band 0x20) -eq 0)) hotkey-toggles-mode "Ctrl+Alt+I switched to pass-through ($toPass) and back to interactive"

  # ---- 8. collapse and expand --------------------------------------------------------------------------------------------
  $hBefore = (Rect $oh).H
  Click (Overlay $proc) 'OverlayCollapseButton'; Start-Sleep -Milliseconds 500
  $hCollapsed = (Rect $oh).H
  Click (Overlay $proc) 'OverlayCollapseButton'; Start-Sleep -Milliseconds 500
  $hExpanded = (Rect $oh).H
  Check (($hCollapsed -lt ($hBefore / 3)) -and ([Math]::Abs($hExpanded - $hBefore) -le 4)) collapse-expand "height $hBefore -> $hCollapsed -> $hExpanded"

  # ---- 9. the overlay does not translate itself: display capture + OCR never reads the overlay -------------------------
  $aff = 0; [void][W6]::GetWindowDisplayAffinity($oh, [ref]$aff)
  Check ($aff -eq 0x11) display-affinity "GetWindowDisplayAffinity=0x$('{0:X}' -f $aff) (0x11 = WDA_EXCLUDEFROMCAPTURE)"
  Click $main 'StopCaptureButton'; Start-Sleep 1
  (Find $main 'TargetKindDisplay').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500
  Click $main 'StartCaptureButton'
  $null = Until { (Txt $main 'CaptureStatusText') -eq 'Capturing' } 15
  Start-Sleep 2
  Click $main 'ReadTextButton'
  $null = Until { (Txt $main 'OcrStatusText') -match 'lines' } 120
  Start-Sleep 1
  # The OCR list is virtualised, so read everything the recogniser returned through the Copy button.
  Set-Clipboard -Value 'empty'
  Click $main 'CopyTextButton'; Start-Sleep -Milliseconds 500
  $allText = Get-Clipboard -Raw
  $lines = @($allText -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
  $overlayLabels = @($lines | Where-Object { $_.Trim() -ceq 'TRANSLATION' -or $_.Trim() -ceq 'ORIGINAL' })
  $hanLines = @($lines | Where-Object { $hanRegex.IsMatch($_) }).Count
  Log INFO display-ocr "recognised lines in the display capture: $($lines.Count) (with Chinese: $hanLines); overlay labels read: $($overlayLabels.Count)"
  if ($ControlWithoutProtection) {
    Check ($overlayLabels.Count -gt 0) control-overlay-captured-when-unprotected "CONTROL: with the protection disabled the overlay's own labels are read ($($overlayLabels.Count)), so the check below can fail"
  }
  else {
    Check (($overlayLabels.Count -eq 0) -and ($lines.Count -ge 5)) overlay-not-captured "the display capture was read ($($lines.Count) lines) and contains none of the overlay's own labels"
  }
  Click $main 'StopCaptureButton'

  # ---- 10. position is remembered; closing the main window keeps the overlay session alive -----------------------------
  $placed = Rect $oh
  Start-Sleep -Milliseconds 900
  Check (Test-Path $settingsFile) settings-saved "layout file written: $settingsFile"
  if (Test-Path $settingsFile) { $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json; Check (([Math]::Abs($saved.x - $placed.X) -le 2) -and ([Math]::Abs($saved.y - $placed.Y) -le 2)) settings-match-window "saved ($($saved.x),$($saved.y)) window ($($placed.X),$($placed.Y))"; Check (-not ($hanRegex.IsMatch((Get-Content $settingsFile -Raw)))) settings-hold-no-text 'no recognised text in the layout file' }
  $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  Start-Sleep 2
  $proc.Refresh()
  Check ((-not $proc.HasExited) -and (OverlayVisible $proc)) main-window-closed-overlay-lives "process running=$(-not $proc.HasExited) overlay visible=$(OverlayVisible $proc)"
  [System.Windows.Forms.SendKeys]::SendWait('^%o')
  $exited = $proc.WaitForExit(20000)
  Check $exited exits-when-overlay-hidden "with the main window closed, hiding the overlay ends the application (exited=$exited)"
}
catch { Log FAIL driver "$($_.Exception.Message) (script line $($_.InvocationInfo.ScriptLineNumber))"; $script:fail++ }
finally {
  if (-not $proc.HasExited) { $proc.Kill(); Log WARN cleanup 'app killed' }
  if (-not $notepad.HasExited) { $notepad.Kill() }
  Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' } | Stop-Process -Force -ErrorAction SilentlyContinue
  if (-not $target.HasExited) { $target.Kill() }
  Log INFO summary "passed=$($script:pass) failed=$($script:fail)"
}
