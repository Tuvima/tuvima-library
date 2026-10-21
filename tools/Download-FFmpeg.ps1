# ──────────────────────────────────────────────────────────────────────────────
# Download-FFmpeg.ps1
# Downloads the pinned FFmpeg Windows build and places ffmpeg.exe + ffprobe.exe
# in tools/ffmpeg/ so the Engine can auto-detect them without any system install.
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File tools/Download-FFmpeg.ps1
# ──────────────────────────────────────────────────────────────────────────────

$ErrorActionPreference = 'Stop'

$release = '9.0.2'
$archive = 'ffmpeg-9.0.2-essentials_build.zip'
$archiveSha256 = '60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA'
$ffmpegSha256 = '3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC'
$ffprobeSha256 = 'F0D36ECBBDD3BCFAC3EFA078C96C7271C2E68B3810595552AC3B7F17E9A65C52'
$url  = "https://github.com/GyanD/codexffmpeg/releases/download/$release/$archive"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("tuvima-ffmpeg-" + [Guid]::NewGuid().ToString('N'))
$zip  = Join-Path $tempRoot $archive
$out  = Join-Path $tempRoot 'extract'
$dest = Join-Path $PSScriptRoot 'ffmpeg'

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Write-Host "Downloading pinned FFmpeg $release from GitHub..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

    $actualArchiveSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
    if ($actualArchiveSha256 -ne $archiveSha256) {
        throw "FFmpeg archive checksum mismatch. Expected $archiveSha256; received $actualArchiveSha256."
    }

    Write-Host "Archive checksum verified. Extracting..." -ForegroundColor Cyan
    Expand-Archive -LiteralPath $zip -DestinationPath $out -Force

    $ffmpeg  = Get-ChildItem -Path $out -Recurse -Filter 'ffmpeg.exe'  | Select-Object -First 1
    $ffprobe = Get-ChildItem -Path $out -Recurse -Filter 'ffprobe.exe' | Select-Object -First 1
    $license = Get-ChildItem -Path $out -Recurse -File |
        Where-Object { $_.Name -in @('LICENSE.txt', 'COPYING.GPLv3', 'LICENSE') } |
        Select-Object -First 1

    if (-not $ffmpeg -or -not $ffprobe) {
        throw 'Could not find ffmpeg.exe and ffprobe.exe in the downloaded archive.'
    }

    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item -LiteralPath $ffmpeg.FullName  -Destination $dest -Force
    Copy-Item -LiteralPath $ffprobe.FullName -Destination $dest -Force
    if ($license) {
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $dest 'LICENSE.txt') -Force
    }

    $actualFfmpegSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $dest 'ffmpeg.exe')).Hash
    $actualFfprobeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $dest 'ffprobe.exe')).Hash
    if ($actualFfmpegSha256 -ne $ffmpegSha256 -or $actualFfprobeSha256 -ne $ffprobeSha256) {
        throw 'Extracted FFmpeg executable checksum mismatch.'
    }

    Write-Host "FFmpeg installed to: $dest" -ForegroundColor Green
    & "$dest\ffmpeg.exe" -version 2>&1 | Select-Object -First 1
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
