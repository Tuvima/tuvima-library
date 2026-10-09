#Requires -Version 7.0
<#
.SYNOPSIS
Regenerates the approved Engine-to-Dashboard contract fixtures (wire-compatibility, wire-type-inventory, contracts-shape) after an intended contract change, then re-runs the tests to confirm they pass.
.EXAMPLE
pwsh -File tools/Update-ContractFixtures.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'tests/MediaEngine.Contracts.Tests'

# Regeneration switches apply to this process only (the child dotnet inherits them) and are always cleared.
try {
    $env:TUVIMA_UPDATE_WIRE_SNAPSHOT = '1'
    $env:TUVIMA_UPDATE_CONTRACT_SHAPE = '1'
    dotnet test $project
    if ($LASTEXITCODE -ne 0) { throw "Fixture regeneration run failed (exit $LASTEXITCODE)." }
}
finally {
    Remove-Item Env:TUVIMA_UPDATE_WIRE_SNAPSHOT -ErrorAction SilentlyContinue
    Remove-Item Env:TUVIMA_UPDATE_CONTRACT_SHAPE -ErrorAction SilentlyContinue
}

dotnet test $project
if ($LASTEXITCODE -ne 0) { throw "Contract tests still fail after regeneration (exit $LASTEXITCODE)." }
Write-Host 'Contract fixtures regenerated and verified. Review and commit the changed Fixtures/*.approved.txt files.'
