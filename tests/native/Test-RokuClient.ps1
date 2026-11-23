$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$rokuRoot = Join-Path $repoRoot 'clients/roku'
$required = @(
    'manifest',
    'source/main.brs',
    'components/MainScene.xml',
    'components/MainScene.brs',
    'components/ApiTask.xml',
    'components/ApiTask.brs',
    'components/PlayerLogic.brs',
    'components/PlayerControls.brs',
    'components/PlayerOverlay.xml',
    'components/PlayerOverlay.brs',
    'components/SidePanel.xml',
    'components/SidePanel.brs',
    'components/PanelRow.xml',
    'components/PanelRow.brs',
    'components/TransportButton.xml',
    'components/TransportButton.brs',
    'components/ToolPill.xml',
    'components/ToolPill.brs',
    'images/circle.png',
    'images/play.png',
    'images/pause.png',
    'images/previous.png',
    'images/next.png',
    'images/skip-back.png',
    'images/skip-forward.png',
    'images/check.png'
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

# Every script and image a component names is in the package, and every component declares the name its file has.
foreach ($xmlFile in Get-ChildItem -LiteralPath (Join-Path $rokuRoot 'components') -Filter '*.xml') {
    $xmlText = Get-Content -LiteralPath $xmlFile.FullName -Raw
    foreach ($match in [regex]::Matches($xmlText, 'uri="pkg:/([^"]+)"')) {
        $target = Join-Path $rokuRoot $match.Groups[1].Value
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "$($xmlFile.Name) names $($match.Groups[1].Value), which is not in the package."
        }
    }
    $expected = [System.IO.Path]::GetFileNameWithoutExtension($xmlFile.Name)
    if ($xmlText -notmatch ('<component\s+name="' + $expected + '"')) {
        throw "$($xmlFile.Name) declares a different component name."
    }
}

# Every sub and function is closed (a quick guard for code that cannot be built here).
foreach ($brsFile in Get-ChildItem -LiteralPath (Join-Path $rokuRoot 'components') -Filter '*.brs') {
    $code = (Get-Content -LiteralPath $brsFile.FullName) | Where-Object { -not $_.TrimStart().StartsWith("'") }
    $codeText = $code -join "`n"
    $opened = [regex]::Matches($codeText, '(?im)^\s*(sub|function)\s').Count
    $closed = [regex]::Matches($codeText, '(?i)\bend\s+(sub|function)\b').Count
    if ($opened -ne $closed) {
        throw "$($brsFile.Name) opens $opened subs or functions and closes $closed."
    }
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
    'refresh_token',
    # Player controls (spec native-apps WP6): the words are the contract's, the keys are its tv-keys table.
    'Audio & subtitles',
    'Playback settings',
    'Bookmark at {0}',
    'Show on timeline',
    'Reset playback preferences',
    'Playback information',
    'No Global bookmarks on this title yet',
    'No bookmarks of yours yet',
    'Select a bookmark to jump to that moment.',
    ('Couldn' + [char]0x2019 + 't save ' + [char]0x00B7 + ' Try again'),
    'Autoplay next episode',
    'Starts in {0} seconds',
    'Next episode',
    'Play now',
    '"Skip back " + skip.back.ToStr() + " s"',
    '"Skip forward " + skip.forward.ToStr() + " s"',
    'if key = "rev" then return "skip-back"',
    'if key = "fwd" then return "skip-forward"',
    'if key = "play" then return "toggle-play"',
    'return "panel-root"',
    'return "close-panel"',
    'return "hide-overlay"',
    'plOverlayMayHide',
    'itemSize="[644,92]"',
    'translation="[1220,0]" width="700" height="1080"',
    'isYours',
    'inEveryone',
    '/api/v1/player/audiobooks/',
    '/bookmarks',
    '/api/v1/devices/current/playback-preferences',
    '/api/v1/details/tvShow/',
    'transfer.SetRequest("PUT")',
    'transfer.SetRequest("DELETE")',
    'KeyboardDialog',
    'm.video.enableUI = false',
    'return playerKey(key)'
)) {
    if (-not $combined.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Roku client is missing required contract behavior '$requiredText'."
    }
}

# A television has no note field and no search box (a remote cannot type); those words never come back as typing UI.
foreach ($forbidden in @('X-Tuvima-Service-Key', 'localhost:61495', 'outputPath', 'Add a note', 'Search bookmarks')) {
    if ($combined.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Roku client contains forbidden boundary text '$forbidden'."
    }
}

Write-Host 'Roku native-client static gate passed.' -ForegroundColor Green
