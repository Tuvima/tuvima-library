---
title: "Install on Unraid"
description: "Run Tuvima Library on Unraid with complete persistent paths, share permissions, and a private Engine."
audience: "administrator"
category: "installation"
product_area: "deployment"
status: current
---

# Install on Unraid

Run Tuvima Library with persistent Unraid shares and a browser Dashboard. Allow 10–20 minutes for configuration, plus image and optional model downloads.

Tuvima Library uses the published `ghcr.io/tuvima/tuvima_library:latest` image. If the NAS cannot download it, use the [local source-build path](docker.md#build-the-image-from-source), including its local image tag and no-pull start command.

## Use the maintained Compose file

1. Follow the [Docker Compose guide](docker.md) to download the full configuration.
2. Replace every host path with a real Unraid share path.
3. Keep all seven mappings: `/library`, `/config`, `/db`, `/models`, `/artwork-cache`, `/backups`, and `/transcode`.
4. Set `TUVIMA_UID=99` and `TUVIMA_GID=100` for the usual `nobody:users` identity, or use the actual owner of your shares.
5. Set `TZ` and the Dashboard origin used by your devices.
6. If the registry image is accessible, start through a Compose-capable manager, or run these commands in the deployment directory:

   ```bash
   docker compose config --quiet
   docker compose pull
   docker compose up -d
   docker compose ps
   ```

The repository also contains an [Unraid template](https://raw.githubusercontent.com/Tuvima/tuvima_library/main/unraid-template.xml). Import it through a compatible template workflow if you prefer that approach. Its presence in the repository does not guarantee listing in Community Applications.

## Check share permissions

The configured user/group needs write access to managed media and every application folder. It needs only read access to an existing protected library.

Keep originals in a separate read-only mapping and add them as an **Existing library** source. Do not use the managed `/library` mapping for originals Tuvima must never change.

If a path is not writable, repair share ownership or ACLs. Do not enable privileged mode to bypass a permission error.

## Complete setup

1. Wait for `tuvima-library` to report healthy.
2. Open `http://UNRAID-IP:5016/setup`.
3. Create the administrator and save recovery codes outside the server.
4. Create, download, and test a recovery point in **Settings → Backup & Recovery**.

Only Dashboard port `5016` is published. Leave Engine port `61495` internal.

## Claim your server

First-run setup is protected by a one-time setup code, so only someone who can reach the server itself can claim it.

- Opening `/setup` from the same computer that runs Tuvima Library needs no code.
- Opening it from another device on your home network asks for a setup code. On the server, run `docker exec -it <container> tuvima-admin setup code` for Docker, Unraid, Synology, QNAP and TrueNAS. On Windows, open a terminal as administrator in the install folder and run `tuvima-admin setup code`.
- The code has eight characters (for example `ABCD-EFGH`), works once, and expires after 30 minutes. A newer code replaces an older one, and five wrong tries cancel it.
- Setup is never available over the internet. Visitors from outside see "Setup has to be finished from your home network."
- Once the administrator exists, setup closes for good and `tuvima-admin setup code` refuses to run.

## Next steps

- [Add your first library](../tutorials/first-library.md).
- [Back up and update](../guides/operations-and-recovery.md).
- [Configure secure remote access](../guides/remote-access.md).
