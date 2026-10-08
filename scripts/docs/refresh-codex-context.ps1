[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FrontMatter {
    param([string]$Content)

    $match = [regex]::Match(
        $Content,
        '^(---\r?\n)(?<yaml>.*?)(\r?\n---\r?\n)',
        [System.Text.RegularExpressions.RegexOptions]::Singleline
    )

    if (-not $match.Success) {
        return [ordered]@{}
    }

    $result = [ordered]@{}
    $currentKey = $null

    foreach ($line in ($match.Groups["yaml"].Value -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match '^\s{2}-\s+"?(?<value>.+?)"?\s*$') {
            if ($null -ne $currentKey) {
                $result[$currentKey] += @($matches["value"])
            }

            continue
        }

        if ($line -match '^(?<key>[a-z_]+):\s*"?(?<value>.*?)"?\s*$') {
            $key = $matches["key"]
            $value = $matches["value"]

            if ($value -eq "") {
                $result[$key] = @()
                $currentKey = $key
            }
            else {
                $result[$key] = $value
                $currentKey = $null
            }
        }
    }

    return $result
}

function Write-Utf8File {
    param(
        [string]$Path,
        [string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$contextRoot = Join-Path $repoRoot ".codex\context"
New-Item -ItemType Directory -Force $contextRoot | Out-Null

Push-Location $repoRoot
try {
    $commitSha = (git rev-parse HEAD).Trim()
}
finally {
    Pop-Location
}

$docsIndex = Get-ChildItem -Recurse -File docs -Filter *.md |
    Where-Object {
        $relative = $_.FullName.Substring($repoRoot.Path.Length + 1)
        $relative -notin @("docs\404.md", "docs\providers.md")
    } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($repoRoot.Path.Length + 1).Replace("\", "/")
        $content = Get-Content -LiteralPath $_.FullName -Raw
        $meta = Get-FrontMatter $content

        [ordered]@{
            title = if ($meta.Contains("title")) { $meta["title"] } else { $_.BaseName }
            path = $relativePath
            audience = $meta["audience"]
            category = $meta["category"]
            product_area = $meta["product_area"]
            summary = $meta["summary"]
            tags = if ($meta.Contains("tags")) { @($meta.tags) } else { @() }
            status = if ($meta.Contains("status")) { $meta.status } else { "active" }
            last_modified_utc = $_.LastWriteTimeUtc.ToString("o")
        }
    }

$workflowFiles = Get-ChildItem -File ".github/workflows" |
    Sort-Object Name |
    ForEach-Object { $_.Name }

$projectFiles = Get-ChildItem -Recurse -File src -Filter *.csproj |
    Sort-Object FullName |
    ForEach-Object { $_.FullName.Substring($repoRoot.Path.Length + 1).Replace("\", "/") }

$repoMap = [ordered]@{
    solution = "MediaEngine.slnx"
    projects = $projectFiles
    key_configs = @(
        "global.json",
        "Directory.Build.props",
        "Directory.Packages.props",
        "config/",
        "website/astro.config.mjs",
        "website/package.json"
    )
    local_ports = [ordered]@{
        engine = "http://localhost:61495"
        dashboard = "http://localhost:5016"
    }
    workflows = $workflowFiles
    shared_truth_sources = @(
        "README.md",
        "CLAUDE.md",
        ".agent/",
        "docs/",
        "config/"
    )
}

$sourceMap = [ordered]@{
    canonical_sources = @(
        [ordered]@{ path = "README.md"; role = "product overview and contributor entry point" },
        [ordered]@{ path = "CLAUDE.md"; role = "authoritative project memory for workflows and architecture summaries" },
        [ordered]@{ path = ".agent/"; role = "shared cross-agent supplementary knowledge" },
        [ordered]@{ path = "docs/"; role = "user-first Pages content" }
    )
    sync_documents = @(
        [ordered]@{ path = "CLAUDE.md"; notes = "Section 5.3 maps architecture docs to .agent files." },
        [ordered]@{ path = ".agent/SYNC-MAP.md"; notes = "Reverse mapping from .agent files back to CLAUDE.md sections." }
    )
}

$overview = @(
    '# Tuvima Codex Context',
    '',
    ("Last refresh: {0}" -f ((Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))),
    ('Source commit: `{0}`' -f $commitSha),
    '',
    '## Shared Truth',
    '',
    '- `README.md` for positioning and entry-level setup guidance.',
    '- `CLAUDE.md` for architecture summaries, workflow rules, and sync guidance.',
    '- `.agent/` for supplementary shared AI context.',
    '- `docs/` for user and developer documentation published to GitHub Pages.',
    '',
    '## Docs Snapshot',
    ''
)

foreach ($doc in ($docsIndex | Select-Object -First 8)) {
    $overview += ('- {0} (`{1}` / `{2}`): {3}' -f $doc.title, $doc.category, $doc.audience, $doc.path)
}

$overview += @(
    '',
    '## Local Services',
    '',
    '- Engine: `http://localhost:61495`',
    '- Dashboard: `http://localhost:5016`',
    '',
    '## Refresh Rule',
    '',
    '- Regenerate this folder after changes to docs, README, CLAUDE, .agent, config, or workflow files.'
)

$refresh = [ordered]@{
    generator = "scripts/docs/refresh-codex-context.ps1"
    generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    source_commit = $commitSha
    source_mode = "repo-owned working tree"
    docs_count = @($docsIndex).Count
}

Write-Utf8File (Join-Path $contextRoot "overview.md") (($overview -join "`n") + "`n")
Write-Utf8File (Join-Path $contextRoot "docs-index.json") ((ConvertTo-Json $docsIndex -Depth 8) + "`n")
Write-Utf8File (Join-Path $contextRoot "repo-map.json") ((ConvertTo-Json $repoMap -Depth 8) + "`n")
Write-Utf8File (Join-Path $contextRoot "source-map.json") ((ConvertTo-Json $sourceMap -Depth 8) + "`n")
Write-Utf8File (Join-Path $contextRoot "refresh.json") ((ConvertTo-Json $refresh -Depth 8) + "`n")
