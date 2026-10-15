$ErrorActionPreference = 'Stop'

# Static gate for the shared TV web client (clients/tv-web): every script parses, no server addresses or secrets are
# baked in, every required screen and API v1 route is present, the wire names it reads exist in the checked-in
# fixtures, and the Node behaviour checks for the API layer and remote input pass.

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$client = Join-Path $repoRoot 'clients/tv-web'
$fixtures = Join-Path $repoRoot 'tests/fixtures/native-client-v1'

$required = @(
    'index.html', 'app.js', 'api.js', 'nav.js', 'player.js', 'styles.css', 'README.md',
    'vendor/hls.min.js', 'vendor/LICENSE-hls.js.txt', 'assets/tuvima-icon.svg', 'assets/tuvima-logo-dark.svg'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $client $relative) -PathType Leaf)) {
        throw "TV web client is missing $relative."
    }
}

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { throw 'Node.js is required for the TV web gate (node --check and the behaviour checks).' }

# 1. Every JavaScript file parses (vendored hls.js included).
$scripts = Get-ChildItem -LiteralPath $client -Recurse -File -Filter '*.js'
foreach ($script in $scripts) {
    & node --check $script.FullName
    if ($LASTEXITCODE -ne 0) { throw "node --check failed for $($script.FullName)." }
}

# Own source = everything except the vendored library; that is what the content rules apply to.
$own = Get-ChildItem -LiteralPath $client -Recurse -File |
    Where-Object { $_.Extension -in @('.js', '.html', '.css') -and $_.FullName -notmatch '[\\/]vendor[\\/]' }
$ownText = ($own | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$js = ($own | Where-Object Extension -eq '.js' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

# 2. No hard-coded servers, ports, file-system paths, or Dashboard credentials.
foreach ($pattern in @(
    'localhost', '127\.0\.0\.1', '0\.0\.0\.0', ':61495', ':61494', ':5016', ':7062', ':5017',
    'X-Tuvima-Service-Key', '[A-Za-z]:\\\\', '/home/[a-z]', '/Users/', 'api_key', 'apikey')) {
    if ($ownText -match "(?i)$pattern") { throw "TV web client contains forbidden text matching '$pattern'." }
}

# 3. Tokens live in localStorage only; no cookies or other stores. No HTML injection or dynamic code.
if (-not $js.Contains('localStorage')) { throw 'TV web client must keep its tokens in localStorage.' }
foreach ($forbidden in @('sessionStorage', 'document.cookie', 'indexedDB', 'innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'eval(', 'new Function')) {
    if ($js.Contains($forbidden)) { throw "TV web client must not use '$forbidden'." }
}

# 4. index.html loads the scripts in order, as classic scripts (so it also runs from file:// in a packaged app).
$html = Get-Content -LiteralPath (Join-Path $client 'index.html') -Raw
$last = -1
foreach ($name in @('api.js', 'nav.js', 'player.js', 'app.js')) {
    $at = $html.IndexOf("src=`"$name`"", [StringComparison]::Ordinal)
    if ($at -lt 0) { throw "index.html does not load $name." }
    if ($at -lt $last) { throw "index.html must load $name after the previous script." }
    $last = $at
}
if ($html -match 'type\s*=\s*"module"') { throw 'index.html must use classic scripts, not ES modules (file:// packaging).' }
if (-not $html.Contains('styles.css')) { throw 'index.html does not load styles.css.' }

# 5. Required API v1 routes, wire names and behaviours are referenced.
foreach ($requiredText in @(
    '/.well-known/tuvima',
    '/api/v1/oauth/device_authorization',
    '/api/v1/oauth/token',
    'urn:ietf:params:oauth:grant-type:device_code',
    'authorization_pending', 'slow_down', 'access_denied', 'expired_token',
    'refresh_token',
    '/api/v1/display/home', '/api/v1/display/browse', '/api/v1/display/continue', '/api/v1/display/recent',
    '/api/v1/display/search', '/api/v1/display/shelves/', '/api/v1/display/groups/',
    '/api/v1/details/', '/next-episodes',
    '/api/v1/collections', '/api/v1/universe/',
    '/api/v1/playback/', '/manifest', 'recommendedDelivery', 'hlsUrl', 'hlsStatus', 'subtitleTracks', 'deliveryUrl',
    '/api/v1/player/heartbeat', '/api/v1/player/state', 'hasPlaybackEnded', 'positionSeconds',
    '/api/v1/devices/current/profiles', '/api/v1/devices/current/switch-profile', '/api/v1/devices/current/capabilities',
    "'/api/v1/devices/'",
    'tv-web', 'tv-web-hevc', 'client_id', 'device_class', 'television')) {
    if (-not $js.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "TV web client is missing required contract behavior '$requiredText'."
    }
}

# 6. Required screens and behaviours.
foreach ($screen in @('connect', 'pair', 'home', 'lane', 'collections', 'collectionItems', 'universe', 'search', 'details', 'shelf', 'group', 'profiles', 'settings')) {
    if (-not $js.Contains("screens.$screen = ", [StringComparison]::Ordinal)) { throw "TV web client has no '$screen' screen." }
}
foreach ($lane in @("lane: 'watch'", "lane: 'read'", "lane: 'listen'")) {
    if (-not $js.Contains($lane, [StringComparison]::Ordinal)) { throw "TV web client has no $lane tab." }
}
foreach ($behavior in @(
    'function initials(',          # profile avatar shows initials when avatar_url is null
    'error.status === 428',        # PIN prompt on 428
    'pinDialog(',
    'confirmDialog(',              # sign out and server change ask first
    'api.signOut()',               # settings sign out revokes the device
    'TV.player.open(',
    'autoLevelCapping',            # the quality setting
    'xhr.setRequestHeader(',       # bearer token on stream requests through hls.js
    'vendor/hls.min.js',
    'isServerUrl(',                # the bearer token goes only to the paired server
    'nav.exitApp()')) {            # Back on the first screen leaves through the platform
    if (-not $js.Contains($behavior, [StringComparison]::Ordinal)) { throw "TV web client is missing '$behavior'." }
}
foreach ($code in @('10009', '461', '415', '10252')) {
    if (-not $js.Contains($code, [StringComparison]::Ordinal)) { throw "TV web client key map lacks code $code (Tizen/webOS keys)." }
}
if ($ownText -notmatch 'font-size:\s*calc\(100vh / 45\)') { throw 'styles.css must set the 24 px root at 1080p (100vh / 45).' }
if ($ownText -notmatch '--target:\s*2\.34rem') { throw 'styles.css must keep 56 px (2.34rem) targets.' }
if ($ownText -notmatch '--margin-x:\s*5vw' -or $ownText -notmatch '--margin-y:\s*5vh') { throw 'styles.css must keep 5 percent safe margins.' }
if ($ownText -notmatch '\.focusable\.focused') { throw 'styles.css must draw a visible focus ring.' }

# 7. Fixtures parse, and the wire names the client reads exist in them and in the client source.
foreach ($fixture in Get-ChildItem -LiteralPath $fixtures -Filter '*.json') {
    try { Get-Content -LiteralPath $fixture.FullName -Raw | ConvertFrom-Json | Out-Null }
    catch { throw "Fixture $($fixture.Name) is not valid JSON: $($_.Exception.Message)" }
}
$wire = [ordered]@{
    'discovery.json'              = @('supported_api_versions', 'device_authorization_endpoint', 'token_endpoint')
    'device-authorization.json'   = @('device_code', 'user_code', 'verification_uri', 'expires_in', 'interval')
    'oauth-token.json'            = @('access_token', 'refresh_token', 'expires_in', 'device_id', 'profile_id', 'scope')
    'switch-profile.json'         = @('access_token', 'refresh_token', 'profile_id')
    'device-profiles.json'        = @('profiles', 'profile_id', 'display_name', 'avatar_url', 'is_kids', 'requires_pin', 'is_active')
    'playback-manifest-hls.json'  = @('recommendedDelivery', 'hlsUrl', 'hlsStatus', 'resume', 'positionSeconds', 'subtitleTracks')
    'next-episodes.json'          = @('episodes', 'asset_id', 'title', 'season_number', 'episode_number')
    'collections.json'            = @('items', 'group_type', 'universe_qid', 'item_count', 'artwork_items', 'cover_url', 'next_cursor', 'has_more')
    'collection-items.json'       = @('work_id', 'title', 'media_type', 'cover_url', 'detail_route')
    'universe.json'               = @('qid', 'name', 'shelves', 'collection_id', 'shelf')
}
foreach ($file in $wire.Keys) {
    $raw = Get-Content -LiteralPath (Join-Path $fixtures $file) -Raw
    foreach ($name in $wire[$file]) {
        if ($raw -notmatch "`"$name`"") { throw "Fixture $file has no '$name' (the client reads it)." }
        if (-not $js.Contains($name, [StringComparison]::Ordinal)) { throw "TV web client never reads '$name' from $file." }
    }
}

# 8. Behaviour checks for the API layer and remote input.
foreach ($test in @('tv-web-api.test.js', 'tv-web-input.test.js')) {
    $path = Join-Path $PSScriptRoot $test
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing $test." }
    & node $path
    if ($LASTEXITCODE -ne 0) { throw "$test failed." }
}

Write-Host 'TV web client static gate passed.' -ForegroundColor Green
