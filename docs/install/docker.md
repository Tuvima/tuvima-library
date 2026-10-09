---
title: "Install with Docker Compose"
description: "Run Tuvima Library with seven persistent folders, private Engine access, and a health-checked Dashboard."
audience: "administrator"
category: "installation"
product_area: "deployment"
status: current
---

# Install with Docker Compose

Run Tuvima Library on a Docker host and keep your catalogue through container updates. Allow about 10 minutes for configuration, plus image downloads or a source build and optional AI downloads.

## Before you start

Use Docker Engine with the Compose plugin, or Docker Desktop with Linux containers. The image workflow targets Linux AMD64 and ARM64.

Tuvima Library is published as `ghcr.io/tuvima/tuvima_library:latest`, built from the main code line. If your host cannot download it, [build the image from source](#build-the-image-from-source) instead.

Choose a managed media folder and six application folders. Give the container's user access to them before starting.

| Container path | What you keep here |
| --- | --- |
| `/library` | Media Tuvima may manage and organize |
| `/config` | Settings, credentials, and data-protection keys |
| `/db` | SQLite catalogue |
| `/models` | Local AI weights; Standard uses 1,260 MB, with a separate optional 1,500 MB audio pack |
| `/artwork-cache` | Artwork, thumbnails, cache, and logs |
| `/backups` | Recovery archives and restore staging |
| `/transcode` | Prepared playback files and temporary work |

All seven paths need persistent mounts. Replacing the container must reuse these same host folders.

Keep user-owned originals separate from `/library` if Tuvima must not change them. Mount that folder read-only elsewhere, then add it as an **Existing library** source.

## Start Tuvima

1. Create a deployment folder and open a terminal there.
2. Download the [maintained Compose file](https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docker-compose.yml):

   ```bash
   curl -fsSL https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docker-compose.yml -o docker-compose.yml
   ```

   In PowerShell, use:

   ```powershell
   Invoke-WebRequest https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docker-compose.yml -OutFile docker-compose.yml
   ```

3. Edit every host path under `volumes`. The supplied `/mnt/user/...` paths are Unraid examples.
4. Set `TUVIMA_UID` and `TUVIMA_GID` to the numeric user and group that own those folders. On Linux or a NAS, `id USERNAME` shows these numbers.
5. Keep `TUVIMA_UMASK: "0002"` if new files should be writable by that group. Use a stricter mask when your host policy requires one.
6. Set `TZ` to your timezone, such as `America/Chicago`. Set `TUVIMA_CORS_ORIGINS` to the Dashboard origin your devices use, such as `http://192.168.1.50:5016`.
7. Validate the file, download the image, and start:

   ```bash
   docker compose config --quiet
   docker compose pull
   docker compose up -d
   docker compose ps
   ```

Only Dashboard port `5016` is published. Keep Engine port `61495` internal; do not add a host mapping or reverse proxy for it.

## Build the image from source

Use this path if you prefer to build locally or your host cannot download the published image. You need Git, Docker with Linux containers, and internet access for base images, NuGet packages, and FFmpeg. The repository Dockerfile includes the .NET SDK; a host .NET installation is not required for this build.

1. Clone a clean checkout and build from its root:

   ```bash
   git clone https://github.com/Tuvima/tuvima_library.git
   cd tuvima_library
   docker build -t tuvima-library:local .
   ```

2. Copy `docker-compose.yml` from that checkout into your deployment folder, or use the maintained file downloaded above. Keep all seven mounts, host paths, and environment settings from **Start Tuvima**.
3. Change the `image` value under `services.tuvima` to your local tag:

   ```yaml
   image: tuvima-library:local
   ```

4. From the deployment folder, start without pulling the registry image:

   ```bash
   docker compose config --quiet
   docker compose up -d --pull never
   docker compose ps
   ```

Build on the Docker host that will run the container. For a separate NAS host, transfer the built image with `docker save` and `docker load`, or build from the checkout on that NAS. Select the same local tag in its app editor and disable automatic pulls. If the NAS editor cannot use local images without pulling, deploy this Compose file from the NAS shell instead.

## Complete first-run setup

1. Open `http://SERVER-IP:5016/setup` on a trusted private network.
2. Create the first administrator account and profile. Save the one-time recovery codes outside this server.
3. Continue through setup. You can add media folders later.

No claim token from container logs is required. From another device on your home network, setup asks for a one-time code (see [Claim your server](#claim-your-server)). Afterward, setup requires administrator authentication.

## Claim your server

First-run setup is protected by a one-time setup code, so only someone who can reach the server itself can claim it.

- Opening `/setup` from the same computer that runs Tuvima Library needs no code.
- Opening it from another device on your home network asks for a setup code. On the server, run `docker exec -it <container> tuvima-admin setup code` for Docker, Unraid, Synology, QNAP and TrueNAS. On Windows, open a terminal as administrator in the install folder and run `tuvima-admin setup code`.
- The code has eight characters (for example `ABCD-EFGH`), works once, and expires after 30 minutes. A newer code replaces an older one, and five wrong tries cancel it.
- Setup is never available over the internet. Visitors from outside see "Setup has to be finished from your home network."
- Once the administrator exists, setup closes for good and `tuvima-admin setup code` refuses to run.

## Check startup

```bash
docker compose logs --tail=100 tuvima
docker inspect --format '{{.State.Health.Status}}' tuvima-library
```

A new container may report `starting` while settings and services initialize. `healthy` means both apps' liveness checks pass. It does not mean media enrichment or optional model downloads have finished.

## Next steps

- [Add your first library](../tutorials/first-library.md).
- [Back up, update, and recover](../guides/operations-and-recovery.md).
- [Set up secure remote access](../guides/remote-access.md).
