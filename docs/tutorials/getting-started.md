---
title: "Getting started"
description: "Start Tuvima Library with Docker, create your administrator account, and choose your first media folders."
audience: "user"
category: "tutorial"
product_area: "library configuration"
status: current
---

# Getting started

Get Tuvima Library running, create your administrator account, and open the Dashboard. Docker configuration takes about 10 minutes; image downloads and optional AI models can take longer.

## Choose an installation

| Your setup | Start here |
| --- | --- |
| Docker host or Docker Desktop | [Docker Compose](../install/docker.md), the recommended starting path |
| Synology, Unraid, TrueNAS SCALE, or QNAP | [Synology](../install/synology.md), [Unraid](../install/unraid.md), [TrueNAS](../install/truenas-scale.md), or [QNAP](../install/qnap.md) |
| Windows installer | [Check availability](../install/windows.md); no published installer is available as of October 8, 2026 |
| Development or source evaluation | [Run from source](../install/from-source.md) |

## Start with Docker

Public access to the configured image has not been confirmed as of October 8, 2026. First [check image access](../install/docker.md#before-you-start). If a pull is unavailable, follow the [local source-build fallback](../install/docker.md#build-the-image-from-source), then return to account setup below.

1. Install Docker with Compose.
2. Download the maintained configuration in a new deployment folder:

   ```bash
   curl -fsSL https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docker-compose.yml -o docker-compose.yml
   ```

   In PowerShell:

   ```powershell
   Invoke-WebRequest https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docker-compose.yml -OutFile docker-compose.yml
   ```

3. Edit every host folder under `volumes`, then set your user/group IDs and timezone. The [Docker guide](../install/docker.md) explains permissions and origins.
4. Keep all seven persistent mounts:

   | Mount | Contents |
   | --- | --- |
   | `/library` | Managed media |
   | `/config` | Settings and credentials |
   | `/db` | Catalogue |
   | `/models` | Optional AI models |
   | `/artwork-cache` | Artwork, cache, and logs |
   | `/backups` | Recovery archives |
   | `/transcode` | Prepared playback files |

5. If the image access check passed, start Tuvima:

   ```bash
   docker compose config --quiet
   docker compose pull
   docker compose up -d
   docker compose ps
   ```

6. Open `http://SERVER-IP:5016/setup`. Use `localhost` only from the Docker host itself.

Only the Dashboard is published. Leave Engine port `61495` internal.

## Create your administrator

1. Keep the server on a trusted private network during setup.
2. Enter an email and password for your administrator account.
3. Choose a separate profile display name and an optional profile PIN.
4. Save the recovery codes in a password manager or another safe place outside this server.
5. Continue through setup. Media folders and provider connections can be added later.

The first reachable browser can create the administrator. Once the account exists, setup requires administrator sign-in. Profile PINs and administrator unlock are separate from your account password.

## Choose your first media

Use **Settings → Libraries** for books, comics, movies, TV, music, and audiobooks. Start with a few files so you can check matching and folder permissions.

For private photos, home videos, or documents, use [View Personal Space](../guides/view-personal-space.md). Those files follow a local path without catalogue providers.

The default Standard AI profile uses one 1,260 MB text model. The separate 1,500 MB Whisper audio pack starts disabled. You can begin library setup while downloads run or AI features remain disabled.

## Next steps

- [Add your first library](first-library.md).
- [Connect metadata providers](../guides/configuring-providers.md).
- [Protect accounts and recovery access](../guides/account-security.md).
- [Resolve setup problems](../guides/troubleshooting.md).
