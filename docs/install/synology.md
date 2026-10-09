---
title: "Install on Synology DSM"
description: "Deploy Tuvima Library as a Container Manager project on Synology DSM."
audience: "administrator"
category: "installation"
product_area: "deployment"
status: current
---

# Install on Synology DSM

Run Tuvima Library as a Container Manager project using a complete Compose file. The project keeps the app configuration together for later updates.

Allow 10–20 minutes for folder and container setup, plus image and model downloads. Follow the NAS documentation for your installed software version.

Tuvima Library uses the published `ghcr.io/tuvima/tuvima_library:latest` image. If the NAS cannot download it, use the [local source-build path](docker.md#build-the-image-from-source) and load that image on the NAS before creating the project.

## Prepare folders

Create a shared folder tree such as:

```text
/volume1/docker/tuvima/config
/volume1/docker/tuvima/db
/volume1/docker/tuvima/models
/volume1/docker/tuvima/artwork-cache
/volume1/docker/tuvima/backups
/volume1/docker/tuvima/transcode
```

Choose the media paths that will become your managed or read-only library sources. Give the service account represented by `TUVIMA_UID` and `TUVIMA_GID` access to every mapped folder. You can obtain a user's numeric IDs over an administrator SSH session with `id USERNAME`.

## Create the project

1. Download the complete maintained configuration from the [Docker guide](docker.md) into a project folder on the NAS.
2. Replace the Unraid-style `/mnt/user/...` example paths with `/volume1/...` paths that exist on this NAS.
3. Set the numeric UID/GID and `TZ`.
4. In **Container Manager → Project**, choose **Create**.
5. Name the project `tuvima`, select its working directory, and upload or paste the Compose file.
6. Build and start the project.

Wait for `tuvima-library` to report healthy, then open `http://NAS-IP:5016/setup`, complete setup directly, and save the generated recovery codes.

Do not add a port mapping for `61495`. Use the Synology reverse proxy only for Dashboard port `5016`, and follow the trusted-proxy and TLS steps in [Operations and Recovery](../guides/operations-and-recovery.md).

See Synology's official [Container Manager Project documentation](https://kb.synology.com/en-us/DSM/help/ContainerManager/docker_project) for DSM-specific project controls.

## Keep all persistent folders

Retain mappings for `/library`, `/config`, `/db`, `/models`, `/artwork-cache`, `/backups`, and `/transcode`. Reuse them when updating the container. An existing read-only media folder needs a separate mapping.

## Claim your server

First-run setup is protected by a one-time setup code, so only someone who can reach the server itself can claim it.

- Opening `/setup` from the same computer that runs Tuvima Library needs no code.
- Opening it from another device on your home network asks for a setup code. On the server, run `docker exec -it <container> tuvima-admin setup code` for Docker, Unraid, Synology, QNAP and TrueNAS. On Windows, open a terminal as administrator in the install folder and run `tuvima-admin setup code`.
- The code has eight characters (for example `ABCD-EFGH`), works once, and expires after 30 minutes. A newer code replaces an older one, and five wrong tries cancel it.
- Setup is never available over the internet. Visitors from outside see "Setup has to be finished from your home network."
- Once the administrator exists, setup closes for good and `tuvima-admin setup code` refuses to run.

## Next steps

- [Add your first library](../tutorials/first-library.md).
- [Create and test a recovery point](../guides/operations-and-recovery.md).
- [Resolve permission or startup problems](../guides/troubleshooting.md).
