[CmdletBinding()]
param(
    [switch]$Start,
    [switch]$Keep,
    [string]$SeedExistingFixture,
    [int]$EnginePort = 61497,
    [int]$DashboardPort = 5018,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

function Set-JsonFile([string]$Path, [scriptblock]$Update) {
    $value = Get-Content -Raw $Path | ConvertFrom-Json
    & $Update $value
    $value | ConvertTo-Json -Depth 30 | Set-Content -NoNewline $Path
}

function Wait-HttpOk([string]$Uri, [int]$TimeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "Timed out waiting for $Uri."
}

function Assert-LocalPortAvailable([int]$Port) {
    $listener = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalPort -eq $Port } | Select-Object -First 1
    if ($listener) { throw "Port $Port is already listening (PID $($listener.OwningProcess)). Choose another smoke port." }
}

function Remove-SmokeFixture([string]$Path) {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not $resolved.StartsWith($tempRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ([System.IO.Path]::GetFileName($resolved) -like 'tuvima-smoke-*')) {
        throw "Refusing to remove a path outside the generated smoke-fixture prefix: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Add-SyntheticRows([string]$DatabasePath, [string]$RepoRoot) {
    $sqliteAssembly = Join-Path $RepoRoot "src\MediaEngine.Api\bin\$Configuration\net10.0\Microsoft.Data.Sqlite.dll"
    if (-not (Test-Path $sqliteAssembly)) {
        throw "Synthetic seeding needs a built Storage project at $sqliteAssembly. Run dotnet build first."
    }
    $assemblyDirectory = Split-Path $sqliteAssembly -Parent
    foreach ($dependency in 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll') {
        Add-Type -Path (Join-Path $assemblyDirectory $dependency)
    }
    Add-Type -Path $sqliteAssembly
    $env:PATH = (Join-Path $assemblyDirectory 'runtimes\win-x64\native') + [IO.Path]::PathSeparator + $env:PATH
    [SQLitePCL.Batteries_V2]::Init()
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath")
    $connection.Open()
    $transaction = $connection.BeginTransaction()
    try {
        $now = [DateTimeOffset]::UtcNow.ToString('O')
        function Invoke-SmokeSql([string]$Sql, [hashtable]$Parameters) {
            $command = $connection.CreateCommand()
            $command.Transaction = $transaction
            $command.CommandText = $Sql
            foreach ($entry in $Parameters.GetEnumerator()) {
                $parameter = $command.CreateParameter()
                $parameter.ParameterName = "@$($entry.Key)"
                if ($entry.Value -is [Guid]) {
                    $parameter.Value = [byte[]]$entry.Value.ToByteArray($true)
                } else {
                    $parameter.Value = $entry.Value
                }
                [void]$command.Parameters.Add($parameter)
            }
            [void]$command.ExecuteNonQuery()
        }
        $types = @('Books', 'Audiobooks', 'Movies', 'Music')
        $musicWorkId = [Guid]::Empty
        foreach ($type in $types) {
            $workId = [Guid]::NewGuid(); $editionId = [Guid]::NewGuid(); $assetId = [Guid]::NewGuid()
            if ($type -eq 'Music') { $musicWorkId = $workId }
            $libraryId = switch ($type) {
                'Books' { '11111111-1111-4111-8111-111111111111' }
                'Audiobooks' { '22222222-2222-4222-8222-222222222222' }
                'Movies' { '44444444-4444-4444-8444-444444444444' }
                default { '55555555-5555-4555-8555-555555555555' }
            }
            Invoke-SmokeSql @'
INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, @mediaType, 'child', 'Owned');
INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Smoke');
INSERT INTO media_assets (id, edition_id, library_id, content_hash, file_path_root) VALUES (@assetId, @editionId, @libraryId, @contentHash, @filePath);
INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'title', @title, @now);
'@ @{ workId = $workId; editionId = $editionId; assetId = $assetId; libraryId = $libraryId; mediaType = $type; contentHash = "smoke-$type-$assetId"; filePath = "Smoke/$type/$type.fixture"; title = "Smoke $type"; now = $now }
        }

        $albumId = [Guid]::NewGuid(); $albumRootId = [Guid]::NewGuid()
        Invoke-SmokeSql @'
INSERT INTO collections (id, display_name, collection_type, primary_area) VALUES (@albumId, 'Smoke Album', 'Album', 'Listen');
INSERT INTO works (id, media_type, work_kind, collection_id, ownership) VALUES (@albumRootId, 'Music', 'parent', @albumId, 'Owned');
UPDATE works SET parent_work_id=@albumRootId, collection_id=@albumId, ordinal=1 WHERE id=@musicWorkId;
INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@albumRootId, 'title', 'Smoke Album', @now);
'@ @{ albumId = $albumId; albumRootId = $albumRootId; musicWorkId = $musicWorkId; now = $now }
        for ($trackNumber = 2; $trackNumber -le 10; $trackNumber++) {
            $trackId = [Guid]::NewGuid(); $trackEditionId = [Guid]::NewGuid(); $trackAssetId = [Guid]::NewGuid()
            Invoke-SmokeSql @'
INSERT INTO works (id, media_type, work_kind, parent_work_id, collection_id, ordinal, ownership)
VALUES (@trackId, 'Music', 'child', @albumRootId, @albumId, @trackNumber, 'Owned');
INSERT INTO editions (id, work_id, format_label) VALUES (@trackEditionId, @trackId, 'MP3');
INSERT INTO media_assets (id, edition_id, library_id, content_hash, file_path_root)
VALUES (@trackAssetId, @trackEditionId, @libraryId, @contentHash, @filePath);
INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
    (@trackAssetId, 'title', @title, @now), (@trackAssetId, 'track_number', @numberText, @now);
'@ @{ trackId = $trackId; trackEditionId = $trackEditionId; trackAssetId = $trackAssetId;
     albumRootId = $albumRootId; albumId = $albumId; trackNumber = $trackNumber;
     libraryId = '55555555-5555-4555-8555-555555555555'; contentHash = "smoke-track-$trackAssetId";
     filePath = "Smoke/Music/Track$trackNumber.mp3"; title = "Smoke Track $trackNumber";
     numberText = "$trackNumber"; now = $now }
        }

        $comicCollectionId = [Guid]::NewGuid(); $comicRootId = [Guid]::NewGuid()
        Invoke-SmokeSql @'
INSERT INTO collections (id, display_name, collection_type, primary_area)
VALUES (@comicCollectionId, 'Smoke Comic Run', 'ComicSeries', 'Read');
INSERT INTO works (id, media_type, work_kind, collection_id, ownership)
VALUES (@comicRootId, 'Comics', 'parent', @comicCollectionId, 'Unowned');
INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
VALUES (@comicRootId, 'series', 'Smoke Comic Run', @now);
'@ @{ comicCollectionId = $comicCollectionId; comicRootId = $comicRootId; now = $now }
        for ($issueNumber = 1; $issueNumber -le 3; $issueNumber++) {
            $issueId = [Guid]::NewGuid(); $issueEditionId = [Guid]::NewGuid(); $issueAssetId = [Guid]::NewGuid()
            Invoke-SmokeSql @'
INSERT INTO works (id, media_type, work_kind, parent_work_id, collection_id, ordinal, ownership)
VALUES (@issueId, 'Comics', 'child', @comicRootId, @comicCollectionId, @issueNumber, 'Owned');
INSERT INTO editions (id, work_id, format_label) VALUES (@issueEditionId, @issueId, 'CBZ');
INSERT INTO media_assets (id, edition_id, library_id, content_hash, file_path_root)
VALUES (@issueAssetId, @issueEditionId, @libraryId, @contentHash, @filePath);
INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
    (@issueAssetId, 'title', @title, @now), (@issueAssetId, 'series_position', @numberText, @now);
'@ @{ issueId = $issueId; issueEditionId = $issueEditionId; issueAssetId = $issueAssetId;
     comicRootId = $comicRootId; comicCollectionId = $comicCollectionId; issueNumber = $issueNumber;
     libraryId = '11111111-1111-4111-8111-111111111111'; contentHash = "smoke-issue-$issueAssetId";
     filePath = "Smoke/Comics/Issue$issueNumber.cbz"; title = "Smoke Issue $issueNumber";
     numberText = "$issueNumber"; now = $now }
        }

        $showId = [Guid]::NewGuid(); $seasonId = [Guid]::NewGuid(); $episodeId = [Guid]::NewGuid(); $editionId = [Guid]::NewGuid(); $assetId = [Guid]::NewGuid()
        Invoke-SmokeSql @'
INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@showId, 'TV', 'parent', 'Owned');
INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership) VALUES (@seasonId, 'TV', 'parent', @showId, 1, 'Owned');
INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership) VALUES (@episodeId, 'TV', 'child', @seasonId, 1, 'Owned');
INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @episodeId, 'MP4');
INSERT INTO media_assets (id, edition_id, library_id, content_hash, file_path_root) VALUES (@assetId, @editionId, @libraryId, @contentHash, 'Smoke/TV/Smoke.Show.S01E01.mp4');
INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
    (@showId, 'show_name', 'Smoke Show', @now),
    (@seasonId, 'season_number', '1', @now),
    (@assetId, 'episode_title', 'Smoke Pilot', @now),
    (@assetId, 'episode_number', '1', @now);
'@ @{ showId = $showId; seasonId = $seasonId; episodeId = $episodeId; editionId = $editionId; assetId = $assetId; libraryId = '33333333-3333-4333-8333-333333333333'; contentHash = "smoke-tv-$assetId"; now = $now }
        for ($episodeNumber = 2; $episodeNumber -le 1000; $episodeNumber++) {
            $episodeId = [Guid]::NewGuid(); $editionId = [Guid]::NewGuid(); $assetId = [Guid]::NewGuid()
            Invoke-SmokeSql @'
INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
VALUES (@episodeId, 'TV', 'child', @seasonId, @episodeNumber, 'Owned');
INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @episodeId, 'MP4');
INSERT INTO media_assets (id, edition_id, library_id, content_hash, file_path_root)
VALUES (@assetId, @editionId, @libraryId, @contentHash, @filePath);
INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
    (@assetId, 'episode_title', @title, @now), (@assetId, 'episode_number', @numberText, @now);
'@ @{ episodeId = $episodeId; editionId = $editionId; assetId = $assetId; seasonId = $seasonId;
     episodeNumber = $episodeNumber; libraryId = '33333333-3333-4333-8333-333333333333';
     contentHash = "smoke-tv-$assetId"; filePath = "Smoke/TV/Episode$episodeNumber.mp4";
     title = "Smoke Episode $episodeNumber"; numberText = "$episodeNumber"; now = $now }
        }
        $transaction.Commit()
    } finally {
        $transaction.Dispose()
        $connection.Dispose()
    }
}

if ($SeedExistingFixture) {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $resolved = [System.IO.Path]::GetFullPath($SeedExistingFixture).TrimEnd('\', '/')
    if ([System.IO.Path]::GetDirectoryName($resolved).TrimEnd('\', '/') -ne $tempRoot -or
        -not ([System.IO.Path]::GetFileName($resolved) -like 'tuvima-smoke-*')) {
        throw 'SeedExistingFixture accepts only a generated fixture directly under temp.'
    }
    Add-SyntheticRows (Join-Path $resolved 'data\library.db') (Get-RepoRoot)
    Write-Host "Seeded synthetic works in $resolved"
    return
}

$repoRoot = Get-RepoRoot
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tuvima-smoke-" + [Guid]::NewGuid().ToString('N'))
$paths = [ordered]@{
    Root = $fixtureRoot
    Config = Join-Path $fixtureRoot 'config'
    Data = Join-Path $fixtureRoot 'data'
    Library = Join-Path $fixtureRoot 'library'
    Database = Join-Path $fixtureRoot 'data\library.db'
    Logs = Join-Path $fixtureRoot 'logs'
    Keys = Join-Path $fixtureRoot 'keys'
    Backups = Join-Path $fixtureRoot 'backups'
    Models = Join-Path $fixtureRoot 'models'
}

New-Item -ItemType Directory -Force -Path $paths.Config, $paths.Data, $paths.Library, $paths.Logs, $paths.Keys, $paths.Backups, $paths.Models | Out-Null
# Copy only known, nonsecret configuration templates. Do not copy provider,
# profile, device, service credential, backup, or secret files from this host.
$templateFiles = @(
    'core.json', 'ai.json', 'network.json', 'libraries.json', 'writeback.json',
    'writeback-fields.json', 'hydration.json', 'pipelines.json', 'media_types.json',
    'field_priorities.json', 'maintenance.json', 'scoring.json',
    'disambiguation.json', 'transcoding.json', 'pipeline-priority-defaults.json'
)
foreach ($name in $templateFiles) {
    Copy-Item -LiteralPath (Join-Path $repoRoot (Join-Path 'config' $name)) -Destination (Join-Path $paths.Config $name)
}
New-Item -ItemType Directory -Force -Path (Join-Path $paths.Config 'ui') | Out-Null
foreach ($name in 'global.json', 'palette.json', 'library-preferences.json', 'playback-client.json') {
    Copy-Item -LiteralPath (Join-Path $repoRoot (Join-Path 'config\ui' $name)) -Destination (Join-Path $paths.Config (Join-Path 'ui' $name))
}
New-Item -ItemType Directory -Force -Path (Join-Path $paths.Config 'providers') | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'config\providers\wikidata_reconciliation.json') `
    -Destination (Join-Path $paths.Config 'providers\wikidata_reconciliation.json')
Set-JsonFile (Join-Path $paths.Config 'providers\wikidata_reconciliation.json') {
    param($provider)
    $provider.enabled = $false
}

Set-JsonFile (Join-Path $paths.Config 'core.json') {
    param($core)
    $core.database_path = $paths.Database
    $core.data_root = $paths.Data
    $core.library_root = $paths.Library
    $core.plugin_catalog.enabled = $false
    $core.auth.external_providers = @()
    $core.auth.password_reset.mode = 'disabled'
    $core.auth.password_reset.smtp_host = ''
    $core.auth.password_reset.username = ''
    $core.storage_policy.artwork_export = $false
    $core.storage_policy.subtitle_export = $false
    $core.storage_policy.metadata_sidecar_export = $false
}
Set-JsonFile (Join-Path $paths.Config 'ai.json') {
    param($ai)
    $ai.dev_skip_download = $true
    $ai.models_directory = $paths.Models
    $ai.features.PSObject.Properties | ForEach-Object { $_.Value = $false }
}
Set-JsonFile (Join-Path $paths.Config 'network.json') {
    param($network)
    $network.local.port = $DashboardPort
    $network.local.discovery_enabled = $false
    $network.remote.enabled = $false
}
Set-JsonFile (Join-Path $paths.Config 'libraries.json') {
    param($libraries)
    foreach ($location in $libraries.storage_locations) {
        $location.path = Join-Path $paths.Library $location.id
        New-Item -ItemType Directory -Force -Path $location.path | Out-Null
    }
    foreach ($library in $libraries.libraries) {
        foreach ($source in $library.sources) {
            $source.path = Join-Path (Join-Path $paths.Library 'media') $library.category
            New-Item -ItemType Directory -Force -Path $source.path | Out-Null
            $source.access_mode = 'read_only'
            $source.participates_in_organization = $false
            $source.intake_role = 'none'
        }
    }
}

$result = [ordered]@{
    FixtureRoot = $paths.Root
    ConfigDirectory = $paths.Config
    DatabasePath = $paths.Database
    EngineUrl = "http://127.0.0.1:$EnginePort"
    DashboardUrl = "http://127.0.0.1:$DashboardPort"
    Authentication = 'Run the SmokeAdmin helper to create a disposable administrator and complete setup; then use -SeedExistingFixture.'
}
Write-Host "Smoke fixture: $($paths.Root)"

if (-not $Start) {
    [pscustomobject]$result
    return
}

Assert-LocalPortAvailable $EnginePort
Assert-LocalPortAvailable $DashboardPort

$previousEnvironment = @{}
foreach ($name in 'TUVIMA_CONFIG_DIR', 'TUVIMA_DB_PATH', 'TUVIMA_LIBRARY_ROOT', 'TUVIMA_MODELS_DIR', 'TUVIMA_BACKUP_DIR', 'TUVIMA_DATA_PROTECTION_DIR', 'TUVIMA_LOG_DIR', 'TUVIMA_ENGINE_URL', 'ASPNETCORE_URLS', 'ASPNETCORE_ENVIRONMENT') {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$engine = $null; $dashboard = $null; $startedSuccessfully = $false
try {
    $env:TUVIMA_CONFIG_DIR = $paths.Config; $env:TUVIMA_DB_PATH = $paths.Database; $env:TUVIMA_LIBRARY_ROOT = $paths.Library
    $env:TUVIMA_MODELS_DIR = $paths.Models; $env:TUVIMA_BACKUP_DIR = $paths.Backups; $env:TUVIMA_DATA_PROTECTION_DIR = $paths.Keys; $env:TUVIMA_LOG_DIR = $paths.Logs
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $result.EngineUrl
    $engineBinary = Join-Path $repoRoot "src\MediaEngine.Api\bin\$Configuration\net10.0\MediaEngine.Api.exe"
    $engine = Start-Process -FilePath $engineBinary -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $paths.Logs 'engine.stdout.log') `
        -RedirectStandardError (Join-Path $paths.Logs 'engine.stderr.log')
    Wait-HttpOk "$($result.EngineUrl)/health/live"
    # The first-run administrator setup resets catalogue state. Seed synthetic
    # works only after the disposable setup flow has completed.
    $result.SyntheticData = 'After setup completion, run this script with -SeedExistingFixture <FixtureRoot>.'

    $env:TUVIMA_ENGINE_URL = $result.EngineUrl; $env:ASPNETCORE_URLS = $result.DashboardUrl
    $dashboardBinary = Join-Path $repoRoot "src\MediaEngine.Web\bin\$Configuration\net10.0\MediaEngine.Web.exe"
    $dashboard = Start-Process -FilePath $dashboardBinary -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $paths.Logs 'dashboard.stdout.log') `
        -RedirectStandardError (Join-Path $paths.Logs 'dashboard.stderr.log')
    Wait-HttpOk "$($result.DashboardUrl)/health/live"
    $result.EnginePid = $engine.Id; $result.DashboardPid = $dashboard.Id
    [pscustomobject]$result
    $startedSuccessfully = $true
    if ($Keep) { return }
} finally {
    foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
    if (-not $Keep -or -not $startedSuccessfully) {
        if ($dashboard -and -not $dashboard.HasExited) { Stop-Process -Id $dashboard.Id -Force; Wait-Process -Id $dashboard.Id -Timeout 10 -ErrorAction SilentlyContinue }
        if ($engine -and -not $engine.HasExited) { Stop-Process -Id $engine.Id -Force; Wait-Process -Id $engine.Id -Timeout 10 -ErrorAction SilentlyContinue }
    }
    if (-not $Keep) {
        if (Test-Path $paths.Root) { Remove-SmokeFixture $paths.Root }
    }
}
