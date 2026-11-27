<#
.SYNOPSIS
    Uninstalls the Pulse Windows Session tray application for the current user.

.DESCRIPTION
    Stops any running Pulse.Agent.Windows.Session process, removes sign-in auto-start
    and the Start Menu shortcut, and deletes installed binaries.
    Preserves %LocalAppData%\Pulse\session.json unless -PurgeConfig is specified.

.EXAMPLE
    .\deploy\uninstall-session.ps1

.EXAMPLE
    .\deploy\uninstall-session.ps1 -PurgeConfig
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Pulse\Session"),
    [string]$ConfigDir = (Join-Path $env:LOCALAPPDATA "Pulse"),
    [switch]$PurgeConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runRegPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueName = "PulseSession"
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) "Pulse Session.lnk"

$running = Get-Process -Name "Pulse.Agent.Windows.Session" -ErrorAction SilentlyContinue
if ($null -ne $running) {
    Write-Host "Stopping Pulse.Agent.Windows.Session..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if (Test-Path $runRegPath) {
    $existing = Get-ItemProperty -Path $runRegPath -Name $runValueName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Write-Host "Removing sign-in auto-start entry '$runValueName'..."
        Remove-ItemProperty -Path $runRegPath -Name $runValueName
    }
}

if (Test-Path $shortcutPath) {
    Write-Host "Removing Start Menu shortcut '$shortcutPath'..."
    Remove-Item -Path $shortcutPath -Force
}

if (Test-Path $InstallDir) {
    Write-Host "Removing binaries at '$InstallDir'..."
    Remove-Item -Path $InstallDir -Recurse -Force
}

$configPath = Join-Path $ConfigDir "session.json"
if ($PurgeConfig) {
    if (Test-Path $configPath) {
        Write-Host "Removing configuration at '$configPath'..."
        Remove-Item -Path $configPath -Force
    }

    if ((Test-Path $ConfigDir) -and @(Get-ChildItem -Path $ConfigDir -Force).Count -eq 0) {
        Remove-Item -Path $ConfigDir -Force
    }
}
elseif (Test-Path $configPath) {
    Write-Host "Configuration preserved at '$configPath'. Re-run with -PurgeConfig to remove it."
}

Write-Host "Uninstall complete."
