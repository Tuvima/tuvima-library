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

Before creating datasets, [check image access](docker.md#before-you-start). Public access has not been confirmed as of October 8, 2026. If a pull is unavailable, use the [local source-build fallback](docker.md#build-the-image-from-source) and load that image on the NAS before deploying the app.

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

## Next steps

- [Add your first library](../tutorials/first-library.md).
- [Create and test a recovery point](../guides/operations-and-recovery.md).
- [Resolve permission or startup problems](../guides/troubleshooting.md).
