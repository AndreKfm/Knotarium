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
  ./publish.ps1 -Version 1.0.0-rc.1 -Zip          # stamp version + emit a zip archive
  ./publish.ps1 -Version 1.0.0-rc.1 -Zip -Installer   # also build the Inno Setup installer
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$OutputDir = (Join-Path $PSScriptRoot 'publish'),
    [int]$Port = 43120,
    [switch]$SkipFrontend,
    # Produce ONE self-extracting .exe instead of a folder. Convenient, but such builds
    # unpack to a temp dir at launch, which antivirus frequently flags as a FALSE POSITIVE
    # (and it's unsigned). The default folder build avoids that trigger entirely.
    [switch]$SingleFile,
    # Release version stamped into the assembly (surfaced by GET /api/version) and used in
    # artifact filenames. Accepts SemVer incl. a pre-release suffix (e.g. 1.0.0-rc.1). When empty,
    # the <Version> from Knotarium.Api.csproj is used.
    [string]$Version = '',
    # Also compress publish/app into publish/Knotarium-<version>-<runtime>.zip (zero-install channel).
    [switch]$Zip,
    # Also build the Windows installer (installer/Knotarium.iss) via Inno Setup's ISCC compiler.
    [switch]$Installer,
    # Path to Inno Setup's ISCC.exe. Auto-detected in the usual install locations when empty.
    [string]$InnoSetupExe = '',
    # Authenticode code signing (optional). Supply a base64-encoded PFX and its password — normally via
    # the SIGN_CERT_BASE64 / SIGN_CERT_PASSWORD env vars (CI secrets). When SignCertBase64 is empty,
    # signing is skipped entirely and the output is identical to an unsigned build (no cert, no change).
    [string]$SignCertBase64 = $env:SIGN_CERT_BASE64,
    [string]$SignCertPassword = $env:SIGN_CERT_PASSWORD,
    # RFC3161 timestamp server, so signatures stay valid after the signing certificate expires.
    [string]$SignTimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$api = Join-Path $root 'Backend/Knotarium.Api/Knotarium.Api.csproj'
$frontend = Join-Path $root 'Frontend'
$dist = Join-Path $frontend 'dist'
$appOut = Join-Path $OutputDir 'app'

# Resolve the release version: use -Version if given, else the <Version> from the API csproj.
if ([string]::IsNullOrWhiteSpace($Version)) {
    $csprojText = Get-Content $api -Raw
    if ($csprojText -match '<Version>\s*([^<]+?)\s*</Version>') {
        $Version = $Matches[1].Trim()
    } else {
        $Version = '0.0.0'
    }
    Write-Host "-> No -Version given; using csproj version $Version" -ForegroundColor DarkGray
}

# --- Optional Authenticode code signing -------------------------------------------------------------
# Enabled only when a base64 PFX is supplied. With no cert this is a complete no-op — the produced files
# are byte-for-byte an unsigned build. When enabled, the app EXEs and the setup.exe are signed with
# SHA-256 + an RFC3161 timestamp. Windows-only (Authenticode); the linux runtime build skips it.
$signEnabled = -not [string]::IsNullOrWhiteSpace($SignCertBase64)
$script:signPfxPath = $null

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $hit = Get-ChildItem -Path $kits -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*\x64\signtool.exe' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Invoke-Sign([string[]]$Files) {
    if (-not $signEnabled) { return }
    if ($Runtime -notlike 'win-*') { return }
    $signtool = Find-SignTool
    if (-not $signtool) { throw 'Code signing requested (SignCertBase64 set) but signtool.exe was not found. Install the Windows SDK.' }
    if (-not $script:signPfxPath) {
        $script:signPfxPath = Join-Path ([System.IO.Path]::GetTempPath()) ("knotarium-sign-$([System.Guid]::NewGuid().ToString('N')).pfx")
        [System.IO.File]::WriteAllBytes($script:signPfxPath, [System.Convert]::FromBase64String($SignCertBase64))
    }
    foreach ($f in $Files) {
        if (-not (Test-Path $f)) { continue }
        Write-Host "-> Signing $(Split-Path $f -Leaf)..." -ForegroundColor Yellow
        # Password is passed as an arg (GitHub masks the secret in logs); signtool does not echo it.
        & $signtool sign /fd SHA256 /f $script:signPfxPath /p $SignCertPassword /tr $SignTimestampUrl /td SHA256 $f | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for '$f' (exit $LASTEXITCODE)." }
    }
}

Write-Host '== Knotarium — productive build (UI included) ==' -ForegroundColor Cyan
if ($signEnabled) { Write-Host '-> Code signing: ENABLED (a certificate was supplied).' -ForegroundColor Green }

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
    "-p:Version=$Version",              # stamps the assembly; surfaced by GET /api/version
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

# 4b. Sign the app executables now — before the zip and installer bundle them, so both the zero-install
#     zip and the installed app carry signed binaries. No-op unless a certificate was supplied.
Invoke-Sign @(
    (Join-Path $appOut 'Knotarium.Api.exe'),
    (Join-Path $appOut 'Knotarium.SandboxWorker.exe')
)

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

# 7. Optional: zip the app folder for the zero-install download channel.
$zipPath = $null
if ($Zip) {
    Write-Host '-> Creating zip archive...' -ForegroundColor Yellow
    $zipPath = Join-Path $OutputDir "Knotarium-$Version-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    # Compress the CONTENTS of app (app\* not app) so the zip extracts to a Knotarium-* folder
    # of files rather than a nested app\ level.
    Compress-Archive -Path (Join-Path $appOut '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
    Set-Content -Path "$zipPath.sha256" -Value "$zipHash  $(Split-Path $zipPath -Leaf)" -Encoding ascii
    Write-Host "   $zipPath" -ForegroundColor DarkGray
}

# 8. Optional: build the Windows installer via Inno Setup (installer/Knotarium.iss).
$setupPath = $null
if ($Installer) {
    Write-Host '-> Building installer (Inno Setup)...' -ForegroundColor Yellow
    $iss = Join-Path $root 'installer/Knotarium.iss'
    if (-not (Test-Path $iss)) { throw "Installer script not found at $iss." }

    # Locate ISCC.exe (Inno Setup command-line compiler).
    $iscc = $InnoSetupExe
    if ([string]::IsNullOrWhiteSpace($iscc)) {
        $candidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
        )
        $iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
    }
    if (-not $iscc -or -not (Test-Path $iscc)) {
        throw 'ISCC.exe (Inno Setup 6) not found. Install it (winget install JRSoftware.InnoSetup) or pass -InnoSetupExe.'
    }

    # ISCC defines: AppVersion, SourceDir (the published app), OutputDir, OutputBase.
    $outputBase = "Knotarium-$Version-setup"
    & $iscc `
        "/DAppVersion=$Version" `
        "/DPort=$Port" `
        "/DSourceDir=$appOut" `
        "/DOutputDir=$OutputDir" `
        "/DOutputBase=$outputBase" `
        $iss
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed.' }

    $setupPath = Join-Path $OutputDir "$outputBase.exe"
    if (Test-Path $setupPath) {
        # Sign the installer BEFORE hashing so the published .sha256 matches the signed file. No-op unless
        # a certificate was supplied.
        Invoke-Sign @($setupPath)
        $setupHash = (Get-FileHash $setupPath -Algorithm SHA256).Hash
        Set-Content -Path "$setupPath.sha256" -Value "$setupHash  $(Split-Path $setupPath -Leaf)" -Encoding ascii
        Write-Host "   $setupPath" -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host '== Done ==' -ForegroundColor Green
Write-Host "Version       : $Version"
if ($zipPath)   { Write-Host "Zip archive   : $zipPath" }
if ($setupPath) { Write-Host "Installer     : $setupPath" }
Write-Host "Output folder : $appOut"
Write-Host "Launch        : double-click Start.bat  (or run `"$exeName`")"
Write-Host "URL           : $url"
Write-Host 'Ship          : copy the whole "app" folder to the target PC.'
Write-Host 'Note          : the SQLite database (Knotarium.db) is created next to the exe on first run.'
if ($signEnabled) { Write-Host 'Signed        : yes (Authenticode, SHA-256, timestamped)' }

# Remove the decoded signing certificate from disk (best-effort; CI runners are ephemeral anyway).
if ($script:signPfxPath -and (Test-Path $script:signPfxPath)) {
    Remove-Item $script:signPfxPath -Force -ErrorAction SilentlyContinue
}
