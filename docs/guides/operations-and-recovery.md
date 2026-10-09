---
title: "Operations and recovery"
description: "Monitor imports, create and test recovery points, and update or roll back a persistent Tuvima installation."
audience: "administrator"
category: "guide"
product_area: "operations"
status: current
---

# Operations and recovery

Keep Tuvima Library recoverable and check updates before relying on them. Creating a recovery point takes a few minutes; import and restore times depend on your library.

Commands below use the container name `tuvima-library`. A NAS manager provides equivalent log, restart, and image controls.

## Finish setup safely

Open the Dashboard's `/setup` page on a trusted private network. From another device on your home network, setup asks for a one-time code from `tuvima-admin setup code`. Afterward, setup requires administrator authentication.

Save the recovery codes outside the server. If you lose password and recovery access, use the elevated host command:

```bash
docker exec -it --user 0 tuvima-library /app/admin/tuvima-admin auth reset-password --email administrator@example.com
```

Enter the new password at its interactive prompt. Recovery revokes sessions and rotates recovery codes. It is not callable through the Dashboard or Engine HTTP connection. See [account recovery](account-security.md) for source and host commands.

If someone loses their authenticator app and their recovery codes, turn their two-step codes off from the host:

```bash
docker exec -it --user 0 tuvima-library /app/admin/tuvima-admin auth reset-two-step --email someone@example.com
```

They can turn two-step codes back on from Account > Security. The reset is recorded in the audit log.

## Follow active work

Open Operations at `/settings/ingestion`, shown as **Live Ingestion** in the Settings sidebar. It shows the current run, queued work, provider waits, outcomes, and the three newest batches. Use search and outcome filters for history; **Show older** appends another page. Open a batch to browse its media.

Operations refreshes automatically. **Scan now** starts an extra scan of watched folders. The green navbar activity indicator opens the same page.

An interrupted import resumes from durable job and operation records when the Engine restarts. Expired worker leases do not by themselves fail recoverable work.

A settled file count measures file work. It does not mean the whole run is finished while required identity, enrichment, or organization remains. Album, show, and comic cards report tracks, episodes, or issues added without provider catalogue totals.

## Back up and test recovery

1. Open **Settings → Backup & Recovery**.
2. Create a recovery point.
3. Download a copy to another device or backup system.
4. Choose **Test restore** for that point.
5. Confirm the database and configuration archive passed validation.

The archive contains a consistent SQLite snapshot, a manifest, and non-secret configuration. It excludes provider credentials, data-protection secrets, model files, artwork cache, transcodes, and original media. Back up originals and required host secrets separately.

Test restore uses temporary storage, checks archive boundaries and SQLite integrity, then removes temporary files. It does not schedule or apply a restore.

## Restore a recovery point

1. Choose **Restore** and confirm the recovery point.
2. Wait for validation and staging to succeed.
3. Restart the Engine or container.
4. Check the Dashboard, library access, and Operations.

The staged restore applies before the data store opens at restart. Pre-restore database and configuration copies are retained beside replaced files. A restore does not replace the media files excluded from its archive.

## Upgrade a container

1. Read the available release or image notes.
2. Create, download, and test a recovery point.
3. Record the image used by the running container:

   ```bash
   image_id=$(docker container inspect --format '{{.Image}}' tuvima-library)
   docker image inspect --format '{{json .RepoDigests}}' "$image_id"
   ```

   Save the full `ghcr.io/...@sha256:...` entry. A local build may return `[]` because it has no registry digest. In that case, give the image a local rollback tag and save a copy before updating:

   ```bash
   docker image tag "$image_id" tuvima-library:before-update
   docker image save -o tuvima-before-update.tar tuvima-library:before-update
   ```

   Keep this archive with your recovery files; it can be large.

4. Keep the same seven persistent mounts and update:

   ```bash
   docker compose pull
   docker compose up -d
   docker compose ps
   ```

5. Wait for `healthy`.
6. Check **Settings → System Overview**, a representative library page, playback, and Operations.

A digest identifies the exact image. Tags such as `latest` are moving update channels. The image workflow is configured to generate an SBOM and provenance and sign the image digest; verify the particular image you use.

## Roll back a container

Set the Compose image to the digest recorded before the update:

```yaml
image: ghcr.io/tuvima/tuvima_library@sha256:RECORDED_DIGEST
```

Then recreate it with the same persistent folders:

```bash
docker compose pull
docker compose up -d
```

For a saved local build, load `tuvima-before-update.tar` with `docker image load -i tuvima-before-update.tar`, set the Compose image to `tuvima-library:before-update`, and run `docker compose up -d --pull never`. Do not run `docker compose pull` for that local rollback tag.

If the newer version changed pre-beta application state incompatibly, use your tested pre-upgrade recovery point and the release's recovery instructions. Some older state requires deliberate reset and reingestion rather than automatic migration.

Never delete or relocate user-owned source media as part of rollback.

## Add secure remote access

Expose only Dashboard port `5016` through a trusted HTTPS path. Never publish or proxy Engine port `61495`.

Use [secure remote access](remote-access.md) to configure Tailscale Serve or reverse proxy trust, verify readiness, and enable remote sign-in. Keep normal Tuvima authentication enabled.

<details>
<summary>Technical details: verify an image signature</summary>

Install Cosign separately, replace the digest below, and verify the workflow identity:

```bash
cosign verify \
  --certificate-identity-regexp='https://github.com/Tuvima/tuvima_library/.github/workflows/docker-publish.yml@refs/(heads/main|tags/v.*)' \
  --certificate-oidc-issuer=https://token.actions.githubusercontent.com \
  ghcr.io/tuvima/tuvima_library@sha256:DIGEST
```

Do not treat a tag alone as signature or provenance verification.

</details>

## Next steps

- [Review account recovery](account-security.md).
- [Set up secure remote access](remote-access.md).
- [Troubleshoot failed work](troubleshooting.md).
