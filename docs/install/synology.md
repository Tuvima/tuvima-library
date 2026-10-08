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

Before creating folders, [check image access](docker.md#before-you-start). Public access has not been confirmed as of October 8, 2026. If a pull is unavailable, use the [local source-build fallback](docker.md#build-the-image-from-source) and load that image on the NAS before creating the project.

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

## Next steps

- [Add your first library](../tutorials/first-library.md).
- [Create and test a recovery point](../guides/operations-and-recovery.md).
- [Resolve permission or startup problems](../guides/troubleshooting.md).
