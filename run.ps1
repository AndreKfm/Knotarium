# Knotarium MVP Monolith Launcher
# This script starts the C# Minimal API Backend and Vite Frontend in separate windows.

param(
	[switch]$ElevatedRestart,
	# Also launch the local mock REST API (dev/mock-api) for resource-locator testing.
	[switch]$MockApi,
	# Wipe .NET build outputs (bin/obj) before launching so the backend rebuilds from scratch.
	[switch]$Clean
)

Clear-Host
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  Knotarium MVP Monolith Dev Environment" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$PSScriptRoot = Split-Path -Parent -Path $MyInvocation.MyCommand.Definition
$BackendDir = Join-Path $PSScriptRoot "Backend"
$FrontendDir = Join-Path $PSScriptRoot "Frontend"
$MockApiDir = Join-Path $PSScriptRoot "dev\mock-api"

function Test-IsAdministrator {
	$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
	$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)

	return $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-ListeningProcessInfo {
	param(
		[Parameter(Mandatory = $true)]
		[int]$Port
	)

	$connection = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
		Select-Object -First 1

	if ($null -eq $connection) {
		return $null
	}

	$process = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue

	[pscustomobject]@{
		Port = $Port
		ProcessId = $connection.OwningProcess
		ProcessName = if ($null -ne $process) { $process.ProcessName } else { "Unknown" }
	}
}

function Get-ParentProcessInfo {
	param(
		[Parameter(Mandatory = $true)]
		[int]$ProcessId
	)

	$process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue

	if ($null -eq $process -or $process.ParentProcessId -le 0) {
		return $null
	}

	$parent = Get-Process -Id $process.ParentProcessId -ErrorAction SilentlyContinue

	if ($null -eq $parent) {
		return $null
	}

	[pscustomobject]@{
		ProcessId = $parent.Id
		ProcessName = $parent.ProcessName
	}
}

function Stop-ListeningProcess {
	param(
		[Parameter(Mandatory = $true)]
		[int]$Port,

		[Parameter(Mandatory = $true)]
		[string]$DisplayName
	)

	$processInfo = Get-ListeningProcessInfo -Port $Port
	$result = [pscustomobject]@{
		Port = $Port
		DisplayName = $DisplayName
		Status = 'NotRunning'
		ProcessId = $null
		ProcessName = $null
	}

	if ($null -eq $processInfo) {
		Write-Host "$DisplayName is not currently listening on http://localhost:$Port." -ForegroundColor DarkGray
		return $result
	}

	$result.ProcessId = $processInfo.ProcessId
	$result.ProcessName = $processInfo.ProcessName
	$result.Status = 'Running'

	Write-Host "Stopping existing $DisplayName on http://localhost:$Port (PID $($processInfo.ProcessId), $($processInfo.ProcessName))..." -ForegroundColor DarkYellow

	$processIdsToTry = [System.Collections.Generic.List[int]]::new()
	$processIdsToTry.Add($processInfo.ProcessId)
	$accessDenied = $false

	$parentProcess = Get-ParentProcessInfo -ProcessId $processInfo.ProcessId
	while ($null -ne $parentProcess -and $parentProcess.ProcessName -in @('cmd', 'powershell', 'pwsh')) {
		if (-not $processIdsToTry.Contains($parentProcess.ProcessId)) {
			$processIdsToTry.Add($parentProcess.ProcessId)
		}

		$parentProcess = Get-ParentProcessInfo -ProcessId $parentProcess.ProcessId
	}

	foreach ($processIdToTry in $processIdsToTry) {
		try {
			Stop-Process -Id $processIdToTry -Force -ErrorAction Stop
		}
		catch {
			if ($_.Exception.Message -match 'Access is denied') {
				$accessDenied = $true
			}

			Write-Host "Unable to stop PID $processIdToTry directly: $($_.Exception.Message)" -ForegroundColor DarkYellow
		}

		if ($null -eq (Get-ListeningProcessInfo -Port $Port)) {
			Write-Host "$DisplayName port $Port is clear." -ForegroundColor DarkGray
			$result.Status = 'Cleared'
			return $result
		}
	}

	for ($attempt = 0; $attempt -lt 20; $attempt++) {
		if ($null -eq (Get-ListeningProcessInfo -Port $Port)) {
			Write-Host "$DisplayName port $Port is clear." -ForegroundColor DarkGray
			$result.Status = 'Cleared'
			return $result
		}

		Start-Sleep -Milliseconds 250
	}

	if ($accessDenied -and -not (Test-IsAdministrator)) {
		$result.Status = 'RequiresElevation'
		return $result
	}

	throw "$DisplayName on port $Port did not stop cleanly."
}

$backendPort = 43120
$frontendPort = 5273
$mockApiPort = 8787

Write-Host "Checking for existing development services..." -ForegroundColor Yellow
$backendStopResult = Stop-ListeningProcess -Port $backendPort -DisplayName "Backend API"
$frontendStopResult = Stop-ListeningProcess -Port $frontendPort -DisplayName "Frontend dev server"

$stopResults = @($backendStopResult, $frontendStopResult)
if ($MockApi) {
	$stopResults += Stop-ListeningProcess -Port $mockApiPort -DisplayName "Mock API"
}

$requiresElevationResults = $stopResults | Where-Object { $_.Status -eq 'RequiresElevation' }

if ($requiresElevationResults.Count -gt 0) {
	if ($ElevatedRestart) {
		$blockedPorts = ($requiresElevationResults | ForEach-Object { $_.Port }) -join ', '
		throw "Unable to stop services on ports $blockedPorts even after elevation."
	}

	$blockedPorts = ($requiresElevationResults | ForEach-Object { $_.Port }) -join ', '
	Write-Host "Existing development services on ports $blockedPorts require elevation to terminate. Relaunching with elevation..." -ForegroundColor Yellow
	$relaunchArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-ElevatedRestart")
	if ($MockApi) { $relaunchArgs += "-MockApi" }
	if ($Clean) { $relaunchArgs += "-Clean" }
	Start-Process powershell -Verb RunAs -WorkingDirectory $PSScriptRoot -ArgumentList $relaunchArgs
	return
}

if ($Clean) {
	Write-Host "Cleaning .NET build outputs (bin/obj) under Backend for a fresh build..." -ForegroundColor Yellow
	$cleanDirs = Get-ChildItem -Path $BackendDir -Recurse -Directory -Force -ErrorAction SilentlyContinue |
		Where-Object { $_.Name -in @('bin', 'obj') }

	foreach ($dir in $cleanDirs) {
		Remove-Item -Recurse -Force -LiteralPath $dir.FullName -ErrorAction SilentlyContinue
	}

	Write-Host "Removed $($cleanDirs.Count) bin/obj folder(s). The backend will rebuild from scratch." -ForegroundColor DarkGray
}

Write-Host "Starting C# Backend API..." -ForegroundColor Yellow
Start-Process powershell -WorkingDirectory $BackendDir -ArgumentList "-NoExit", "-Command", "dotnet run --project Knotarium.Api/Knotarium.Api.csproj --launch-profile http" -WindowStyle Normal

# Wait briefly for backend port bind to start cleanly
Start-Sleep -Seconds 3

Write-Host "Starting Vite Frontend..." -ForegroundColor Yellow
Start-Process powershell -WorkingDirectory $FrontendDir -ArgumentList "-NoExit", "-Command", "npm run dev" -WindowStyle Normal

if ($MockApi) {
	$pythonCommand = if (Get-Command python -ErrorAction SilentlyContinue) { "python" }
		elseif (Get-Command py -ErrorAction SilentlyContinue) { "py" }
		else { $null }

	if ($null -eq $pythonCommand) {
		Write-Host "Skipping Mock API: Python was not found on PATH (install Python 3.7+ to use -MockApi)." -ForegroundColor DarkYellow
		$MockApi = $false
	}
	else {
		Write-Host "Starting Mock API (dev/mock-api)..." -ForegroundColor Yellow
		Start-Process powershell -WorkingDirectory $MockApiDir -ArgumentList "-NoExit", "-Command", "$pythonCommand server.py" -WindowStyle Normal
	}
}

Write-Host "---------------------------------------------" -ForegroundColor Gray
Write-Host "Development environment is ready." -ForegroundColor Green
Write-Host "URLs:" -ForegroundColor White
Write-Host " - Frontend UI:  http://localhost:$frontendPort" -ForegroundColor Cyan
Write-Host " - Backend API:  http://localhost:$backendPort" -ForegroundColor Cyan
if ($MockApi) {
	Write-Host " - Mock API:     http://127.0.0.1:$mockApiPort  (spec at /openapi.json)" -ForegroundColor Cyan
}
Write-Host "---------------------------------------------" -ForegroundColor Gray
Write-Host "Logs are streaming in the newly opened windows." -ForegroundColor Gray
Write-Host "To stop the services, simply close their respective console windows." -ForegroundColor Gray
