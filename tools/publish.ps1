#requires -Version 5.1
<#
.SYNOPSIS
    Reproducible release packaging for Axis SD Card Reader.

.DESCRIPTION
    Publishes a self-contained win-x64 build (folder deployment — NOT single-file, because libVLC needs its
    libvlc.dll + plugins/ laid out on disk), copies the LICENSE and THIRD-PARTY-NOTICES into the package for
    LGPL/MIT compliance, zips it, and prints the zip's SHA-256 for out-of-band download verification.

.EXAMPLE
    pwsh tools/publish.ps1 -Version 1.0.1
#>
param(
    [string]$Version = "1.0.1",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$repo       = Split-Path -Parent $PSScriptRoot
$app        = Join-Path $repo "src\AxisSdReader.App\AxisSdReader.App.csproj"
$publishDir = Join-Path $repo "build\publish\AxisSdReader"
$zipPath    = Join-Path $repo "build\AxisSdReader-v$Version-win-x64.zip"

Write-Host "Publishing self-contained win-x64 (v$Version, $Configuration)..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $app -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=false --nologo -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# Ship the app's licence and the third-party attributions inside the package (LGPL/MIT compliance).
Copy-Item (Join-Path $repo "LICENSE") (Join-Path $publishDir "LICENSE.txt") -Force
Copy-Item (Join-Path $repo "THIRD-PARTY-NOTICES.md") $publishDir -Force

Write-Host "Zipping -> $zipPath"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$mb   = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "Package : $zipPath ($mb MB)"
Write-Host "SHA-256 : $hash"
