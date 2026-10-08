---
title: "Install on QNAP"
description: "Deploy Tuvima Library as a Docker Compose application in QNAP Container Station."
audience: "administrator"
category: "installation"
product_area: "deployment"
status: current
---

# Install on QNAP

Run Tuvima Library as a Container Station application from Compose YAML. Keep the complete configuration together so you can repeat the deployment.

Allow 10–20 minutes for folder and container setup, plus downloads. Menu labels can vary by Container Station version.

Before creating folders, [check image access](docker.md#before-you-start). Public access has not been confirmed as of October 8, 2026. If a pull is unavailable, use the [local source-build fallback](docker.md#build-the-image-from-source) and load that image on the NAS before creating the application.

## Prepare storage

Create persistent folders under a share such as `/share/Container/tuvima` for `config`, `db`, `models`, `artwork-cache`, `backups`, and `transcode`. Choose the media share paths that will become your managed or read-only library sources.

Use an administrator SSH session and `id USERNAME` to find a numeric UID/GID with the required share permissions. Put those values in `TUVIMA_UID` and `TUVIMA_GID`, and set `TZ` to the NAS timezone.

## Create the application

1. Open **Container Station → Applications → Create**.
2. Name the application `tuvima`.
3. Paste the maintained `docker-compose.yml` from the [Docker guide](docker.md) after replacing every host path with a real `/share/...` path.
4. Validate the YAML, then create the application.
5. Wait for `tuvima-library` to report healthy.
6. Open `http://NAS-IP:5016/setup`, complete setup directly, and save the generated recovery codes.

Publish only host port `5016`. Keep the Engine on container loopback. If Container Station reports a permission error, repair the share ACL or ownership for the configured numeric identity; do not enable privileged mode.

See QNAP's official [Container Station application guidance](https://www.qnap.com/en-us/how-to/tutorial/article/how-to-use-container-station-3) for the current YAML editor workflow.

## Keep all persistent folders

Retain mappings for `/library`, `/config`, `/db`, `/models`, `/artwork-cache`, `/backups`, and `/transcode`. Reuse them when updating the container. An existing read-only media folder needs a separate mapping.

## Next steps

- [Add your first library](../tutorials/first-library.md).
- [Create and test a recovery point](../guides/operations-and-recovery.md).
- [Resolve permission or startup problems](../guides/troubleshooting.md).
