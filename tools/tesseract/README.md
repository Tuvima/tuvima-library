# Packaged Tesseract

Windows installers bundle the Tesseract OCR executable, its libraries and the English
language data downloaded by `tools/Download-Tesseract.ps1`. The executables, libraries and
language data are intentionally gitignored because of their size. Tuvima runs Tesseract as a
separate program on this computer to read scanned PDFs and photos of paper; nothing is sent
anywhere.

- Upstream program: [tesseract-ocr/tesseract](https://github.com/tesseract-ocr/tesseract), Apache License 2.0
- Windows build: [UB Mannheim](https://github.com/UB-Mannheim/tesseract/wiki) `v5.4.0.20240606`
  (`tesseract-ocr-w64-setup-5.4.0.20240606.exe`)
- Installer SHA-256: `C885FFF6998E0608BA4BB8AB51436E1C6775C2BAFC2559A19B423E18678B60C9`
- `tesseract.exe` SHA-256: `BABB405F4366B480D02CD8FF2BAC8D497170F6C1711CE6F3D5D8BF0FB7FA6ED9`
- Language data: [tesseract-ocr/tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast),
  Apache License 2.0, pinned to commit `923915d4ced2a7235221788285785a29c4a42d4a`
  - `eng.traineddata` SHA-256: `7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2`
  - `osd.traineddata` (page orientation) SHA-256: `19E9B11EE4549A15BE965B74514745FBDA70EFA791B11CFBC5D53609F86EE60A`

The Windows build also ships the libraries Tesseract needs (Leptonica, libpng, libjpeg-turbo,
libtiff, libwebp and others); each keeps its own permissive or LGPL licence. They are used
unmodified as separate files next to the executable.

The download script unpacks the installer with 7-Zip (nothing is installed on the computer) and
validates the installer, executable and language data hashes. `build-installer.bat` validates the
executable hash before publishing. `LICENSE.txt` is the Apache 2.0 licence from the upstream bundle.

Containers install the distribution packages `tesseract-ocr` and `tesseract-ocr-eng`.
Other languages are an explicit download from Settings and are stored in the Tuvima data location
(`.data/ocr/tessdata`), never in build output.
