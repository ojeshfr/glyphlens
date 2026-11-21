<#
.SYNOPSIS
  Measures incremental OCR in the real app: a Chinese chat window that receives a new message every few seconds is read in Live mode.

.DESCRIPTION
  Starts the chat-progressive scene (4 messages visible, one more revealed every -RevealSeconds once the app is reading Live), starts
  GlyphLens.exe with a models folder that has the OCR models and the zh_en package, captures the window, turns translation and Live
  reading on (the local Chinese OCR is selected by the app itself) and logs every recognition pass: its text (full / partial /
  unchanged), duration and line count, plus the CPU time the app and its worker used while Live was running. At the end it copies the
  recognised original and the translation. Run it twice, once normally and once with -FullOcr (GLYPHLENS_FULL_OCR=1: every frame is read
  completely), and compare the JSON files: the recognised text must be identical, the work must differ.

  The file is ASCII on purpose; Chinese is matched with Unicode ranges.
#>
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$ModelsDir,
  [Parameter(Mandatory)][string]$Out,
  [switch]$FullOcr,
  [int]$RevealSeconds = 7,
  [int]$TailSeconds = 12
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PW8 { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
$AE = [System.Windows.Automation.AutomationElement]
. (Join-Path $PSScriptRoot 'ui-pages.ps1')
$han = New-Object System.Text.RegularExpressions.Regex(('[' + [char]0x3400 + '-' + [char]0x9fff + ']'))
function Log($s, $n, $d) { "{0,-6} {1} :: {2}" -f $s, $n, $d }
function Find($root, $id) { Enter-PageOf $root $id; $root.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function Txt($root, $id) { $e = Find $root $id; if ($e) { $e.Current.Name } else { "<missing $id>" } }
function Click($root, $id) { (Find $root $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Until($sb, $sec = 30) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $sec) { if (& $sb) { return $true }; Start-Sleep -Milliseconds 150 }; return (& $sb) }
function SelectedOcr($win) { $c = Find $win 'OcrLanguageSelector'; $s = $c.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection(); if ($s.Count -gt 0) { $s[0].Current.Name } else { '' } }
function CpuSeconds($pids) { $sum = 0.0; foreach ($p in $pids) { $pr = Get-Process -Id $p -ErrorAction SilentlyContinue; if ($pr) { $sum += $pr.TotalProcessorTime.TotalSeconds } }; return $sum }

if ($FullOcr) { $env:GLYPHLENS_FULL_OCR = '1' } else { Remove-Item Env:GLYPHLENS_FULL_OCR -ErrorAction SilentlyContinue }
$env:GLYPHLENS_MODELS_DIR = $ModelsDir
$trigger = Join-Path ([IO.Path]::GetTempPath()) ('glyphlens-reveal-' + [Guid]::NewGuid().ToString('N') + '.flag')
$renderer = Join-Path $PSScriptRoot 'render-scenes.ps1'
$target = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $renderer, '-Mode', 'Show', '-SceneName', 'chat-progressive', '-Title', 'GlyphLensChineseTarget', '-ProgressiveSeconds', $RevealSeconds, '-InitialVisible', 4, '-RevealWhenFileExists', $trigger -PassThru
$null = Until { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' }).Count -ge 1 } 30
Start-Sleep 2
$proc = Start-Process $Exe -PassThru
$pass = 0; $fail = 0
function Check($ok, $name, $detail) { if ($ok) { $script:pass++; Log PASS $name $detail } else { $script:fail++; Log FAIL $name $detail } }
$passes = New-Object System.Collections.ArrayList
try {
  $win = $null
  $null = Until { $script:win = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); $null -ne $script:win } 25
  Start-Sleep 2

  # capture the window
  (Find $win 'TargetKindWindow').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
  Click $win 'RefreshButton'; Start-Sleep 1
  $found = Select-CaptureWindow $win '*GlyphLensChineseTarget*'
  Check $found select-window 'the chat window was selected'
  if (-not $found) { throw 'the test window is not in the target list: not starting a capture of any other window' }
  Click $win 'StartCaptureButton'
  Check (Until { (Txt $win 'CaptureStatusText') -eq 'Capturing' } 15) capture "frame=$(Txt $win 'FrameSizeText')"

  # translation on -> worker starts; the app selects the local Chinese OCR itself because a Chinese model is installed
  (Find $win 'TranslateToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  Check (Until { (Txt $win 'WorkerStatusText') -eq 'Ready' } 90) worker-started "worker=$(Txt $win 'WorkerStatusText')"
  $auto = Until { (SelectedOcr $win) -like '*Chinese*' } 30
  Check $auto ocr-auto-selected "ocr='$(SelectedOcr $win)'"
  $null = Until { (Txt $win 'ModelsSummaryText') -like '*installed*' } 30

  # warm the engine (first read loads the models) before measuring
  Click $win 'ReadTextButton'
  $null = Until { (Txt $win 'OcrStatusText') -match '\d+ lines' } 120
  Start-Sleep -Milliseconds 500

  # Live reading while the chat receives messages
  # the worker is a grandchild of the app (venv launcher -> python): measure every descendant, separately from the app
  $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
  $workerIds = @(); $queue = @($proc.Id)
  while ($queue.Count) { $cur = $queue[0]; $queue = @($queue | Select-Object -Skip 1); foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $cur })) { $workerIds += $c.ProcessId; $queue += $c.ProcessId } }
  $pids = @($proc.Id) + $workerIds
  $cpu0 = CpuSeconds $pids
  $appCpu0 = CpuSeconds @($proc.Id); $workerCpu0 = CpuSeconds $workerIds
  $clock = [Diagnostics.Stopwatch]::StartNew()
  (Find $win 'LiveOcrToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  Set-Content -Path $trigger -Value 'go'   # the chat starts revealing messages now
  $total = 11 - 4
  $deadline = [Diagnostics.Stopwatch]::StartNew()
  $last = ''
  $limit = ($total * $RevealSeconds) + $TailSeconds
  $netSamples = 0; $netRemote = 0; $netUdp = 0; $netSeen = New-Object System.Collections.Generic.HashSet[string]; $nextNet = 0
  while ($deadline.Elapsed.TotalSeconds -lt $limit) {
    # every ~2 s while recognition and translation are running: which connections do the app and ALL its worker processes have?
    if ($deadline.Elapsed.TotalSeconds -ge $nextNet) {
      $nextNet = $deadline.Elapsed.TotalSeconds + 2
      $remote = @(Get-NetTCPConnection -OwningProcess $pids -ErrorAction SilentlyContinue | Where-Object { $_.RemoteAddress -notin '127.0.0.1', '::1', '0.0.0.0', '::' })
      $udpNow = @(Get-NetUDPEndpoint -OwningProcess $pids -ErrorAction SilentlyContinue)
      $netSamples++; $netRemote += $remote.Count; $netUdp += $udpNow.Count
      foreach ($c in $remote) { [void]$netSeen.Add("$($c.RemoteAddress):$($c.RemotePort)") }
    }
    $s = Txt $win 'OcrStatusText'
    if ($s -ne $last -and $s -match '(\d+) lines') {
      [void]$passes.Add([pscustomobject]@{ t = [math]::Round($clock.Elapsed.TotalSeconds, 1); status = $s })
      $last = $s
    }
    Start-Sleep -Milliseconds 200
  }
  $cpu = [math]::Round((CpuSeconds $pids) - $cpu0, 1)
  $appCpu = [math]::Round((CpuSeconds @($proc.Id)) - $appCpu0, 1); $workerCpu = [math]::Round((CpuSeconds $workerIds) - $workerCpu0, 1)
  $liveSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
  (Find $win 'LiveOcrToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  Start-Sleep 2

  Check (($netSamples -ge 10) -and ($netRemote -eq 0) -and ($netUdp -eq 0)) no-remote-during-live "samples=$netSamples processes=$($pids.Count) remoteConnectionsSeen=$netRemote udpEndpointsSeen=$netUdp $(($netSeen | Select-Object -First 3) -join ',')"

  # results
  Click $win 'CopyOriginalButton'; Start-Sleep -Milliseconds 400
  $original = (Get-Clipboard -Raw)
  Click $win 'CopyTranslationButton'; Start-Sleep -Milliseconds 400
  $translation = (Get-Clipboard -Raw)
  $english = -not $han.IsMatch($translation)
  Check (($original -split "`r?`n" | Where-Object { $_.Trim() }).Count -ge 11) all-messages-read "originalLines=$(@($original -split "`r?`n" | Where-Object { $_.Trim() }).Count)"
  Check ($english -and $translation.Length -gt 100) translated "translationChars=$($translation.Length)"

  $ms = @($passes | ForEach-Object { if ($_.status -match '(\d+) ms') { [int]$Matches[1] } })
  $partial = @($passes | Where-Object { $_.status -match 'updated' })
  $unchanged = @($passes | Where-Object { $_.status -match 'unchanged' })
  Log INFO live "mode=$(if ($FullOcr) { 'full-only' } else { 'incremental' }) seconds=$liveSeconds cpuSeconds=$cpu app=$appCpu worker=$workerCpu passes=$($passes.Count) partial=$($partial.Count) unchanged=$($unchanged.Count)"
  $result = [ordered]@{ mode = $(if ($FullOcr) { 'full-only' } else { 'incremental' }); liveSeconds = $liveSeconds; cpuSeconds = $cpu; appCpuSeconds = $appCpu; workerCpuSeconds = $workerCpu; passes = $passes; original = $original; translation = $translation }
  [IO.File]::WriteAllText($Out, ($result | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
  Log INFO written $Out

  $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  Check ($proc.WaitForExit(30000) -and $proc.ExitCode -eq 0) shutdown "code=$($proc.ExitCode)"
}
catch { Log FAIL driver $_.Exception.Message; $fail++ }
finally {
  if (-not $proc.HasExited) { $proc.Kill(); Log WARN cleanup 'app killed' }
  Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' } | Stop-Process -Force -ErrorAction SilentlyContinue
  if (-not $target.HasExited) { $target.Kill() }
  Remove-Item $trigger -ErrorAction SilentlyContinue
  Log INFO summary "passed=$pass failed=$fail"
}
