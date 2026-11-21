<#
.SYNOPSIS
  End-to-end test of the automatic choice of the local Chinese OCR in the real GlyphLens app (UI Automation, real file dialog).

.DESCRIPTION
  Launches the Chinese test window (render-scenes.ps1, scene chat-light), starts GlyphLens.exe and drives it like a user, WITHOUT
  choosing an OCR engine by hand:

    capture the window -> translation on (starts the worker) -> import the zh_en model (Chinese becomes a situation GlyphLens knows)
      Scenario Ready:      the local Chinese OCR is selected by itself, with an explanation; Read text recognises Chinese and
                           translates it; an explicit choice of the English-only Windows OCR is then kept (and explained).
      Scenario MissingOcr: the models folder has NO OCR files: nothing is switched, the app says which models are missing and
                           offers "Open OCR folder" (Explorer opens it); no Chinese text is read behind the user's back.

  The file is ASCII on purpose; Chinese is matched with Unicode ranges.

.PARAMETER Exe        Path to GlyphLens.exe.
.PARAMETER ModelsDir  Isolated absolute models directory. Scenario Ready: it must already contain ocr\PP-OCRv5_mobile_det_onnx and ..._rec_onnx.
                      Scenario MissingOcr: it must be EMPTY (the script uses it to prove the folder is created and opened).
.PARAMETER ModelFile  The translate-zh_en .argosmodel file to import.
.PARAMETER Scenario   Ready (default) or MissingOcr.
#>
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$ModelsDir,
  [Parameter(Mandatory)][string]$ModelFile,
  [ValidateSet('Ready', 'MissingOcr')][string]$Scenario = 'Ready'
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PW7 { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
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
function SelectedOcr($win) { $c = Find $win 'OcrLanguageSelector'; $s = $c.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection(); if ($s.Count -gt 0) { $s[0].Current.Name } else { '' } }
function PickOcr($win, $like) {
  $lang = Find $win 'OcrLanguageSelector'
  $lexp = $lang.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $lexp.Expand()
  $null = Until { @($lang.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))).Count -ge 3 } 10
  $items = @($lang.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
  $item = $items | Where-Object { $_.Current.Name -like $like } | Select-Object -First 1
  if ($item) { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
  $lexp.Collapse(); Start-Sleep -Milliseconds 400
  return ($null -ne $item)
}

$env:GLYPHLENS_MODELS_DIR = $ModelsDir
$renderer = Join-Path $PSScriptRoot 'render-scenes.ps1'
$target = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $renderer, '-Mode', 'Show', '-SceneName', 'chat-light', '-Title', 'GlyphLensChineseTarget' -PassThru
$null = Until { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' }).Count -ge 1 } 30
Start-Sleep 2
$proc = Start-Process $Exe -PassThru
$pass = 0; $fail = 0
function Check($ok, $name, $detail) { if ($ok) { $script:pass++; Log PASS $name $detail } else { $script:fail++; Log FAIL $name $detail } }
try {
  $win = $null
  $null = Until { $script:win = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); $null -ne $script:win } 25
  Start-Sleep 2

  # 1. capture the Chinese window
  (Find $win 'TargetKindWindow').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
  Click $win 'RefreshButton'; Start-Sleep 1
  $found = Select-CaptureWindow $win '*GlyphLensChineseTarget*'
  Check $found select-window 'the Chinese test window was selected in the target list'
  if (-not $found) { throw 'the test window is not in the target list: not starting a capture of any other window' }
  Click $win 'StartCaptureButton'
  $cap = Until { (Txt $win 'CaptureStatusText') -eq 'Capturing' } 15
  $null = Until { (Txt $win 'FrameSequenceText') -ne [string][char]0x2014 } 10
  Check $cap capture "status=$(Txt $win 'CaptureStatusText') frame=$(Txt $win 'FrameSizeText')"

  # 2. translation on: the worker starts. No Chinese situation yet (automatic source, no model): nothing may be switched or said.
  (Find $win 'TranslateToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  $ready = Until { (Txt $win 'WorkerStatusText') -eq 'Ready' } 90
  $null = Until { (Txt $win 'ModelsSummaryText') -notlike 'Start the worker*' } 90
  Check $ready worker-started "worker=$(Txt $win 'WorkerStatusText')"
  $before = SelectedOcr $win
  Check (($before -like 'Automatic*') -and ((Txt $win 'OcrEngineNotice') -like '<missing*')) nothing-before-chinese "ocr='$before' notice='$(Txt $win 'OcrEngineNotice')'"

  # 3. import the zh_en model through the real file dialog
  Click $win 'ImportModelButton'
  $dlg = $null
  $null = Until { $script:dlg = $win.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Import a translation model'))); $null -ne $script:dlg } 15
  if (-not $dlg) { Check $false import-dialog 'file dialog did not appear'; throw 'no dialog' }
  for ($attempt = 1; $attempt -le 2; $attempt++) {
    [void][PW7]::SetForegroundWindow([IntPtr]$dlg.Current.NativeWindowHandle)
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait($ModelFile)
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    $closed = Until { $null -eq $win.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Import a translation model'))) } 6
    if ($closed) { break }
  }
  $imported = Until { (Txt $win 'ModelsMessageText') -like 'Installed*' } 180
  Check $imported model-import "message='$(Txt $win 'ModelsMessageText')'"

  if ($Scenario -eq 'Ready') {
    # 4a. Chinese is now a known situation and the local OCR is ready: it is selected by itself, with an explanation
    $auto = Until { (SelectedOcr $win) -like '*Chinese*' } 20
    $notice = Txt $win 'OcrEngineNotice'
    Check ($auto -and ($notice -match 'local Chinese OCR')) ocr-auto-selected "ocr='$(SelectedOcr $win)' notice='$notice'"
    Check ((Txt $win 'OcrErrorText') -like '<missing*') no-ocr-error "error='$(Txt $win 'OcrErrorText')'"

    # 5a. Read text: recognised without the user ever choosing an engine, and translated
    Click $win 'ReadTextButton'
    $null = Until { (TranslatedRows $win).Count -ge 2 } 120
    $rows = Rows $win
    $hanRows = @($rows | Where-Object { $han.IsMatch($_) }).Count
    $english = @(TranslatedRows $win | Where-Object { $parts = $_ -split $arrow, 2; ($parts.Count -eq 2) -and ($parts[1] -match '[A-Za-z]{3}') -and (-not $han.IsMatch($parts[1])) }).Count
    Check (($hanRows -ge 1) -and ($english -ge 2)) read-and-translate "hanRows=$hanRows englishTranslations=$english status='$(Txt $win 'TranslationStatusText')'"

    # 5a2. the Overview checklist agrees (it is derived from what the worker reported, and the offline check is a real probe)
    $null = Until { (Txt $win 'ReadinessSummary') -like 'Ready to translate*' } 15
    $netRow = Txt $win 'Readiness-network'
    Check (((Txt $win 'ReadinessSummary') -like 'Ready to translate*') -and ((Txt $win 'Readiness-ocr') -like '*ready*') -and ($netRow -like '*No remote connections*')) readiness-overview "summary='$(Txt $win 'ReadinessSummary')' ocr='$(Txt $win 'Readiness-ocr')' network='$netRow'"

    # 6a. an explicit choice of the English-only engine is kept, and explained
    $picked = PickOcr $win 'English*'
    $kept = Until { (SelectedOcr $win) -like 'English*' } 5
    Start-Sleep -Milliseconds 800
    $notice2 = Txt $win 'OcrEngineNotice'
    Check ($picked -and $kept -and ((SelectedOcr $win) -like 'English*') -and ($notice2 -match 'cannot read Chinese')) explicit-choice-kept "ocr='$(SelectedOcr $win)' notice='$notice2'"
  }
  else {
    # 4b. the OCR models are missing: nothing is switched, the app says what is missing and offers the folder
    $null = Until { (Txt $win 'OcrEngineNotice') -match 'models are not installed' } 20
    $notice = Txt $win 'OcrEngineNotice'
    Check (((SelectedOcr $win) -like 'Automatic*') -and ($notice -match 'models are not installed')) missing-models-explained "ocr='$(SelectedOcr $win)' notice='$notice'"
    $openBtn = Find $win 'OpenOcrFolderButton'
    Check ($null -ne $openBtn) open-folder-offered 'the "Open OCR folder" button is shown'

    # 4b2. the Overview checklist names the same problem and offers the same real action
    $ocrRow = Txt $win 'Readiness-ocr'
    $null = Until { $null -ne (Find $win 'ReadinessAction-ocr') } 10
    Check (($ocrRow -like '*not installed*') -and ($null -ne (Find $win 'ReadinessAction-ocr')) -and ((Txt $win 'ReadinessSummary') -like 'Setup needed*')) readiness-overview "ocr='$ocrRow' summary='$(Txt $win 'ReadinessSummary')'"

    # 5b. reading does not pretend: no Chinese text appears (Windows OCR here has English only)
    Click $win 'ReadTextButton'
    Start-Sleep 6
    $hanRows = @((Rows $win) | Where-Object { $han.IsMatch($_) }).Count
    Check ($hanRows -eq 0) no-silent-chinese "chineseRows=$hanRows (nothing Chinese was read without the local OCR)"

    # 6b. the button opens the folder (created on demand) in Explorer
    $ocrDir = Join-Path $ModelsDir 'ocr'
    function ExplorerWindows { @($AE::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { ($_.Current.ClassName -eq 'CabinetWClass') -and ($_.Current.Name -like 'ocr*') }) }
    $existing = (ExplorerWindows).Count
    Click $win 'OpenOcrFolderButton'
    $opened = Until { (Test-Path $ocrDir) -and ((ExplorerWindows).Count -gt $existing) } 15
    Check $opened open-folder-works "folderExists=$(Test-Path $ocrDir) explorerWindowsBefore=$existing after=$((ExplorerWindows).Count)"
    foreach ($ex in ExplorerWindows) { try { $ex.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { Log WARN explorer-close $_.Exception.Message } }
  }

  $workerIds = @(Get-DescendantIds $proc.Id)   # launcher and the real worker (a grandchild of the app)
  $wpid = ($workerIds -join ',')
  Click $win 'StopCaptureButton'
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
  Log INFO summary "scenario=$Scenario passed=$pass failed=$fail"
}
