#Requires -Version 7.0
<#
.SYNOPSIS
Stops only the .NET processes (Engine, Dashboard, test hosts, `dotnet <dll>` runs)
whose command line or executable path belongs to this checkout.
.DESCRIPTION
Several sessions build and run Tuvima at once, each in its own git worktree, and
worktrees live inside the main checkout (.claude/worktrees/). A process belongs to
the checkout whose path is the longest one it references, so the main checkout never
claims a worktree's processes. Processes that name no checkout path (shared MSBuild
nodes, `dotnet build` started with a relative path) are left alone. Never replace
this with a machine-wide `taskkill /IM dotnet.exe`: it stops other sessions' work.
Exits 1 if a matched process is still running afterwards.
.EXAMPLE
pwsh -File tools/Stop-WorktreeDotnet.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param()

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath (Join-Path $repo 'MediaEngine.slnx'))) {
    throw 'This script must live in the Tuvima Library tools directory.'
}

function ConvertTo-PathKey([string] $path) {
    $key = $path.Replace('/', '\').TrimEnd('\')
    if ($IsWindows) { $key.ToLowerInvariant() } else { $key }
}

# Every registered checkout of this code history, plus any folder under
# .claude/worktrees that git no longer lists, so nested checkouts are never ours.
$roots = [Collections.Generic.List[string]]::new()
$roots.Add((ConvertTo-PathKey $repo))
foreach ($line in @(& git -C $repo worktree list --porcelain 2>$null)) {
    if ($line -like 'worktree *') { $roots.Add((ConvertTo-PathKey $line.Substring(9))) }
}
$self = ConvertTo-PathKey $repo
$nestedPrefix = $self + '\.claude\worktrees\'

function Get-Owner([string] $text) {
    $haystack = ConvertTo-PathKey $text
    $owner = $null
    foreach ($root in ($roots | Sort-Object -Unique)) {
        $pattern = [Regex]::Escape($root) + '(?=[\\"''\s]|$)'
        if ($haystack -match $pattern -and (-not $owner -or $root.Length -gt $owner.Length)) { $owner = $root }
    }
    if ($owner -eq $self -and $haystack.Contains($nestedPrefix)) {
        # Our path only appears as the prefix of a nested, unregistered worktree.
        $outside = $haystack.Replace($nestedPrefix, "`0")
        if (-not ($outside -match ([Regex]::Escape($self) + '(?=[\\"''\s]|$)'))) { $owner = $null }
    }
    $owner
}

function Get-DotnetProcesses {
    $namePattern = '^(dotnet|testhost(\.x86)?|MediaEngine\..+)(\.exe)?$'
    if ($IsWindows) {
        Get-CimInstance Win32_Process | Where-Object { $_.Name -match $namePattern } | ForEach-Object {
            [pscustomobject]@{ Id = [int]$_.ProcessId; ParentId = [int]$_.ParentProcessId; Name = $_.Name
                Text = "$($_.ExecutablePath) $($_.CommandLine)" }
        }
    } else {
        Get-Process | Where-Object { $_.ProcessName -match $namePattern } | ForEach-Object {
            [pscustomobject]@{ Id = $_.Id; ParentId = $(if ($_.Parent) { $_.Parent.Id } else { 0 }); Name = $_.ProcessName
                Text = "$($_.Path) $($_.CommandLine)" }
        }
    }
}

$all = @(Get-DotnetProcesses)
$byId = @{}
foreach ($p in $all) { $byId[$p.Id] = $p }

# Never stop the shell chain that is running this script.
$protected = [Collections.Generic.HashSet[int]]::new()
$cursor = $PID
while ($cursor -and $protected.Add($cursor)) {
    $parent = if ($IsWindows) { (Get-CimInstance Win32_Process -Filter "ProcessId = $cursor").ParentProcessId }
              else { (Get-Process -Id $cursor -ErrorAction SilentlyContinue).Parent.Id }
    $cursor = [int]$parent
}

$ours = @($all | Where-Object { -not $protected.Contains($_.Id) -and (Get-Owner $_.Text) -eq $self })
$others = @($all | Where-Object { $o = Get-Owner $_.Text; $o -and $o -ne $self }).Count

if (-not $ours.Count) {
    Write-Output "No .NET processes from $repo are running ($others from other checkouts left alone)."
    exit 0
}

foreach ($p in ($ours | Sort-Object Id)) {
    $label = "PID $($p.Id) $($p.Name)"
    if ($PSCmdlet.ShouldProcess($label, 'Stop process from this checkout')) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $p.Id -Timeout 10 -ErrorAction SilentlyContinue
        Write-Output "Stopped $label"
    }
}
Write-Output "Left $others .NET processes from other checkouts alone."

if ($WhatIfPreference) { exit 0 }
$left = @($ours | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue })
if ($left.Count) {
    Write-Warning "Still running: $(($left | ForEach-Object { "PID $($_.Id)" }) -join ', '). Report it; do not kill other processes."
    exit 1
}
