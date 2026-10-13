@echo off
setlocal

REM ─────────────────────────────────────────────────────────────────────────
REM  build-installer.bat — builds self-contained win-x64 binaries then runs
REM  Inno Setup to produce dist\TuvimaLibrary-Setup-{version}.exe
REM
REM  Prerequisites
REM    - .NET 10 SDK  (dotnet publish)
REM    - Inno Setup 6  (ISCC.exe on PATH, or set ISCC= env var)
REM ─────────────────────────────────────────────────────────────────────────

:: Locate ISCC (Inno Setup compiler)
if "%ISCC%"=="" (
    if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" (
        set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
    ) else if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" (
        set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
    ) else (
        echo ERROR: Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php
        echo        or set the ISCC environment variable to point to ISCC.exe
        exit /b 1
    )
)

:: Output directories
set "ENGINE_OUT=dist\win\engine"
set "DASHBOARD_OUT=dist\win\dashboard"
set "FFMPEG_SOURCE=tools\ffmpeg"
set "TESSERACT_SOURCE=tools\tesseract"

if not exist "%FFMPEG_SOURCE%\ffmpeg.exe" (
    echo ERROR: Packaged FFmpeg is missing: %FFMPEG_SOURCE%\ffmpeg.exe
    exit /b 1
)
if not exist "%FFMPEG_SOURCE%\ffprobe.exe" (
    echo ERROR: Packaged FFprobe is missing: %FFMPEG_SOURCE%\ffprobe.exe
    exit /b 1
)
"%FFMPEG_SOURCE%\ffmpeg.exe" -hide_banner -version >nul 2>&1 || exit /b 1
"%FFMPEG_SOURCE%\ffprobe.exe" -hide_banner -version >nul 2>&1 || exit /b 1
powershell -NoProfile -Command "if ((Get-FileHash -Algorithm SHA256 -LiteralPath '%FFMPEG_SOURCE%\ffmpeg.exe').Hash -ne '989A60089B9B1A98896A5BD99EE793AB6841724E1B2441D5EF3E5D17DB0B0938') { exit 1 }" || (
    echo ERROR: Packaged FFmpeg checksum does not match the approved release.
    exit /b 1
)
powershell -NoProfile -Command "if ((Get-FileHash -Algorithm SHA256 -LiteralPath '%FFMPEG_SOURCE%\ffprobe.exe').Hash -ne '001D80FDDF67BC303E91C6B8ECCDF53AF29A5F87ECF3837056B391CC3DD3F7B4') { exit 1 }" || (
    echo ERROR: Packaged FFprobe checksum does not match the approved release.
    exit /b 1
)
"%FFMPEG_SOURCE%\ffmpeg.exe" -hide_banner -encoders 2>&1 | findstr /c:"libx264" >nul || exit /b 1
"%FFMPEG_SOURCE%\ffmpeg.exe" -hide_banner -encoders 2>&1 | findstr /c:" aac " >nul || exit /b 1
"%FFMPEG_SOURCE%\ffmpeg.exe" -hide_banner -encoders 2>&1 | findstr /c:" webvtt " >nul || exit /b 1
"%FFMPEG_SOURCE%\ffmpeg.exe" -hide_banner -muxers 2>&1 | findstr /c:" hls " >nul || exit /b 1

if not exist "%TESSERACT_SOURCE%\tesseract.exe" (
    echo ERROR: Packaged Tesseract is missing: %TESSERACT_SOURCE%\tesseract.exe ^(run tools\Download-Tesseract.ps1^)
    exit /b 1
)
if not exist "%TESSERACT_SOURCE%\tessdata\eng.traineddata" (
    echo ERROR: Packaged Tesseract English data is missing: %TESSERACT_SOURCE%\tessdata\eng.traineddata
    exit /b 1
)
powershell -NoProfile -Command "if ((Get-FileHash -Algorithm SHA256 -LiteralPath '%TESSERACT_SOURCE%\tesseract.exe').Hash -ne 'BABB405F4366B480D02CD8FF2BAC8D497170F6C1711CE6F3D5D8BF0FB7FA6ED9') { exit 1 }" || (
    echo ERROR: Packaged Tesseract checksum does not match the approved release.
    exit /b 1
)
"%TESSERACT_SOURCE%\tesseract.exe" --version >nul 2>&1 || exit /b 1

echo.
echo ── Cleaning previous build output ──────────────────────────────────────
if exist dist\win rmdir /s /q dist\win

echo.
echo ── Publishing Engine (win-x64 self-contained) ───────────────────────────
dotnet publish src\MediaEngine.Api\MediaEngine.Api.csproj ^
    -p:TuvimaBundledAiRuntime=true ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained true ^
    --output "%ENGINE_OUT%" ^
    -p:PublishSingleFile=false ^
    -p:UseAppHost=true
if errorlevel 1 (
    echo ERROR: Engine publish failed.
    exit /b 1
)

echo.
echo ── Publishing Dashboard (win-x64 self-contained) ────────────────────────
dotnet publish src\MediaEngine.Web\MediaEngine.Web.csproj ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained true ^
    --output "%DASHBOARD_OUT%" ^
    -p:PublishSingleFile=false ^
    -p:UseAppHost=true
if errorlevel 1 (
    echo ERROR: Dashboard publish failed.
    exit /b 1
)

echo.
echo ── Running Inno Setup compiler ──────────────────────────────────────────
"%ISCC%" installer.iss
if errorlevel 1 (
    echo ERROR: Inno Setup compilation failed.
    exit /b 1
)

echo.
echo ── Done ─────────────────────────────────────────────────────────────────
echo Installer written to:  dist\TuvimaLibrary-Setup-*.exe
