#requires -Version 5.1
<#
.SYNOPSIS
    Reproducible release packaging for Axis SD Card Reader.

.DESCRIPTION
    Produces the FOLDER package: AxisSdReader.exe (the native launcher and .NET host, the only way in), the app
    (AxisSdReader.App.dll, with no exe of its own) and the MCP server for AI agents (AxisSdReader.Mcp.exe) side by
    side, self-contained (.NET runtime + WPF), with a reduced libVLC and an LGPL FFmpeg bundled - no
    prerequisites, no PATH lookup, nothing to install, nothing extracted at run time.

    The app needs administrator rights, and gets them only through AxisSdReader.exe: native code that replaces its
    environment with one built from scratch before it loads the runtime, so variables any program running as the
    user can set (profiler DLLs, dump paths) never reach the elevated .NET runtime. The publish's own
    AxisSdReader.App.exe is deleted: an exe like that could be elevated some other way with the user's environment.

    Install it somewhere only administrators can change - e.g. "C:\Program Files\Axis SD Card Reader". A copy
    in a folder the user can write to (Downloads, Desktop) lets any program running as that user replace its
    files and gain administrator rights; the app checks its folder (and everything in it) at startup and warns.

    Refuses to package a working tree whose build inputs have uncommitted changes, or a -Version that differs
    from Directory.Build.props (override with -AllowDirty for local experiments), and verifies the result
    before zipping it: no symbols, no createdump, the expected manifests, the pinned FFmpeg, only x64 libVLC.

.EXAMPLE
    powershell -File tools\publish.ps1 -Version 1.2.0
#>
param(
    [string]$Version = "1.2.0",
    [string]$Configuration = "Release",
    # The oldest .NET runtime patch this release may ship (bump with each runtime security update).
    [string]$MinRuntime = "10.0.15",
    [switch]$AllowDirty
)
$ErrorActionPreference = "Stop"

$repo      = Split-Path -Parent $PSScriptRoot
$app       = Join-Path $repo "src\AxisSdReader.App\AxisSdReader.App.csproj"
$mcp       = Join-Path $repo "src\AxisSdReader.Mcp\AxisSdReader.Mcp.csproj"
$ffmpegDir = Join-Path $repo "src\AxisSdReader.App\ffmpeg"
$stageDir  = Join-Path $repo "build\stage\folder\AxisSdReader"
$zipName   = "AxisSdReader-v$Version-win-x64-folder.zip"
$zipFolder = Join-Path $repo "build\$zipName"
$sums      = Join-Path $repo "build\SHA256SUMS.txt"

# --- 0. Guards -----------------------------------------------------------------------------------------
$props = [xml](Get-Content (Join-Path $repo "Directory.Build.props") -Raw)
$propsVersion = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if ($propsVersion -ne $Version -and -not $AllowDirty) {
    throw "-Version $Version does not match Directory.Build.props ($propsVersion)."
}

# Only what goes into the package counts (unrelated work elsewhere in the tree doesn't).
$inputs = @("src", "tools", "Directory.Build.props", "nuget.config", "AxisSdReader.sln", "LICENSE", "THIRD-PARTY-NOTICES.md", "licenses")
$dirty = @(& git -C $repo status --porcelain -- $inputs)
if ($LASTEXITCODE -ne 0) { throw "git status failed." }
if ($dirty.Count -gt 0) {
    if (-not $AllowDirty) { throw ("Uncommitted changes in build inputs (commit them, or pass -AllowDirty):`n" + ($dirty -join "`n")) }
    Write-Warning "Packaging with uncommitted changes (-AllowDirty): this is not a release build."
}
$commit = (& git -C $repo rev-parse HEAD).Trim()

# Licence texts that must accompany the binaries (our MIT + the bundled components' licences).
function Add-Licences([string]$dir) {
    Copy-Item (Join-Path $repo "LICENSE") (Join-Path $dir "LICENSE.txt") -Force
    Copy-Item (Join-Path $repo "THIRD-PARTY-NOTICES.md") $dir -Force
    Copy-Item (Join-Path $ffmpegDir "FFMPEG-LICENSE.txt") $dir -Force
    Copy-Item (Join-Path $repo "licenses") (Join-Path $dir "licenses") -Recurse -Force
}

# Entry names with forward slashes (the zip standard), whatever this PowerShell's .NET does by default.
function New-Zip([string]$sourceDir, [string]$zipPath) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $root = Split-Path -Parent $sourceDir
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $sourceDir -Recurse -File) {
            $entry = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Test-Contains([string]$path, [string]$text) {
    $bytes = [IO.File]::ReadAllBytes($path)
    return [Text.Encoding]::ASCII.GetString($bytes).Contains($text)
}

# --- 1. Bundled FFmpeg ---------------------------------------------------------------------------------
# An LGPL v3 build: configured WITHOUT --enable-gpl and with --disable-libx264/--disable-libx265, so it
# carries no GPL components (the app only runs `-c copy`; the MCP server decodes frames). BtbN is one of the
# Windows builders linked from ffmpeg.org.
#
# Both the downloaded zip AND the ffmpeg.exe that ships are pinned by SHA-256, and the exe is checked again in
# the staged package - so a binary dropped into src\AxisSdReader.App\ffmpeg (or swapped after download) never
# ships. The URL names one dated build (n8.1.3, 2026-11-23), not the "latest" tag, which is rebuilt daily.
# The builder prunes old dated builds, so to move on: pick a newer one, check it is still LGPL
# (`ffmpeg -buildconf` must show --enable-version3 and no --enable-gpl / --enable-nonfree), then update the URL
# and both hashes. (The builds' repository moved from BtbN/FFmpeg-Builds to the FFmpeg-Builds organization in
# November 2026; the old URLs redirect.)
$ffUrl    = "https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-11-23-12-43/ffmpeg-n8.1.3-39-g660b5aa7db-win64-lgpl-8.1.zip"
$ffSha    = "F0CA6F38361A853BAAA010DF76E62444E862437C29FBF09B13B008040641D10C"
$ffExeSha = "51F05F24611D447DE5B562ED2F0DAF72327F7F1413CA0E83086324B9D00F74DD"
$ffExe    = Join-Path $ffmpegDir "ffmpeg.exe"

if (-not (Test-Path $ffExe)) {
    Write-Host "Fetching LGPL FFmpeg (~163 MB)..."
    New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
    # Unpredictable scratch names: nothing else can pre-create or swap them between the check and the copy.
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ("axis-ffmpeg-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    try {
        $tmpZip = Join-Path $scratch "ffmpeg.zip"
        $tmpDir = Join-Path $scratch "x"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $ffUrl -OutFile $tmpZip -UseBasicParsing

        $got = (Get-FileHash $tmpZip -Algorithm SHA256).Hash
        if ($got -ne $ffSha) {
            throw ("FFmpeg zip SHA-256 mismatch.`n  expected $ffSha`n  got      $got`n" +
                   "Upstream rebuilt the 'latest' asset. Re-verify the build is still LGPL (no --enable-gpl) " +
                   "and then update `$ffSha and `$ffExeSha in tools\publish.ps1.")
        }

        Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force
        $found = @(Get-ChildItem $tmpDir -Recurse -Filter ffmpeg.exe | Where-Object { $_.Directory.Name -eq "bin" })
        if ($found.Count -ne 1) { throw "Expected exactly one bin\ffmpeg.exe in the FFmpeg zip, found $($found.Count)." }
        Copy-Item $found[0].FullName $ffExe -Force
        Copy-Item (Get-ChildItem $tmpDir -Recurse -Filter LICENSE.txt | Select-Object -First 1).FullName `
                  (Join-Path $ffmpegDir "FFMPEG-LICENSE.txt") -Force
    }
    finally {
        Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$exeSha = (Get-FileHash $ffExe -Algorithm SHA256).Hash
if ($exeSha -ne $ffExeSha) {
    throw ("ffmpeg.exe SHA-256 mismatch - refusing to ship an unverified binary.`n  expected $ffExeSha`n  got      $exeSha`n" +
           "Delete $ffmpegDir and run again to fetch the pinned build, or (after verifying a new LGPL build) update `$ffExeSha.")
}
Write-Host "FFmpeg verified ($([math]::Round((Get-Item $ffExe).Length/1MB,1)) MB, sha256 $exeSha)"

# --- 2. Folder deploy: launcher + app + MCP server side by side ----------------------------------------
Write-Host "Publishing FOLDER deploy (v$Version, $Configuration, $commit)..."
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
dotnet publish $app -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:ContinuousIntegrationBuild=true --nologo -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "folder publish (app) failed (exit $LASTEXITCODE)" }
# The launcher hosts the app (AxisSdReader.App.dll) itself; the SDK's apphost must not ship.
Remove-Item (Join-Path $stageDir "AxisSdReader.App.exe") -Force

# The MCP server shares the folder (it finds ffmpeg\ffmpeg.exe beside it; AI clients point at
# "<install folder>\AxisSdReader.Mcp.exe"). It is published as ONE self-contained exe so its newer
# dependencies (System.Text.Json 10.x etc.) stay inside it instead of overwriting the app's copies in the
# shared folder. It has no native libraries, so nothing is extracted at run time either.
$mcpDir = Join-Path $repo "build\publish-mcp"
if (Test-Path $mcpDir) { Remove-Item $mcpDir -Recurse -Force }
dotnet publish $mcp -c $Configuration -r win-x64 --self-contained true `
    -p:IncludeNativeLibrariesForSelfExtract=false -p:PublishReadyToRun=false `
    -p:ContinuousIntegrationBuild=true --nologo -o $mcpDir
if ($LASTEXITCODE -ne 0) { throw "publish (MCP server) failed (exit $LASTEXITCODE)" }
$extra = @(Get-ChildItem $mcpDir -File | Where-Object { $_.Name -ne "AxisSdReader.Mcp.exe" })
if ($extra.Count -gt 0) { throw "MCP single-file publish produced unexpected loose files: $($extra.Name -join ', ')" }
Copy-Item (Join-Path $mcpDir "AxisSdReader.Mcp.exe") $stageDir -Force

# The native launcher (needs the Visual Studio C++ build tools).
$launcherDir = Join-Path $repo "build\launcher"
& (Join-Path $repo "src\AxisSdReader.Launcher\build.ps1") -Version $Version -OutDir $launcherDir
Copy-Item (Join-Path $launcherDir "AxisSdReader.exe") $stageDir -Force

Add-Licences $stageDir

# --- 3. Verify the staged package ----------------------------------------------------------------------
$problems = New-Object System.Collections.Generic.List[string]
$all = @(Get-ChildItem $stageDir -Recurse -File)
foreach ($f in $all | Where-Object { $_.Extension -eq ".pdb" }) { $problems.Add("symbols shipped: $($f.FullName)") }
if (Test-Path (Join-Path $stageDir "createdump.exe")) { $problems.Add("createdump.exe shipped") }
foreach ($d in "libvlc\win-x86", "libvlc\win-arm64", "libvlc\win-x64\lua", "libvlc\win-x64\plugins\lua") {
    if (Test-Path (Join-Path $stageDir $d)) { $problems.Add("$d shipped") }
}
# Exactly the libVLC files tools\libvlc-files.txt lists: nothing the allow-list didn't mean to ship, nothing missing.
$vlcRoot = Join-Path $stageDir "libvlc\win-x64"
$vlcExpected = @(Get-Content (Join-Path $PSScriptRoot "libvlc-files.txt") | Where-Object { $_ -and -not $_.StartsWith("#") })
$vlcActual = @(Get-ChildItem $vlcRoot -Recurse -File -ErrorAction SilentlyContinue |
    ForEach-Object { $_.FullName.Substring($vlcRoot.Length + 1).Replace('\', '/') })
foreach ($f in $vlcActual | Where-Object { $vlcExpected -notcontains $_ }) { $problems.Add("unexpected libVLC file: $f") }
foreach ($f in $vlcExpected | Where-Object { $vlcActual -notcontains $_ }) { $problems.Add("libVLC file missing: $f") }
if (Test-Path (Join-Path $stageDir "libvlc\win-x64\plugins\codec\libx26410b_plugin.dll")) { $problems.Add("GPL x264 plugin shipped") }
# What playing a camera chunk needs: stream input, Matroska, the decoders and packetizers for H.264 / H.265 / AV1 /
# MJPEG, and a video output for the app's window.
foreach ($plugin in "access\libimem_plugin.dll", "demux\libmkv_plugin.dll", "codec\libavcodec_plugin.dll", "codec\libdav1d_plugin.dll",
                    "packetizer\libpacketizer_h264_plugin.dll", "packetizer\libpacketizer_hevc_plugin.dll", "packetizer\libpacketizer_av1_plugin.dll",
                    "video_output\libdirect3d11_plugin.dll", "video_output\libdrawable_plugin.dll", "audio_output\libmmdevice_plugin.dll") {
    if (-not (Test-Path (Join-Path $stageDir "libvlc\win-x64\plugins\$plugin"))) { $problems.Add("libVLC plugin missing: $plugin") }
}
foreach ($file in "AxisSdReader.exe", "AxisSdReader.App.dll", "AxisSdReader.App.runtimeconfig.json", "hostfxr.dll",
                  "AxisSdReader.Mcp.exe", "ffmpeg\ffmpeg.exe") {
    if (-not (Test-Path (Join-Path $stageDir $file))) { $problems.Add("$file missing") }
}
if (Test-Path (Join-Path $stageDir "AxisSdReader.App.exe")) { $problems.Add("AxisSdReader.App.exe shipped (only the launcher may start the app)") }
if ((Get-FileHash (Join-Path $stageDir "ffmpeg\ffmpeg.exe") -Algorithm SHA256).Hash -ne $ffExeSha) { $problems.Add("staged ffmpeg.exe is not the pinned build") }
$launcherExe = Join-Path $stageDir "AxisSdReader.exe"
if (-not (Test-Contains $launcherExe 'level="requireAdministrator"')) { $problems.Add("launcher manifest does not require administrator") }
if (-not (Test-Contains $launcherExe 'PerMonitorV2')) { $problems.Add("launcher manifest lacks the app's DPI awareness") }
foreach ($exe in "AxisSdReader.exe", "AxisSdReader.App.dll") {
    $v = (Get-Item (Join-Path $stageDir $exe)).VersionInfo.ProductVersion
    if (-not $v -or -not $v.StartsWith($Version)) { $problems.Add("$exe has version '$v', expected $Version") }
}
$runtimeConfig = Get-Content (Join-Path $stageDir "AxisSdReader.App.runtimeconfig.json") -Raw | ConvertFrom-Json
$core = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq "Microsoft.NETCore.App" })[0]
if (-not $core -or [version]$core.version -lt [version]$MinRuntime) { $problems.Add(".NET runtime $($core.version) is older than $MinRuntime") }
if ($problems.Count -gt 0) { throw ("The staged package failed verification:`n  " + ($problems -join "`n  ")) }
Write-Host "Package verified: $($all.Count) files, .NET runtime $($core.version), commit $commit"

New-Zip $stageDir $zipFolder
$zipSha = (Get-FileHash $zipFolder -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $sums -Encoding Ascii -Value "$zipSha *$zipName"

# --- 4. Report ------------------------------------------------------------------------------------------
Write-Host ""
Write-Host "FOLDER      $($all.Count) files - launcher + app + MCP server, no extraction"
Write-Host "  package : $zipFolder ($([math]::Round((Get-Item $zipFolder).Length/1MB,1)) MB)"
Write-Host "  sha256  : $zipSha"
Write-Host "  sums    : $sums"
