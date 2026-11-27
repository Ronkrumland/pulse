<#
.SYNOPSIS
    Installs or upgrades the Pulse Windows Session tray application for the current user.

.DESCRIPTION
    Publishes Pulse.Agent.Windows.Session as a self-contained win-x64 build (unless -SourceDir is provided),
    installs or updates the binaries in -InstallDir (default: %LocalAppData%\Pulse\Session),
    preserves any existing %LocalAppData%\Pulse\session.json (seeding MQTT settings from
    %ProgramData%\Pulse\agent.json on first install if present), registers sign-in auto-start
    (unless -NoAutoStart is passed), creates a Start Menu shortcut, and launches the tray app
    (unless -NoStart is passed).

.EXAMPLE
    .\deploy\install-session.ps1

.EXAMPLE
    .\deploy\install-session.ps1 -NoAutoStart -NoStart

.EXAMPLE
    .\deploy\install-session.ps1 -SourceDir .\publish-session
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Pulse\Session"),
    [string]$ConfigDir = (Join-Path $env:LOCALAPPDATA "Pulse"),
    [string]$SourceDir,
    [switch]$NoAutoStart,
    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\Pulse.Agent.Windows.Session\Pulse.Agent.Windows.Session.csproj"
$runRegPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueName = "PulseSession"
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) "Pulse Session.lnk"
$stagingDir = $null

try {
    if ([string]::IsNullOrWhiteSpace($SourceDir)) {
        if (-not (Test-Path $projectPath)) {
            throw "Could not find project at '$projectPath'. Run from the repository or pass -SourceDir."
        }

        $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pulse-session-publish-" + [Guid]::NewGuid().ToString("N"))
        Write-Host "Publishing Pulse.Agent.Windows.Session (win-x64, self-contained)..."
        & dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $stagingDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
        $publishPath = $stagingDir
    }
    else {
        $publishPath = (Resolve-Path $SourceDir).Path
    }

    $stagedExe = Join-Path $publishPath "Pulse.Agent.Windows.Session.exe"
    if (-not (Test-Path $stagedExe)) {
        throw "Published executable not found at '$stagedExe'."
    }

    $running = Get-Process -Name "Pulse.Agent.Windows.Session" -ErrorAction SilentlyContinue
    if ($null -ne $running) {
        Write-Host "Stopping running Pulse.Agent.Windows.Session instance(s)..."
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
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

    $configPath = Join-Path $ConfigDir "session.json"
    if (-not (Test-Path $configPath)) {
        Write-Host "Creating default session configuration at '$configPath'..."
        $mqttConfig = [ordered]@{
            Host     = ""
            Port     = 1883
            DeviceId = $env:COMPUTERNAME
            UseTls   = $false
        }

        # Seed MQTT settings from %ProgramData%\Pulse\agent.json if already configured
        $agentConfigPath = Join-Path $env:ProgramData "Pulse\agent.json"
        if (Test-Path $agentConfigPath) {
            try {
                $agentJson = Get-Content -Path $agentConfigPath -Raw | ConvertFrom-Json
                if ($null -ne $agentJson.Mqtt) {
                    if ($null -ne $agentJson.Mqtt.Host) { $mqttConfig.Host = [string]$agentJson.Mqtt.Host }
                    if ($null -ne $agentJson.Mqtt.Port) { $mqttConfig.Port = [int]$agentJson.Mqtt.Port }
                    if ($null -ne $agentJson.Mqtt.DeviceId -and -not [string]::IsNullOrWhiteSpace($agentJson.Mqtt.DeviceId)) {
                        $mqttConfig.DeviceId = [string]$agentJson.Mqtt.DeviceId
                    }
                    if ($null -ne $agentJson.Mqtt.Username -and -not [string]::IsNullOrWhiteSpace($agentJson.Mqtt.Username)) {
                        $mqttConfig.Username = [string]$agentJson.Mqtt.Username
                    }
                    if ($null -ne $agentJson.Mqtt.Password -and -not [string]::IsNullOrEmpty($agentJson.Mqtt.Password)) {
                        $mqttConfig.Password = [string]$agentJson.Mqtt.Password
                    }
                    if ($null -ne $agentJson.Mqtt.UseTls) { $mqttConfig.UseTls = [bool]$agentJson.Mqtt.UseTls }
                    Write-Host "Seeded MQTT settings from '$agentConfigPath'."
                }
            }
            catch {
                Write-Warning "Could not read '$agentConfigPath' to seed MQTT settings: $_"
            }
        }

        $defaultConfig = [ordered]@{
            PollingIntervalSeconds = 5
            ServiceName            = "Pulse Agent"
            CustomGameProcesses    = @()
            Mqtt                   = $mqttConfig
        }
        $defaultConfig | ConvertTo-Json -Depth 4 | Set-Content -Path $configPath -Encoding UTF8
    }
    else {
        Write-Host "Preserving existing configuration at '$configPath'."
    }

    $exePath = Join-Path $InstallDir "Pulse.Agent.Windows.Session.exe"

    if (-not $NoAutoStart) {
        Write-Host "Registering sign-in auto-start ($runValueName)..."
        if (-not (Test-Path $runRegPath)) {
            New-Item -Path $runRegPath -Force | Out-Null
        }
        Set-ItemProperty -Path $runRegPath -Name $runValueName -Value "`"$exePath`""
    }

    Write-Host "Creating Start Menu shortcut at '$shortcutPath'..."
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = "Pulse Windows Session Agent"
    $shortcut.Save()

    if (-not $NoStart) {
        Write-Host "Starting Pulse.Agent.Windows.Session..."
        Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
    }

    Write-Host ""
    Write-Host "Installation complete."
    Write-Host "  Binaries  : $InstallDir"
    Write-Host "  Config    : $configPath"
    Write-Host "  Auto-Start: $(-not $NoAutoStart)"
    Write-Host "  Shortcut  : $shortcutPath"
}
finally {
    if ($null -ne $stagingDir -and (Test-Path $stagingDir)) {
        Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
