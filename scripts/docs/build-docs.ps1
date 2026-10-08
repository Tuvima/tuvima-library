[CmdletBinding()]
param([switch]$InstallDependencies)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not (Get-Command node -ErrorAction SilentlyContinue) -or -not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'Node 24 and npm are required to build the documentation.' }
$nodeVersion = & node --version
if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v24\.') { throw "Node 24 is required; found $nodeVersion." }
$result = 0
Push-Location (Join-Path $repoRoot 'website')
try {
    if ($InstallDependencies) {
        & npm ci
        if ($LASTEXITCODE -ne 0) { $result = $LASTEXITCODE; return }
    }
    foreach ($task in @('test', 'check', 'build')) {
        & npm run $task
        if ($LASTEXITCODE -ne 0) { $result = $LASTEXITCODE; break }
    }
} finally {
    Pop-Location
    if ($result -ne 0) { exit $result }
}
