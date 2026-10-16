@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Repair-GpuSpoofPending.ps1"
set "repairResult=%errorlevel%"
echo.
pause
exit /b %repairResult%
