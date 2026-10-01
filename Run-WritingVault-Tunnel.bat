@echo off
setlocal
set "BACKGROUND_MODE="
for %%A in (%*) do if /I "%%~A"=="-Background" set "BACKGROUND_MODE=1"

set "LAUNCHER=%~dp0tools\Run-WritingVaultTunnel.ps1"

if not exist "%LAUNCHER%" (
    echo ERROR: Tunnel launcher was not found:
    echo   %LAUNCHER%
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%LAUNCHER%" %*
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo Writing Vault tunnel exited with code %EXIT_CODE%.
    if not defined BACKGROUND_MODE pause
)

exit /b %EXIT_CODE%
