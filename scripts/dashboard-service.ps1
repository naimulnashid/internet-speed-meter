<#
    Runs the Speed Meter dashboard in the background, with no console window.

    Invoked by scripts\dashboard-hidden.vbs, which is what the "Speed Meter
    Dashboard" logon task runs. Safe to run by hand too.

    Nothing is visible while this runs, so everything it does is appended to
    logs\dashboard.log. That file is the only way to find out why the dashboard
    did not come up.

    The dashboard is OPTIONAL. The meter records to disk whether or not any of
    this is installed, and removing the task loses no history.

    It listens on this machine only (127.0.0.1). Pass -Lan to listen on every
    interface so a phone on the same Wi-Fi can open it - which it does only
    when lan-ready.ps1 finds a site password. Otherwise it logs why and serves
    on this machine only, rather than not at all.

    ASCII ONLY -- do not paste in an em-dash or a bullet. Windows PowerShell 5.1
    reads a BOM-less .ps1 as ANSI, and a UTF-8 em-dash decodes to CP1252 0x94 =
    U+201D, which PowerShell accepts as a STRING DELIMITER. It closes the string
    early and silently changes the logic of the enclosing block.
#>

param([switch]$Lan)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is reliable in the body (it is NOT during param() defaults).
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'dashboard'

$port = 7845
$logDir = Join-Path $root 'logs'
$log = Join-Path $logDir 'dashboard.log'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

<#
    The log has more than one writer at once. While the dashboard runs, its
    output streams in here for as long as it lives - and a second launch (a
    re-run task, a double-clicked .vbs) still has to add its "Already running"
    line. Out-File and Add-Content both open the file refusing other writers,
    so that line used to be lost and the launch crashed on an IOException.

    So every writer here opens the log sharing it (FileShare.ReadWrite), with
    the AppendData right and nothing else. That right is what makes sharing
    safe: with no general write access, Windows places each write at the end of
    the file as one operation, wherever it has grown to since. A plain write
    handle keeps its own position, and seeking to the end before each write is
    not enough - measured in a sibling project with two processes appending at
    once, seek-then-write lost 2677 of 6000 lines, each written over by the
    other; AppendData lost none, in Windows PowerShell 5.1 and PowerShell 7.

    One Write() per line keeps each line one operation. The bytes are what
    Out-File wrote: UTF-8, CRLF, and a BOM on a new file.
#>
$logEncoding = New-Object System.Text.UTF8Encoding $false
$logPreamble = (New-Object System.Text.UTF8Encoding $true).GetPreamble()

function Open-LogStream {
    $mode = [System.IO.FileMode]::Append
    $rights = [System.Security.AccessControl.FileSystemRights]::AppendData
    $share = [System.IO.FileShare]::ReadWrite
    $none = [System.IO.FileOptions]::None
    if ($PSVersionTable.PSEdition -eq 'Core') {
        # .NET Core dropped this FileStream constructor; the same call lives here.
        return [System.IO.FileSystemAclExtensions]::Create((New-Object System.IO.FileInfo $log),
            $mode, $rights, $share, 4096, $none, $null)
    }
    return New-Object System.IO.FileStream($log, $mode, $rights, $share, 4096, $none)
}

function Add-LogLine([System.IO.FileStream]$stream, [string]$text) {
    $bytes = $logEncoding.GetBytes($text + [Environment]::NewLine)
    if ($stream.Length -eq 0) { $bytes = [byte[]]($logPreamble + $bytes) }
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

function Write-Log($message) {
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message
    # Logging is never what stops this script. A writer that does not share -
    # a copy of this script from before the log was shared, still serving -
    # can hold the file for hours, so try briefly and then carry on without.
    for ($try = 0; $try -lt 5; $try++) {
        try {
            $stream = Open-LogStream
            try { Add-LogLine $stream $line } finally { $stream.Dispose() }
            return
        }
        catch { Start-Sleep -Milliseconds 200 }
    }
}

# Streams a command's output into the log line by line as it arrives; the
# server runs for hours, so nothing may wait for it to finish. Strings go in as
# they are, anything else through the same formatting Out-File applied.
function Write-LogOutput {
    begin { $stream = Open-LogStream }
    process {
        $lines = if ($_ -is [string]) { $_ } else { $_ | Out-String -Stream }
        foreach ($text in $lines) { Add-LogLine $stream $text }
    }
    end { $stream.Dispose() }
}

# Is whatever holds $port THIS dashboard? The same rule as dashboard-stop.ps1;
# change the two together. A port is not an identity: on Windows a server bound
# to 127.0.0.1 can share one with another program's wildcard (0.0.0.0 or ::)
# listener, and the other local dashboards are node too. A listener is this
# dashboard only when its command line runs Next.js out of this project's own
# dashboard\node_modules - anchored there, since the bare project path would
# also match a sibling folder whose name merely starts with this one's.
$modules = (Join-Path $app 'node_modules') + '\'

function ConvertTo-ComparablePath([string]$Text) {
    return (($Text -replace '/', '\') -replace '\\{2,}', '\')
}

function Test-ThisDashboard([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    $needle = ConvertTo-ComparablePath $modules
    return (ConvertTo-ComparablePath $CommandLine).IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-PortHolder {
    $found = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    foreach ($procId in ($found.OwningProcess | Select-Object -Unique)) {
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$procId" -ErrorAction SilentlyContinue
        if (-not $proc) { continue }
        [pscustomobject]@{ Id = [int]$procId; Name = $proc.Name; Ours = Test-ThisDashboard $proc.CommandLine }
    }
}

try {
    if (-not (Test-Path $app)) {
        Write-Log "ERROR: no dashboard folder at $app."
        exit 1
    }

    Set-Location $app

    # Already listening? Never start a second server on the port. If it is this
    # dashboard, another copy is up and there is nothing to do. If it is some
    # other program, say so - this used to log "Already running" for a port the
    # dashboard did not hold, leaving it down until the next logon with a log
    # saying it was up - and do not start beside it either: a different bind
    # address would let both listen.
    $held = @(Get-PortHolder)
    if ($held.Count -gt 0) {
        $ours = @($held | Where-Object { $_.Ours })
        if ($ours.Count -gt 0) {
            Write-Log "Already running on port $port (PID $(($ours.Id) -join ', ')) - nothing to do."
            exit 0
        }
        $others = ($held | ForEach-Object { "'$($_.Name)' PID $($_.Id)" }) -join '; '
        Write-Log "ERROR: port $port is held by another program ($others), not this dashboard. Dashboard not started."
        exit 1
    }

    # npm is npm.cmd on Windows, and a scheduled task's PATH is not an
    # interactive shell's, so resolve it explicitly rather than assuming.
    # No ?. operator here: this runs under Windows PowerShell 5.1, where that is
    # a parse error and the script dies before it can log why.
    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    $npm = if ($npmCommand) { $npmCommand.Source } else { $null }
    if (-not $npm) {
        Write-Log 'ERROR: npm.cmd not found on PATH. Is Node.js installed?'
        exit 1
    }

    # Dependencies, and a build that is complete and current. The rule lives in
    # ensure-build.ps1, shared with start-dashboard.bat; -RebuildStale is this
    # launcher's half of it. Without the rebuild it would build once and then
    # serve stale code forever -- which for a dashboard whose whole job is
    # reporting current numbers is a quiet, convincing kind of wrong.
    & (Join-Path $PSScriptRoot 'ensure-build.ps1') -RebuildStale *>&1 | Write-LogOutput
    if ($LASTEXITCODE -ne 0) {
        Write-Log "ERROR: no build to serve (ensure-build exited $LASTEXITCODE). Dashboard not started."
        exit 1
    }
    Set-Location $app

    # The history may live on another drive. Not fatal -- the dashboard renders
    # an empty state that says so -- but worth recording, because an absent drive
    # is exactly the case where the numbers would otherwise look like zeroes.
    $settings = Join-Path $env:APPDATA 'InternetSpeedMeter\settings.ini'
    $logFolder = 'C:\Users\Default\AppData\Local\InternetSpeedMeter\history'
    if (Test-Path $settings) {
        $line = Select-String -Path $settings -Pattern '^\s*logfolder\s*=' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($line) {
            $logFolder = [Environment]::ExpandEnvironmentVariables(
                ($line.Line -replace '^\s*logfolder\s*=\s*', '').Trim())
        }
    } else {
        $logFolder = Join-Path $env:LOCALAPPDATA 'InternetSpeedMeter\history'
        Write-Log "No settings.ini yet; assuming the meter's default of $logFolder."
    }

    if (-not (Test-Path $logFolder)) {
        Write-Log "WARNING: no recorded history at $logFolder. The dashboard will show an empty state."
    }

    $script = 'start'
    $scope = 'this machine only'
    if ($Lan) {
        & (Join-Path $PSScriptRoot 'lan-ready.ps1') *>&1 | Write-LogOutput
        if ($LASTEXITCODE -eq 0) {
            $script = 'start:lan'
            $scope = 'every network interface'
        }
        else {
            Write-Log 'WARNING: network access (-Lan) was refused - see the line above. Serving on this machine only.'
        }
        Set-Location $app
    }

    # Restart on a crash. The logon task cannot do this itself: it runs
    # dashboard-hidden.vbs, which starts this script and exits at once, so as
    # far as Task Scheduler knows the task finished long before any crash. So
    # this script stays with the server and restarts it when it exits without
    # being asked to - 3 times at most within 10 minutes, a minute apart, the
    # same policy as the task's own restart setting. A server that keeps dying
    # (a broken build, say) is left down rather than restarted forever.
    #
    # "Asked to" is dashboard-stop.ps1, which writes logs\dashboard.stop before
    # it stops anything. A marker left over from an earlier stop is cleared
    # here, so it cannot end this run's first crash-restart.
    $stopMarker = Join-Path $logDir 'dashboard.stop'
    Remove-Item $stopMarker -ErrorAction SilentlyContinue
    $maxRestarts = 3
    $crashes = @()

    while ($true) {
        Write-Log "Starting the dashboard on port $port, listening on $scope."
        & $npm run $script *>&1 | Write-LogOutput
        $code = $LASTEXITCODE

        if (Test-Path $stopMarker) {
            Remove-Item $stopMarker -ErrorAction SilentlyContinue
            Write-Log "Server stopped on request (exit code $code)."
            break
        }

        $now = Get-Date
        $crashes = @($crashes | Where-Object { $_ -gt $now.AddMinutes(-10) }) + $now
        if ($crashes.Count -gt $maxRestarts) {
            Write-Log "ERROR: the server exited unexpectedly (code $code), the $($crashes.Count)th time in 10 minutes. Not restarting it again until the next logon; the lines above say why."
            exit 1
        }
        Write-Log "WARNING: the server exited unexpectedly (code $code). Restarting in 60 s (restart $($crashes.Count) of $maxRestarts)."
        Start-Sleep -Seconds 60

        # Stopped during the wait, or started again by hand meanwhile? Then
        # this restart is not wanted, and on a held port it would only fail.
        if (Test-Path $stopMarker) {
            Remove-Item $stopMarker -ErrorAction SilentlyContinue
            Write-Log 'Stop requested while waiting to restart - not restarting.'
            break
        }
        $held = @(Get-PortHolder)
        if ($held.Count -gt 0) {
            Write-Log "Port $port is in use again (PID $(($held.Id) -join ', ')) - not restarting."
            break
        }
    }
}
catch {
    Write-Log "ERROR: $($_.Exception.Message)"
    exit 1
}
