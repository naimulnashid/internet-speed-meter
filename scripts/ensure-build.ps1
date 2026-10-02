<#
    Makes sure the dashboard has a production build to serve.

        powershell -ExecutionPolicy Bypass -File scripts\ensure-build.ps1
        powershell -ExecutionPolicy Bypass -File scripts\ensure-build.ps1 -RebuildStale
        powershell -ExecutionPolicy Bypass -File scripts\ensure-build.ps1 -Force

    Both launchers call this, so there is one answer to "is there a build, and
    is it current", in one place:

      - start-dashboard.bat calls it plainly. It builds only when there is no
        complete build, and WARNS when the source is newer than the build: the
        window is in front of you, so the warning is seen, and a build is a
        thing you run where its output and any failure are in front of you.
        FORCE_BUILD=1 there passes -Force.

      - dashboard-service.ps1 (the logon task) passes -RebuildStale. It has no
        window to warn in, and a warning nobody reads would leave it serving
        stale code indefinitely -- a quiet, convincing kind of wrong in a
        dashboard whose job is reporting current numbers. So it rebuilds.

    "Stale" means anything under src\ or config\, or package.json,
    next.config.mjs or tsconfig.json (all in dashboard\), is newer than
    .next\BUILD_ID. The same list as the other local dashboards' launchers;
    there is no config\ here today, and a missing folder is skipped.

    "A build" means BUILD_ID AND .next\server. BUILD_ID is written when a build
    finishes, but a .next emptied by hand, or half deleted, can leave it behind
    with nothing behind it -- and `next start` would then come up and fail
    every route.

    Installs dependencies first if dashboard\node_modules is missing.

    Writes to its output only, never to a log file. The logon task pipes that
    output into logs\dashboard.log through its own shared-append writer, which
    keeps every writer to that file on the same safe rule.

    Exit 0 means there is a build to serve; anything else means there is not.

    ASCII ONLY -- see the note at the top of dashboard-service.ps1.
#>

param(
    [switch]$RebuildStale,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$app = Join-Path (Split-Path -Parent $PSScriptRoot) 'dashboard'
Set-Location $app

function Say([string]$message) {
    Write-Output ("{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message)
}

# npm is npm.cmd on Windows, and a scheduled task's PATH is not an interactive
# shell's, so resolve it explicitly rather than assuming. No ?. operator: this
# runs under Windows PowerShell 5.1, where that is a parse error.
$npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
$npm = if ($npmCommand) { $npmCommand.Source } else { $null }

# Runs npm, passing its output through as plain lines. The preference drops to
# Continue for the call: under Windows PowerShell 5.1, redirecting a native
# command's stderr while it is Stop turns any stderr line into a terminating
# error, and npm writes ordinary progress there.
function Invoke-Npm([string[]]$arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $npm @arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }
}

function Test-Built {
    return (Test-Path '.next\BUILD_ID') -and (Test-Path '.next\server')
}

if (-not (Test-Path 'node_modules')) {
    if (-not $npm) { Say 'ERROR: npm.cmd not found on PATH. Is Node.js installed?'; exit 1 }
    Say 'Installing dependencies. This happens once and takes a minute...'
    Invoke-Npm @('install')
    if ($LASTEXITCODE -ne 0) { Say "ERROR: npm install failed with exit code $LASTEXITCODE."; exit 1 }
}

$reason = $null
if (-not (Test-Path '.next\BUILD_ID')) {
    $reason = 'No production build found - building (about 20s)...'
}
elseif (-not (Test-Path '.next\server')) {
    $reason = 'The build in .next is incomplete (no .next\server) - building (about 20s)...'
}
elseif ($Force) {
    $reason = 'Rebuilding, as asked (about 20s)...'
}
else {
    $builtAt = (Get-Item '.next\BUILD_ID').LastWriteTime
    $sources = @()
    foreach ($dir in 'src', 'config') {
        if (Test-Path $dir) {
            $sources += Get-ChildItem -Path $dir -Recurse -File -ErrorAction SilentlyContinue
        }
    }
    foreach ($file in 'package.json', 'next.config.mjs', 'tsconfig.json') {
        if (Test-Path $file) { $sources += Get-Item $file }
    }
    $newest = ($sources | Measure-Object -Property LastWriteTime -Maximum).Maximum

    if (($null -ne $newest) -and ($newest -gt $builtAt)) {
        if ($RebuildStale) {
            $reason = 'Source is newer than the build - rebuilding (about 20s)...'
        }
        else {
            Say ("WARNING: the build is from {0}, but the source changed at {1}." -f
                $builtAt.ToString('yyyy-MM-dd HH:mm'), $newest.ToString('yyyy-MM-dd HH:mm'))
            Say "Serving the OLD build. Run 'npm run build' in dashboard\ (or set FORCE_BUILD=1) to pick the change up."
        }
    }
}

if (-not $reason) { exit 0 }

if (-not $npm) { Say 'ERROR: npm.cmd not found on PATH. Is Node.js installed?'; exit 1 }
Say $reason
Invoke-Npm @('run', 'build')
if ($LASTEXITCODE -ne 0) { Say "ERROR: the build failed with exit code $LASTEXITCODE."; exit 1 }
if (-not (Test-Built)) {
    Say 'ERROR: the build reported success but left no .next\BUILD_ID or .next\server.'
    exit 1
}

Say 'Build complete.'
exit 0
