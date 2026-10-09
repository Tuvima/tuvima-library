---
title: "Install on TrueNAS SCALE"
description: "Deploy Tuvima Library as a Compose-based Custom App with host-path datasets on TrueNAS SCALE."
audience: "administrator"
category: "installation"
product_area: "deployment"
status: current
---

# Install on TrueNAS SCALE

Use a TrueNAS SCALE version with Compose-based Custom Apps to run Tuvima Library. Allow 10–20 minutes for configuration, plus downloads. Create storage datasets before opening the app editor.

Tuvima Library uses the published `ghcr.io/tuvima/tuvima_library:latest` image. If the NAS cannot download it, use the [local source-build path](docker.md#build-the-image-from-source) and load that image on the NAS before deploying the app.

## Prepare datasets

Create host-path datasets or directories such as:

```text
/mnt/POOL/apps/tuvima/config
/mnt/POOL/apps/tuvima/db
/mnt/POOL/apps/tuvima/models
/mnt/POOL/apps/tuvima/artwork-cache
/mnt/POOL/apps/tuvima/backups
/mnt/POOL/apps/tuvima/transcode
```

Choose separate datasets for incoming and managed library media. Grant a numeric user and group—commonly the TrueNAS `apps` identity, UID/GID `568`—the required access, then use those numbers for `TUVIMA_UID` and `TUVIMA_GID`.

Do not enable the Custom App **Custom User** override for this image. The container starts briefly as root to prepare bind-mount ownership, then its entrypoint drops to the configured non-root UID/GID before either Tuvima process starts.

## Create the Custom App

1. Open **Apps → Discover Apps → Custom App**.
2. Choose **Install via YAML** or the **Custom Config** editor.
3. Name the app `tuvima`.
4. Download the full configuration from the [Docker guide](docker.md), then paste `docker-compose.yml`, replace all `/mnt/user/...` examples with the datasets created above, and set `TZ`.
5. Save and deploy the app.
6. Wait for the workload health state to become healthy.
7. Open `http://TRUENAS-IP:5016/setup`, complete setup directly, and save the generated recovery codes.

Only Dashboard port `5016` belongs in the portal or port-forwarding configuration. Do not publish Engine port `61495`.

TrueNAS performs basic YAML validation but does not validate application-specific values, so verify every host path and permission before importing media. See the official [TrueNAS Custom App screen documentation](https://www.truenas.com/docs/scale/apps/installcustomappscreens/).

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
