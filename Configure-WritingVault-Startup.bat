@echo off
setlocal
set "ACTION=%~1"
if "%ACTION%"=="" set "ACTION=status"
set "CONFIGURATION=%~2"
if "%CONFIGURATION%"=="" set "CONFIGURATION=Release"
set "SCRIPT=%~dp0tools\Configure-WritingVaultStartup.ps1"
if not exist "%SCRIPT%" (
  echo ERROR: Startup configuration script was not found: %SCRIPT%
  pause
  exit /b 1
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" "%ACTION%" -Configuration "%CONFIGURATION%"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" pause
exit /b %EXIT_CODE%
