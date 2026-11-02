@echo off
setlocal
set "LIGHTSVN_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%LIGHTSVN_POWERSHELL%" (
    echo ERROR: Windows PowerShell is required to build this Windows .NET Framework application. 1>&2
    exit /b 2
)
"%LIGHTSVN_POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build.ps1" %*
exit /b %errorlevel%
