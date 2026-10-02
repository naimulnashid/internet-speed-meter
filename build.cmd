@echo off
setlocal enabledelayedexpansion

rem Builds bin\InternetSpeedMeter.exe with the C# compiler that ships inside Windows.
rem No SDK, no NuGet, no internet access required.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: could not find the .NET Framework 4 C# compiler.
    echo Looked in %%WINDIR%%\Microsoft.NET\Framework[64]\v4.0.30319\csc.exe
    exit /b 1
)

pushd "%~dp0"

if not exist "bin" mkdir "bin"
if not exist "assets\app.ico" (
    echo Generating assets\app.ico ...
    powershell -NoProfile -ExecutionPolicy Bypass -File "tools\make-icon.ps1" || goto :fail
)

set "ICON="
if exist "assets\app.ico" set "ICON=/win32icon:assets\app.ico"

echo Compiling ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 ^
    /out:"bin\InternetSpeedMeter.exe" %ICON% ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    "src\*.cs" || goto :fail

echo.
echo Built bin\InternetSpeedMeter.exe
popd
exit /b 0

:fail
echo.
echo BUILD FAILED
popd
exit /b 1
