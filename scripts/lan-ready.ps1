<#
    May the dashboard listen beyond this PC? Exit 0 for yes, 1 for no.

        powershell -ExecutionPolicy Bypass -File scripts\lan-ready.ps1

    Both launchers ask this before network access - dashboard-service.ps1 for
    -Lan, start-dashboard.bat for DASHBOARD_LAN=1 - and serve on this machine
    only when the answer is no. One rule, in one place.

    The rule is a site password of at least 8 characters (the same minimum the
    dashboard enforces). On localhost an open dashboard only protects you from
    yourself, which is why a password is optional there. On the network it is
    the only thing in front of the dashboard, so it is not optional.

    SESSION_SECRET is checked too, but only warned about: without it a session
    cookie captured on the Wi-Fi (this is plain HTTP) can be used to test
    password guesses offline, which a long password survives and a short one
    does not.

    Reads the process environment first, then dashboard\.env.local - the two
    places Next takes these values from. Never prints a value.

    ASCII ONLY -- see the note at the top of dashboard-service.ps1.
#>

$ErrorActionPreference = 'Stop'

$envFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'dashboard\.env.local'

# The value of NAME, from the environment or .env.local; '' when unset.
function Get-Setting([string]$name) {
    $fromEnv = [Environment]::GetEnvironmentVariable($name)
    if ($fromEnv) { return $fromEnv }
    if (-not (Test-Path $envFile)) { return '' }
    foreach ($line in Get-Content $envFile) {
        if ($line -match ('^\s*' + [regex]::Escape($name) + '\s*=\s*(.*)$')) {
            $value = $Matches[1].Trim()
            # dotenv strips one pair of matching quotes.
            if ($value.Length -ge 2 -and ($value[0] -eq '"' -or $value[0] -eq "'") -and $value[-1] -eq $value[0]) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            return $value
        }
    }
    return ''
}

$password = Get-Setting 'DASHBOARD_PASSWORD'
if ($password.Trim().Length -eq 0) {
    Write-Output 'Network access refused: no DASHBOARD_PASSWORD is set in dashboard\.env.local.'
    Write-Output 'Off this PC, the password is the only thing in front of the dashboard. See the README, "Using it from your phone".'
    exit 1
}
if ($password.Length -lt 8) {
    Write-Output "Network access refused: DASHBOARD_PASSWORD is only $($password.Length) characters; use at least 8."
    exit 1
}

if ((Get-Setting 'SESSION_SECRET').Trim().Length -eq 0) {
    Write-Output 'WARNING: SESSION_SECRET is not set. A session cookie captured on the Wi-Fi could be used to test password guesses offline. See dashboard\.env.example.'
}
exit 0
