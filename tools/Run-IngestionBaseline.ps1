<#
.SYNOPSIS
  Runs one full, isolated ingestion of a sample library and exports a repeatable "baseline" record of what the
  Library identified.

.DESCRIPTION
  Builds the Engine (unless -NoBuild) and starts the offline command `--run-baseline` of the Engine you just built.
  That command does the work: it copies config/ into .tmp/ingestion-baseline/<run-id>/ (provider keys included, and
  deleted again at the end), generates or reads the samples, prepares an empty data store, starts the same Engine
  build headless on its own port with every location pointing into the run folder, waits until ingestion settles
  (read-only look at the run's own SQLite file; budget 3 hours, polls every 15 s, settled after 3 identical
  snapshots with no running identity work), stops only the process it started, exports the record and prints its path.

  Outside answers: a run without -ReplayFrom records every answer the providers give into
  <run>/provider-answers/ (key values and sign-in tokens removed, and checked before the copied keys are deleted). A run
  with -ReplayFrom answers every outside request from that recording instead and never reaches the network, so comparing
  two replays of one build shows only what our own code does differently. Requests the recording does not hold get a
  "not found" answer (a definite no-answer, never a provider outage) and are listed in <run>/provider-answers-not-recorded.txt (exit code 5).

  It never writes to config/ or to the user's data store, never stops a process it did not start, and only reads a
  -SampleRoot (a before/after listing proves nothing there changed).

  Compare two records with:
    dotnet src/MediaEngine.Api/bin/Debug/net10.0/MediaEngine.Api.dll --compare-baseline <before.json> <after.json> <report.md> [--margin 0.2]
  The exit code is 0 when there is no comparable difference and 1 otherwise; timing changes never count.

.PARAMETER SampleRoot
  An existing sample folder (real media can be used later). Needs books/, audiobooks/, movies/, tv/, music/ and
  optionally comics/, personal_photos/, personal_videos/. Read-only for the run.

.PARAMETER Generate
  Generate the deterministic sample library inside the run folder instead of using -SampleRoot.

.PARAMETER Label
  A short name for the run (for example before-1). Part of the run folder name; the record is also copied to
  .tmp/ingestion-baseline/<label>.json.

.PARAMETER ReplayFrom
  Replay the outside answers recorded by an earlier run: its run folder, its run id, or its label (newest run with that
  label). Without it, this run records.

.PARAMETER NoBuild
  Skip building the Engine.

.PARAMETER ConfigSource
  The config folder to copy. Defaults to this checkout's config/. Provider keys missing from it are taken from the
  main checkout's config/secrets (read only) when this is a linked worktree.

.PARAMETER BudgetMinutes
  How long to wait for ingestion to settle (default 180).

.PARAMETER PollSeconds
  Seconds between settle checks (default 15).
#>
[CmdletBinding()]
param(
    [string]$SampleRoot = '',
    [switch]$Generate,
    [string]$Label = '',
    [string]$ReplayFrom = '',
    [switch]$NoBuild,
    [string]$ConfigSource = '',
    [int]$BudgetMinutes = 180,
    [int]$PollSeconds = 15
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($Generate -and $SampleRoot) { throw 'Use either -Generate or -SampleRoot, not both.' }
if (-not $Generate -and -not $SampleRoot) { throw 'Give -SampleRoot <folder> or -Generate.' }
if ($SampleRoot -and -not (Test-Path -LiteralPath $SampleRoot -PathType Container)) { throw "Sample root not found: $SampleRoot" }

if (-not $NoBuild) {
    & dotnet build (Join-Path $repo 'src/MediaEngine.Api/MediaEngine.Api.csproj') --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Engine build failed; nothing was run.' }
}
$engineDll = Join-Path $repo 'src/MediaEngine.Api/bin/Debug/net10.0/MediaEngine.Api.dll'
if (-not (Test-Path -LiteralPath $engineDll)) { throw "Engine build output not found: $engineDll (build first, or drop -NoBuild)." }

$arguments = @($engineDll, '--run-baseline', '--repo', $repo, '--budget-minutes', "$BudgetMinutes", '--poll-seconds', "$PollSeconds")
if ($Generate) { $arguments += '--generate' } else { $arguments += @('--sample-root', (Resolve-Path -LiteralPath $SampleRoot).Path) }
if ($Label) { $arguments += @('--label', $Label) }
if ($ReplayFrom) { $arguments += @('--replay-from', $ReplayFrom) }
if ($ConfigSource) { $arguments += @('--config-source', (Resolve-Path -LiteralPath $ConfigSource).Path) }

& dotnet @arguments
exit $LASTEXITCODE
