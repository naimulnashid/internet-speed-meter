<#
    Registers (or removes) the logon task that starts the dashboard invisibly.

        powershell -ExecutionPolicy Bypass -File scripts\install-autostart.ps1
        powershell -ExecutionPolicy Bypass -File scripts\install-autostart.ps1 -Remove
        powershell -ExecutionPolicy Bypass -File scripts\install-autostart.ps1 -Lan

    -Lan registers the task to listen on every network interface, so a phone on
    the same Wi-Fi can open the dashboard. It needs a site password, which the
    service checks at every start (lan-ready.ps1); without one it serves on
    this machine only. Re-run without -Lan to switch back.

    Runs as you, only when you are logged on, so Windows never has to store your
    password and no admin rights are needed.

    This has NOTHING to do with the meter starting at logon. That is the tray
    menu's "Start with Windows", a per-user HKCU\...\Run entry written by the
    .exe itself. Removing this task stops the dashboard being served; it does
    not stop recording, and no history is lost.

    ASCII ONLY -- see the note at the top of dashboard-service.ps1.
#>

param([switch]$Remove, [switch]$Lan)

$ErrorActionPreference = 'Stop'

$taskName = 'Start Speed Meter Dashboard'
$root = Split-Path -Parent $PSScriptRoot
$vbs = Join-Path $root 'scripts\dashboard-hidden.vbs'

if ($Remove) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Removed the '$taskName' logon task. The dashboard will no longer start automatically."
        Write-Host "Anything already running keeps running - use scripts\dashboard-stop.ps1 to stop it."
        Write-Host "Recording is unaffected; the meter writes to disk on its own."
    } else {
        Write-Host "No '$taskName' task is registered."
    }
    exit 0
}

if (-not (Test-Path $vbs)) { throw "Launcher not found at $vbs" }

$vbsArgs = if ($Lan) { '"{0}" -Lan' -f $vbs } else { '"{0}"' -f $vbs }
$action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument $vbsArgs
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

# Defaults built for laptops actively fight a long-running server: Windows will
# refuse to start it on battery and kill it after three days. Turn all of that
# off. StartWhenAvailable catches a logon the task missed.
#
# RestartCount covers the task failing to START. It cannot see the server
# crash later - the task runs dashboard-hidden.vbs, which exits as soon as it
# has launched the service - so dashboard-service.ps1 restarts the server
# itself, on the same 3-times-a-minute-apart policy.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -DontStopOnIdleEnd `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $taskName `
    -Action $action -Trigger $trigger -Settings $settings `
    -Description 'Starts the local Speed Meter dashboard in the background at logon (http://localhost:7845).' `
    -Force | Out-Null

$scope = if ($Lan) { 'every network interface' } else { 'this machine only' }
Write-Host "Registered '$taskName'. It will start the dashboard hidden at every logon, listening on $scope."
if ($Lan) {
    & (Join-Path $PSScriptRoot 'lan-ready.ps1')
    Write-Host "Keep it off cafe and hotel Wi-Fi: run 'npm --prefix dashboard run firewall' once from an Administrator shell."
}
Write-Host "First run builds the app, which takes about 20 seconds; see logs\dashboard.log."
