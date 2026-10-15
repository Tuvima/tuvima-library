param(
    [string]$Device = 'tv',
    [string]$BuildDir = ''
)
$ErrorActionPreference = 'Stop'

# Builds the webOS package folder from the shared client and PRINTS the ares-* commands. It runs none of them.
# The shared client is not duplicated in git: this copies it, adds appinfo.json, icons and the key map, and loads
# the key map before the other scripts.

$here = $PSScriptRoot
$client = Split-Path -Parent (Split-Path -Parent $here)
if (-not $BuildDir) { $BuildDir = Join-Path $here 'build' }

if (Test-Path -LiteralPath $BuildDir) { Remove-Item -LiteralPath $BuildDir -Recurse -Force }
New-Item -ItemType Directory -Path $BuildDir | Out-Null

foreach ($file in Get-ChildItem -LiteralPath $client -File | Where-Object { $_.Extension -in @('.html', '.js', '.css') }) {
    Copy-Item -LiteralPath $file.FullName -Destination $BuildDir
}
Copy-Item -LiteralPath (Join-Path $client 'assets') -Destination $BuildDir -Recurse
Copy-Item -LiteralPath (Join-Path $client 'vendor') -Destination $BuildDir -Recurse
foreach ($name in @('appinfo.json', 'icon.png', 'largeIcon.png', 'webos-keys.js')) {
    Copy-Item -LiteralPath (Join-Path $here $name) -Destination $BuildDir
}

$indexPath = Join-Path $BuildDir 'index.html'
$html = [IO.File]::ReadAllText($indexPath)
$marker = '<script src="api.js"></script>'
if (-not $html.Contains($marker)) { throw 'index.html no longer has the api.js script tag.' }
$html = $html.Replace($marker, '<script src="webos-keys.js"></script>' + "`n  " + $marker)
[IO.File]::WriteAllText($indexPath, $html, (New-Object Text.UTF8Encoding($false)))

$appId = (Get-Content -LiteralPath (Join-Path $here 'appinfo.json') -Raw | ConvertFrom-Json).id
$ipk = "dist/$appId`_0.1.0_all.ipk"

Write-Host "Package folder ready: $BuildDir"
Write-Host ''
Write-Host 'Run these yourself (webOS TV CLI, ares-*):'
Write-Host "  ares-package `"$BuildDir`" -o dist"
Write-Host '  ares-setup-device                      # once: add the TV (host, port 9922, user prisoner), name it e.g. tv'
Write-Host "  ares-novacom --device $Device --getkey   # once: fetch the key using the passphrase from the Developer Mode app"
Write-Host "  ares-install --device $Device $ipk"
Write-Host "  ares-launch --device $Device $appId"
Write-Host "  ares-inspect --device $Device --app $appId --open   # optional: remote debugger"
