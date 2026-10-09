#Requires -Version 5.1
<#
.SYNOPSIS
Accepts an intended Engine-to-Dashboard contract change: runs the Contracts tests, copies every Fixtures/*.received.txt over its .approved.txt, then re-runs the tests to confirm they pass.
.DESCRIPTION
On a mismatch the snapshot tests write the actual output to <name>.received.txt next to the approved file. This script only copies those files into place; it sets no environment variables. Review the resulting diff of the *.approved.txt files before committing.
.EXAMPLE
powershell -File tools\Update-ContractFixtures.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'tests/MediaEngine.Contracts.Tests'
$fixtures = Join-Path $project 'Fixtures'

# First run: a mismatch is expected and writes the .received.txt files, so its exit code is not an error.
dotnet test $project
$firstExit = $LASTEXITCODE

$received = @(Get-ChildItem -Path $fixtures -Filter '*.received.txt' -ErrorAction SilentlyContinue)
foreach ($file in $received) {
    $approved = $file.FullName -replace '\.received\.txt$', '.approved.txt'
    Copy-Item -LiteralPath $file.FullName -Destination $approved -Force
    Remove-Item -LiteralPath $file.FullName -Force
    Write-Host ("Accepted " + (Split-Path $approved -Leaf))
}

if ($received.Count -eq 0 -and $firstExit -ne 0) {
    throw "Contract tests failed (exit $firstExit) but wrote no .received.txt files, so this is not a fixture mismatch. Fix the failing tests first."
}
if ($received.Count -eq 0) {
    Write-Host 'No fixture changes were needed.'
}

dotnet test $project
if ($LASTEXITCODE -ne 0) { throw "Contract tests still fail after accepting fixtures (exit $LASTEXITCODE)." }
Write-Host 'Contract tests pass. Review and commit the changed Fixtures/*.approved.txt files.'
