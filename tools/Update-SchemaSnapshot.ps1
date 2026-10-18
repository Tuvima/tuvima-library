#Requires -Version 5.1
<#
.SYNOPSIS
Refreshes the generated data structure files after an upgrade step was added: src/MediaEngine.Storage/Schema/schema.snapshot.sql and src/MediaEngine.Storage/Schema/Upgrades/released-steps.txt.
.DESCRIPTION
Runs the schema upgrade guardrail tests. On a mismatch they write schema.snapshot.received.sql and released-steps.received.txt next to the committed files; this script copies the first over schema.snapshot.sql and merges the second into released-steps.txt, then re-runs the tests to confirm they pass.

released-steps.txt records the fingerprint of every upgrade step. New steps are appended. A listed step whose fingerprint changed (or that was removed) is only updated when that step is not in origin/main's copy of released-steps.txt, so a step that has already been merged can never be edited through this script. If git or origin/main is unavailable, existing lines are never changed.
.EXAMPLE
pwsh -File tools/Update-SchemaSnapshot.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'tests/MediaEngine.Storage.Tests'
$schemaDir = Join-Path $repo 'src/MediaEngine.Storage/Schema'
$upgradesDir = Join-Path $schemaDir 'Upgrades'
$snapshot = Join-Path $schemaDir 'schema.snapshot.sql'
$snapshotReceived = Join-Path $schemaDir 'schema.snapshot.received.sql'
$released = Join-Path $upgradesDir 'released-steps.txt'
$releasedReceived = Join-Path $upgradesDir 'released-steps.received.txt'
$releasedRelativePath = 'src/MediaEngine.Storage/Schema/Upgrades/released-steps.txt'
$neverEdit = 'Never edit a released step - revert it and add a new step.'

function Read-StepLines([string[]]$lines) {
    # Returns number -> original line for every "NNNN name sha256" line.
    $result = @{}
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
        $parts = $trimmed -split '\s+'
        if ($parts.Count -ne 3) { throw "released-steps.txt has a line that is not 'NNNN name sha256': $trimmed" }
        $result[[int]$parts[0]] = $trimmed
    }
    return $result
}

function Get-MainReleasedNumbers {
    # $null when it cannot be known (no git, no origin/main): existing lines then stay untouched.
    # An empty set when origin/main exists but does not have the list yet: nothing has been released.
    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) { return $null }
    & git -C $repo rev-parse --verify --quiet 'origin/main' *> $null
    if ($LASTEXITCODE -ne 0) { return $null }
    & git -C $repo cat-file -e "origin/main:$releasedRelativePath" *> $null
    if ($LASTEXITCODE -ne 0) { return @{} }
    $text = & git -C $repo show "origin/main:$releasedRelativePath"
    if ($LASTEXITCODE -ne 0) { return $null }
    return Read-StepLines @($text)
}

# First run: a mismatch is expected and writes the received files, so its exit code is not an error.
dotnet test $project --filter 'FullyQualifiedName~SchemaUpgradeGuardrailTests'
$firstExit = $LASTEXITCODE

$changed = $false

if (Test-Path -LiteralPath $releasedReceived) {
    $receivedLines = @(Get-Content -LiteralPath $releasedReceived)
    $received = Read-StepLines $receivedLines
    $current = if (Test-Path -LiteralPath $released) { Read-StepLines @(Get-Content -LiteralPath $released) } else { @{} }
    $onMain = $null
    $needsMain = $false
    foreach ($number in $current.Keys) {
        if (-not $received.ContainsKey($number) -or $received[$number] -ne $current[$number]) { $needsMain = $true }
    }
    if ($needsMain) {
        $onMain = Get-MainReleasedNumbers
        if ($null -eq $onMain) {
            throw "Some listed upgrade steps changed or were removed, but git or origin/main is unavailable, so this script cannot tell whether they were released. Existing lines in released-steps.txt were not changed. $neverEdit"
        }
        foreach ($number in $current.Keys) {
            $differs = (-not $received.ContainsKey($number)) -or ($received[$number] -ne $current[$number])
            if ($differs -and $onMain.ContainsKey($number)) {
                $name = ($current[$number] -split '\s+')[1]
                throw ("Upgrade step {0:D4} ($name) changed after release. $neverEdit" -f $number)
            }
        }
    }
    Copy-Item -LiteralPath $releasedReceived -Destination $released -Force
    Remove-Item -LiteralPath $releasedReceived -Force
    Write-Host 'Updated released-steps.txt'
    $changed = $true
}

if (Test-Path -LiteralPath $snapshotReceived) {
    Copy-Item -LiteralPath $snapshotReceived -Destination $snapshot -Force
    Remove-Item -LiteralPath $snapshotReceived -Force
    Write-Host 'Updated schema.snapshot.sql'
    $changed = $true
}

if (-not $changed -and $firstExit -ne 0) {
    throw "The schema upgrade guardrail tests failed (exit $firstExit) but wrote no received files, so this is not a snapshot or released-steps mismatch. Fix the failing tests first."
}
if (-not $changed) {
    Write-Host 'No snapshot changes were needed.'
}

dotnet test $project --filter 'FullyQualifiedName~SchemaUpgradeGuardrailTests'
if ($LASTEXITCODE -ne 0) { throw "The schema upgrade guardrail tests still fail after refreshing the files (exit $LASTEXITCODE)." }
if ($changed) {
    Write-Host 'Guardrail tests pass. Review and commit schema.snapshot.sql and released-steps.txt.'
}
