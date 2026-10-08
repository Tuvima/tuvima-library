---
title: "Install on TrueNAS SCALE"
description: "Deploy Tuvima as a Compose-based Custom App with host-path datasets on TrueNAS SCALE."
audience: "administrator"
category: "installation"
product_area: "deployment"
---
Current TrueNAS SCALE releases support Custom Apps defined with Docker Compose YAML. Create the storage datasets before opening the app editor.

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
4. Paste `docker-compose.yml`, replace all `/mnt/user/...` examples with the datasets created above, and set `TZ`.
5. Save and deploy the app.
6. Wait for the workload health state to become healthy.
7. Open `http://TRUENAS-IP:5016/setup`, complete setup directly, and save the generated recovery codes.

Only Dashboard port `5016` belongs in the portal or port-forwarding configuration. Do not publish Engine port `61495`.

TrueNAS performs basic YAML validation but does not validate application-specific values, so verify every host path and permission before importing media. See the official [TrueNAS Custom App screen documentation](https://www.truenas.com/docs/scale/apps/installcustomappscreens/).
