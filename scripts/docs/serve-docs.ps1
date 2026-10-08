[CmdletBinding()]
param([switch]$InstallDependencies, [string]$Address = '127.0.0.1:8000')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not (Get-Command node -ErrorAction SilentlyContinue) -or -not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'Node 24 and npm are required to preview the documentation.' }
$nodeVersion = & node --version
if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v24\.') { throw "Node 24 is required; found $nodeVersion." }
if ($Address -notmatch '^(?<host>\[[^\]]+\]|[^:]+):(?<port>\d+)$') { throw 'Address must be a host and port, for example 127.0.0.1:8000.' }
$listenHost = $Matches['host'].Trim('[', ']')
$listenPort = [int]$Matches['port']
if ($listenPort -lt 1 -or $listenPort -gt 65535) { throw 'Address port must be between 1 and 65535.' }
$result = 0
Push-Location (Join-Path $repoRoot 'website')
try {
    if ($InstallDependencies) {
        & npm ci
        if ($LASTEXITCODE -ne 0) { $result = $LASTEXITCODE; return }
    }
    & npm run dev -- --host $listenHost --port $listenPort
    $result = $LASTEXITCODE
} finally {
    Pop-Location
    if ($result -ne 0) { exit $result }
}
