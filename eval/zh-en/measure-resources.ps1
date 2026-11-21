<#
.SYNOPSIS
  Measures start-up times, memory and CPU of the real GlyphLens app and its worker on this machine (one run, not a benchmark).

.DESCRIPTION
  Starts the Chinese test window and GlyphLens.exe, captures the window, turns translation on and records:
    - time from "translation on" until the worker is Ready (cold start of Python, imports and the engines' state check),
    - time of the first recognition (cold: the OCR models are loaded) and of a second one (warm), as shown in the OCR status line,
    - working set (memory) of the app and of the worker after the first read,
    - CPU seconds used by the app and by the worker while idle (capture running, nothing read) and during Live reading of a still screen.
  Output is a small JSON file. ASCII only.
#>
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$ModelsDir,
  [Parameter(Mandatory)][string]$Out,
  [int]$WindowSeconds = 20
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
$AE = [System.Windows.Automation.AutomationElement]
. (Join-Path $PSScriptRoot 'ui-pages.ps1')
function Log($s, $n, $d) { "{0,-6} {1} :: {2}" -f $s, $n, $d }
function Find($root, $id) { Enter-PageOf $root $id; $root.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function Txt($root, $id) { $e = Find $root $id; if ($e) { $e.Current.Name } else { "<missing $id>" } }
function Click($root, $id) { (Find $root $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Until($sb, $sec = 30) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $sec) { if (& $sb) { return $true }; Start-Sleep -Milliseconds 150 }; return (& $sb) }
# The worker is started through the venv launcher, so it is a grandchild of the app: measure every descendant process of the app.
function Descendants($rootPid) {
  $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
  $found = @(); $queue = @($rootPid)
  while ($queue.Count) { $cur = $queue[0]; $queue = $queue[1..($queue.Count)]; foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $cur })) { $found += $c.ProcessId; $queue += $c.ProcessId } }
  return $found
}
function Cpu($ids) { $sum = 0.0; foreach ($i in @($ids)) { $p = Get-Process -Id $i -ErrorAction SilentlyContinue; if ($p) { $sum += $p.TotalProcessorTime.TotalSeconds } }; return $sum }
function MemMb($ids) { $sum = 0.0; foreach ($i in @($ids)) { $p = Get-Process -Id $i -ErrorAction SilentlyContinue; if ($p) { $sum += $p.WorkingSet64 } }; return [math]::Round($sum / 1MB, 0) }

$env:GLYPHLENS_MODELS_DIR = $ModelsDir
$renderer = Join-Path $PSScriptRoot 'render-scenes.ps1'
$target = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $renderer, '-Mode', 'Show', '-SceneName', 'chat-light', '-Title', 'GlyphLensChineseTarget' -PassThru
$null = Until { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' }).Count -ge 1 } 30
Start-Sleep 2
$proc = Start-Process $Exe -PassThru
$r = [ordered]@{ machine = "$([Environment]::OSVersion.VersionString), $((Get-CimInstance Win32_Processor | Select-Object -First 1).Name), $([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)) GB RAM" }
try {
  $win = $null
  $null = Until { $script:win = $AE::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))); $null -ne $script:win } 25
  Start-Sleep 2
  $r.appIdleMemMb = MemMb $proc.Id
  (Find $win 'TargetKindWindow').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
  Click $win 'RefreshButton'; Start-Sleep 1
  if (-not (Select-CaptureWindow $win '*GlyphLensChineseTarget*')) { throw 'the test window is not in the target list' }
  Click $win 'StartCaptureButton'
  $null = Until { (Txt $win 'CaptureStatusText') -eq 'Capturing' } 15

  # worker cold start
  $t0 = [Diagnostics.Stopwatch]::StartNew()
  (Find $win 'TranslateToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  $null = Until { (Txt $win 'WorkerStatusText') -eq 'Ready' } 120
  $r.workerStartSeconds = [math]::Round($t0.Elapsed.TotalSeconds, 1)
  $null = Until { (Txt $win 'OcrLanguageSelector') -ne '' } 5
  Start-Sleep 3
  $worker = @(Descendants $proc.Id)
  $r.workerProcesses = $worker.Count
  $r.workerIdleMemMb = MemMb $worker

  # first (cold) and second (warm) recognition
  Click $win 'ReadTextButton'
  $null = Until { (Txt $win 'OcrStatusText') -match '\d+ lines' } 120
  $r.firstReadStatus = (Txt $win 'OcrStatusText') -replace '[^\x20-\x7e]', '.'
  Start-Sleep 2
  Click $win 'ReadTextButton'; Start-Sleep 4
  $r.secondReadStatus = (Txt $win 'OcrStatusText') -replace '[^\x20-\x7e]', '.'
  $r.appMemMbAfterRead = MemMb $proc.Id
  $r.workerMemMbAfterRead = MemMb $worker

  # CPU while idle (capture running, nothing is read)
  $a0 = Cpu $proc.Id; $w0 = Cpu $worker; Start-Sleep $WindowSeconds
  $r.idle = [ordered]@{ seconds = $WindowSeconds; appCpuSeconds = [math]::Round((Cpu $proc.Id) - $a0, 2); workerCpuSeconds = [math]::Round((Cpu $worker) - $w0, 2) }

  # CPU during Live reading of a still screen
  (Find $win 'LiveOcrToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  Start-Sleep 3
  $a0 = Cpu $proc.Id; $w0 = Cpu $worker; Start-Sleep $WindowSeconds
  $r.liveStill = [ordered]@{ seconds = $WindowSeconds; appCpuSeconds = [math]::Round((Cpu $proc.Id) - $a0, 2); workerCpuSeconds = [math]::Round((Cpu $worker) - $w0, 2); status = ((Txt $win 'OcrStatusText') -replace '[^\x20-\x7e]', '.') }
  (Find $win 'LiveOcrToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  [IO.File]::WriteAllText($Out, ($r | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
  Log INFO result ($r | ConvertTo-Json -Compress -Depth 4)
  $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  [void]$proc.WaitForExit(30000)
}
catch { Log FAIL driver $_.Exception.Message }
finally {
  if (-not $proc.HasExited) { $proc.Kill() }
  Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'GlyphLensChineseTarget' } | Stop-Process -Force -ErrorAction SilentlyContinue
  if (-not $target.HasExited) { $target.Kill() }
}
