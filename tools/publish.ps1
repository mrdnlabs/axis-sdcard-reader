#requires -Version 5.1
<#
.SYNOPSIS
    Reproducible release packaging for Axis SD Card Reader.

.DESCRIPTION
    Produces a STANDALONE single .exe: the .NET runtime, WPF, libVLC (+ its plugins) and an LGPL FFmpeg are
    all bundled inside one file. .NET self-extracts the payload to a per-version cache folder on first run,
    so there is no installer and no directory structure for the user to create.

    Why single-file works here despite libVLC needing real files on disk: IncludeAllContentForSelfExtract
    bundles content (libvlc\**, ffmpeg\**) into the exe and extracts it before Main runs, so
    AppContext.BaseDirectory — which both libVLC's Core.Initialize() and FfmpegExporter.FindFfmpeg()
    resolve against — points at a real directory that contains them.

    FFmpeg is fetched once (pinned URL + SHA-256 verified) into src\AxisSdReader.App\ffmpeg\ (gitignored);
    the csproj copies it to ffmpeg\ffmpeg.exe in the output, exactly where FfmpegExporter looks.

    The zip carries the exe plus the licence texts that must accompany the binaries (MIT + LGPL).

.EXAMPLE
    pwsh tools/publish.ps1 -Version 1.1.0
#>
param(
    [string]$Version = "1.1.0",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$repo       = Split-Path -Parent $PSScriptRoot
$app        = Join-Path $repo "src\AxisSdReader.App\AxisSdReader.App.csproj"
$ffmpegDir  = Join-Path $repo "src\AxisSdReader.App\ffmpeg"
$publishDir = Join-Path $repo "build\publish-single"
$stageDir   = Join-Path $repo "build\stage\AxisSdReader"
$zipPath    = Join-Path $repo "build\AxisSdReader-v$Version-win-x64.zip"

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

# --- 2. Standalone single-file publish -----------------------------------------------------------------
Write-Host "Publishing standalone exe (v$Version, $Configuration)..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $app -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false --nologo -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# --- 3. Stage: the exe plus the licences that must accompany it ----------------------------------------
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item (Join-Path $publishDir "AxisSdReader.App.exe") $stageDir -Force
Copy-Item (Join-Path $repo "LICENSE") (Join-Path $stageDir "LICENSE.txt") -Force
Copy-Item (Join-Path $repo "THIRD-PARTY-NOTICES.md") $stageDir -Force
Copy-Item (Join-Path $ffmpegDir "FFMPEG-LICENSE.txt") $stageDir -Force

# --- 4. Zip + hash --------------------------------------------------------------------------------------
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stageDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$exeMb = [math]::Round((Get-Item (Join-Path $stageDir "AxisSdReader.App.exe")).Length / 1MB, 1)
$zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$hash  = (Get-FileHash $zipPath -Algorithm SHA256).Hash
Write-Host ""
Write-Host "Standalone exe : $exeMb MB (one file, no install, FFmpeg + libVLC inside)"
Write-Host "Package        : $zipPath ($zipMb MB)"
Write-Host "SHA-256        : $hash"
