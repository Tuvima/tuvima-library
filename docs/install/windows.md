---
title: "Install on Windows"
description: "Install Tuvima Library on Windows 10 or 11 with the guided installer. Early Access: some features are still in progress."
audience: "user"
category: "installation"
product_area: "install"
status: "early-access"
tags:
  - "install"
  - "windows"
---

:::caution[Early Access]
The Windows installer is Early Access. It works from tagged releases, and some features are still in progress. No installer release has been published yet, so for now [Docker](docker.md) is the recommended way to run Tuvima Library.
:::

The installer sets up Tuvima Library as two background services on your PC. Allow about 10 minutes.

## What you need

- Windows 10 or 11, 64-bit
- Administrator rights on the PC
- A modern web browser

## Install

1. Open the [Tuvima Library Releases page](https://github.com/Tuvima/tuvima_library/releases) and download `TuvimaLibrary-Setup-<version>.exe` from the newest Early Access release.
2. Run the file and approve the administrator prompt.
3. Choose whether to add a desktop shortcut, then finish the wizard.
4. Leave **Open Tuvima Library in your browser** ticked. The Dashboard opens at `http://localhost:5016`.

The first time you open it, the Dashboard walks you through [first-run setup](../tutorials/getting-started.md): the administrator password, optional profile PIN, and recovery codes. You can add media folders afterwards with [Your First Library](../tutorials/first-library.md).

## Update or uninstall

- **Update:** run the newer installer. It replaces the program files and keeps your existing configuration files.
- **Uninstall:** use **Apps > Installed apps**. The uninstaller stops and removes the two services. It leaves the data folders under `C:\ProgramData\Tuvima` in place, so your library data store, settings, and media are not deleted. Remove that folder yourself if you want a clean slate.

## Known limitations

- **No published release yet.** Installers are attached to tagged Early Access releases on GitHub.
- **Local AI is not set up by the installer.** Models and runtimes use the defaults in `config/ai.json`. If you want Local AI on Windows, follow [Local AI Models](../guides/local-ai-model-rollout.md) after installing.
- **Windows only, one PC.** To reach Tuvima from other devices, read [Remote access](../guides/remote-access.md) first.

## Next steps

- [Getting Started](../tutorials/getting-started.md)
- [Your First Library](../tutorials/first-library.md)
- [Product Status](../product/status.md)

<details>
<summary>Technical details</summary>

- **Services:** `TuvimaEngine` (port 61495) and `TuvimaDashboard` (port 5016), set to start automatically.
- **Program files:** `C:\Program Files\Tuvima Library` by default (`engine`, `dashboard`, and bundled FFmpeg under `engine\tools\ffmpeg`).
- **Data:** `C:\ProgramData\Tuvima\{config,db,watch,library}`. The installer rewrites only the packaged development default library path in `core.json` and `libraries.json`; existing configuration files are never overwritten on upgrade.
- **Engine environment:** `TUVIMA_CONFIG_DIR`, `TUVIMA_DB_PATH`, `TUVIMA_CORS_ORIGINS=http://localhost:5016`. No `TUVIMA_MODELS_DIR` or `TUVIMA_AI_RUNTIME_DIR` is set.
- **Build it yourself:** install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run `build-installer.bat` from the repository root. The release workflow builds it on `v*.*.*` tags.

</details>
