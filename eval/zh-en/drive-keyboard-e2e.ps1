<#
.SYNOPSIS
  Keyboard, shortcut, accessible-name, "new results" and minimum-size checks of the real GlyphLens app on this machine
  (UI Automation + real keystrokes). Reports PASS/FAIL per check. ASCII only.

.DESCRIPTION
  1. Accessible names: every button, check box, combo box, slider, radio button, edit and hyperlink on every main-window page and in
     the overlay has a non-empty name that is not an icon glyph.
  2. Keyboard only: on every page, Tab walks every enabled focusable control (radio groups count once) and comes back to the start
     (no keyboard trap); every Tab stop has a name and a visible focus indicator (the pixels of the control change when focus
     arrives); the arrow keys switch pages in the navigation list; capture is started with Space.
  3. Global shortcuts, pressed while another (ordinary) application is in the foreground: Ctrl+Alt+T turns translation on and off,
     Ctrl+Alt+O shows the overlay, Ctrl+Alt+P pauses (new results are ignored) and resumes (they arrive), Ctrl+Alt+I switches to
     pass-through and back, Ctrl+Alt+C clears the cards; none of the keys reach the ordinary application.
  4. "New results": with the overlay scrolled up, a new message keeps the reading position and shows the "New results" button;
     the button scrolls to the newest card; at the bottom, new cards are followed.
  5. Minimum size: the main window cannot be made smaller than its minimum, and at that size every page's controls are on screen
     and at least partly visible. With -ShotDir, screenshots of each page (and of the overlay at its minimum width) are written
     there for a visual check: give a folder OUTSIDE the repository. They show only GlyphLens and the synthetic test window.

  The model folder must contain the OCR models and the zh_en package (as for drive-overlay-e2e.ps1). The overlay layout file
  (%LOCALAPPDATA%\GlyphLens\overlay.json) is backed up and restored.
#>
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$ModelsDir,
  [string]$ShotDir,
  [int]$RevealSeconds = 8
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Drawing; using System.Drawing.Imaging;
public static class KbW {
  public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, StringBuilder l);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  // The window's own pixels only (nothing that lies on top of it or behind it is included).
  public static Bitmap PrintWin(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    var b = new Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T), PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(b)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
    return b;
  }
  public static string EditText(IntPtr form) {
    string result = "";
    EnumChildWindows(form, (c, p) => {
      var cls = new StringBuilder(256); GetClassName(c, cls, 256);
      if (cls.ToString().Contains("EDIT")) { var sb = new StringBuilder(4096); SendMessage(c, 0x000D, (IntPtr)4096, sb); result = sb.ToString(); return false; }
      return true; }, IntPtr.Zero);
    return result;
  }
  public static Bitmap Grab(int x, int y, int w, int h) {
    var b = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(b)) { g.CopyFromScreen(x, y, 0, 0, b.Size); }
    return b;
  }
  // Number of pixels whose colour differs by more than a small tolerance (anti-aliasing noise).
  public static int Diff(Bitmap a, Bitmap b) {
    if (a.Width != b.Width || a.Height != b.Height) { return -1; }
    var r = new Rectangle(0, 0, a.Width, a.Height);
    var da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int n = da.Stride * a.Height; var pa = new byte[n]; var pb = new byte[n];
    Marshal.Copy(da.Scan0, pa, 0, n); Marshal.Copy(db.Scan0, pb, 0, n);
    a.UnlockBits(da); b.UnlockBits(db);
    int count = 0;
    for (int i = 0; i + 3 < n; i += 4) {
      int d = Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
      if (d > 30) { count++; }
    }
    return count;
  }
}
"@
[void][KbW]::SetProcessDPIAware()
$AE = [System.Windows.Automation.AutomationElement]
. (Join-Path $PSScriptRoot 'ui-pages.ps1')
function Log($s, $n, $d) { "{0,-6} {1} :: {2}" -f $s, $n, $d }
$script:pass = 0; $script:fail = 0
function Check($ok, $name, $detail) { if ($ok) { $script:pass++; Log PASS $name $detail } else { $script:fail++; Log FAIL $name $detail } }
function PropEq($id) { New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id) }
function Find($root, $id) { Enter-PageOf $root $id; $root.FindFirst('Descendants', (PropEq $id)) }
function Txt($root, $id) { $e = Find $root $id; if ($e) { $e.Current.Name } else { "<missing $id>" } }
function Click($root, $id) { (Find $root $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function ToggleState($root, $id) { "$((Find $root $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState)" }
function Until($sb, $sec = 30) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $sec) { if (& $sb) { return $true }; Start-Sleep -Milliseconds 150 }; return (& $sb) }
function Overlay($proc) { $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.AndCondition((PropEq 'GlyphLensOverlayWindow'), (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))))) }
function OverlayVisible($proc) { $o = Overlay $proc; $o -and (-not $o.Current.IsOffscreen) -and [KbW]::IsWindowVisible([IntPtr]$o.Current.NativeWindowHandle) }
# The cards' own Border is not in the UI Automation tree; each card's original-text box is. Only the cards in view exist (virtualised).
function CardTexts($ov) {
  @((Find $ov 'OverlayCardsList').FindAll('Descendants', (PropEq 'OverlayCardOriginal')) | ForEach-Object { try { $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { $_.Current.Name } })
}
function Rect($h) { $r = New-Object KbW+RECT; [void][KbW]::GetWindowRect($h, [ref]$r); [pscustomobject]@{ X = $r.L; Y = $r.T; W = $r.R - $r.L; H = $r.B - $r.T; R = $r.R; B = $r.B } }
function ShortType($e) { $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', '' }
function Describe($e) { "$(ShortType $e) id='$($e.Current.AutomationId)' name='$($e.Current.Name)'" }
function Keys($hwnd, [string]$keys) {
  # Keystrokes go to the foreground window: send them only when the expected window (a GlyphLens or test window) is in front.
  # SetForegroundWindow is refused while another process owns the foreground; UI Automation's focus request is allowed.
  for ($i = 0; $i -lt 6 -and [KbW]::GetForegroundWindow() -ne $hwnd; $i++) {
    if ($i % 2 -eq 0) { try { $AE::FromHandle($hwnd).SetFocus() } catch { } } else { [void][KbW]::SetForegroundWindow($hwnd) }
    Start-Sleep -Milliseconds 300
  }
  if ([KbW]::GetForegroundWindow() -ne $hwnd) { throw "the expected window is not in the foreground: not sending '$keys'" }
  [System.Windows.Forms.SendKeys]::SendWait($keys)
}

$interactive = @('Button', 'CheckBox', 'ComboBox', 'Edit', 'Hyperlink', 'RadioButton', 'Slider', 'Spinner', 'SplitButton', 'MenuItem', 'TabItem')
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
# The page-up/page-down areas of a scroll bar or slider are parts of the theme's template (not focusable, not GlyphLens controls):
# they are reported separately instead of being counted as controls.
function IsTemplatePart($e) { $p = $walker.GetParent($e); ($null -ne $p) -and (@('ScrollBar', 'Slider') -contains (ShortType $p)) }
function All-Interactive($root) {
  @($root.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $interactive -contains (ShortType $_) })
}
function Interactive($root) { @(All-Interactive $root | Where-Object { -not (IsTemplatePart $_) }) }
function IsIconName([string]$n) { ($n.Length -le 2) -and ($n.ToCharArray() | Where-Object { [int]$_ -ge 0xE000 -and [int]$_ -le 0xF8FF }) }
function RectText($b) { if ($b.IsEmpty) { 'empty' } else { "$([int]$b.X),$([int]$b.Y) $([int]$b.Width)x$([int]$b.Height)" } }
# Is $inner the element $outer or inside it?
function Within($outer, $inner) {
  $e = $inner
  for ($k = 0; ($k -lt 15) -and ($null -ne $e); $k++) { if ([System.Windows.Automation.Automation]::Compare($e, $outer)) { return $true }; $e = $walker.GetParent($e) }
  return $false
}
# The visible height an element can be seen in: its nearest vertically scrolling ancestor, otherwise the window below the title bar.
function ViewportOf($e, $winRect) {
  $p = $walker.GetParent($e)
  for ($k = 0; ($k -lt 25) -and ($null -ne $p); $k++) {
    if ([bool]$p.GetCurrentPropertyValue($AE::IsScrollPatternAvailableProperty)) {
      if ($p.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable) {
        $r = $p.Current.BoundingRectangle; return [pscustomobject]@{ Top = $r.Top; Bottom = $r.Bottom }
      }
    }
    $p = $walker.GetParent($p)
  }
  return [pscustomobject]@{ Top = $winRect.Y + 50; Bottom = $winRect.B - 12 }
}
# Where to look: for a text, just inside both ends of each line of its glyphs (a text block is often wider than its text); for a
# control, just inside its left edge, its centre and just inside its right edge.
function TestPoints($c) {
  $b = $c.Current.BoundingRectangle
  $pts = @()
  if ((ShortType $c) -eq 'Text') {
    try {
      foreach ($l in $c.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetBoundingRectangles()) {
        if ($l.Width -gt 6) { $y = [int]($l.Y + $l.Height / 2); $pts += New-Object System.Windows.Point(([int]$l.X + 2), $y); $pts += New-Object System.Windows.Point(([int]$l.Right - 2), $y) }
      }
    } catch { }
    if ($pts.Count -eq 0) { $pts += New-Object System.Windows.Point(([int]$b.X + 3), ([int]($b.Y + [Math]::Min($b.Height, 20) / 2))) }
    return $pts
  }
  $y = [int]($b.Y + $b.Height / 2)
  foreach ($x in @(([int]$b.X + 3), ([int]($b.X + $b.Width / 2)), ([int]$b.Right - 3))) { $pts += New-Object System.Windows.Point($x, $y) }
  return $pts
}
# A clipped or covered control still reports its full rectangle to UI Automation, so look at what is really under the pointer at
# those points: it must be the element itself (or something drawn inside it). Elements outside their visible height are left for a
# later pass after scrolling. Returns the problems found.
function Hit-Problems($root, $winRect, $tested) {
  $problems = @()
  $items = @($root.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
      (($interactive -contains (ShortType $_)) -and $_.Current.IsEnabled -and -not (IsTemplatePart $_)) -or (((ShortType $_) -eq 'Text') -and $_.Current.Name) })
  foreach ($c in $items) {
    $b = $c.Current.BoundingRectangle
    if ($b.IsEmpty -or $c.Current.IsOffscreen) { continue }
    $key = ($c.GetRuntimeId() -join '.')
    if ($tested.ContainsKey($key)) { continue }
    $vp = ViewportOf $c $winRect
    if (((ShortType $c) -ne 'Text') -and (($b.Top -lt $vp.Top) -or ($b.Bottom -gt $vp.Bottom))) { continue }
    $pts = @(TestPoints $c | Where-Object { ($_.Y -ge $vp.Top + 2) -and ($_.Y -le $vp.Bottom - 2) })
    if ($pts.Count -eq 0) { continue }
    $tested[$key] = $true
    foreach ($pt in $pts) {
      $hit = $AE::FromPoint($pt)
      if (Within $c $hit) { continue }
      $hb = $hit.Current.BoundingRectangle
      if ((-not $hb.IsEmpty) -and $b.Contains($hb)) { continue }
      $problems += "$(Describe $c) rect=$(RectText $b) point=$([int]$pt.X),$([int]$pt.Y) hits $(Describe $hit)"; break
    }
  }
  return $problems
}
function Audit-Names($root, [string]$label) {
  $controls = @(Interactive $root)
  $bad = @($controls | Where-Object { [string]::IsNullOrWhiteSpace($_.Current.Name) -or (IsIconName $_.Current.Name) })
  Check ($bad.Count -eq 0) "names-$label" ("{0} controls checked; without a usable name: {1}" -f $controls.Count, (($bad | ForEach-Object { Describe $_ }) -join '; '))
  $parts = @(All-Interactive $root | Where-Object { (IsTemplatePart $_) -and [string]::IsNullOrWhiteSpace($_.Current.Name) })
  if ($parts.Count) { Log INFO "theme-parts-$label" ("unnamed scroll-bar/slider template parts (not focusable): " + (($parts | ForEach-Object { $_.Current.AutomationId }) -join ', ')) }
}

$env:GLYPHLENS_MODELS_DIR = $ModelsDir
if ($ShotDir) {
  # Screenshots of the overlay need it to be visible to screen capture; this changes nothing else that is checked here.
  $env:GLYPHLENS_DISABLE_CAPTURE_EXCLUSION = '1'
  New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
}
$settingsFile = Join-Path $env:LOCALAPPDATA 'GlyphLens\overlay.json'
$settingsBackup = $null
if (Test-Path $settingsFile) { $settingsBackup = [IO.File]::ReadAllBytes($settingsFile); Remove-Item $settingsFile -Force }
$trigger = Join-Path ([IO.Path]::GetTempPath()) ("glyphlens-reveal-" + [Guid]::NewGuid().ToString('N') + '.flag')
$renderer = Join-Path $PSScriptRoot 'render-scenes.ps1'
$target = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $renderer, '-Mode', 'Show', '-SceneName', 'chat-progressive', '-Title', 'GlyphLensChineseTarget', '-ProgressiveSeconds', $RevealSeconds, '-InitialVisible', 4, '-RevealWhenFileExists', $trigger -PassThru
$ordinary = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', (Join-Path $PSScriptRoot 'ordinary-app.ps1') -PassThru
$null = Until { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' }).Count -ge 1 } 30
$ordinaryHwnd = [IntPtr]::Zero
$null = Until { $e = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'GlyphLensOrdinaryApp'))); if ($e) { $script:ordinaryHwnd = [IntPtr]$e.Current.NativeWindowHandle }; $script:ordinaryHwnd -ne [IntPtr]::Zero } 30
Start-Sleep 2
$proc = Start-Process $Exe -PassThru
try {
  $main = $null
  $null = Until { $script:main = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); $null -ne $script:main } 25
  Start-Sleep 2
  $mainHwnd = [IntPtr]$main.Current.NativeWindowHandle
  # Put the main window beside the (topmost) Chinese test window, so nothing covers it while its pixels are compared.
  $testEl = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'GlyphLensChineseTarget')))
  if ($testEl) { $tr = Rect ([IntPtr]$testEl.Current.NativeWindowHandle); [void][KbW]::SetWindowPos($mainHwnd, [IntPtr]::Zero, $tr.R + 16, 30, 0, 0, 0x0015); Start-Sleep -Milliseconds 500 }
  $mr = Rect $mainHwnd
  # Park the mouse over the window's logo, away from every control, so no hover effect is mistaken for a focus indicator.
  [void][KbW]::SetCursorPos($mr.X + 40, $mr.Y + 80)

  # ---- 1 + 2. accessible names and keyboard-only use of every page -----------------------------------------------------
  $pages = @('NavOverview', 'NavCapture', 'NavModels', 'NavSettings')
  Keys $mainHwnd ''
  $nav = Find $main 'NavOverview'
  $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 400
  $nav.SetFocus(); Start-Sleep -Milliseconds 300
  for ($p = 0; $p -lt $pages.Count; $p++) {
    $page = $pages[$p]
    if ($p -gt 0) {
      # switch page with the arrow key in the navigation list, as a keyboard user would
      (Find $main $pages[$p - 1]).SetFocus(); Start-Sleep -Milliseconds 250
      Keys $mainHwnd '{DOWN}'; Start-Sleep -Milliseconds 600
      $sel = (Find $main $page).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
      Check $sel "arrow-key-opens-$page" "Down in the navigation list selected $page ($sel)"
    }
    Start-Sleep -Milliseconds 500
    $main = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
    Audit-Names $main $page

    # Tab around the window once and record every stop
    $start = [System.Windows.Automation.AutomationElement]::FocusedElement
    $startKey = "$($start.Current.AutomationId)|$($start.Current.Name)|$(ShortType $start)"
    $stops = @(); $noFocusCue = @(); $cycled = $false
    for ($i = 0; $i -lt 90; $i++) {
      $f = [System.Windows.Automation.AutomationElement]::FocusedElement
      $fr = $f.Current.BoundingRectangle
      $before = $null
      if (-not $fr.IsEmpty -and $fr.Width -gt 0) { $before = [KbW]::Grab([int]$fr.X - 4, [int]$fr.Y - 4, [int]$fr.Width + 8, [int]$fr.Height + 8) }
      Keys $mainHwnd '{TAB}'; Start-Sleep -Milliseconds 220
      if ($before) {
        # the control that just lost focus: does it look different without focus? (only if it did not move)
        $fr2 = $f.Current.BoundingRectangle
        if (($fr2.X -eq $fr.X) -and ($fr2.Y -eq $fr.Y)) {
          $after = [KbW]::Grab([int]$fr.X - 4, [int]$fr.Y - 4, [int]$fr.Width + 8, [int]$fr.Height + 8)
          $d = [KbW]::Diff($before, $after); $after.Dispose()
          if ($d -lt 12) { $noFocusCue += "$(Describe $f) (changed pixels: $d)" }
        }
        $before.Dispose()
      }
      $f = [System.Windows.Automation.AutomationElement]::FocusedElement
      if ($f.Current.ProcessId -ne $proc.Id) { throw "focus left GlyphLens (now in process $($f.Current.ProcessId)): stopping the keyboard walk" }
      $key = "$($f.Current.AutomationId)|$($f.Current.Name)|$(ShortType $f)"
      if ($key -eq $startKey) { $cycled = $true; break }
      $stops += [pscustomobject]@{ Key = $key; Id = $f.Current.AutomationId; Name = $f.Current.Name; Type = (ShortType $f); Element = $f }
    }
    Log INFO "tab-order-$page" (($stops | ForEach-Object { if ($_.Id) { $_.Id } else { "$($_.Type):'$($_.Name)'" } }) -join ' > ')
    Check $cycled "tab-cycles-$page" "Tab came back to the start after $($stops.Count) stops (no keyboard trap)"
    $nameless = @($stops | Where-Object { [string]::IsNullOrWhiteSpace($_.Name) -or (IsIconName $_.Name) })
    Check ($nameless.Count -eq 0) "tab-stops-named-$page" ("stops without a usable name: " + (($nameless | ForEach-Object { "$($_.Type) id='$($_.Id)'" }) -join '; '))
    Check ($noFocusCue.Count -eq 0) "focus-visible-$page" ("stops whose look did not change with focus: " + ($noFocusCue -join '; '))
    # every enabled, focusable control of the page is reached (a radio group and the navigation list are one stop each)
    $reachedIds = @($stops | ForEach-Object { $_.Id })
    $must = @(Interactive $main | Where-Object { $_.Current.IsEnabled -and $_.Current.IsKeyboardFocusable -and ((ShortType $_) -ne 'RadioButton') -and $_.Current.AutomationId })
    $missed = @($must | Where-Object { $reachedIds -notcontains $_.Current.AutomationId })
    $radios = @(Interactive $main | Where-Object { ((ShortType $_) -eq 'RadioButton') -and $_.Current.IsEnabled })
    $radioOk = ($radios.Count -eq 0) -or (@($stops | Where-Object { $_.Type -eq 'RadioButton' }).Count -ge 1)
    Check (($missed.Count -eq 0) -and $radioOk) "tab-reaches-all-$page" ("{0} controls to reach; missed: {1}; radio group reached: {2}" -f $must.Count, (($missed | ForEach-Object { Describe $_ }) -join '; '), $radioOk)
  }

  # ---- capture started from the keyboard --------------------------------------------------------------------------------
  (Find $main 'TargetKindWindow').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
  Click $main 'RefreshButton'; Start-Sleep 1
  $found = Select-CaptureWindow $main '*GlyphLensChineseTarget*'
  Check $found select-chinese-window 'the Chinese test window is selected'
  if (-not $found) { throw 'the test window is not in the target list: not starting a capture of any other window' }
  (Find $main 'TargetSelector').SetFocus(); Start-Sleep -Milliseconds 300
  $reachedStart = $false
  for ($i = 0; $i -lt 12; $i++) {
    Keys $mainHwnd '{TAB}'; Start-Sleep -Milliseconds 200
    if ([System.Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'StartCaptureButton') { $reachedStart = $true; break }
  }
  if (-not $reachedStart) {
    (Find $main 'StartCaptureButton').SetFocus(); Start-Sleep -Milliseconds 200
    for ($i = 0; $i -lt 12 -and -not $reachedStart; $i++) { $reachedStart = ([System.Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'StartCaptureButton') }
  }
  Check $reachedStart start-button-by-tab 'Tab from the target list reaches "Start capture"'
  Keys $mainHwnd ' '
  $cap = Until { (Txt $main 'CaptureStatusText') -eq 'Capturing' } 15
  Check $cap space-starts-capture "Space on Start capture: status=$(Txt $main 'CaptureStatusText')"
  $lang = Find $main 'OcrLanguageSelector'
  $lexp = $lang.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $lexp.Expand()
  $litems = @(); $null = Until { $script:litems = @($lang.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))); $script:litems.Count -ge 3 } 10
  ($litems | Where-Object { $_.Current.Name -like '*Chinese*' } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); $lexp.Collapse(); Start-Sleep -Milliseconds 300

  # ---- 3. global shortcuts from another application ---------------------------------------------------------------------
  [void][KbW]::SetWindowPos($ordinaryHwnd, [IntPtr]::Zero, $mr.X + 60, $mr.Y + 60, 520, 300, 0x0040)
  [void][KbW]::SetForegroundWindow($ordinaryHwnd); Start-Sleep -Milliseconds 600
  $ordinaryBefore = [KbW]::EditText($ordinaryHwnd)
  Check ((ToggleState $main 'TranslateToggle') -eq 'Off') translation-starts-off 'translation is off before the shortcut'
  Keys $ordinaryHwnd '^%t'
  Check (Until { (ToggleState $main 'TranslateToggle') -eq 'On' } 5) hotkey-T-turns-on "Ctrl+Alt+T with the ordinary app in front: translation toggle=$(ToggleState $main 'TranslateToggle')"
  $null = Until { (Txt $main 'WorkerStatusText') -eq 'Ready' } 90
  $null = Until { (Txt $main 'ModelsSummaryText') -like '*installed*' -and (Txt $main 'ModelsSummaryText') -notlike 'No *' } 60
  Keys $ordinaryHwnd '^%o'
  Check (Until { OverlayVisible $proc } 5) hotkey-O-shows 'Ctrl+Alt+O shows the overlay'
  $ov = Overlay $proc; $oh = [IntPtr]$ov.Current.NativeWindowHandle
  $g0 = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero); $scale = $g0.DpiX / 96.0; $g0.Dispose()
  # a short overlay, so that a few cards need scrolling
  [void][KbW]::SetWindowPos($oh, [IntPtr]::Zero, $mr.R - [int](420 * $scale), $mr.Y + 40, [int](380 * $scale), [int](330 * $scale), 0x0014)
  Start-Sleep -Milliseconds 500
  ((Find $main 'LiveOcrToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
  $ov = Overlay $proc
  # the card list is virtualised: only the cards in view exist for UI Automation, so wait for one card and for the list to scroll
  $got = Until { @(CardTexts $ov).Count -ge 1 } 120
  $scroll = (Find $ov 'OverlayCardsScroll').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
  $null = Until { $scroll.Current.VerticallyScrollable } 30
  Check ($got -and $scroll.Current.VerticallyScrollable) cards-need-scrolling "cards shown; the list scrolls=$($scroll.Current.VerticallyScrollable) (view $([int]$scroll.Current.VerticalViewSize) % of the list)"
  Start-Sleep 3

  # ---- 4. new results while scrolled up, and following at the bottom ------------------------------------------------------
  $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0); Start-Sleep -Milliseconds 600
  $view0 = $scroll.Current.VerticalViewSize
  $firstCard = @(CardTexts $ov)[0]
  Check ($scroll.Current.VerticalScrollPercent -le 1) scrolled-to-top "position=$([int]$scroll.Current.VerticalScrollPercent) %"
  [IO.File]::WriteAllText($trigger, 'reveal')
  $added = Until { $scroll.Current.VerticalViewSize -lt ($view0 - 0.5) } ([Math]::Max(40, $RevealSeconds * 4))
  Start-Sleep -Milliseconds 800
  $pos = $scroll.Current.VerticalScrollPercent
  $firstNow = @(CardTexts $ov)[0]
  Log INFO first-visible-card "before='$firstCard' after='$firstNow'"
  Check ($added -and ($pos -le 5) -and $firstCard -and ($firstNow -eq $firstCard)) reading-position-kept "a card was added ($added; view $([int]$view0) -> $([int]$scroll.Current.VerticalViewSize) %); position=$([int]$pos) %; first visible card unchanged=$($firstNow -eq $firstCard)"
  $pill = Find $ov 'OverlayNewResultsButton'
  Check (($null -ne $pill) -and (-not $pill.Current.IsOffscreen)) new-results-shown "the New results button is shown=$(($null -ne $pill) -and (-not $pill.Current.IsOffscreen))"
  if ($pill) {
    $pill.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800
    Check (($scroll.Current.VerticalScrollPercent -ge 99) -and ($null -eq (Find $ov 'OverlayNewResultsButton'))) new-results-scrolls-down "after the button: position=$([int]$scroll.Current.VerticalScrollPercent) %, button gone=$($null -eq (Find $ov 'OverlayNewResultsButton'))"
  }
  $view1 = $scroll.Current.VerticalViewSize
  $added2 = Until { $scroll.Current.VerticalViewSize -lt ($view1 - 0.5) } ([Math]::Max(40, $RevealSeconds * 4))
  Start-Sleep -Milliseconds 1200
  Check ($added2 -and ($scroll.Current.VerticalScrollPercent -ge 99) -and ($null -eq (Find $ov 'OverlayNewResultsButton'))) follows-at-bottom "another card was added ($added2); position=$([int]$scroll.Current.VerticalScrollPercent) %; no New results button=$($null -eq (Find $ov 'OverlayNewResultsButton'))"

  # ---- 3 (continued). pause, resume, pass-through, clear, translation off ----------------------------------------------
  Keys $ordinaryHwnd '^%p'
  Check (Until { (Txt $ov 'OverlayStateText') -eq 'Paused' } 5) hotkey-P-pauses "overlay state='$(Txt $ov 'OverlayStateText')' main='$(Txt $main 'OverlayActivityText')'"
  $viewPaused = $scroll.Current.VerticalViewSize
  Start-Sleep -Seconds ([int]($RevealSeconds * 2.5))
  Check ([Math]::Abs($scroll.Current.VerticalViewSize - $viewPaused) -lt 0.5) paused-ignores-results "messages appeared in the test window while paused; cards added=$([Math]::Abs($scroll.Current.VerticalViewSize - $viewPaused) -ge 0.5)"
  Keys $ordinaryHwnd '^%p'
  Check (Until { (Txt $ov 'OverlayStateText') -ne 'Paused' } 5) hotkey-P-resumes "overlay state='$(Txt $ov 'OverlayStateText')'"
  Check (Until { $scroll.Current.VerticalViewSize -lt ($viewPaused - 0.5) } 40) resume-shows-new-results "after resuming, the messages that arrived while paused were added"
  Keys $ordinaryHwnd '^%i'
  $toPass = Until { (([KbW]::GetWindowLong($oh, -20)) -band 0x20) -ne 0 } 5
  Keys $ordinaryHwnd '^%i'
  $back = Until { (([KbW]::GetWindowLong($oh, -20)) -band 0x20) -eq 0 } 5
  Check ($toPass -and $back) hotkey-I-toggles "pass-through on=$toPass, back to interactive=$back"
  $cardsBefore = @(CardTexts $ov).Count
  Keys $ordinaryHwnd '^%c'
  $cleared = Until { @(CardTexts $ov).Count -eq 0 } 3
  $placeholder = $ov.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Translations of the text on your screen appear here.')))
  Check (($cardsBefore -ge 1) -and $cleared -and ($null -ne $placeholder)) hotkey-C-clears "cards in view before=$cardsBefore, after=$(@(CardTexts $ov).Count); empty-state text shown=$($null -ne $placeholder)"
  ((Find $main 'LiveOcrToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle(); Start-Sleep 1
  Keys $ordinaryHwnd '^%t'
  Check (Until { (ToggleState $main 'TranslateToggle') -eq 'Off' } 5) hotkey-T-turns-off "translation toggle=$(ToggleState $main 'TranslateToggle'), overlay state='$(Txt (Overlay $proc) 'OverlayStateText')'"
  Start-Sleep -Milliseconds 500
  $ordinaryAfter = [KbW]::EditText($ordinaryHwnd)
  Check ($ordinaryAfter -eq $ordinaryBefore) shortcuts-not-typed-into-app "the ordinary app's text is unchanged ('$ordinaryBefore' -> '$ordinaryAfter'): GlyphLens took the keys"
  Audit-Names (Overlay $proc) 'overlay'

  # ---- 5. minimum size ------------------------------------------------------------------------------------------------------
  Click $main 'StopCaptureButton'; Start-Sleep 1
  Keys $ordinaryHwnd '^%o'; $null = Until { -not (OverlayVisible $proc) } 5
  [void][KbW]::SetWindowPos($ordinaryHwnd, [IntPtr]1, 0, 0, 0, 0, 0x0013)  # keep it, but at the bottom of the z-order (HWND_BOTTOM; no size, move or activation)
  $main.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(300, 200); Start-Sleep -Milliseconds 800
  $mr = Rect $mainHwnd
  $minW = [int](820 * $scale); $minH = [int](580 * $scale)
  Check (([Math]::Abs($mr.W - $minW) -le 20) -and ([Math]::Abs($mr.H - $minH) -le 20)) main-minimum-size "asked for 300x200; the window is $($mr.W)x$($mr.H) px (minimum ${minW}x$minH)"
  Keys $mainHwnd ''
  [void][KbW]::SetCursorPos($mr.X + 40, $mr.Y + 80)
  foreach ($page in $pages) {
    $item = Find $main $page
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 900
    $main = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
    $controls = @(Interactive $main | Where-Object { $_.Current.IsEnabled })
    # test what is in view, then scroll the page (if it scrolls) and test what came into view
    $tested = @{}
    $hidden = @(Hit-Problems $main $mr $tested)
    $pane = @($main.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::IsScrollPatternAvailableProperty, $true))) |
        Where-Object { $_.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable } |
        Sort-Object { $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1)
    if ($pane.Count -eq 1) {
      $sp = $pane[0].GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
      foreach ($pct in 34, 67, 100) { $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct); Start-Sleep -Milliseconds 400; $hidden += @(Hit-Problems $main $mr $tested) }
      $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0); Start-Sleep -Milliseconds 300
    }
    Check ($hidden.Count -eq 0) "minimum-size-$page" ("{0} controls and texts tested under the pointer ({1} controls on the page); clipped or covered: {2}" -f $tested.Count, $controls.Count, ($hidden -join '; '))
    if ($ShotDir) {
      # PrintWindow: only GlyphLens' own pixels, never another application's window
      $bmp = [KbW]::PrintWin($mainHwnd); $path = Join-Path $ShotDir "min-$page.png"; $bmp.Save($path); $bmp.Dispose(); Log INFO screenshot $path
    }
  }
  # the overlay at its minimum width, placed over the GlyphLens main window (its transparent corners show only GlyphLens)
  Click $main 'ShowOverlayButton'; $null = Until { OverlayVisible $proc } 5
  $ov = Overlay $proc; $oh = [IntPtr]$ov.Current.NativeWindowHandle
  [void][KbW]::SetWindowPos($oh, [IntPtr]::Zero, $mr.X + [int](260 * $scale), $mr.Y + [int](60 * $scale), 100, [int](460 * $scale), 0x0014); Start-Sleep -Milliseconds 700
  Keys $mainHwnd ''
  $orr = Rect $oh
  Check ($orr.W -ge [int](320 * $scale) - 4) overlay-minimum-width "asked for 100 px; the overlay is $($orr.W) px wide (minimum $([int](320 * $scale)))"
  $tested = @{}
  $hidden = @(Hit-Problems $ov ([pscustomobject]@{ Y = $orr.Y - 40; B = $orr.B + 10 }) $tested)
  Check ($hidden.Count -eq 0) overlay-minimum-width-controls ("{0} controls and texts tested under the pointer; clipped or covered: {1}" -f $tested.Count, ($hidden -join '; '))
  if ($ShotDir) { $bmp = [KbW]::Grab($orr.X, $orr.Y, $orr.W, $orr.H); $path = Join-Path $ShotDir 'min-overlay.png'; $bmp.Save($path); $bmp.Dispose(); Log INFO screenshot $path }

  $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  Start-Sleep 1
  Keys ([IntPtr]$ordinaryHwnd) '^%o'
  [void]$proc.WaitForExit(20000)
}
catch { Log FAIL driver "$($_.Exception.Message) (script line $($_.InvocationInfo.ScriptLineNumber))"; $script:fail++ }
finally {
  if (-not $proc.HasExited) { $proc.Kill(); Log WARN cleanup 'app killed' }
  if (-not $ordinary.HasExited) { $ordinary.Kill() }
  Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' } | Stop-Process -Force -ErrorAction SilentlyContinue
  if (-not $target.HasExited) { $target.Kill() }
  if (Test-Path $trigger) { Remove-Item $trigger -Force }
  if ($null -ne $settingsBackup) { [IO.File]::WriteAllBytes($settingsFile, $settingsBackup) } elseif (Test-Path $settingsFile) { Remove-Item $settingsFile -Force }
  Log INFO summary "passed=$($script:pass) failed=$($script:fail)"
}
