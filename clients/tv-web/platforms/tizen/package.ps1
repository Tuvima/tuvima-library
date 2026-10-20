param(
    [string]$Certificate = '<certificate-profile>',
    [string]$Tv = '<tv-name>'
)
$ErrorActionPreference = 'Stop'

# Stages the Tizen package folder (build/) from the shared TV web client, then PRINTS the Tizen CLI commands.
# It never runs them: building, signing and installing need Tizen Studio and a TV in Developer Mode.

$here = $PSScriptRoot
$client = Split-Path -Parent (Split-Path -Parent $here)
$build = Join-Path $here 'build'

if (Test-Path -LiteralPath $build) { Remove-Item -LiteralPath $build -Recurse -Force }
New-Item -ItemType Directory -Path $build | Out-Null

foreach ($file in @('api.js', 'universe.js', 'nav.js', 'player.js', 'app.js', 'styles.css')) {
    Copy-Item -LiteralPath (Join-Path $client $file) -Destination $build
}
foreach ($folder in @('assets', 'vendor')) {
    Copy-Item -LiteralPath (Join-Path $client $folder) -Destination $build -Recurse
}
foreach ($file in @('config.xml', 'icon.png', 'tizen-keys.js')) {
    Copy-Item -LiteralPath (Join-Path $here $file) -Destination $build
}

# The shared index.html plus the Tizen key registration, loaded first.
$html = [IO.File]::ReadAllText((Join-Path $client 'index.html'))
$marker = '<script src="api.js"></script>'
if (-not $html.Contains($marker)) { throw 'Shared index.html no longer loads api.js; update package.ps1.' }
$html = $html.Replace($marker, '<script src="tizen-keys.js"></script>' + "`n  " + $marker)
[IO.File]::WriteAllText((Join-Path $build 'index.html'), $html, (New-Object Text.UTF8Encoding($false)))

Write-Host "Staged the Tizen package folder: $build" -ForegroundColor Green
Write-Host ''
Write-Host 'Run these from Tizen Studio''s command line (tizen, sdb on PATH). Nothing below has been run.'
Write-Host ''
Write-Host "tizen build-web -- `"$build`""
Write-Host "tizen package -t wgt -s $Certificate -- `"$build/.buildResult`""
Write-Host "tizen install -n Tuvima.wgt -t $Tv -- `"$build/.buildResult`""
Write-Host "tizen run -p TuvimaLib0.TuvimaLibrary -t $Tv"
Write-Host ''
Write-Host 'Before first launch, turn on native_app_access.allow_browser_apps in config/network.json.'
