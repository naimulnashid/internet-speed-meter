<#
    Stops the background dashboard.

    With no console window there is no Ctrl+C, so this is how you turn it off.

    This does not touch the meter, and no history is lost: the .exe records to
    disk whether or not the dashboard is running.

    It stops only THIS project's server, never "whatever is listening on the
    port". On Windows a server bound to 127.0.0.1 can share a port with another
    program's wildcard (0.0.0.0 or ::) listener, so a port can have two owners
    - and the other local dashboards are node too, so the process name cannot
    tell them apart. A listener counts as this dashboard only when its command
    line runs Next.js out of this project's own dashboard\node_modules.
    Anything else, including a process whose command line cannot be read, is
    left alone with a message saying what it is.

        -WhatIf    show which process would be stopped, and stop nothing
        -Port      try the rule on a spare port; the dashboard is on 7845

    ASCII ONLY -- see the note at the top of dashboard-service.ps1.
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param([int]$Port = 7845)

# -WhatIf would otherwise reach PowerShell's automatic import of the two
# modules used below and print a "What if: Set Alias" line for every alias they
# define. Import them first with it off; ShouldProcess still sees -WhatIf.
$dryRun = $WhatIfPreference
$WhatIfPreference = $false
Import-Module NetTCPIP, CimCmdlets
$WhatIfPreference = $dryRun

# "<project>\dashboard\node_modules\" rather than "<project>": the bare path
# would also match a sibling folder whose name merely starts with this one's.
$modules = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dashboard\node_modules') + '\'

# Launchers spell the same path differently: forward slashes, and the doubled
# separators npm writes into its shims ("node_modules\.bin\\..\next").
function ConvertTo-ComparablePath([string]$Text) {
    return (($Text -replace '/', '\') -replace '\\{2,}', '\')
}

function Test-ThisDashboard([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    $needle = ConvertTo-ComparablePath $modules
    return (ConvertTo-ComparablePath $CommandLine).IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

# Tell dashboard-service.ps1 this stop is wanted, before anything is stopped:
# it restarts a server that exits on its own, and without this marker a stop
# would look exactly like a crash. Written even when nothing is listening,
# because the service may be in its 60-second wait before a restart. A
# marker nobody reads is cleared when the service next starts.
if (-not $dryRun) {
    $logDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'logs'
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
    Set-Content -Path (Join-Path $logDir 'dashboard.stop') -Value (Get-Date -Format o) -Encoding ascii
}

$listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue

if (-not $listener) {
    Write-Host "The dashboard is not running (nothing is listening on port $Port)."
    exit 0
}

$ours = 0
foreach ($procId in ($listener.OwningProcess | Select-Object -Unique)) {
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$procId" -ErrorAction SilentlyContinue
    if (-not $proc) { continue }
    if (-not (Test-ThisDashboard $proc.CommandLine)) {
        $why = if ($proc.CommandLine) { "it runs: $($proc.CommandLine)" } else { 'its command line could not be read' }
        Write-Host "Port $Port is held by '$($proc.Name)' (PID $procId), which is not this dashboard - $why. Leaving it alone."
        continue
    }
    $ours++
    if ($PSCmdlet.ShouldProcess("$($proc.Name), PID $procId, port $Port", 'Stop the dashboard')) {
        Stop-Process -Id $procId -Force
        Write-Host "Stopped the dashboard ($($proc.Name), PID $procId)."
    }
}

if ($ours -eq 0) { exit 1 }
