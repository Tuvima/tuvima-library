---
title: "Install on QNAP"
description: "Deploy Tuvima as a Docker Compose application in QNAP Container Station."
audience: "administrator"
category: "installation"
product_area: "deployment"
---
QNAP Container Station can create an application from Docker Compose YAML. Use an application instead of creating the container field by field so the deployment remains reproducible.

## Prepare storage

Create persistent folders under a share such as `/share/Container/tuvima` for `config`, `db`, `models`, `artwork-cache`, `backups`, and `transcode`. Choose the media share paths that will become your managed or read-only library sources.

Use an administrator SSH session and `id USERNAME` to find a numeric UID/GID with the required share permissions. Put those values in `TUVIMA_UID` and `TUVIMA_GID`, and set `TZ` to the NAS timezone.

## Create the application

1. Open **Container Station → Create → Create Application**.
2. Name the application `tuvima`.
3. Paste the repository's `docker-compose.yml` after replacing every host path with a real `/share/...` path.
4. Validate the YAML, then create the application.
5. Wait for `tuvima-library` to report healthy.
6. Open `http://NAS-IP:5016/setup`, complete setup directly, and save the generated recovery codes.

Publish only host port `5016`. Keep the Engine on container loopback. If Container Station reports a permission error, repair the share ACL or ownership for the configured numeric identity; do not enable privileged mode.

See QNAP's official [Container Station application guidance](https://docs.qnap.com/operating-system/qne-network/1.0.x/en-us/container-creation-1A95801A.html) for the current YAML editor workflow.
