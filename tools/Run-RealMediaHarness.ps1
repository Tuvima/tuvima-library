[CmdletBinding()]
param(
    [string]$SourceRoot = 'C:\Temp\real_import',
    [Guid]$ProfileId = [Guid]::Empty,
    [string]$OutputPath = '',
    [switch]$NoBuild,
    [switch]$PrepareOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$config = Join-Path $repo 'config'
if (-not $OutputPath) { $OutputPath = Join-Path $repo ('tools/reports/real-media-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
# The offline Engine command owns the same process lease as the running Engine and
# also requires the Dashboard lease. It cannot reset a live instance.
Get-CimInstance Win32_Process | Where-Object {
    $_.Name -in @('dotnet.exe', 'MediaEngine.Api.exe', 'MediaEngine.Web.exe') -and
    $_.CommandLine -match 'MediaEngine\.(Api|Web)'
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (-not $NoBuild) {
    & dotnet build (Join-Path $repo 'src/MediaEngine.Api') --no-restore --disable-build-servers -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Engine build failed; no reset performed.' }
    & dotnet build (Join-Path $repo 'src/MediaEngine.Web') --no-restore --disable-build-servers -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed; no reset performed.' }
}
$engine = Join-Path $repo 'src/MediaEngine.Api/bin/Debug/net10.0/MediaEngine.Api.dll'
$arguments = @($engine, '--prepare-real-media', $config, $SourceRoot, $OutputPath)
if ($ProfileId -ne [Guid]::Empty) { $arguments += $ProfileId.ToString() }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Protected preparation failed. Do not start ingestion until the reported issue is resolved.' }
Write-Host "Baseline, backup and run details: $OutputPath"
if (-not $PrepareOnly) { & (Join-Path $PSScriptRoot 'Start-TuvimaApp.ps1') -NoBuild -NoStop -NoBrowser }
