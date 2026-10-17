# Build check for Claude Code Stop / SubagentStop hooks.
# Builds MediaEngine.slnx when the turn left uncommitted code changes, and blocks
# the agent from finishing (decision=block) until compile errors are fixed.
# Skips cheaply when: no code files changed, the same changes already built
# cleanly, Engine/Dashboard are running (locked binaries), or a block was
# already issued this turn (stop_hook_active) so it can never loop.

$ErrorActionPreference = 'Stop'

$raw = [Console]::In.ReadToEnd()
$hookInput = $null
try { $hookInput = $raw | ConvertFrom-Json } catch { $hookInput = $null } # malformed input: fall back to defaults below

if ($hookInput -and $hookInput.stop_hook_active) { exit 0 }
# Read-only helpers can't fix a build; only check agents that edit code.
if ($hookInput -and $hookInput.agent_type -in @('scout', 'reviewer', 'verifier', 'Explore', 'Plan')) { exit 0 }

$cwd = if ($hookInput -and $hookInput.cwd) { $hookInput.cwd } else { (Get-Location).Path }
$root = (& git -C $cwd rev-parse --show-toplevel 2>$null)
if (-not $root) { exit 0 }
$slnx = Join-Path $root 'MediaEngine.slnx'
if (-not (Test-Path $slnx)) { exit 0 }

$codePattern = '\.(cs|razor|cshtml|csproj|props|targets|slnx|resx)$'
$changed = @(& git -C $root status --porcelain --untracked-files=all 2>$null | ForEach-Object {
    $path = $_.Substring(3).Trim('"')
    if ($path -match ' -> ') { $path = ($path -split ' -> ')[-1] }
    $path
} | Where-Object { $_ -match $codePattern -or ($_ -match '^(src|tests)/' -and $_ -match '\.json$') })

if ($changed.Count -eq 0) { exit 0 }

# Signature of the current code changes; skip if this exact state already built cleanly.
$gitDir = (& git -C $root rev-parse --absolute-git-dir 2>$null)
$sigFile = Join-Path $gitDir 'tuvima-build-check.sig'
$head = (& git -C $root rev-parse HEAD 2>$null)
$fileParts = foreach ($p in ($changed | Sort-Object)) {
    $full = Join-Path $root $p
    if (Test-Path $full) { "$p|$((Get-Item $full).LastWriteTimeUtc.Ticks)" } else { "$p|deleted" }
}
$sigParts = @("HEAD|$head") + @($fileParts)
$sig = [BitConverter]::ToString(
    [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes(($sigParts -join "`n"))))
if ((Test-Path $sigFile) -and ((Get-Content $sigFile -Raw).Trim() -eq $sig)) { exit 0 }

$running = Get-Process -Name 'MediaEngine.Api', 'MediaEngine.Web' -ErrorAction SilentlyContinue
if ($running) {
    @{ systemMessage = 'Build check skipped: the Engine or Dashboard is running and would lock build outputs. Stop them and build before reporting done.' } |
        ConvertTo-Json -Compress
    exit 0
}

$output = & dotnet build $slnx --no-restore -nologo -v q -clp:ErrorsOnly 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -and $output -match 'NETSDK1004') {
    # Projects never restored in this checkout or worktree: restore once, then build again.
    & dotnet restore $slnx -nologo -v q 2>&1 | Out-Null
    $output = & dotnet build $slnx --no-restore -nologo -v q -clp:ErrorsOnly 2>&1 | Out-String
}
if ($LASTEXITCODE -eq 0) {
    Set-Content -Path $sigFile -Value $sig -NoNewline
    exit 0
}

$errors = @($output -split "`r?`n" | Where-Object { $_ -match ': error ' } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
$summary = if ($errors.Count -gt 0) {
    ($errors | Select-Object -First 30) -join "`n"
} else {
    ($output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 30) -join "`n"
}
$hint = if ($output -match 'NETSDK1004|project\.assets\.json') { "`nRun 'dotnet restore MediaEngine.slnx' first." } else { '' }

@{
    decision = 'block'
    reason   = "Build check failed ($($errors.Count) error(s)) for uncommitted code changes. Fix these before finishing, or explain why they are out of scope:`n$summary$hint"
} | ConvertTo-Json -Compress
exit 0
