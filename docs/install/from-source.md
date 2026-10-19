---
title: "Run from source"
description: "Run the Engine and Dashboard from a source checkout with the required .NET SDK and local configuration checks."
audience: "developer"
category: "installation"
product_area: "deployment"
status: current
---

# Run from source

Use a source checkout to develop or evaluate Tuvima Library. Allow 15–30 minutes, plus tool installation, package restore, and optional AI downloads.

## Check prerequisites

Install Git and a stable .NET 10 SDK. `global.json` requests `10.0.100` with `latestFeature` roll-forward, so a later stable .NET 10 feature band can be selected.

```powershell
git --version
dotnet --version
```

Review `nuget.config` before restore. This checkout uses nuget.org. Some local development setups map `Tuvima.Wikidata*` to the sibling feed at `C:\Users\shaya\OneDrive\Documents\Source\Repos\tuvima-wikidata\artifacts`. If restore cannot find those packages, check your checkout's feed before changing package versions.

## Get the code

```powershell
git clone https://github.com/Tuvima/tuvima-library.git
cd tuvima-library
dotnet restore MediaEngine.slnx
```

## Choose safe local paths

Before starting, read `config/core.json` and `config/libraries.json`. The committed files can contain development-machine paths. Set your own data root, managed library root, approved storage locations, and sources.

Keep originals read-only when evaluating. Put provider keys in ignored `config/secrets/` files. Local AI weights and native runtimes use separate storage; see [shared AI storage](../guides/shared-ai-storage.md).

## Helper tools

The Library uses two helper tools that come from outside the .NET build:

- **FFmpeg and FFprobe** are always required, as one set. They read every video and audio file (for example `.mkv`, `.mp4`, `.mp3`, `.m4b` and `.flac`), and they are needed for thumbnails, smooth (adaptive) streaming, casting and subtitle and audio syncing. Without them, those files show **Waiting for media tools** and finish by themselves once FFmpeg is found. Nothing fails.
- **Tesseract** is optional. It needs its English and page-orientation data. Without it, scanned pages and photos of paper are not searchable. Documents that already contain text keep working. The OCR settings are in [configuration](../reference/configuration.md#configocrjson).

The image engine and the local AI engine ship inside the app. AI models download on request from the same Tools page.

**Windows.** On the Tools page, choose **Install for me**. For FFmpeg it downloads the pinned, checksum-verified build into the Library's data folder. For Tesseract it downloads the official installer and asks you to run it; Windows asks for administrator approval. You can also run `winget install Gyan.FFmpeg` and `winget install UB-Mannheim.TesseractOCR`, or the developer scripts `pwsh -File tools/Download-FFmpeg.ps1` and `pwsh -File tools/Download-Tesseract.ps1` (the Tesseract script needs 7-Zip).

**Linux.** `sudo apt install ffmpeg tesseract-ocr`

**macOS.** `brew install ffmpeg tesseract`

Then choose **Check again** on the Tools page. No restart is needed.

The Library looks for each tool in this order:

1. A location you chose with **Locate it** on the Tools page. It is saved as the tool's explicit path in `config/transcoding.json` (`ffmpeg_binary_path` and `ffprobe_binary_path`) or `config/ocr.json` (`tesseract_binary_path`).
2. The Library's data folder, `tools/ffmpeg` or `tools/tesseract`.
3. The app's own `tools/` folder.
4. The system PATH.
5. The tool's standard install folder. On Windows, Tesseract is in `C:\Program Files\Tesseract-OCR`.

To check, open **Settings > Tools and requirements** and look for **Ready** beside each tool. The Engine log also has one `Tools check:` summary line at start-up. The Library checks again at first run and at every Engine start-up.

## Start both apps

1. Stop any existing Tuvima Engine or Dashboard development processes.
2. From the repository root, start the Engine:

   ```powershell
   dotnet run --project src/MediaEngine.Api
   ```

3. Wait for `Now listening on: http://localhost:61495`.
4. In another terminal at the same repository root, start the Dashboard:

   ```powershell
   dotnet run --project src/MediaEngine.Web
   ```

5. Open `http://localhost:5016/setup` and create the administrator. Store the recovery codes safely.

Launch profiles point both apps at `../../config`. If you override `TUVIMA_CONFIG_DIR`, use the same resolved directory for both apps. Set `TUVIMA_ENGINE_URL` before launching the Dashboard if the Engine address differs.

Press `Ctrl+C` in both terminals to stop. Do not expose the Engine to other devices.

## Next steps

- [Set up development tools and verification](../tutorials/dev-setup.md).
- [Add your first library](../tutorials/first-library.md).
- [Resolve startup problems](../guides/troubleshooting.md).
