---
title: "Install on Synology DSM"
description: "Deploy Tuvima as a Container Manager project on Synology DSM."
audience: "administrator"
category: "installation"
product_area: "deployment"
---
This guide uses **Container Manager → Project**, which accepts a Compose file. Synology documents projects as the place to create and operate one or more containers from uploaded or editor-provided Compose YAML.

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

1. Copy `docker-compose.yml` into a project folder on the NAS.
2. Replace the Unraid-style `/mnt/user/...` example paths with `/volume1/...` paths that exist on this NAS.
3. Set the numeric UID/GID and `TZ`.
4. In **Container Manager → Project**, choose **Create**.
5. Name the project `tuvima`, select its working directory, and upload or paste the Compose file.
6. Build and start the project.

Wait for `tuvima-library` to report healthy, then open `http://NAS-IP:5016/setup`, complete setup directly, and save the generated recovery codes.

Do not add a port mapping for `61495`. Use the Synology reverse proxy only for Dashboard port `5016`, and follow the trusted-proxy and TLS steps in [Operations and Recovery](../guides/operations-and-recovery.md).

See Synology's official [Container Manager Project documentation](https://kb.synology.com/en-us/DSM/help/ContainerManager/docker_project) for DSM-specific project controls.
