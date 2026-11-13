@echo off
:: Launches Tuvima Library (Engine + Dashboard) as it is: your data is left exactly as it was.
:: Double-click to start. Close this window (or press Ctrl+C) to stop the app.
:: The Dashboard opens in your browser at http://localhost:5016
cd /d "%~dp0"
where pwsh >nul 2>&1 && (set "PS=pwsh") || (set "PS=powershell")
echo.
echo  Tuvima Library - launching (data untouched)
echo.
dotnet restore MediaEngine.slnx --verbosity quiet
if errorlevel 1 goto :failed
%PS% -NoProfile -ExecutionPolicy Bypass -File "tools\Start-TuvimaApp.ps1"
if errorlevel 1 goto :failed
exit /b 0
:failed
echo.
echo  Something went wrong. Scroll up for the reason, and check the logs folder.
pause
exit /b 1
