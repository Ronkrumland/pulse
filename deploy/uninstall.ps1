#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the Pulse Windows Agent service and binaries.

.DESCRIPTION
    Stops and removes the Pulse Agent Windows Service and deletes installed binaries.
    Preserves %ProgramData%\Pulse\agent.json unless -PurgeConfig is specified.

.EXAMPLE
    .\deploy\uninstall.ps1

.EXAMPLE
    .\deploy\uninstall.ps1 -PurgeConfig
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles "Pulse\Agent"),
    [string]$ConfigDir = (Join-Path $env:ProgramData "Pulse"),
    [string]$ServiceName = "Pulse Agent",
    [switch]$PurgeConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existingService) {
    if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force
        $existingService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }

    Write-Host "Removing Windows Service '$ServiceName'..."
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Host "Service '$ServiceName' is not installed."
}

if (Test-Path $InstallDir) {
    Write-Host "Removing binaries at '$InstallDir'..."
    Remove-Item -Path $InstallDir -Recurse -Force
}

if ($PurgeConfig) {
    if (Test-Path $ConfigDir) {
        Write-Host "Removing configuration at '$ConfigDir'..."
        Remove-Item -Path $ConfigDir -Recurse -Force
    }
}
elseif (Test-Path $ConfigDir) {
    Write-Host "Configuration preserved at '$ConfigDir'. Re-run with -PurgeConfig to remove it."
}

Write-Host "Uninstall complete."
