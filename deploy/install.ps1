#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or upgrades the Pulse Windows Agent as a Windows Service.

.DESCRIPTION
    Publishes Pulse.Agent.Windows as a self-contained win-x64 build (unless -SourceDir is provided),
    installs or updates the binaries in -InstallDir, preserves any existing machine configuration
    in %ProgramData%\Pulse\agent.json, and registers or updates the Windows Service.

.EXAMPLE
    .\deploy\install.ps1

.EXAMPLE
    .\deploy\install.ps1 -SourceDir .\publish -NoStart
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles "Pulse\Agent"),
    [string]$ConfigDir = (Join-Path $env:ProgramData "Pulse"),
    [string]$ServiceName = "Pulse Agent",
    [string]$SourceDir,
    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\Pulse.Agent.Windows\Pulse.Agent.Windows.csproj"
$stagingDir = $null

try {
    if ([string]::IsNullOrWhiteSpace($SourceDir)) {
        if (-not (Test-Path $projectPath)) {
            throw "Could not find project at '$projectPath'. Run from the repository or pass -SourceDir."
        }

        $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pulse-agent-publish-" + [Guid]::NewGuid().ToString("N"))
        Write-Host "Publishing Pulse.Agent.Windows (win-x64, self-contained)..."
        & dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $stagingDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
        $publishPath = $stagingDir
    }
    else {
        $publishPath = (Resolve-Path $SourceDir).Path
    }

    $stagedExe = Join-Path $publishPath "Pulse.Agent.Windows.exe"
    if (-not (Test-Path $stagedExe)) {
        throw "Published executable not found at '$stagedExe'."
    }

    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $existingService -and $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping existing service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force
        $existingService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }

    if (Test-Path $InstallDir) {
        Write-Host "Updating binaries in '$InstallDir'..."
        Get-ChildItem -Path $InstallDir -Force | Remove-Item -Recurse -Force
    }
    else {
        Write-Host "Creating install directory '$InstallDir'..."
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    Copy-Item -Path (Join-Path $publishPath "*") -Destination $InstallDir -Recurse -Force

    if (-not (Test-Path $ConfigDir)) {
        New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
    }

    $configPath = Join-Path $ConfigDir "agent.json"
    if (-not (Test-Path $configPath)) {
        Write-Host "Creating default configuration at '$configPath'..."
        $defaultConfig = [ordered]@{
            Telemetry = [ordered]@{
                PollingIntervalSeconds = 5
            }
            Mqtt = [ordered]@{
                Host   = ""
                Port   = 1883
                UseTls = $false
            }
        }
        $defaultConfig | ConvertTo-Json -Depth 4 | Set-Content -Path $configPath -Encoding UTF8
    }
    else {
        Write-Host "Preserving existing configuration at '$configPath'."
    }

    $exePath = Join-Path $InstallDir "Pulse.Agent.Windows.exe"
    $quotedBinPath = "`"$exePath`""

    if ($null -eq $existingService) {
        Write-Host "Registering Windows Service '$ServiceName'..."
        & sc.exe create $ServiceName binPath= $quotedBinPath start= auto DisplayName= $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe create failed with exit code $LASTEXITCODE."
        }
    }
    else {
        Write-Host "Updating Windows Service '$ServiceName' configuration..."
        & sc.exe config $ServiceName binPath= $quotedBinPath start= auto DisplayName= $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe config failed with exit code $LASTEXITCODE."
        }
    }

    & sc.exe description $ServiceName "Collects hardware telemetry and publishes it over MQTT for Pulse." | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null

    & sc.exe query PawnIO 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "PawnIO driver was not detected. CPU temperature sensors may be unavailable until PawnIO is installed: https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe"
    }

    if (-not $NoStart) {
        Write-Host "Starting service '$ServiceName'..."
        Start-Service -Name $ServiceName
        $service = Get-Service -Name $ServiceName
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
        Write-Host "Service '$ServiceName' is running."
    }

    Write-Host ""
    Write-Host "Installation complete."
    Write-Host "  Binaries : $InstallDir"
    Write-Host "  Config   : $configPath"
    Write-Host "  Service  : $ServiceName"
}
finally {
    if ($null -ne $stagingDir -and (Test-Path $stagingDir)) {
        Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
