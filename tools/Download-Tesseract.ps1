# ──────────────────────────────────────────────────────────────────────────────
# Download-Tesseract.ps1
# Downloads the pinned Tesseract OCR Windows build and the English language data and
# places them in tools/tesseract/ so the Engine can auto-detect text recognition
# without any system install.
#
# The Windows build is the UB Mannheim installer. It is unpacked with 7-Zip instead of being
# run, so nothing is installed on this computer and no administrator prompt appears.
# Requires 7-Zip (7z.exe) on PATH, in Program Files, or passed with -SevenZipPath.
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File tools/Download-Tesseract.ps1
# ──────────────────────────────────────────────────────────────────────────────
param(
    [string]$SevenZipPath = ''
)

$ErrorActionPreference = 'Stop'

$version = '5.4.0.20240606'
$installer = "tesseract-ocr-w64-setup-$version.exe"
$installerSha256 = 'C885FFF6998E0608BA4BB8AB51436E1C6775C2BAFC2559A19B423E18678B60C9'
$tesseractSha256 = 'BABB405F4366B480D02CD8FF2BAC8D497170F6C1711CE6F3D5D8BF0FB7FA6ED9'
$installerUrl = "https://github.com/UB-Mannheim/tesseract/releases/download/v$version/$installer"

# English and orientation data from tessdata_fast (Apache-2.0), pinned to one commit.
$dataCommit = '923915d4ced2a7235221788285785a29c4a42d4a'
$dataFiles = @{
    'eng.traineddata' = '7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2'
    'osd.traineddata' = '19E9B11EE4549A15BE965B74514745FBDA70EFA791B11CFBC5D53609F86EE60A'
}

function Find-SevenZip {
    if ($SevenZipPath -and (Test-Path -LiteralPath $SevenZipPath)) { return $SevenZipPath }
    $onPath = Get-Command '7z.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($candidate in @(
            (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
            (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe'),
            (Join-Path $env:USERPROFILE 'scoop\apps\7zip\current\7z.exe'))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw 'Could not find 7z.exe. Install 7-Zip or pass -SevenZipPath.'
}

function Assert-Hash([string]$path, [string]$expected, [string]$label) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($actual -ne $expected) {
        throw "$label checksum mismatch. Expected $expected; received $actual."
    }
}

$sevenZip = Find-SevenZip
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("tuvima-tesseract-" + [Guid]::NewGuid().ToString('N'))
$setup = Join-Path $tempRoot $installer
$out = Join-Path $tempRoot 'extract'
$dest = Join-Path $PSScriptRoot 'tesseract'

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Write-Host "Downloading pinned Tesseract $version from GitHub..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $installerUrl -OutFile $setup -UseBasicParsing
    Assert-Hash $setup $installerSha256 'Tesseract installer'

    Write-Host 'Installer checksum verified. Unpacking...' -ForegroundColor Cyan
    & $sevenZip x -y "-o$out" $setup | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '7-Zip could not unpack the installer.' }

    $exe = Join-Path $out 'tesseract.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'tesseract.exe was not found in the installer.' }
    Assert-Hash $exe $tesseractSha256 'tesseract.exe'

    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item -LiteralPath $exe -Destination $dest -Force
    Get-ChildItem -LiteralPath $out -Filter '*.dll' -File | Copy-Item -Destination $dest -Force
    $license = Join-Path $out 'doc\LICENSE'
    if (Test-Path -LiteralPath $license) {
        Copy-Item -LiteralPath $license -Destination (Join-Path $dest 'LICENSE.txt') -Force
    }

    $tessdata = Join-Path $dest 'tessdata'
    New-Item -ItemType Directory -Path $tessdata -Force | Out-Null
    foreach ($name in $dataFiles.Keys) {
        $target = Join-Path $tessdata $name
        Invoke-WebRequest -Uri "https://github.com/tesseract-ocr/tessdata_fast/raw/$dataCommit/$name" -OutFile $target -UseBasicParsing
        Assert-Hash $target $dataFiles[$name] $name
    }

    Write-Host "Tesseract installed to: $dest" -ForegroundColor Green
    & (Join-Path $dest 'tesseract.exe') --version 2>&1 | Select-Object -First 1
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
