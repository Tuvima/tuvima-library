@echo off
:: Fresh start, then launch Tuvima Library (Engine + Dashboard).
:: Wipes the database and caches (asks you to type YES first). Keeps provider keys, plugin
:: settings, library paths, tools and OCR data. Never touches your media files.
:: Double-click to run. Close this window (or press Ctrl+C) to stop the app.
cd /d "%~dp0"
where pwsh >nul 2>&1 && (set "PS=pwsh") || (set "PS=powershell")
%PS% -NoProfile -ExecutionPolicy Bypass -File "tools\Reset-TuvimaData.ps1"
if errorlevel 1 goto :stopped
echo.
echo  Tuvima Library - launching on a fresh start
echo.
dotnet restore MediaEngine.slnx --verbosity quiet
if errorlevel 1 goto :failed
%PS% -NoProfile -ExecutionPolicy Bypass -File "tools\Start-TuvimaApp.ps1"
if errorlevel 1 goto :failed
exit /b 0
:stopped
echo.
echo  Stopped before launching. Nothing was started.
pause
exit /b 1
:failed
echo.
echo  Something went wrong. Scroll up for the reason, and check the logs folder.
pause
exit /b 1
