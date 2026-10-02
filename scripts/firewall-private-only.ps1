<#
    Keep the dashboard off PUBLIC networks.

        powershell -ExecutionPolicy Bypass -File scripts\firewall-private-only.ps1
        powershell -ExecutionPolicy Bypass -File scripts\firewall-private-only.ps1 -Remove
        npm --prefix dashboard run firewall

    Run from an ADMINISTRATOR PowerShell: firewall rules need elevation.

    ------------------------------------------------------------------------
    WHY

    The dashboard listens on every interface when you ask for network access
    (install-autostart.ps1 -Lan, `npm run start:lan` in dashboard\, or
    DASHBOARD_LAN=1 for start-dashboard.bat), so your phone on the home Wi-Fi
    can open it. On localhost only, this rule changes nothing, and it is
    harmless to keep.

    But Windows' first-run prompt for node.exe typically ends with node
    allowed on BOTH Private and Public profiles, for any port. So a laptop
    that joins cafe or hotel Wi-Fi offers the login form to everyone on that
    network -- and the password travels in cleartext, because this is plain
    HTTP.

    This adds ONE inbound BLOCK rule for the dashboard's port on the Public
    profile. Windows Firewall evaluates block rules before allow rules, so it
    wins over node.exe's blanket allow without touching that rule, and it
    leaves every other node program alone.

    ------------------------------------------------------------------------
    !! YOUR HOME NETWORK MUST BE "PRIVATE" FIRST, or your phone is cut off.

    Windows files many home networks as Public by default. The script checks
    the networks you are connected to right now and REFUSES if any is Public,
    because blocking then would silently cut your phone off from the
    dashboard on that network -- it would only see a connection error.

    To fix: Settings > Network & internet > Wi-Fi (or Ethernet) > your
    network > Network profile type > Private. Then run this again. Use
    -Force only if you really mean to block on the network you are on.

    Keep this file pure ASCII: Windows PowerShell 5.1 reads a BOM-less script as ANSI.
#>

[CmdletBinding()]
param(
    [int]$Port = 7845,
    [switch]$Remove,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$RuleName = "Speed Meter Dashboard - block port $Port on Public networks"

function Write-Ok   { param($m) Write-Host "  [OK]   $m" -ForegroundColor Green }
function Write-Info { param($m) Write-Host "         $m" -ForegroundColor DarkGray }
function Write-Bad  { param($m) Write-Host "  [FAIL] $m" -ForegroundColor Red }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Bad 'needs an Administrator PowerShell (firewall rules require elevation)'
    exit 1
}

$existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue

if ($Remove) {
    if ($existing) {
        $existing | Remove-NetFirewallRule
        Write-Ok "removed: $RuleName"
    } else {
        Write-Info 'no such rule; nothing to remove'
    }
    exit 0
}

$public = @(Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' })
if ($public.Count -gt 0 -and -not $Force) {
    Write-Bad "you are connected to $($public.Count) network(s) marked PUBLIC right now"
    Write-Info 'Blocking now would cut your phone off from the dashboard on this network.'
    Write-Info 'If this is your home network, mark it Private first:'
    Write-Info '  Settings > Network & internet > Wi-Fi > (network) > Network profile type > Private'
    Write-Info 'Then run this again. Use -Force to block anyway.'
    exit 1
}

if ($existing) {
    Write-Ok "already in place: $RuleName"
} else {
    New-NetFirewallRule -DisplayName $RuleName `
        -Description 'Added by scripts\firewall-private-only.ps1 in the Internet Speed Meter repo.' `
        -Direction Inbound -Protocol TCP -LocalPort $Port -Profile Public -Action Block | Out-Null
    Write-Ok "added: $RuleName"
}
Write-Info 'Private and Domain networks are unaffected; your phone still reaches it at home.'
Write-Info "Undo with: powershell -ExecutionPolicy Bypass -File scripts\firewall-private-only.ps1 -Remove"
