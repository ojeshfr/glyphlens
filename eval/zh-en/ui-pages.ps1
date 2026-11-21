# Dot-sourced by the drive-*.ps1 / shoot-ui.ps1 scripts. ASCII only.
#
# The GlyphLens main window shows one page at a time (Overview, Capture, Models, Settings) and a collapsed page is not in the UI
# Automation tree. The scripts look elements up by AutomationId; Enter-PageOf selects the page that holds an id (like a user clicking
# the navigation entry) before the lookup, so the scripts stay readable.

function Get-PageOfId([string]$id) {
  if ($id -match '^(Models|ImportModel|RefreshModels|Worker|StartWorkerButton|StopWorkerButton|RestartWorkerButton|RefreshHealth)') { return 'NavModels' }
  if ($id -match '^(ShowOverlay|HideOverlay|OverlayActivityText|OverlayModeMainButton|OverlayPauseMainButton|OverlayShortcutsText|OverlayMainProtectionText|Readiness|Overview)') { return 'NavOverview' }
  if ($id -match '^(Settings|PrivacyText)') { return 'NavSettings' }
  if ($id -match '^(Target|RefreshButton|StartCapture|StopCapture|Capture|Frame|Ocr|ReadText|LiveOcr|CopyText|OpenOcrFolderButton|Translat|CopyOriginal|CopyTranslation)') { return 'NavCapture' }
  return ''
}

function Enter-PageOf($root, [string]$id) {
  $nav = Get-PageOfId $id
  if (-not $nav) { return }
  if ($root.Current.AutomationId -eq 'GlyphLensOverlayWindow') { return }
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $nav)
  $item = $root.FindFirst('Descendants', $cond)
  if (-not $item) { return }
  $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  if (-not $pattern.Current.IsSelected) {
    $pattern.Select(); Start-Sleep -Milliseconds 350
    # the page's elements appear in the UI Automation tree a moment after the page is shown: wait for the one being looked up
    $idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    for ($i = 0; $i -lt 25 -and -not $root.FindFirst('Descendants', $idCond); $i++) { Start-Sleep -Milliseconds 100 }
  }
}

# Keystrokes (SendKeys) go to whatever window is in the foreground. Before walking a list with the keyboard, bring the GlyphLens
# window to the front and verify it is there; otherwise stop instead of typing into some other application.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class UiForeground {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@

function Use-AppWindowForKeys($win) {
  $hwnd = [IntPtr]$win.Current.NativeWindowHandle
  for ($i = 0; $i -lt 6; $i++) {
    # SetForegroundWindow is refused while another process owns the foreground; UI Automation's focus request is allowed.
    if ($i % 2 -eq 0) { try { $win.SetFocus() } catch { } } else { [void][UiForeground]::SetForegroundWindow($hwnd) }
    Start-Sleep -Milliseconds 400
    if ([UiForeground]::GetForegroundWindow() -eq $hwnd) { return }
  }
  throw 'the GlyphLens window could not be brought to the foreground: not sending keystrokes to another application'
}

# Chooses the window whose name matches $like in the capture target list, like a user would: open the list and pick the entry; when the
# list is virtualised (only its first rows exist for UI Automation) walk it with the keyboard, but only if the list really has the
# keyboard focus and the keys visibly change the selection. Returns $false (and sends nothing further) when the window is not found.
function Select-CaptureWindow($win, [string]$like) {
  $combo = Find $win 'TargetSelector'
  $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
  $expand.Expand(); Start-Sleep -Milliseconds 500
  $listItems = $combo.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
  $item = $listItems | Where-Object { $_.Current.Name -like $like } | Select-Object -First 1
  if ($item) {
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 200
    $expand.Collapse(); Start-Sleep -Milliseconds 300
    return $true
  }
  $expand.Collapse(); Start-Sleep -Milliseconds 300
  Use-AppWindowForKeys $win
  $combo.SetFocus(); Start-Sleep -Milliseconds 300
  if (-not $combo.Current.HasKeyboardFocus) { Log WARN window-list 'the target list does not have the keyboard focus: not walking it'; return $false }
  $seen = @(); $stuck = 0
  for ($i = 0; $i -lt 80; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}'); Start-Sleep -Milliseconds 120
    $selection = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    $name = if ($selection.Count -gt 0) { $selection[0].Current.Name } else { '' }
    if ($name -like $like) { return $true }
    if ($seen.Count -gt 0 -and $seen[-1] -eq $name) { $stuck++ } else { $stuck = 0 }
    $seen += $name
    if ($stuck -ge 3) { break }
  }
  Log WARN window-list-walk ((@($seen | Select-Object -Unique) -join ' | '))
  return $false
}

# The worker is started through the venv launcher (python.exe -> python.exe), so it is a GRANDCHILD of GlyphLens.exe. Anything that
# must cover the worker (connections, CPU, "is it gone") has to look at every descendant process, not only the direct children.
function Get-DescendantIds([int]$rootPid) {
  $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
  $found = @(); $queue = @($rootPid)
  while ($queue.Count) {
    $cur = $queue[0]; $queue = @($queue | Select-Object -Skip 1)
    foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $cur })) { $found += [int]$c.ProcessId; $queue += [int]$c.ProcessId }
  }
  return $found
}
