@echo off
REM ---------------------------------------------------------------------------
REM Stops the Speed Meter dashboard. Double-click this.
REM
REM When the dashboard runs from its logon task it runs HIDDEN - there is no
REM console window to Ctrl+C - so this is the off switch. It also works on a
REM server started by start-dashboard.bat or `npm run dev`: it stops THIS
REM project's server on port 7845, however it was started, and leaves any other
REM program on that port alone.
REM
REM Run it before `npm run dev` or `npm run build` in dashboard\: they share
REM .next with the running server, and a build under a live server replaces
REM chunks it holds open.
REM
REM It stops the SERVER only. The meter keeps recording - the .exe writes its
REM history to disk on its own and nothing about it waits on the dashboard. It
REM also leaves the logon task REGISTERED, so the dashboard comes back at your
REM next sign-in; to unregister it as well, run
REM   powershell -ExecutionPolicy Bypass -File scripts\install-autostart.ps1 -Remove
REM ---------------------------------------------------------------------------
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\dashboard-stop.ps1"
echo.
pause
