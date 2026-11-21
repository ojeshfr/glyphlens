<#
.SYNOPSIS
  End-to-end test of the real GlyphLens app on a real Chinese application window, driven through UI Automation.

.DESCRIPTION
  Launches render-scenes.ps1 in Show mode (a real top-most Windows window that draws Chinese chat bubbles with ClearType
  text), launches GlyphLens.exe, and then drives the app exactly as a user would:

    select the window -> Start capture (Windows Graphics Capture) -> choose the local Chinese OCR -> turn translation on
    (this starts the Python worker) -> Read text -> [no model yet: the app must say so and invent nothing]
    -> Import model (real file dialog) -> the same screen text is translated -> separate copy buttons
    -> Live updates are answered from the cache -> no remote network connection -> Stop clears everything -> exit.

  Nothing is injected: the Chinese text reaches the translator only through screen capture and OCR. The file is ASCII on
  purpose; Chinese is matched with Unicode ranges.

.PARAMETER Exe        Path to GlyphLens.exe (Release or Debug build).
.PARAMETER ModelsDir  An isolated, absolute models directory that already contains ocr\PP-OCRv5_mobile_det_onnx and ..._rec_onnx.
.PARAMETER ModelFile  The translate-zh_en .argosmodel (or zt_en) file to import through the file dialog.
.PARAMETER SceneName  Scene of ocr-scenes.json to show (default chat-light; use traditional-chat with the zt model).
.PARAMETER Shot       Optional PNG path prefix for screenshots of the app window.
.PARAMETER ExpectedMessages  Optional. With the chat-multiline scene pass 4: asserts that the 4 chat messages (some wrapped over two or
                      three lines) are translated as exactly 4 groups although OCR returned more lines.
#>
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$ModelsDir,
  [Parameter(Mandatory)][string]$ModelFile,
  [string]$SceneName = 'chat-light',
  [string]$Shot,
  [int]$ExpectedMessages = 0
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PW5 { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f); [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); public struct R { public int L,T,Rr,B; } }
"@
$AE = [System.Windows.Automation.AutomationElement]
. (Join-Path $PSScriptRoot 'ui-pages.ps1')
$han = New-Object System.Text.RegularExpressions.Regex(('[' + [char]0x3400 + '-' + [char]0x9fff + ']'))
$arrow = [string][char]0x2192
function Log($s, $n, $d) { "{0,-6} {1} :: {2}" -f $s, $n, $d }
function Find($root, $id) { Enter-PageOf $root $id; $root.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function Txt($root, $id) { $e = Find $root $id; if ($e) { $e.Current.Name } else { "<missing $id>" } }
function Click($root, $id) { (Find $root $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Until($sb, $sec = 30) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $sec) { if (& $sb) { return $true }; Start-Sleep -Milliseconds 150 }; return (& $sb) }
function Rows($win) { $list = Find $win 'TranslationResultsList'; if (-not $list) { return @() }; $items = $list.FindAll('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))); @($items | ForEach-Object { $_.Current.Name }) }
function TranslatedRows($win) { @((Rows $win) | Where-Object { $_ -like "*$arrow*" }) }
function Shot($win, $path) {
  $h = [IntPtr]$win.Current.NativeWindowHandle; $r = New-Object PW5+R; [void][PW5]::GetWindowRect($h, [ref]$r)
  $bmp = New-Object System.Drawing.Bitmap(($r.Rr - $r.L), ($r.B - $r.T)); $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  [void][PW5]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $bmp.Save($path); Log INFO screenshot $path
}

# The app and the worker it starts inherit the isolated model directory.
$env:GLYPHLENS_MODELS_DIR = $ModelsDir
$renderer = Join-Path $PSScriptRoot 'render-scenes.ps1'
$target = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $renderer, '-Mode', 'Show', '-SceneName', $SceneName, '-Title', 'GlyphLensChineseTarget' -PassThru
$null = Until { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' }).Count -ge 1 } 30
Start-Sleep 2
$proc = Start-Process $Exe -PassThru
$pass = 0; $fail = 0
function Check($ok, $name, $detail) { if ($ok) { $script:pass++; Log PASS $name $detail } else { $script:fail++; Log FAIL $name $detail } }
try {
  $win = $null
  $null = Until { $script:win = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); $null -ne $script:win } 25
  Start-Sleep 2

  # 1. capture the real Chinese window
  (Find $win 'TargetKindWindow').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
  Click $win 'RefreshButton'; Start-Sleep 1
  $combo = Find $win 'TargetSelector'
  $exp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $exp.Expand(); Start-Sleep -Milliseconds 400
  $items = $combo.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
  $item = $items | Where-Object { $_.Current.Name -like '*GlyphLensChineseTarget*' } | Select-Object -First 1
  if ($item) {
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); $exp.Collapse(); Start-Sleep -Milliseconds 300
  }
  else {
    # The drop-down is virtualised: only the first rows exist for UI Automation. Walk the list with the keyboard, as a user would.
    $exp.Collapse(); Start-Sleep -Milliseconds 300; Use-AppWindowForKeys $win; $combo.SetFocus(); $found = $false; $seen = @()
    for ($i = 0; $i -lt 80 -and -not $found; $i++) {
      [System.Windows.Forms.SendKeys]::SendWait('{DOWN}'); Start-Sleep -Milliseconds 120
      $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
      $found = ($sel.Count -gt 0) -and ($sel[0].Current.Name -like '*GlyphLensChineseTarget*')
      if ($sel.Count -gt 0) { $seen += $sel[0].Current.Name }
    }
    Log INFO window-list-walk (($seen | Select-Object -Unique) -join ' | ')
    if (-not $found) { Check $false select-window 'the Chinese test window is not listed'; throw 'no window' }
  }
  Click $win 'StartCaptureButton'
  $cap = Until { (Txt $win 'CaptureStatusText') -eq 'Capturing' } 15
  $null = Until { (Txt $win 'FrameSequenceText') -ne [string][char]0x2014 } 10
  Check $cap capture "status=$(Txt $win 'CaptureStatusText') frame=$(Txt $win 'FrameSizeText')"

  # 2. choose the local Chinese OCR (Windows OCR on this machine has only English)
  $lang = Find $win 'OcrLanguageSelector'
  $lexp = $lang.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $lexp.Expand()
  $litems = @()
  $null = Until { $script:litems = @($lang.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))); $script:litems.Count -ge 3 } 10
  Log INFO ocr-languages (($litems | ForEach-Object { $_.Current.Name }) -join ' | ')
  $chinese = $litems | Where-Object { $_.Current.Name -like '*Chinese*' } | Select-Object -First 1
  Check ($null -ne $chinese) ocr-engine-offered 'the local Chinese OCR is in the language list'
  $chinese.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); $lexp.Collapse(); Start-Sleep -Milliseconds 300

  # 3. turn translation on: starts the worker
  $toggle = (Find $win 'TranslateToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  $toggle.Toggle()
  $ready = Until { (Txt $win 'WorkerStatusText') -eq 'Ready' } 90
  Check $ready worker-started "worker=$(Txt $win 'WorkerStatusText') engines='$(Txt $win 'TranslationEngineText')'"
  $null = Until { (Txt $win 'ModelsSummaryText') -notlike 'Start the worker*' } 90
  Log INFO models-before "summary='$(Txt $win 'ModelsSummaryText')'"

  # 4. Read the screen: OCR runs in the worker; with no translation model the app must say so
  Click $win 'ReadTextButton'
  $null = Until { (Rows $win).Count -ge 2 -and (Txt $win 'TranslationStatusText') -notlike 'Translating*' } 120
  $rows = Rows $win   # the list is virtualised: UI Automation sees only the rows that are on screen
  $ocrError = Txt $win 'OcrErrorText'
  Log INFO rows-before-model "ocrStatus='$(Txt $win 'OcrStatusText')' ocrError='$ocrError' rows=$($rows.Count)"
  $hanRows = @($rows | Where-Object { $han.IsMatch($_) }).Count
  Check ($hanRows -ge 1) ocr-chinese "recognised Chinese rows: $hanRows of $($rows.Count)"
  $notice = Txt $win 'TranslationNotice'
  Check (($notice -match 'No installed model') -and ((TranslatedRows $win).Count -eq 0)) missing-model-honest "translatedRows=$((TranslatedRows $win).Count) notice='$notice'"
  if ($Shot) { Shot $win "$Shot-1-nomodel.png" }

  # 5. import the model through the real file dialog
  Click $win 'ImportModelButton'
  $dlg = $null
  $null = Until { $script:dlg = $win.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Import a translation model'))); $null -ne $script:dlg } 15
  if (-not $dlg) { Check $false import-dialog 'file dialog did not appear'; throw 'no dialog' }
  # The common file dialog exposes its file-name box only as a pane, so type like a user: bring the dialog to the front (its
  # file-name box has the focus when it opens), type the path and confirm; retry once if the dialog is still open.
  for ($attempt = 1; $attempt -le 2; $attempt++) {
    [void][PW5]::SetForegroundWindow([IntPtr]$dlg.Current.NativeWindowHandle)
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait($ModelFile)
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    $closed = Until { $null -eq $win.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Import a translation model'))) } 6
    if ($closed) { break }
  }
  $imported = Until { (Txt $win 'ModelsMessageText') -like 'Installed*' } 180
  Check $imported model-import "message='$(Txt $win 'ModelsMessageText')' error='$(Txt $win 'ModelsErrorText')' summary='$(Txt $win 'ModelsSummaryText')'"

  # 6. the same on-screen text is now translated
  $null = Until { (TranslatedRows $win).Count -ge 2 } 120
  $rows = Rows $win
  $translated = TranslatedRows $win
  Log INFO rows-translated ($rows -join ' || ')
  $english = @($translated | Where-Object { $parts = $_ -split $arrow, 2; ($parts.Count -eq 2) -and ($parts[1] -match '[A-Za-z]{3}') -and (-not $han.IsMatch($parts[1])) }).Count
  Check (($translated.Count -ge 2) -and ($english -eq $translated.Count)) translated "translatedRows=$($translated.Count) englishOnlyTranslations=$english status='$(Txt $win 'TranslationStatusText')'"
  if ($Shot) { Shot $win "$Shot-2-translated.png" }

  # 6b. optional: wrapped (multi-line) messages must come out as ONE translated group per message, not one per OCR line
  if ($ExpectedMessages -gt 0) {
    $ocrLines = 0; $translatedGroups = 0
    if ((Txt $win 'OcrStatusText') -match '(\d+) lines') { $ocrLines = [int]$Matches[1] }
    if ((Txt $win 'TranslationStatusText') -match '(\d+) of \d+ translated') { $translatedGroups = [int]$Matches[1] }
    Check (($translatedGroups -eq $ExpectedMessages) -and ($ocrLines -gt $ExpectedMessages)) multiline-grouped "translatedGroups=$translatedGroups expectedMessages=$ExpectedMessages ocrLines=$ocrLines (more OCR lines than messages means wrapped lines were merged)"
  }

  # 7. copy actions are separate
  Click $win 'CopyOriginalButton'; Start-Sleep -Milliseconds 300
  $orig = Get-Clipboard -Raw
  Click $win 'CopyTranslationButton'; Start-Sleep -Milliseconds 300
  $trans = Get-Clipboard -Raw
  Check (($han.IsMatch($orig)) -and (-not $han.IsMatch($trans)) -and ($trans.Length -gt 10)) copy-separate "originalHasChinese=$($han.IsMatch($orig)) translationHasChinese=$($han.IsMatch($trans)) translation='$($trans.Trim() -replace '\s+', ' ')'"

  # 8. offline evidence: no remote connection owned by the app or its worker
  $workerIds = @(Get-DescendantIds $proc.Id)   # launcher and the real worker (a grandchild of the app)
  $wpid = ($workerIds -join ',')
  $pids = @($proc.Id) + $workerIds
  $conns = @(Get-NetTCPConnection -OwningProcess $pids -ErrorAction SilentlyContinue | Where-Object { $_.RemoteAddress -notin '127.0.0.1', '::1', '0.0.0.0', '::' })
  $udp = @(Get-NetUDPEndpoint -OwningProcess $pids -ErrorAction SilentlyContinue)
  Check ($conns.Count -eq 0) no-remote-connections "appPid=$($proc.Id) workerPid=$wpid remoteTcp=$($conns.Count) udpEndpoints=$($udp.Count)"

  # 9. Live: the screen did not change, so rows stay and answers come from the cache
  $live = (Find $win 'LiveOcrToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  $live.Toggle(); $null = Until { (Txt $win 'TranslationStatusText') -match 'cached' } 30
  $status = Txt $win 'TranslationStatusText'
  Check ($status -match 'cached') live-cache "status='$status' rows=$((Rows $win).Count)"
  $live.Toggle(); Start-Sleep 1

  # 10. stopping the capture clears the translation too
  Click $win 'StopCaptureButton'
  $cleared = Until { (Rows $win).Count -eq 0 } 15
  Check $cleared stop-clears "rows=$((Rows $win).Count)"

  $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  $exited = $proc.WaitForExit(30000)
  Check ($exited -and $proc.ExitCode -eq 0) shutdown "exited=$exited code=$($proc.ExitCode)"
  $left = @($workerIds | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count
  Check ($left -eq 0) worker-gone "workerStillRunning=$left"
}
catch { Log FAIL driver $_.Exception.Message; $fail++ }
finally {
  if (-not $proc.HasExited) { $proc.Kill(); Log WARN cleanup 'app killed' }
  Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' } | Stop-Process -Force -ErrorAction SilentlyContinue
  if (-not $target.HasExited) { $target.Kill() }
  Log INFO summary "passed=$pass failed=$fail"
}
