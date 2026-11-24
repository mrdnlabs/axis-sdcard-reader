#requires -Version 5.1
<#
.SYNOPSIS
    Builds AxisSdReader.exe, the native launcher that starts the app elevated with a clean environment.

.DESCRIPTION
    Compiles launcher.c with the Visual Studio C++ build tools (found with vswhere): static C runtime (no
    vcruntime DLL to plant), Control Flow Guard, CET shadow stack compatible, ASLR/DEP, imports resolved from
    System32 only, reproducible output, and launcher.manifest (administrator rights; the app's DPI settings,
    since this exe hosts the app).

.EXAMPLE
    powershell -File src\AxisSdReader.Launcher\build.ps1 -Version 1.2.0 -OutDir build\launcher
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = "Stop"

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "Version must be major.minor.patch, got '$Version'." }
$major = $Matches[1]; $minor = $Matches[2]; $patch = $Matches[3]

$here = $PSScriptRoot
$OutDir = [IO.Path]::GetFullPath($OutDir)
$obj = Join-Path $OutDir "obj"
New-Item -ItemType Directory -Force -Path $obj | Out-Null

# Locate the C++ tools and load their environment (cl, rc, link, the Windows SDK) into this process.
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found: install the Visual Studio C++ build tools." }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "No Visual Studio installation with the x64 C++ tools was found." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
$envDump = & cmd.exe /d /c "`"$vcvars`" >nul 2>&1 && set"
if ($LASTEXITCODE -ne 0) { throw "vcvars64.bat failed." }
foreach ($line in $envDump) {
    if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], "Process") }
}

Set-Content -Path (Join-Path $obj "version.h") -Encoding Ascii -Value @(
    "#define VER_MAJOR $major",
    "#define VER_MINOR $minor",
    "#define VER_PATCH $patch",
    "#define VER_STRING `"$Version`""
)

$res = Join-Path $obj "launcher.res"
& rc.exe /nologo /I $obj /fo $res (Join-Path $here "launcher.rc")
if ($LASTEXITCODE -ne 0) { throw "rc failed (exit $LASTEXITCODE)." }

$exe = Join-Path $OutDir "AxisSdReader.exe"
$cflags = @("/nologo", "/W4", "/WX", "/O1", "/GS", "/sdl", "/guard:cf", "/MT", "/Brepro", "/DUNICODE", "/D_UNICODE",
            "/Fo$obj\", (Join-Path $here "launcher.c"))
$lflags = @("/link", "/SUBSYSTEM:WINDOWS", "/DYNAMICBASE", "/NXCOMPAT", "/HIGHENTROPYVA", "/GUARD:CF", "/CETCOMPAT",
            "/DEPENDENTLOADFLAG:0x800", "/Brepro", "/MANIFEST:EMBED", "/MANIFESTUAC:NO",
            "/MANIFESTINPUT:$(Join-Path $here 'launcher.manifest')",
            "/OUT:$exe", $res, "kernel32.lib", "user32.lib", "advapi32.lib", "userenv.lib")
& cl.exe @cflags @lflags
if ($LASTEXITCODE -ne 0) { throw "cl failed (exit $LASTEXITCODE)." }

Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"
