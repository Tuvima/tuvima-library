$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$clientRoot = Join-Path $repoRoot 'clients'
$required = @(
    'android/core/src/main/java/com/tuvima/library/core/TuvimaClient.kt',
    'android/core/src/main/java/com/tuvima/library/core/SecureTokenStore.kt',
    'android/tv/src/main/java/com/tuvima/library/tv/PlaybackActivity.kt',
    'android/mobile/src/main/java/com/tuvima/library/mobile/TuvimaMediaLibraryService.kt',
    'android/mobile/src/main/java/com/tuvima/library/mobile/OfflineDownloadWorker.kt',
    'apple/Shared/TuvimaAPIClient.swift',
    'apple/Shared/KeychainTokenStore.swift',
    'apple/iOS/PlaybackCoordinator.swift',
    'apple/iOS/CarPlaySceneDelegate.swift',
    'roku/components/MainScene.brs'
)

foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $clientRoot $relative) -PathType Leaf)) {
        throw "Native-client source is missing $relative."
    }
}

$sourceFiles = Get-ChildItem -LiteralPath $clientRoot -Recurse -File |
    Where-Object Extension -In @('.kt', '.swift', '.brs', '.xml', '.yml', '.kts')
$combined = ($sourceFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

foreach ($forbidden in @('X-Tuvima-Service-Key', 'localhost:61495', 'http://localhost:61495', 'outputPath')) {
    if ($combined.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Native clients contain forbidden Engine-boundary text '$forbidden'."
    }
}

foreach ($requiredText in @(
    '/.well-known/tuvima',
    '/api/v1/oauth/device_authorization',
    '/api/v1/oauth/token',
    '/api/v1/display/home',
    '/api/v1/display/search',
    '/api/v1/details/',
    '/api/v1/playback/',
    '/api/v1/player/heartbeat',
    'authorization_pending',
    'refresh_token'
)) {
    if (-not $combined.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Native clients are missing required API v1 behavior '$requiredText'."
    }
}

$carPlay = Get-Content -LiteralPath (Join-Path $clientRoot 'apple/iOS/CarPlaySceneDelegate.swift') -Raw
$androidAuto = Get-Content -LiteralPath (Join-Path $clientRoot 'android/mobile/src/main/java/com/tuvima/library/mobile/TuvimaMediaLibraryService.kt') -Raw
$androidCar = Get-Content -LiteralPath (Join-Path $clientRoot 'android/core/src/main/java/com/tuvima/library/core/CarBrowse.kt') -Raw
foreach ($forbiddenAutomotive in @('lane: "watch"', 'lane: "read"', 'lane = "watch"', 'lane = "read"', 'admin', 'settings')) {
    if ($carPlay.Contains($forbiddenAutomotive, [StringComparison]::OrdinalIgnoreCase) -or
        $androidAuto.Contains($forbiddenAutomotive, [StringComparison]::OrdinalIgnoreCase) -or
        $androidCar.Contains($forbiddenAutomotive, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Automotive client contains out-of-scope surface '$forbiddenAutomotive'."
    }
}

if (-not $carPlay.Contains('lane: "listen"', [StringComparison]::Ordinal) -or
    -not $androidAuto.Contains('ListenLogic.LANE', [StringComparison]::Ordinal) -or
    -not $androidCar.Contains('lane = ListenLogic.LANE', [StringComparison]::Ordinal)) {
    throw 'Automotive clients must expose only the Listen lane.'
}

# CarPlay still lists the profile Listen queue. Android Auto follows the contract's car list (clients/contracts/v1/player-controls.md, "Cars"):
# the service builds its root from CarList.rootEntries() and CarBrowse.kt names every entry of the contract's list.
if (-not $carPlay.Contains('("Queue", "Queue")', [StringComparison]::Ordinal) -or
    -not $androidAuto.Contains('CarList.rootEntries()', [StringComparison]::Ordinal)) {
    throw 'Automotive clients must expose the contract car list.'
}

# Playlists is in the contract's list but no route reads playlists for the apps yet, so the only way it may be missing from the car's root is the
# documented flag (CarList.PLAYLISTS_AVAILABLE) that the root list filters on; the service takes the root as it comes, with no list of its own.
foreach ($playlistRule in @('const val PLAYLISTS_AVAILABLE', 'playlistsAvailable || it != CarEntry.Playlists')) {
    if (-not $androidCar.Contains($playlistRule, [StringComparison]::Ordinal)) {
        throw "Android Auto car list must leave Playlists out only through the documented flag ($playlistRule)."
    }
}

if ($androidAuto.Contains('rootEntries(playlistsAvailable', [StringComparison]::Ordinal) -or
    $androidAuto.Contains('CarEntry.Playlists.nodeId', [StringComparison]::Ordinal)) {
    throw 'Android Auto service must not decide on its own whether the Playlists entry is in the root.'
}

foreach ($carEntry in @('"Continue"', '"The Listen shelves"', '"Audiobooks"', '"Playlists"', '"Recently played"')) {
    if (-not $androidCar.Contains($carEntry, [StringComparison]::Ordinal)) {
        throw "Android Auto car list is missing the contract entry $carEntry."
    }
}

Write-Host 'Native-client source boundary gate passed.' -ForegroundColor Green
