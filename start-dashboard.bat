@echo off
setlocal

REM ---------------------------------------------------------------------------
REM Starts the Speed Meter dashboard and opens it in your browser.
REM
REM This is the MANUAL way in. Normally the "Start Speed Meter Dashboard" logon
REM task starts it hidden at every sign-in (scripts\install-autostart.ps1
REM registers it). This file is for when that task is not installed, or you
REM simply want a window you can watch the server in.
REM
REM It does NOT touch RECORDING. The meter (.exe) writes its history to disk on
REM its own and knows nothing about the dashboard, so closing this window, or
REM never opening it, loses nothing.
REM
REM Double-click this file. It will, in order:
REM   1. install dependencies if dashboard\node_modules is missing
REM   2. build the production bundle ONLY if there isn't a whole one - after a code
REM      change you rebuild yourself with `npm run build`; this warns if you
REM      forgot rather than rebuilding behind your back
REM   3. start the server on http://localhost:7845
REM   4. open your browser once the server is actually responding
REM
REM By default only this machine can reach it. To open it from your phone on
REM the same Wi-Fi, set DASHBOARD_LAN=1 before running this - it then listens on
REM every network interface, but only once scripts\lan-ready.ps1 finds a site
REM password (see the README, "Using it from your phone").
REM
REM Keep this window open - closing it stops the dashboard.
REM ---------------------------------------------------------------------------

cd /d "%~dp0dashboard"

set "PORT=7845"
set "URL=http://localhost:%PORT%"
set "START_SCRIPT=start"

where npm >nul 2>&1
if errorlevel 1 (
    echo ERROR: npm was not found on your PATH.
    echo Install Node.js 22.5+ from https://nodejs.org and try again.
    echo.
    pause
    exit /b 1
)

REM --- Is something already serving the port? --------------------------------
REM This check earns its place twice over. Beyond the obvious "the logon task
REM already started it", `npm run dev` also binds 7845 - and a `next build`
REM underneath a live server replaces chunks it holds open, killing it on the
REM next request. Exiting here means this window can never do that to one.
netstat -ano | findstr /r /c:"LISTENING" | findstr /c:":%PORT% " >nul 2>&1
if not errorlevel 1 (
    echo The dashboard is already running on port %PORT%.
    echo.
    echo NOTE: this window did not start it - something else is already serving
    echo that port. Most likely the "Start Speed Meter Dashboard" logon task, or
    echo an "npm run dev" you left running. Nothing to do; opening the browser.
    echo Run stop-dashboard.bat first if you want this window to serve it instead.
    start "" "%URL%"
    echo.
    pause
    exit /b 0
)

REM --- Dependencies and a production build -----------------------------------
REM scripts\ensure-build.ps1 decides, and it is the SAME script the logon task
REM runs, so the two launchers never disagree about what "a build" or "stale"
REM means. Called plainly, as here, it:
REM   - installs dependencies if node_modules is missing
REM   - builds when there is no complete build (BUILD_ID and .next\server) -
REM     `npm start` against no `.next` exits immediately, so there would be
REM     nothing to open
REM   - only WARNS when the source is newer than the build. Building on every
REM     launch would cost ~20s each time and turn a broken build into a start-up
REM     failure; a build is a thing you run after changing code, where its
REM     output is in front of you. (The logon task rebuilds instead: it has no
REM     window to warn in.)
REM
REM Set FORCE_BUILD=1 before running this to rebuild anyway.
set "FORCE="
if not "%FORCE_BUILD%"=="" set "FORCE=-Force"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\ensure-build.ps1" %FORCE%
if errorlevel 1 (
    echo.
    echo ERROR: there is no build to serve. See the messages above.
    pause
    exit /b 1
)

REM --- Network access, if asked for ---------------------------------------------
REM lan-ready.ps1 is the same check the logon task makes for -Lan: no site
REM password, no network access. It serves on this machine only rather than
REM not at all, and says why.
if "%DASHBOARD_LAN%"=="1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\lan-ready.ps1"
    if errorlevel 1 (
        echo Serving on this machine only.
        echo.
    ) else (
        set "START_SCRIPT=start:lan"
    )
)

REM --- Open the browser once the server is listening -------------------------
REM A listening port is the signal, not an HTTP answer. `next start` opens the
REM port a moment before it has finished starting, but holds any request that
REM arrives in that moment until it is ready, rather than refusing it - so once
REM the port listens, the browser's request will be answered. Nothing else can
REM be the listener: the check above exits if anything held the port already.
REM
REM This is what the Data Usage dashboard's launcher does, and it sidesteps
REM everything an HTTP probe has to get right: localhost resolving to ::1 first
REM (a ~2 s fallback against a server on 127.0.0.1 alone), a login redirect
REM that names localhost, and a password gate answering 401.
REM
REM /b keeps the waiter in this console, so closing the window takes it along,
REM and its "did not start" message lands here rather than in a window of its
REM own.
start "" /b powershell -NoProfile -ExecutionPolicy Bypass -Command "$deadline = (Get-Date).AddSeconds(180); while ((Get-Date) -lt $deadline) { if (Get-NetTCPConnection -LocalPort %PORT% -State Listen -ErrorAction SilentlyContinue) { Start-Process '%URL%'; exit 0 }; Start-Sleep -Milliseconds 400 }; Write-Host 'The server did not start within 3 minutes - see the output above.'; exit 1"

echo.
echo Starting the dashboard on %URL%
if "%START_SCRIPT%"=="start:lan" (
    echo Listening on every network interface - open it from your phone at this PC's address, port %PORT%.
) else (
    echo Listening on this machine only. Set DASHBOARD_LAN=1 to open it from your phone.
)
echo Your browser will open automatically once it is ready.
echo.
echo Close this window to stop the dashboard. The meter keeps recording either way.
echo.

call npm run %START_SCRIPT%
