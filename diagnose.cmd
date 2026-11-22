@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0bin\Release\L2PointAI.exe"
if not exist "%EXE%" set "EXE=%~dp0bin\Debug\L2PointAI.exe"
if not exist "%EXE%" set "EXE=%~dp0dist\L2PointAI.exe"

if not exist "%EXE%" (
  echo Cannot find L2PointAI.exe. Run run.cmd or build.ps1 first.
  pause
  exit /b 1
)

echo Using: %EXE%
echo.
"%EXE%" --diag
echo.
echo Log folder: "%LOCALAPPDATA%\L2PointAI\logs"
echo diag.txt written next to the exe.
echo.
pause
