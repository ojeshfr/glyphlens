<#
.SYNOPSIS
  Takes real screenshots (PrintWindow) of the GlyphLens main window and floating overlay while they show a translated Chinese chat.

.DESCRIPTION
  Launches the Chinese test window and GlyphLens.exe, captures the window, lets the app choose the local Chinese OCR (a Chinese model is
  installed in -ModelsDir), turns translation on, reads the text and saves <OutPrefix>-main.png and <OutPrefix>-overlay.png. The
  screenshots show this machine's DPI scale (the scale is printed); other scales are not produced by this script. Nothing is uploaded.
  ASCII only; Chinese is matched with Unicode ranges.

.PARAMETER Exe        Path to GlyphLens.exe.
.PARAMETER ModelsDir  Models folder with ocr\PP-OCRv5_*_onnx and the installed zh_en package.
.PARAMETER OutPrefix  Path prefix of the PNG files.
.PARAMETER SceneName  Scene of ocr-scenes.json (default chat-light).
#>
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$ModelsDir,
  [Parameter(Mandatory)][string]$OutPrefix,
  [string]$SceneName = 'chat-light'
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PW9 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  public static IntPtr FindByTitle(uint pid, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256); if (p == pid && sb.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
  public struct R { public int L, T, Rr, B; }
}
"@
[void][PW9]::SetProcessDPIAware()
$AE = [System.Windows.Automation.AutomationElement]
. (Join-Path $PSScriptRoot 'ui-pages.ps1')
function Log($s, $n, $d) { "{0,-6} {1} :: {2}" -f $s, $n, $d }
function Find($root, $id) { Enter-PageOf $root $id; $root.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function Txt($root, $id) { $e = Find $root $id; if ($e) { $e.Current.Name } else { "<missing $id>" } }
function Click($root, $id) { (Find $root $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Until($sb, $sec = 30) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $sec) { if (& $sb) { return $true }; Start-Sleep -Milliseconds 150 }; return (& $sb) }
function ShootScreen($hwnd, $path) {
  $r = New-Object PW9+R; [void][PW9]::GetWindowRect($hwnd, [ref]$r)
  $w = $r.Rr - $r.L; $h = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap($w, $h); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $h))); $bmp.Save($path); $bmp.Dispose(); $g.Dispose()
  Log INFO screenshot ("{0} ({1}x{2}, from the screen)" -f $path, $w, $h)
}
function Shoot($hwnd, $path) {
  $r = New-Object PW9+R; [void][PW9]::GetWindowRect($hwnd, [ref]$r)
  $w = $r.Rr - $r.L; $h = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap($w, $h); $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  [void][PW9]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $bmp.Save($path); $bmp.Dispose(); $g.Dispose()
  Log INFO screenshot ("{0} ({1}x{2})" -f $path, $w, $h)
}

$env:GLYPHLENS_MODELS_DIR = $ModelsDir
# The overlay is excluded from screen capture (that is its purpose), so for these screenshots only the exclusion is switched off in the
# app under test; the main window is captured with PrintWindow, the overlay with CopyFromScreen of its on-screen rectangle.
$env:GLYPHLENS_DISABLE_CAPTURE_EXCLUSION = '1'
$renderer = Join-Path $PSScriptRoot 'render-scenes.ps1'
$target = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $renderer, '-Mode', 'Show', '-SceneName', $SceneName, '-Title', 'GlyphLensChineseTarget' -PassThru
$null = Until { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' }).Count -ge 1 } 30
Start-Sleep 2
$proc = Start-Process $Exe -PassThru
try {
  Log INFO system-dpi ("{0} dpi = {1} % scale" -f [PW9]::GetDpiForSystem(), [int]([PW9]::GetDpiForSystem() / 96 * 100))
  $win = $null
  $null = Until { $script:win = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); $null -ne $script:win } 25
  Start-Sleep 2
  (Find $win 'TargetKindWindow').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
  Click $win 'RefreshButton'; Start-Sleep 1
  $found = Select-CaptureWindow $win '*GlyphLensChineseTarget*'
  if (-not $found) { Log INFO window-list-walk ((@($seen | Select-Object -Unique) -join ' | ')); throw 'the test window is not in the target list: not starting a capture of any other window' }
  Click $win 'StartCaptureButton'
  $null = Until { (Txt $win 'CaptureStatusText') -eq 'Capturing' } 15
  (Find $win 'TranslateToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  $null = Until { (Txt $win 'WorkerStatusText') -eq 'Ready' } 90
  $null = Until { ((Txt $win 'OcrLanguageSelector') -ne '') -and ($null -ne (Find $win 'OcrEngineNotice')) } 30
  Click $win 'ReadTextButton'
  $null = Until { @($win.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'OverlayCard')))).Count -ge 1 -or (Txt $win 'TranslationStatusText') -match 'translated' } 120
  Start-Sleep 2
  # one screenshot per page of the main window (the page is selected like a user clicking its navigation entry)
  foreach ($page in 'Overview', 'Capture', 'Models', 'Settings') {
    $item = $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "Nav$page")))
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 900
    Shoot ([IntPtr]$win.Current.NativeWindowHandle) "$OutPrefix-main-$($page.ToLower()).png"
  }
  Click $win 'ShowOverlayButton'; Start-Sleep -Milliseconds 1500
  $overlay = [PW9]::FindByTitle([uint32]$proc.Id, 'GlyphLens overlay')
  if ($overlay -ne [IntPtr]::Zero) { Start-Sleep -Milliseconds 500; ShootScreen $overlay "$OutPrefix-overlay.png" } else { Log WARN overlay 'overlay window not found' }
  $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  [void]$proc.WaitForExit(30000)
}
catch { Log FAIL driver $_.Exception.Message }
finally {
  if (-not $proc.HasExited) { $proc.Kill() }
  Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' } | Stop-Process -Force -ErrorAction SilentlyContinue
  if (-not $target.HasExited) { $target.Kill() }
}
