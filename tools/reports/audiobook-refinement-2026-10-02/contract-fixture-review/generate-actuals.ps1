$ErrorActionPreference = 'Stop'
$taskRepo = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$taskFixtureRoot = Join-Path $taskRepo 'tests/MediaEngine.Contracts.Tests/Fixtures'
$taskNames = @('contracts-shape.approved.txt', 'wire-compatibility.approved.txt', 'wire-type-inventory.approved.txt')
$taskFixtureBytes = @{}
$taskOldShape = [Environment]::GetEnvironmentVariable('TUVIMA_UPDATE_CONTRACT_SHAPE', 'Process')
$taskOldWire = [Environment]::GetEnvironmentVariable('TUVIMA_UPDATE_WIRE_SNAPSHOT', 'Process')
$taskGenerationExit = 1
foreach ($taskName in $taskNames) {
    $taskPath = Join-Path $taskFixtureRoot $taskName
    $taskOriginal = Join-Path $PSScriptRoot ('original-' + $taskName)
    if ((Get-FileHash -LiteralPath $taskPath).Hash -ne (Get-FileHash -LiteralPath $taskOriginal).Hash) {
        throw "Fixture changed before generation: $taskName"
    }
    $taskFixtureBytes[$taskName] = [IO.File]::ReadAllBytes($taskPath)
}
try {
    [Environment]::SetEnvironmentVariable('TUVIMA_UPDATE_CONTRACT_SHAPE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('TUVIMA_UPDATE_WIRE_SNAPSHOT', '1', 'Process')
    Push-Location $taskRepo
    try {
        dotnet test tests/MediaEngine.Contracts.Tests --no-build --filter 'FullyQualifiedName~PublicContractShape_MatchesApprovedFixture|FullyQualifiedName~EngineClientWireCompatibility_MatchesApprovedFixture|FullyQualifiedName~ExportedWireTypeInventory_MatchesApprovedFixture' --logger 'trx;LogFileName=wp7-raw-fixture-generation.trx' --results-directory tools/reports/audiobook-refinement-2026-10-02
        $taskGenerationExit = $LASTEXITCODE
        if ($taskGenerationExit -ne 0) { throw "Snapshot generation failed: $taskGenerationExit" }
        foreach ($taskName in $taskNames) {
            [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot ('actual-' + $taskName)), [IO.File]::ReadAllBytes((Join-Path $taskFixtureRoot $taskName)))
        }
    } finally { Pop-Location }
} finally {
    [Environment]::SetEnvironmentVariable('TUVIMA_UPDATE_CONTRACT_SHAPE', $taskOldShape, 'Process')
    [Environment]::SetEnvironmentVariable('TUVIMA_UPDATE_WIRE_SNAPSHOT', $taskOldWire, 'Process')
    foreach ($taskName in $taskNames) {
        [IO.File]::WriteAllBytes((Join-Path $taskFixtureRoot $taskName), $taskFixtureBytes[$taskName])
    }
    $taskRestoreProof = foreach ($taskName in $taskNames) {
        $taskRestoredHash = (Get-FileHash -LiteralPath (Join-Path $taskFixtureRoot $taskName)).Hash
        $taskOriginalHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot ('original-' + $taskName))).Hash
        [PSCustomObject]@{ Fixture = $taskName; RestoredHash = $taskRestoredHash; OriginalHash = $taskOriginalHash; ByteExactRestored = $taskRestoredHash -eq $taskOriginalHash }
    }
    $taskRestoreProof | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'generation-restore-proof.json') -Encoding utf8
    $taskRestoreProof | Format-Table Fixture, ByteExactRestored
}
exit $taskGenerationExit
