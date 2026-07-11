#requires -Version 7.0
<#
.SYNOPSIS
  Build a productive, copy-and-run build of Knotarium with the UI included.

.DESCRIPTION
  Produces a self-contained .NET build (the whole runtime is bundled) plus a `wwwroot` folder
  holding the built UI, served by the backend on the same origin. Copy the resulting
  `publish/app` folder to any matching PC and run the launcher / exe.

  Default output is a FOLDER of files (recommended): no runtime self-extraction, so antivirus
  doesn't false-positive on it, and Assembly.Location stays populated so the Roslyn node compiler
  works. Use -SingleFile for one self-extracting exe instead (convenient but commonly AV-flagged).

  NOT NativeAOT: the workflow engine compiles inline-code and dynamic custom nodes with Roslyn at
  runtime, which needs the JIT — AOT would kill that feature.

.EXAMPLE
  ./publish.ps1                     # win-x64 Release folder build into ./publish/app
  ./publish.ps1 -Runtime win-arm64
  ./publish.ps1 -SkipFrontend       # reuse an existing Frontend/dist
  ./publish.ps1 -SingleFile         # one self-extracting exe (may trip antivirus)
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$OutputDir = (Join-Path $PSScriptRoot 'publish'),
    [int]$Port = 5232,
    [switch]$SkipFrontend,
    # Produce ONE self-extracting .exe instead of a folder. Convenient, but such builds
    # unpack to a temp dir at launch, which antivirus frequently flags as a FALSE POSITIVE
    # (and it's unsigned). The default folder build avoids that trigger entirely.
    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$api = Join-Path $root 'Backend/Knotarium.Api/Knotarium.Api.csproj'
$frontend = Join-Path $root 'Frontend'
$dist = Join-Path $frontend 'dist'
$appOut = Join-Path $OutputDir 'app'

Write-Host '== Knotarium — productive build (UI included) ==' -ForegroundColor Cyan

# 1. Build the UI (tsc -b && vite build -> Frontend/dist)
if (-not $SkipFrontend) {
    Write-Host '-> Building frontend (vite)...' -ForegroundColor Yellow
    Push-Location $frontend
    try {
        if (-not (Test-Path (Join-Path $frontend 'node_modules'))) { npm ci }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    }
    finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $dist 'index.html'))) {
    throw "Frontend build output not found at $dist (run without -SkipFrontend)."
}

# 2. Clean previous output
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $appOut | Out-Null

# 3. Publish the backend self-contained (the whole .NET runtime is bundled).
#    Default: a folder of files (no runtime self-extraction) — avoids antivirus false
#    positives and keeps Assembly.Location populated so the Roslyn node compiler works.
#    -SingleFile: one self-extracting exe (convenient, but commonly AV-flagged + unsigned).
$publishArgs = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishTrimmed=false',          # trimming breaks EF/Roslyn/reflection
    '-p:DebugType=none',                # no .pdb in the shipped folder
    '-p:DebugSymbols=false',
    '-o', $appOut
)
if ($SingleFile) {
    Write-Host 'WARNING: -SingleFile builds self-extract at launch and are often flagged as a' -ForegroundColor Red
    Write-Host '         FALSE POSITIVE by antivirus (and are unsigned). Prefer the folder build.' -ForegroundColor Red
    $publishArgs += @(
        '-p:PublishSingleFile=true',
        '-p:IncludeAllContentForSelfExtract=true',  # needed so Roslyn finds Assembly.Location
        '-p:EnableCompressionInSingleFile=true'
    )
}
Write-Host "-> Publishing backend ($Runtime, self-contained $(if ($SingleFile) {'single-file'} else {'folder'}))..." -ForegroundColor Yellow
dotnet publish $api @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed.' }

# 4. Drop the built UI next to the exe; the backend serves it from ./wwwroot at runtime.
Write-Host '-> Copying UI into wwwroot...' -ForegroundColor Yellow
$wwwroot = Join-Path $appOut 'wwwroot'
New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
Copy-Item -Path (Join-Path $dist '*') -Destination $wwwroot -Recurse -Force

# 5. Resolve the produced executable name.
$exe = Get-ChildItem $appOut -File |
    Where-Object { $_.BaseName -eq 'Knotarium.Api' -and ($_.Extension -eq '.exe' -or $_.Extension -eq '') } |
    Select-Object -First 1
$exeName = if ($exe) { $exe.Name } else { 'Knotarium.Api.exe' }

# 6. Write a friendly launcher that pins the port, opens the browser, then runs the server.
$url = "http://localhost:$Port"
if ($Runtime -like 'win-*') {
    $launcher = Join-Path $appOut 'Start.bat'
    @"
@echo off
rem Launches the app on a fixed local port and opens the browser.
set ASPNETCORE_URLS=$url
start "" "$url"
"%~dp0$exeName"
"@ | Set-Content -Path $launcher -Encoding ascii
}

Write-Host ''
Write-Host '== Done ==' -ForegroundColor Green
Write-Host "Output folder : $appOut"
Write-Host "Launch        : double-click Start.bat  (or run `"$exeName`")"
Write-Host "URL           : $url"
Write-Host 'Ship          : copy the whole "app" folder to the target PC.'
Write-Host 'Note          : the SQLite database (Knotarium.db) is created next to the exe on first run.'
