---
title: "Install on Unraid"
description: "Install the Tuvima Unraid template with complete persistent paths, permissions, timezone, and health guidance."
audience: "administrator"
category: "installation"
product_area: "deployment"
---
Tuvima supplies `unraid-template.xml` for a bridge-network container. The template publishes only Dashboard port `5016`; the Engine port is internal.

## Install the template

1. Add the template repository URL shown in the repository's `unraid-template.xml` header, or import the template file through your preferred Unraid template workflow.
2. Open **Apps**, find **Tuvima Library**, and choose **Install**.
3. Review every host path. The defaults place application state under `/mnt/user/appdata/tuvima` and media under `/mnt/user/media`.
4. Keep `TUVIMA_UID=99` and `TUVIMA_GID=100` for the normal Unraid `nobody:users` identity, or replace them with the numeric owner of your shares.
5. Set `TZ` to the same IANA timezone used by the server.
6. Apply the template and wait for the container health state to become healthy.

The template includes separate mappings for configuration, database, models, artwork/cache, backups, and transcodes. Do not merge them into the container writable layer; image updates replace that layer.

## Permissions and media safety

The configured UID/GID needs the access required by each library source and read/write access to `/library`, `/config`, `/db`, `/models`, `/artwork-cache`, `/backups`, and `/transcode`. Keep an existing library read-only and add it as an existing source in Tuvima instead of using it as the managed Library Root.

If startup reports a path is not writable, correct the share ownership or ACL. Do not solve it by enabling privileged mode; the template deliberately runs unprivileged after preparing its mounted folders.

## Set up and verify

Open `http://UNRAID-IP:5016/setup`. Complete setup directly and save the generated recovery codes; no container-log claim token is required.

After setup:

1. Confirm the container is healthy.
2. Create and download a backup from **Settings → Backup & Recovery**.
3. Run **Test restore** against that recovery point.
4. Follow [Operations and Recovery](../guides/operations-and-recovery.md) for updates, rollback, TLS, and Tailscale.
