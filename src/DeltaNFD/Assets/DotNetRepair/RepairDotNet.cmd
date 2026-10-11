@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0RepairDotNet.ps1"
if errorlevel 1 pause
