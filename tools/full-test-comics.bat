@echo off
:: ─────────────────────────────────────────────────────────────────────────────
:: full-test-comics.bat
:: Wipes the database + ALL library files, seeds COMICS only,
:: and kicks off the ingestion pipeline.
::
:: Usage:  full-test-comics.bat
:: ─────────────────────────────────────────────────────────────────────────────

set ENGINE_URL=http://localhost:61495
set TYPES=comics
set LABEL=COMICS

echo.
echo  FULL TEST — %LABEL%
echo  Engine: %ENGINE_URL%
echo ─────────────────────────────────────────────────────────────────────────────
echo.
echo  [!] WARNING: This will PERMANENTLY DELETE:
echo        - The entire database (all library records)
echo        - All files in the library root and watch folders
echo.
echo  Press Ctrl+C NOW to cancel, or any other key to continue...
pause >nul
echo.

echo [1/3] Checking engine is reachable...
curl -sf "%ENGINE_URL%/system/status" >nul 2>&1
if errorlevel 1 (
    echo  ERROR: Engine not responding at %ENGINE_URL%
    echo  Start the engine first:  dotnet run --project src\MediaEngine.Api --launch-profile "MediaEngine.Api"
    echo.
    pause
    exit /b 1
)
echo  OK

echo.
echo [2/3] Wiping database and library files...
curl -s -X POST "%ENGINE_URL%/dev/wipe" ^
     -H "Accept: application/json" ^
     | powershell -NoProfile -Command "$r = $input | ConvertFrom-Json; Write-Host '  '$r.message; $r.details | ForEach-Object { Write-Host '   ·' $_ }"
echo.

echo [3/3] Seeding %LABEL% + scanning into pipeline...
echo.
curl -s -X POST "%ENGINE_URL%/dev/full-test?types=%TYPES%&wipe=false" ^
     -H "Accept: application/json" ^
     | powershell -NoProfile -Command "$input | ConvertFrom-Json | ConvertTo-Json -Depth 5"

echo.
echo ─────────────────────────────────────────────────────────────────────────────
echo  Monitor progress:
echo    Batches   : GET %ENGINE_URL%/ingestion/batches
echo    Items     : GET %ENGINE_URL%/dev/library-items?limit=50
echo    Review    : GET %ENGINE_URL%/review/pending
echo    Activity  : GET %ENGINE_URL%/activity/recent
echo ─────────────────────────────────────────────────────────────────────────────
echo.
pause
