<#
.SYNOPSIS
  Renders Chinese test "application" windows and either screenshots them (real screen capture) or keeps one open.

.DESCRIPTION
  Reads ocr-scenes.json (layouts) and dataset.json (texts). Each scene is a real top-most Windows Forms window drawn with
  ClearType text, the way an ordinary application draws it. Mode Capture asks Windows for the window's own pixels
  (PrintWindow with PW_RENDERFULLCONTENT, so other windows on top cannot get into the picture) into PNG files and writes truth.json (the lines that were drawn, in reading order). Mode Show keeps one scene open under a
  given window title so that GlyphLens can capture it with Windows Graphics Capture.

  The file is ASCII on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI); all Chinese text lives in the JSON files.

.EXAMPLE
  powershell -File eval\zh-en\render-scenes.ps1 -Mode Capture -OutDir eval\zh-en\screenshots
  powershell -File eval\zh-en\render-scenes.ps1 -Mode Show -SceneName chat-light -Title GlyphLensChineseTarget
#>
param(
  [ValidateSet('Capture', 'Show')][string]$Mode = 'Capture',
  [string]$OutDir = (Join-Path $PSScriptRoot 'screenshots'),
  [string]$SceneName,
  [string]$Title = 'GlyphLensChineseTarget',
  [int]$ShowSeconds = 600,
  [int]$ProgressiveSeconds = 0,   # Show mode: reveal one more item every N seconds (a chat that receives new messages); 0 = all at once
  [int]$InitialVisible = 4,       # with -ProgressiveSeconds: how many items are shown at first
  [string]$RevealWhenFileExists   # with -ProgressiveSeconds: do not reveal anything until this file exists (the test creates it)
)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class DpiFix {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@
[void][DpiFix]::SetProcessDPIAware()   # draw and capture in physical pixels, like a modern per-monitor-aware app

function Read-Json($path) { [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json }
function ToColor($hex) { [Drawing.ColorTranslator]::FromHtml($hex) }

$dataset = (Read-Json (Join-Path $PSScriptRoot 'dataset.json')).items
$byId = @{}; foreach ($i in $dataset) { $byId[$i.id] = $i }
$scenes = (Read-Json (Join-Path $PSScriptRoot 'ocr-scenes.json')).scenes
if ($SceneName) { $scenes = @($scenes | Where-Object { $_.name -eq $SceneName }) }

function New-SceneForm($scene) {
  $form = New-Object Windows.Forms.Form
  $form.Text = $Title
  $form.FormBorderStyle = $(if ($Mode -eq 'Capture') { 'None' } else { 'FixedSingle' })
  $form.StartPosition = 'Manual'
  $form.Location = New-Object Drawing.Point(40, 40)
  $form.ClientSize = New-Object Drawing.Size($scene.w, $scene.h)
  $form.TopMost = $true
  $form.ShowInTaskbar = $true
  $form.BackColor = ToColor $scene.bg.c1
  $form.GetType().GetProperty('DoubleBuffered', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($form, $true, $null)

  # Resolve every item to final text lines once; the same list is the ground truth.
  $items = @()
  foreach ($it in $scene.items) {
    $lines = @()
    if ($it.lines) { $lines = @($it.lines) }
    elseif ($it.ref) {
      $src = $byId[$it.ref]
      $script = if ($scene.script) { $scene.script } else { 'zh' }
      $lines = @($(if ($script -eq 'zt' -and $src.zt) { $src.zt } else { $src.zh }))
    }
    else { $lines = @($it.text) }
    $items += [pscustomobject]@{ item = $it; lines = $lines }
  }
  $form | Add-Member -NotePropertyName Resolved -NotePropertyValue $items
  $visible = if (($ProgressiveSeconds -gt 0) -and ($Mode -eq 'Show')) { [Math]::Min($InitialVisible, $items.Count) } else { $items.Count }
  $form | Add-Member -NotePropertyName VisibleCount -NotePropertyValue $visible
  $form | Add-Member -NotePropertyName SceneSpec -NotePropertyValue $scene

  $form.Add_Paint({
    param($sender, $e)
    $scene = $sender.SceneSpec
    $g = $e.Graphics
    $g.TextRenderingHint = [Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    if ($scene.bg.c2) {
      $brush = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Rectangle(0, 0, $scene.w, $scene.h)), (ToColor $scene.bg.c1), (ToColor $scene.bg.c2), 45.0)
      $g.FillRectangle($brush, 0, 0, $scene.w, $scene.h)
    }
    $margin = [int]$scene.margin; $gap = [int]$scene.gap; $y = $margin
    $shown = 0
    foreach ($r in $sender.Resolved) {
      if ($shown -ge $sender.VisibleCount) { break }
      $shown++
      $it = $r.item
      $px = if ($it.px) { [int]$it.px } else { [int]$scene.px }
      $fontName = if ($it.font) { $it.font } else { $scene.font }
      $style = if ($it.bold) { [Drawing.FontStyle]::Bold } else { [Drawing.FontStyle]::Regular }
      $font = New-Object Drawing.Font($fontName, $px, $style, [Drawing.GraphicsUnit]::Pixel)
      $fg = ToColor $(if ($it.fg) { $it.fg } else { $scene.fg })
      $flags = [Windows.Forms.TextFormatFlags]::NoPadding -bor [Windows.Forms.TextFormatFlags]::NoPrefix
      if ($it.vertical) {
        # Vertical writing: one character per line, top to bottom.
        $x = if ($it.x) { [int]$it.x } else { $margin }
        $chars = $r.lines[0].ToCharArray(); $cy = $y
        foreach ($c in $chars) { [Windows.Forms.TextRenderer]::DrawText($g, [string]$c, $font, (New-Object Drawing.Point($x, $cy)), $fg, $flags); $cy += [int]($px * 1.15) }
        $y = $cy + $gap; continue
      }
      $lineH = [int]($px * 1.4)
      $widest = 0
      foreach ($l in $r.lines) { $w = [Windows.Forms.TextRenderer]::MeasureText($g, $l, $font, (New-Object Drawing.Size(4000, 200)), $flags).Width; if ($w -gt $widest) { $widest = $w } }
      $pad = if ($it.bubble) { [int]($px * 0.7) } else { 0 }
      $boxW = $widest + 2 * $pad; $boxH = $lineH * $r.lines.Count + 2 * $pad
      $x = if ($it.x) { [int]$it.x } elseif ($it.align -eq 'right') { $scene.w - $margin - $boxW } else { $margin }
      if ($it.bubble) {
        $bb = New-Object Drawing.SolidBrush((ToColor $it.bubble))
        $g.FillRectangle($bb, $x, $y, $boxW, $boxH)
      }
      $ly = $y + $pad
      foreach ($l in $r.lines) {
        [Windows.Forms.TextRenderer]::DrawText($g, $l, $font, (New-Object Drawing.Point(($x + $pad), $ly)), $fg, $flags)
        $ly += $lineH
      }
      $y += $boxH + $gap
    }
  })
  return $form
}

if ($Mode -eq 'Show') {
  $form = New-SceneForm $scenes[0]
  $form.Show()
  $deadline = (Get-Date).AddSeconds($ShowSeconds)
  $nextReveal = (Get-Date).AddSeconds([Math]::Max(1, $ProgressiveSeconds))
  while ($form.Visible -and (Get-Date) -lt $deadline) {
    [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50
    if ($RevealWhenFileExists -and -not (Test-Path $RevealWhenFileExists)) { $nextReveal = (Get-Date).AddSeconds([Math]::Max(1, $ProgressiveSeconds)); continue }
    if (($ProgressiveSeconds -gt 0) -and ((Get-Date) -ge $nextReveal) -and ($form.VisibleCount -lt $form.Resolved.Count)) {
      $form.VisibleCount = $form.VisibleCount + 1; $form.Invalidate(); $nextReveal = (Get-Date).AddSeconds($ProgressiveSeconds)
    }
  }
  $form.Close()
  return
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$truth = [ordered]@{}
foreach ($scene in $scenes) {
  $form = New-SceneForm $scene
  $form.Show()
  $end = (Get-Date).AddMilliseconds(900)
  while ((Get-Date) -lt $end) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 30 }
  $bmp = New-Object Drawing.Bitmap($form.Width, $form.Height)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc()
  [void][DpiFix]::PrintWindow($form.Handle, $dc, 2)
  $g.ReleaseHdc($dc)
  $g.Dispose()
  $file = Join-Path $OutDir ($scene.name + '.png')
  $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  $lines = @(); foreach ($r in $form.Resolved) { $lines += $r.lines }
  $truth[$scene.name] = [ordered]@{ file = ($scene.name + '.png'); category = $scene.category; script = $(if ($scene.script) { $scene.script } else { 'zh' }); lines = $lines }
  $form.Close(); $form.Dispose()
  Write-Output ("captured {0}  {1}x{2}" -f $scene.name, $scene.w, $scene.h)
}
$json = $truth | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $OutDir 'truth.json'), $json, (New-Object Text.UTF8Encoding($false)))
