[CmdletBinding()]
param(
    [switch]$InstallDependencies
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw "Node.js 24 (with npm) is required to preview the documentation. Install Node 24, then rerun this script."
}

$websiteRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..\website")

Push-Location $websiteRoot
try {
    if ($InstallDependencies -or -not (Test-Path "node_modules")) {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
    }

    npm run dev
}
finally {
    Pop-Location
}
