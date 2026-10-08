[CmdletBinding()]
param([string]$OutputDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Utf8File {
    param(
        [string]$Path,
        [string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$contextRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repoRoot '.codex/context' }
New-Item -ItemType Directory -Force $contextRoot | Out-Null

Push-Location $repoRoot
try {
    $commitSha = (git rev-parse HEAD).Trim()
}
finally {
    Pop-Location
}

# Use the same YAML parser, strict schema and protected-file adapter as the site.
# This covers Markdown and MDX through the explicit publication manifest.
Push-Location (Join-Path $repoRoot 'website')
try {
    & node scripts/prepare-content.mjs
    if ($LASTEXITCODE -ne 0) { throw "Documentation preparation failed with exit code $LASTEXITCODE. Run npm ci in website first." }
}
finally { Pop-Location }
$prepared = Get-Content -LiteralPath (Join-Path $repoRoot 'website/.generated/routes.json') -Raw | ConvertFrom-Json
$docsIndex = $prepared.pages | Sort-Object source |
    ForEach-Object {
        [ordered]@{
            title = $_.title
            path = 'docs/' + $_.source
            route = $_.route
            audience = $_.audience
            category = $_.category
            product_area = $_.product_area
            description = $_.description
            tags = @($_.tags)
            status = $_.status
            last_updated = $_.lastUpdated
        }
    }

$workflowFiles = Get-ChildItem -File (Join-Path $repoRoot '.github/workflows') |
    Sort-Object Name |
    ForEach-Object { $_.Name }

$projectFiles = Get-ChildItem -Recurse -File (Join-Path $repoRoot 'src') -Filter *.csproj |
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
        "website/package.json",
        "website/publication.json"
    )
    local_ports = [ordered]@{
        engine = "http://localhost:61495"
        dashboard = "http://localhost:5016"
    }
    workflows = $workflowFiles
    shared_truth_sources = @(
        "README.md",
        "CLAUDE.md",
        "AGENTS.md",
        "src/MediaEngine.Web/CLAUDE.md",
        "engineering/",
        "docs/",
        "config/"
    )
}

$sourceMap = [ordered]@{
    canonical_sources = @(
        [ordered]@{ path = "README.md"; role = "product overview and contributor entry point" },
        [ordered]@{ path = "CLAUDE.md"; role = "authoritative project memory for workflows and architecture summaries" },
        [ordered]@{ path = "AGENTS.md"; role = "repository instructions and developer code tour" },
        [ordered]@{ path = "src/MediaEngine.Web/CLAUDE.md"; role = "Dashboard engineering guidance" },
        [ordered]@{ path = "engineering/"; role = "unpublished engineering plans and verification records" },
        [ordered]@{ path = "docs/"; role = "user-first Pages content" }
    )
    sync_documents = @(
        [ordered]@{ path = "CLAUDE.md"; notes = "Cross-cutting project guardrails." },
        [ordered]@{ path = "docs/product/presentation-rules.md"; notes = "Current product presentation behavior. The .agent mirror is retired and is not synchronized." },
        [ordered]@{ path = "docs/architecture/architecture-summary.md"; notes = "Subsystem summary and owning architecture documents." }
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
    '- `docs/product/presentation-rules.md` and `docs/architecture/` for current detailed behavior.',
    '- `engineering/` for unpublished engineering plans and verification records.',
    '- `.agent/` is retired; do not synchronize it or treat it as current authority.',
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
    '- Regenerate this folder after changes to docs, README, AGENTS, CLAUDE, engineering, config, or workflow files.'
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
