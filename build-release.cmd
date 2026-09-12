@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Check-Environment.ps1
if errorlevel 1 exit /b %errorlevel%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Build.ps1 -Configuration Release
if errorlevel 1 exit /b %errorlevel%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Test.ps1 -Suite All
if errorlevel 1 exit /b %errorlevel%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Publish.ps1 -Configuration Release -Runtime win-x64
if errorlevel 1 exit /b %errorlevel%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Package.ps1
if errorlevel 1 exit /b %errorlevel%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Verify-Latest.ps1
if errorlevel 1 exit /b %errorlevel%
echo Candidate packaging complete. Check reports for remaining NO-GO gates.
