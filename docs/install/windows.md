---
title: "Windows installation availability"
description: "Check Windows installer availability and the unverified packaging path before choosing Docker or source."
audience: "administrator"
category: "installation"
product_area: "deployment"
status: early-access
---

# Windows installation availability

Tuvima Library has Windows packaging scripts. No installer release is published as of October 8, 2026. Use [Docker](docker.md) or [run from source](from-source.md) today.

**Early access:** install, update, removal, and AI have not been tested together. The packaging scripts do not prove that a working installer is available.

## Check for a release

The [GitHub Releases page](https://github.com/Tuvima/tuvima_library/releases) is the future download location. Read the release's requirements and notes before running an installer. The packaging workflow describes Windows 10/11, 64-bit, with administrator rights.

## Understand the intended installation

The current script is designed to perform these tasks:

- install app files under `C:\Program Files\Tuvima Library`.
- register `TuvimaEngine` and `TuvimaDashboard` Windows services.
- create `config`, `db`, `watch`, and `library` folders under `C:\ProgramData\Tuvima`.
- include local FFmpeg and FFprobe binaries.
- add a Start menu launcher and optional desktop shortcut opening `http://localhost:5016`.

These are source-defined intentions, not tested results. The script configures ports `5016` and `61495`, but binds the Engine broadly. Keep `61495` blocked from other devices.

## Plan updates and removal

The script stops and deletes both service registrations before installing replacements. Existing configuration files use `onlyifdoesntexist`, so upgrades are intended to retain them.

The uninstall hook stops and deletes the services. There is no explicit recursive `UninstallDelete` rule for `C:\ProgramData\Tuvima`; runtime-created data is intended to remain. Do not treat that as a tested retention guarantee. Back up application state and originals separately before an update or uninstall.

## Know the current limitations

The source needs packaging verification before normal use. In particular:

- the Dashboard service does not explicitly receive the Engine's `TUVIMA_CONFIG_DIR`;
- the installer grants authenticated Windows users modify access to its data folders;
- the local builder enables bundled AI runtimes, while the tagged release workflow does not pass that setting;
- the installer expects local FFmpeg files and does not package the host recovery console.

For a predictable deployment, use the maintained Compose configuration.

<details>
<summary>Technical details: build the packaging candidate</summary>

Read `installer.iss`, `build-installer.bat`, and `.github/workflows/release.yml` first. You need the repository's .NET SDK, Inno Setup 6, and approved FFmpeg/FFprobe binaries under `tools/ffmpeg/`. The builder checks their hashes and required encoders.

Run from the repository root:

```text
build-installer.bat
```

The script publishes self-contained `win-x64` apps and invokes Inno Setup. Output is `dist\TuvimaLibrary-Setup-*.exe`. Building it does not establish correct service startup, credentials, upgrades, or uninstall. Test the candidate in an isolated Windows environment before using real media.

</details>

## Next steps

- [Install with Docker Compose](docker.md).
- [Run from source](from-source.md).
- [Plan backups and recovery](../guides/operations-and-recovery.md).
