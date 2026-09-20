@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_cs2_mod.ps1" %*
if errorlevel 1 (
  echo.
  echo CSLModernMap CS2 deployment failed.
  exit /b 1
)
endlocal
