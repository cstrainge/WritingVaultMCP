@echo off
setlocal

set "LAUNCHER=%~dp0tools\Run-WritingVaultImageProbeTunnel.ps1"
if not exist "%LAUNCHER%" (
    echo ERROR: Image-probe tunnel launcher was not found:
    echo   %LAUNCHER%
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%LAUNCHER%"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" pause
exit /b %EXIT_CODE%
