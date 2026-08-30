@echo off
setlocal
cd /d "%~dp0"

echo Installing ChangExport for Revit 2026...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Revit2026.ps1"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo INSTALL FAILED.
    echo Please send a screenshot of this window.
) else (
    echo INSTALL COMPLETE.
    echo Close Revit 2026 completely, then start it again.
)
echo.
pause
exit /b %EXIT_CODE%
