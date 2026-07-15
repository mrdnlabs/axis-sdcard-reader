#requires -Version 5.1
<#
.SYNOPSIS
    Reproducible release packaging for Axis SD Card Reader. Produces TWO artifacts.

.DESCRIPTION
    Both artifacts contain the same code, self-contained (.NET runtime + WPF), with libVLC and an LGPL
    FFmpeg bundled — no prerequisites, no PATH lookup, nothing for the user to install:

      1. STANDALONE  — one AxisSdReader.App.exe. Everything is inside it; .NET self-extracts the payload to
                       a per-version cache on first run (~529 MB / 1,720 files: ~6 s on an SSD, but 20-60 s+
                       on an HDD, with NO progress UI possible — extraction happens in the native host
                       before Main runs). Great on any modern SSD.
      2. FOLDER      — the classic folder deploy. Nothing to extract, so it launches instantly every time
                       and leaves nothing in %TEMP%. The right pick for HDDs and IT rollouts.

    Why single-file works at all despite libVLC needing real files on disk: IncludeAllContentForSelfExtract
    bundles content (libvlc\**, ffmpeg\**) into the exe and extracts it before Main, so
    AppContext.BaseDirectory — which both libVLC's Core.Initialize() and FfmpegExporter.FindFfmpeg() resolve
    against — points at a real directory containing them. Verified on hardware.

.EXAMPLE
    pwsh tools/publish.ps1 -Version 1.1.0
#>
param(
    [string]$Version = "1.1.0",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$repo      = Split-Path -Parent $PSScriptRoot
$app       = Join-Path $repo "src\AxisSdReader.App\AxisSdReader.App.csproj"
$ffmpegDir = Join-Path $repo "src\AxisSdReader.App\ffmpeg"
$singleDir = Join-Path $repo "build\publish-single"
$stageSolo = Join-Path $repo "build\stage\standalone\AxisSdReader"
$stageDir  = Join-Path $repo "build\stage\folder\AxisSdReader"
$zipSolo   = Join-Path $repo "build\AxisSdReader-v$Version-win-x64-standalone.zip"
$zipFolder = Join-Path $repo "build\AxisSdReader-v$Version-win-x64-folder.zip"

# Licence texts that must accompany the binaries (our MIT + the bundled LGPL components).
function Add-Licences([string]$dir) {
    Copy-Item (Join-Path $repo "LICENSE") (Join-Path $dir "LICENSE.txt") -Force
    Copy-Item (Join-Path $repo "THIRD-PARTY-NOTICES.md") $dir -Force
    Copy-Item (Join-Path $ffmpegDir "FFMPEG-LICENSE.txt") $dir -Force
}

function New-Zip([string]$sourceDir, [string]$zipPath) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $sourceDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
}

# --- 1. Bundled FFmpeg ---------------------------------------------------------------------------------
# An LGPL v3 build: configured WITHOUT --enable-gpl and with --disable-libx264/--disable-libx265, so it
# carries no GPL components (we only ever run `-c copy`, so no encoders are needed). BtbN is one of the
# Windows builders linked from ffmpeg.org.
#
# NOTE: BtbN REBUILDS the "latest" tag, so this hash will eventually go stale by design. A mismatch means the
# upstream binary changed: re-verify the build is still LGPL (`ffmpeg -version` must show no --enable-gpl)
# before shipping it, then update $ffSha below.
$ffUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n7.1-latest-win64-lgpl-7.1.zip"
$ffSha = "69D3FBA3A520B55182506CB1E0E5F3A73BE22D08D40E1DE041BA626961719CE1"
$ffExe = Join-Path $ffmpegDir "ffmpeg.exe"

if (Test-Path $ffExe) {
    Write-Host "FFmpeg already present ($([math]::Round((Get-Item $ffExe).Length/1MB,1)) MB): $ffExe"
} else {
    Write-Host "Fetching LGPL FFmpeg (~133 MB)..."
    New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
    $tmpZip = Join-Path $env:TEMP "axis-ffmpeg-lgpl.zip"
    $tmpDir = Join-Path $env:TEMP "axis-ffmpeg-lgpl"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $ffUrl -OutFile $tmpZip -UseBasicParsing

    $got = (Get-FileHash $tmpZip -Algorithm SHA256).Hash
    if ($got -ne $ffSha) {
        throw ("FFmpeg SHA-256 mismatch.`n  expected $ffSha`n  got      $got`n" +
               "Upstream rebuilt the 'latest' asset. Re-verify the build is still LGPL (no --enable-gpl) " +
               "and then update `$ffSha in tools\publish.ps1.")
    }

    if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
    Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force
    Copy-Item (Get-ChildItem $tmpDir -Recurse -Filter ffmpeg.exe | Select-Object -First 1).FullName $ffExe -Force
    Copy-Item (Get-ChildItem $tmpDir -Recurse -Filter LICENSE.txt | Select-Object -First 1).FullName `
              (Join-Path $ffmpegDir "FFMPEG-LICENSE.txt") -Force
    Remove-Item $tmpZip -Force
    Remove-Item $tmpDir -Recurse -Force
    Write-Host "FFmpeg bundled ($([math]::Round((Get-Item $ffExe).Length/1MB,1)) MB)"
}

# --- 2. Standalone (single self-extracting exe) --------------------------------------------------------
Write-Host "Publishing STANDALONE exe (v$Version, $Configuration)..."
if (Test-Path $singleDir) { Remove-Item $singleDir -Recurse -Force }
dotnet publish $app -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false --nologo -o $singleDir
if ($LASTEXITCODE -ne 0) { throw "standalone publish failed (exit $LASTEXITCODE)" }

if (Test-Path $stageSolo) { Remove-Item $stageSolo -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageSolo | Out-Null
Copy-Item (Join-Path $singleDir "AxisSdReader.App.exe") $stageSolo -Force
Add-Licences $stageSolo
New-Zip $stageSolo $zipSolo

# --- 3. Folder deploy (no extraction — instant launch, HDD/IT friendly) --------------------------------
Write-Host "Publishing FOLDER deploy (v$Version, $Configuration)..."
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
dotnet publish $app -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=false --nologo -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "folder publish failed (exit $LASTEXITCODE)" }

Add-Licences $stageDir
New-Zip $stageDir $zipFolder

# --- 4. Report ------------------------------------------------------------------------------------------
$exeMb = [math]::Round((Get-Item (Join-Path $stageSolo "AxisSdReader.App.exe")).Length / 1MB, 1)
Write-Host ""
Write-Host "STANDALONE  one $exeMb MB exe - self-extracts on first run (~6s SSD, slower on HDD)"
Write-Host "  package : $zipSolo ($([math]::Round((Get-Item $zipSolo).Length/1MB,1)) MB)"
Write-Host "  sha256  : $((Get-FileHash $zipSolo -Algorithm SHA256).Hash)"
Write-Host ""
Write-Host "FOLDER      $((Get-ChildItem $stageDir -Recurse -File | Measure-Object).Count) files - no extraction, instant launch, nothing left in %TEMP%"
Write-Host "  package : $zipFolder ($([math]::Round((Get-Item $zipFolder).Length/1MB,1)) MB)"
Write-Host "  sha256  : $((Get-FileHash $zipFolder -Algorithm SHA256).Hash)"
