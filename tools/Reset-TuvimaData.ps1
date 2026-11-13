<#
.SYNOPSIS
  Gives Tuvima Library a fresh start: wipes the data store (database) and the caches the app rebuilds by itself.

.DESCRIPTION
  WIPED:  the database, artwork and picture caches, bookmark frames, scrub previews, staging, video variants and sidecar caches.
  KEPT:   provider keys and plugin settings (config and the data folder's secrets and plugin-directory), library paths
          (config\libraries.json), downloaded tools (FFmpeg, ExifTool, Tesseract), OCR language data, and the photo and
          personal-video intake folders.
  NEVER TOUCHED: your media files, anywhere. Only folders inside the Library's own data folder are deleted.
  No backup is made (standing rule for local checks).
#>
param(
    [switch]$Yes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$CoreJson = Join-Path $RepoRoot "config\core.json"

function Get-DataRoot {
    if ($env:TUVIMA_DATA_DIR) { return [System.IO.Path]::GetFullPath($env:TUVIMA_DATA_DIR) }
    return (Join-Path $env:LOCALAPPDATA "Tuvima\data")
}

function Get-LibraryRoot {
    if (-not (Test-Path -LiteralPath $CoreJson)) { throw "Cannot find $CoreJson" }
    $core = Get-Content -LiteralPath $CoreJson -Raw | ConvertFrom-Json
    $value = [string]$core.library_root
    if ([string]::IsNullOrWhiteSpace($value)) { throw "config\core.json has no library_root, so I cannot tell where the data store is." }
    if ($value.StartsWith("{TuvimaData}")) {
        $rest = $value.Substring("{TuvimaData}".Length).TrimStart('\', '/')
        $value = if ($rest) { Join-Path (Get-DataRoot) $rest } else { Get-DataRoot }
    }
    return [System.IO.Path]::GetFullPath($value)
}

function Stop-TuvimaApp {
    $found = @()
    $found += @(Get-Process -Name "MediaEngine.Api", "MediaEngine.Web" -ErrorAction SilentlyContinue)
    $dotnet = Get-CimInstance Win32_Process -Filter "name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*MediaEngine.Api*" -or $_.CommandLine -like "*MediaEngine.Web*" }
    foreach ($d in $dotnet) {
        $p = Get-Process -Id $d.ProcessId -ErrorAction SilentlyContinue
        if ($p) { $found += $p }
    }
    foreach ($p in @($found | Sort-Object Id -Unique)) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        $p.WaitForExit(10000) | Out-Null
        Write-Host "  Stopped the running app (PID $($p.Id))."
    }
}

$libraryRoot = Get-LibraryRoot
$dataFolder = Join-Path $libraryRoot ".data"
$dataRoot = Get-DataRoot

# Everything here lives inside the Library's own data folder and is rebuilt by the app.
$targets = @(
    (Join-Path $dataFolder "database"),
    (Join-Path $dataFolder "assets"),
    (Join-Path $dataFolder "bookmark-frames"),
    (Join-Path $dataFolder "scrub-previews"),
    (Join-Path $dataFolder "staging"),
    (Join-Path $dataFolder "variants"),
    (Join-Path $dataFolder "sidecars-cache"),
    (Join-Path $dataFolder "generated"),
    (Join-Path $dataRoot "hash-cache.db"),
    (Join-Path $dataRoot "hash-cache.db-shm"),
    (Join-Path $dataRoot "hash-cache.db-wal")
)

# Safety: every target must sit inside a data folder that belongs to the Library.
foreach ($t in $targets) {
    $full = [System.IO.Path]::GetFullPath($t)
    $inside = $full.StartsWith($dataFolder + "\", [StringComparison]::OrdinalIgnoreCase) -or
              $full.StartsWith($dataRoot + "\", [StringComparison]::OrdinalIgnoreCase)
    if (-not $inside -or $full.Length -le ($dataFolder.Length + 1)) { throw "Refusing to delete outside the Library data folder: $full" }
}

Write-Host ""
Write-Host "  TUVIMA LIBRARY - RESET" -ForegroundColor Yellow
Write-Host "  ----------------------"
Write-Host "  Data folder: $dataFolder"
Write-Host ""
Write-Host "  Will be WIPED (no backup):"
Write-Host "    - the database (all library records, profiles, sign-ins)"
Write-Host "    - artwork and picture caches, bookmark frames, scrub previews, staging, video variants"
Write-Host ""
Write-Host "  Will be KEPT:"
Write-Host "    - provider keys and plugin settings"
Write-Host "    - library folder paths (config\libraries.json)"
Write-Host "    - downloaded tools and OCR language data"
Write-Host "    - ALL of your media files (never touched)"
Write-Host ""

if (-not $Yes) {
    $answer = Read-Host "  Type YES to wipe and start fresh (anything else cancels)"
    if ($answer -cne "YES") {
        Write-Host "  Cancelled. Nothing was changed."
        exit 2
    }
}

Write-Host ""
Write-Host "  Stopping the app if it is running..."
Stop-TuvimaApp

$failed = $false
foreach ($t in $targets) {
    if (-not (Test-Path -LiteralPath $t)) { continue }
    try {
        Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction Stop
        Write-Host "  Wiped $t"
    }
    catch {
        $failed = $true
        Write-Host "  COULD NOT wipe $t : $($_.Exception.Message)" -ForegroundColor Red
    }
}

if ($failed) {
    Write-Host ""
    Write-Host "  Some files were still in use. Close anything using them and run this again." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "  Fresh start ready."
exit 0
