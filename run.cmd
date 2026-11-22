@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo  L2PointAI - build and run
echo ============================================
echo.

powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
  echo [FAILED] exit code %RC%
  echo Please send this window's content to the developer.
) else (
  echo [OK] application started.
  echo If no window appears, check this log:
  echo   "%LOCALAPPDATA%\L2PointAI\logs\startup.log"
  echo or run diagnose.cmd
)
echo.
pause
