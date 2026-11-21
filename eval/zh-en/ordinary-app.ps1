<#
.SYNOPSIS
  A minimal ordinary Windows application (a window with a text box) used to check that the GlyphLens overlay stays above it and
  lets mouse clicks and typing through in pass-through mode.
#>
param([string]$Title = 'GlyphLensOrdinaryApp', [int]$ShowSeconds = 600)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = $Title
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(300, 200)
$form.ClientSize = New-Object System.Drawing.Size(520, 520)
$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true
$box.Dock = 'Fill'
$box.Font = New-Object System.Drawing.Font('Consolas', 14)
$box.AccessibleName = 'OrdinaryAppText'
$form.Controls.Add($box)
$form.Show()
$deadline = (Get-Date).AddSeconds($ShowSeconds)
while ($form.Visible -and (Get-Date) -lt $deadline) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 40 }
$form.Close()
