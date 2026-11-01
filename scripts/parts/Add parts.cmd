@echo off
echo Close Safety Gate Support before continuing.
echo This updates the parts data only and creates a backup.
pause
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Add-Safe2CrossParts.ps1"
set "partsResult=%ERRORLEVEL%"
pause
exit /b %partsResult%
