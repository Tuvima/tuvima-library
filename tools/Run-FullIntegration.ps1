[CmdletBinding()]
param(
    # Use -WipeScope full only after confirming the Engine is pointed at harness
    # folders such as C:\temp\tuvima-watch and C:\temp\tuvima-library.
    [string]$EngineUrl = "http://localhost:61495",
    [ValidateSet("Generated", "Real")]
    [string]$Mode = "Generated",
    [string]$SourceRoot = "C:\Temp\real_import",
    [Guid]$ProfileId = [Guid]::Empty,
    [ValidateSet(1, 12, 123)]
    [int]$Stages = 123,
    [ValidateSet("generated-state", "full")]
    [string]$WipeScope = "generated-state",
    [string[]]$Types = @("books", "audiobooks", "movies", "tv", "music", "comics"),
    [switch]$MusicOnly,
    [int]$TimeoutSec = 10800,
    [string]$OutputPath = "",
    [switch]$NoOpen
)

$ErrorActionPreference = "Stop"

if ($Mode -eq "Real") {
    if ($WipeScope -eq "full") { throw 'Real mode never permits source-destructive wipeScope=full.' }
    if ($PSBoundParameters.ContainsKey('Types') -or $PSBoundParameters.ContainsKey('Stages') -or $MusicOnly) {
        throw 'Types, Stages and MusicOnly select generated-fixture tests; they are not valid in real-media mode.'
    }
    & (Join-Path $PSScriptRoot 'Run-RealMediaHarness.ps1') -SourceRoot $SourceRoot -ProfileId $ProfileId
    return
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$reportsDir = Join-Path $scriptRoot "reports"
if (-not (Test-Path $reportsDir)) {
    New-Item -ItemType Directory -Path $reportsDir | Out-Null
}

if (-not $OutputPath) {
    $timestamp = Get-Date -Format "yyyy-MM-dd-HHmmss"
    $OutputPath = Join-Path $reportsDir "integration-test-$timestamp.html"
}

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath, (Get-Location).Path)
$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

if ($MusicOnly) {
    $Types = @("music")
}

$requestedTypes = @($Types | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.ToLowerInvariant() })
$query = @("stages=$Stages", "wipeScope=$([uri]::EscapeDataString($WipeScope))")
if ($requestedTypes.Count -gt 0) {
    $query += "types=$([uri]::EscapeDataString(($requestedTypes -join ',')))"
}

$uri = "$EngineUrl/dev/integration-test"
if ($query.Count -gt 0) {
    $uri += "?" + ($query -join "&")
}

Write-Host ""
Write-Host "Tuvima Library integration harness" -ForegroundColor White
Write-Host "Engine : $EngineUrl" -ForegroundColor DarkGray
Write-Host "Stages : $Stages" -ForegroundColor DarkGray
Write-Host "Wipe   : $WipeScope" -ForegroundColor DarkGray
Write-Host "Types  : $($requestedTypes -join ', ')" -ForegroundColor DarkGray
Write-Host "Timeout: $TimeoutSec seconds" -ForegroundColor DarkGray
Write-Host ""

Write-Host "Checking engine status..." -ForegroundColor Cyan
Invoke-WebRequest -Uri "$EngineUrl/system/status" -UseBasicParsing -TimeoutSec 30 | Out-Null
Write-Host "Engine reachable." -ForegroundColor Green

Write-Host ""
if ($WipeScope -eq "full") {
    Write-Host "Running full integration test. Destructive mode will wipe the configured harness source/library folders and generated runtime state." -ForegroundColor Yellow
}
else {
    Write-Host "Running full integration test. This will wipe generated runtime state and known harness fixtures, preserving unrelated watch-folder files." -ForegroundColor Yellow
}
Write-Host "Saving report to: $OutputPath" -ForegroundColor DarkGray

Invoke-WebRequest -Method Post -Uri $uri -OutFile $OutputPath -UseBasicParsing -TimeoutSec $TimeoutSec | Out-Null

Write-Host ""
Write-Host "Report saved to $OutputPath" -ForegroundColor Green

if (-not $NoOpen) {
    Start-Process $OutputPath | Out-Null
}
