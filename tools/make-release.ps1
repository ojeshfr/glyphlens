<#
.SYNOPSIS
  Builds the preview release zip: a framework-dependent win-x64 publish of the app, the worker source, the licence files and a short
  README, plus a SHA-256 file. Nothing is uploaded or published; the output goes to artifacts\release (ignored by Git).

.EXAMPLE
  powershell -File tools\make-release.ps1 -Version 0.1.0-preview
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0-preview',
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'artifacts\release' }
$name = "GlyphLens-$Version-win-x64"
$stage = Join-Path $OutDir "stage"
$folder = Join-Path $stage $name
$zip = Join-Path $OutDir "$name.zip"

if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $folder -Force | Out-Null
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }

# 1. The app (framework-dependent: the user installs the .NET 10 Desktop Runtime).
& dotnet publish (Join-Path $root 'src\GlyphLens.App\GlyphLens.App.csproj') -c Release -r win-x64 --self-contained false `
    -p:DebugType=none -p:DebugSymbols=false -p:Version=$Version -o $folder
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Get-ChildItem -LiteralPath $folder -Recurse -File |
    Where-Object { $_.Extension -in '.xml', '.pdb' } |
    Remove-Item -Force
if (-not (Test-Path -LiteralPath (Join-Path $folder 'GlyphLens.exe'))) { throw 'GlyphLens.exe is missing from the publish output.' }

# 2. The worker source (standard library only; engines are installed by the user).
$worker = Join-Path $folder 'worker'
New-Item -ItemType Directory -Path $worker -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'worker\glyphlens_worker') -Destination $worker -Recurse
Get-ChildItem -LiteralPath $worker -Directory -Recurse -Filter '__pycache__' | Remove-Item -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'worker\pyproject.toml'), (Join-Path $root 'worker\README.md') -Destination $worker

# 3. Licence files and the short user README.
foreach ($f in 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item -LiteralPath (Join-Path $root $f) -Destination $folder
}
Copy-Item -LiteralPath (Join-Path $root 'tools\release\README-PREVIEW.txt') -Destination (Join-Path $folder 'README-PREVIEW.txt')

# 4. Zip (forward-slash entries) and checksum.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $prefix = (Resolve-Path -LiteralPath $stage).Path.TrimEnd('\') + '\'
    foreach ($file in (Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName)) {
        $entryName = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$name.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii -NoNewline

$size = (Get-Item -LiteralPath $zip).Length
"{0}`n  size   {1:N0} bytes`n  sha256 {2}" -f $zip, $size, $hash
