$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$rokuRoot = Join-Path $repoRoot 'clients/roku'
$required = @(
    'manifest',
    'source/main.brs',
    'components/MainScene.xml',
    'components/MainScene.brs',
    'components/ApiTask.xml',
    'components/ApiTask.brs'
)

foreach ($relative in $required) {
    $path = Join-Path $rokuRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Roku client is missing $relative."
    }
}

foreach ($xmlFile in Get-ChildItem -LiteralPath (Join-Path $rokuRoot 'components') -Filter '*.xml') {
    try { [xml](Get-Content -LiteralPath $xmlFile.FullName -Raw) | Out-Null }
    catch { throw "Invalid SceneGraph XML in $($xmlFile.Name): $($_.Exception.Message)" }
}

$source = Get-ChildItem -LiteralPath $rokuRoot -Recurse -File |
    Where-Object Extension -In @('.brs', '.xml') |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
$combined = $source -join "`n"

foreach ($requiredText in @(
    '/.well-known/tuvima',
    '/api/v1/oauth/device_authorization',
    '/api/v1/oauth/token',
    '/api/v1/display/home',
    '/api/v1/display/search',
    '/api/v1/display/continue',
    '/api/v1/view/timeline',
    '/api/v1/view/items/',
    '/preview?size=',
    '/api/v1/collections',
    '/items?limit=',
    '/api/v1/universe/',
    'universe_qid',
    'system_key',
    'also_in_this_world_count',
    'collection_id',
    'kind: "universeSeries"',
    'm.inUniverse',
    '/api/v1/devices/current/profiles',
    '/api/v1/devices/current/switch-profile',
    'id="photoViewer"',
    'id="profileList"',
    'title: "Collections"',
    'title: "Photos"',
    'photoDeniedText',
    'Photo access was not allowed for this device. Re-pair and tick photos and videos to enable it',
    'error.status = 428',
    'error.status = 409',
    'error.status = 429',
    '/api/v1/playback/',
    '/api/v1/player/heartbeat',
    'recommendedDelivery',
    'authorization_pending',
    'refresh_token'
)) {
    if (-not $combined.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Roku client is missing required contract behavior '$requiredText'."
    }
}

foreach ($forbidden in @('X-Tuvima-Service-Key', 'localhost:61495', 'outputPath')) {
    if ($combined.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Roku client contains forbidden boundary text '$forbidden'."
    }
}

Write-Host 'Roku native-client static gate passed.' -ForegroundColor Green
