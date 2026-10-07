#Requires -Version 7.0
<#
.SYNOPSIS
Removes reproducible project outputs; -IncludeQa also removes generated QA builds.
.EXAMPLE
pwsh -File tools/Clean-RepoOutputs.ps1 -IncludeQa -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param([switch] $IncludeQa, [switch] $QaOnly)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not (Test-Path -LiteralPath (Join-Path $repo 'MediaEngine.slnx'))) {
    throw 'This script must live in the Tuvima Library tools directory.'
}
$prefix = $repo + [IO.Path]::DirectorySeparatorChar
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$candidates = [Collections.Generic.HashSet[string]]::new(
    $(if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }))

if ($IsWindows) {
    # OneDrive placeholders also carry ReparsePoint. Read the tag without opening
    # or following the target, and allow only Microsoft's CLOUD tag family.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TuvimaOutputReparse {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FindData {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint SizeHigh, SizeLow, Tag, Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alternate;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileW(string path, out FindData data);
    [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr handle);
    public static uint ReadTag(string path) {
        FindData data;
        var handle = FindFirstFileW(path, out data);
        if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try { return data.Tag; } finally { FindClose(handle); }
    }
}
'@
}

function Assert-NoLink($item) {
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
    if ($IsWindows -and -not $item.LinkTarget -and -not $item.LinkType) {
        $tag = [TuvimaOutputReparse]::ReadTag($item.FullName)
        if (($tag -band 0xFFFF0FFFu) -eq 0x9000001Au) { return }
    }
    throw "Refusing linked or unknown reparse path: $($item.FullName)"
}

function Add-Output([string] $relative) {
    $absolute = [IO.Path]::GetFullPath((Join-Path $repo $relative))
    if (-not $absolute.StartsWith($prefix, $comparison)) { throw "Outside repository: $absolute" }
    if (Test-Path -LiteralPath $absolute) { [void] $candidates.Add($absolute) }
}

# Only immediate outputs of tracked projects, never similarly named user folders.
$projects = @(& git -C $repo ls-files -- '*.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked projects.' }
foreach ($project in $(if ($QaOnly) { @() } else { $projects })) {
    $directory = Split-Path $project -Parent
    Add-Output $(if ($directory) { Join-Path $directory 'bin' } else { 'bin' })
    Add-Output $(if ($directory) { Join-Path $directory 'obj' } else { 'obj' })
}

if ($IncludeQa -or $QaOnly) {
    # Explicit, reproducible build sandboxes. Preserve notes, patches, tooling caches,
    # diagnostics, unmarked fixtures, and library data elsewhere in .tmp/artifacts.
    foreach ($relative in @(
        '.tmp/mud', '.tmp/ui-bugfix', '.tmp/css-ownership', '.tmp/css-ownership-plan-docs',
        '.tmp/harness-onboarding', '.tmp/overlay-razor-check', '.tmp/overlay-unit-check',
        '.tmp/shelf-editor-check', '.tmp/unified-editor-qa', '.tmp/site-check',
        '.tmp/storage-docs-site', '.tmp/storage-publish',
        'artifacts/codex-artwork', 'artifacts/codex-music-move-client',
        'artifacts/codex-work-version-client', 'artifacts/codex-work-versions',
        'artifacts/codex-work-versions-api', 'TestResults', 'TestResults-plan-baseline', 'site'
    )) { Add-Output $relative }
}

# Validate the complete plan before deleting anything. Junctions/symlinks must never
# carry recursive deletion into a shared data store or an unrelated checkout.
foreach ($absolute in $candidates) {
    $ancestor = $absolute
    while ($ancestor.StartsWith($prefix, $comparison) -or $ancestor.Equals($repo, $comparison)) {
        $item = Get-Item -LiteralPath $ancestor -Force
        Assert-NoLink $item
        if ($ancestor.Equals($repo, $comparison)) { break }
        $ancestor = Split-Path $ancestor -Parent
    }
    $links = @(Get-ChildItem -LiteralPath $absolute -Force -Recurse -Attributes ReparsePoint)
    foreach ($link in $links) { Assert-NoLink $link }
    $relative = [IO.Path]::GetRelativePath($repo, $absolute).Replace('\', '/')
    $tracked = @(& git -C $repo ls-files -- "$relative/" "$relative")
    if ($LASTEXITCODE -ne 0 -or $tracked.Count) { throw "Refusing output with tracked files: $relative" }
}

if ($IsWindows) {
    $apps = @(Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object {
        $_.CommandLine -and $_.CommandLine.Contains($repo, [StringComparison]::OrdinalIgnoreCase) -and
        $_.CommandLine -match 'MediaEngine\.(Api|Web)(\.dll|[\\/])'
    })
    if ($apps.Count) { throw 'Stop the repository Engine and Dashboard before cleaning outputs.' }
}

foreach ($absolute in ($candidates | Sort-Object)) {
    if ($PSCmdlet.ShouldProcess($absolute, 'Remove reproducible output directory')) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
        Write-Output "Removed $([IO.Path]::GetRelativePath($repo, $absolute))"
    }
}
