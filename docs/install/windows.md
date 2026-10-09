---
title: "Install on Windows"
description: "Install Tuvima Library on a Windows 10 or 11 PC with the Early Access installer, which runs the Engine and Dashboard as Windows services."
audience: "administrator"
category: "installation"
product_area: "deployment"
status: early-access
---

# Install on Windows

Run Tuvima Library on a Windows 10 or 11 PC without Docker. The installer sets up the Engine and Dashboard as Windows services that start with your computer, then opens the Dashboard in your browser.

:::caution[Early Access]
The Windows installer is still being refined. Read [Known limitations](#known-limitations) before using it with a library you care about, and keep your own backup of your original media. For the most predictable setup today, use [Docker Compose](docker.md).
:::

## Before you start

You need:

- Windows 10 or 11, 64-bit.
- An account with administrator rights.
- Free disk space for the app and your catalogue, plus more if you plan to use local AI models.

## Download the installer

Installers are attached to tagged releases on the [GitHub Releases page](https://github.com/Tuvima/tuvima_library/releases). Download the newest `TuvimaLibrary-Setup-<version>.exe` and read that release's notes.

If no release is listed yet, use [Docker Compose](docker.md), or build the installer yourself (see **Technical details** at the end of this page).

## Install

1. Run `TuvimaLibrary-Setup-<version>.exe` and approve the administrator prompt.
2. Choose the install folder, or keep the default `C:\Program Files\Tuvima Library`.
3. Choose whether to add a desktop shortcut.
4. Finish the installer. It starts both services and opens `http://localhost:5016`.
5. Complete first-run setup: create the administrator account, save the recovery codes outside this PC, and add media folders now or later.

Continue with [Your first library](../tutorials/first-library.md).

## What the installer sets up

| Item | Details |
| --- | --- |
| App files | `C:\Program Files\Tuvima Library` (or your chosen folder) |
| Windows services | `TuvimaEngine` (port `61495`) and `TuvimaDashboard` (port `5016`), set to start automatically |
| Data folders | `config`, `db`, `watch` and `library` under `C:\ProgramData\Tuvima` |
| Media tools | FFmpeg and FFprobe, included with the app |
| Shortcuts | **Open Tuvima Library** in the Start menu, plus an optional desktop shortcut |

Only the Dashboard should be reachable from other devices. Keep port `61495` blocked in Windows Firewall.

## Update or uninstall

To update, run the newer installer. It stops and replaces both services, and it keeps your existing configuration files.

To uninstall, use **Settings > Apps** in Windows or **Uninstall Tuvima Library** in the Start menu. Uninstalling stops and removes both services. Data under `C:\ProgramData\Tuvima` is not deleted. Back up that folder and your original media before an update or uninstall.

## Known limitations

These apply to the current Early Access installer:

- The Dashboard service does not receive the Engine's configuration-folder setting.
- The installer gives every signed-in Windows user modify access to the data folders.
- Installers built by the release pipeline do not enable the bundled local AI runtime; locally built installers do.
- The installer needs FFmpeg files at build time and does not include the host recovery console.

<details>
<summary>Technical details</summary>

The installer is defined in `installer.iss` (Inno Setup 6). `build-installer.bat` builds it locally, and `.github/workflows/release.yml` builds it for every `v*.*.*` tag and attaches it to the GitHub Release.

To build locally, you need the repository's .NET SDK, Inno Setup 6, and approved FFmpeg and FFprobe binaries under `tools/ffmpeg/`. The builder checks their hashes and required encoders. From the repository root, run:

```text
build-installer.bat
```

The script publishes self-contained `win-x64` apps and writes `dist\TuvimaLibrary-Setup-*.exe`. Test a locally built installer in an isolated Windows environment before using it with real media.

</details>

## Next steps

- [Build your first library](../tutorials/first-library.md).
- [Plan backups and recovery](../guides/operations-and-recovery.md).
- [Set up secure remote access](../guides/remote-access.md).
