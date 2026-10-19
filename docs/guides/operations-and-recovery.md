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

Open the Dashboard's `/setup` page from any device on your home network; no code is needed. Finish it before you open any router port. Afterward, setup requires administrator authentication.

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

Open Operations at `/settings/ingestion`, shown as **Library and Ingestion** in the Settings sidebar, which opens on the library summary, then live ingestion, then recently added. It shows the current run, queued work, provider waits, outcomes, and the three newest batches. Use search and outcome filters for history; **Show older** appends another page. Open a batch to browse its media.

Operations refreshes automatically. **Check folders for changes** (in the Ingestion page "⋯" menu) starts an extra scan of watched folders. The green navbar activity indicator opens the same page.

An interrupted import resumes from durable job and operation records when the Engine restarts. Expired worker leases do not by themselves fail recoverable work.

A settled file count measures file work. It does not mean the whole run is finished while required identity, enrichment, or organization remains. Album, show, and comic cards report tracks, episodes, or issues added without provider catalogue totals.

## Back up and test recovery

1. Open **Settings → Manage → System → Backup and recovery**.
2. Create a recovery point.
3. Download a copy to another device or backup system.
4. Choose **Test restore** for that point.
5. Confirm the database and configuration archive passed validation.

Above the recovery points, administrators see a read-only line such as "Data store version: step 3, upgraded on 2026-10-18". It names the latest upgrade step applied to the data store, which helps when asking for support. On a brand-new install it briefly says "setting up", and it is hidden when the Dashboard cannot reach the Engine.

The archive contains a consistent SQLite snapshot, a manifest, and non-secret configuration. It excludes provider credentials, data-protection secrets, model files, artwork cache, transcodes, and original media. Back up originals and required host secrets separately.

Test restore uses temporary storage, checks archive boundaries and SQLite integrity, then removes temporary files. It does not schedule or apply a restore.

## Restore a recovery point

1. Choose **Restore** and confirm the recovery point.
2. Wait for validation and staging to succeed.
3. Restart the Engine or container.
4. Check the Dashboard, library access, and Operations.

The staged restore applies before the data store opens at restart. Pre-restore database and configuration copies are retained beside replaced files. A restore does not replace the media files excluded from its archive.

## When Tuvima upgrades its data store

A new version of Tuvima sometimes changes how the data store (the `library.db` file) is laid out. The Engine does this by itself the first time the new version starts, one numbered step at a time. A start with nothing to upgrade is unchanged. A start with steps to run takes a little longer, and the Engine log lists the steps it ran with lines starting "Data store upgrade:". The Dashboard shows its usual "unreachable" state until the Engine is ready.

**Safety copies.** Before the first step runs on an existing data store, the Engine writes a checked copy of it to the `safety-copies` folder inside your backups folder. That is `config/backups` unless you set `TUVIMA_BACKUP_DIR` (the Docker image uses `/backups`). A copy is named like `tuvima-safety-copy-20261018-093000-step-0001-to-0002.db`, which records the upgrade it was taken before. The Engine keeps the two newest copies and deletes older ones. It never touches the recovery points you create in **Settings → Manage → System → Backup and recovery**, which stay in the backups folder itself. A brand-new install has nothing to copy, so it makes none.

**Go back to a safety copy.** Use this when an upgrade went wrong, or you want the older version back.

1. Stop the Engine (or the container).
2. Move `library.db` and, if they exist, `library.db-wal` and `library.db-shm` together into a safe folder. Keep all three so nothing is lost.
3. Copy the safety copy into the original folder and name it `library.db`. There must be no `-wal` or `-shm` file beside it.
4. Start the version of Tuvima the safety copy came from. The step numbers in its name tell you which upgrade it was taken before.

Starting the newer version again repeats the upgrade.

**When the Engine will not start.** The Engine stops with a plain message, exit code 75 in the console or container log, and leaves the data store as it was. These are the messages:

| The message says | What it means | What to do |
|---|---|---|
| "Data store upgrade step 0002 (name) failed ... The data store was left as it was before this step." | A step could not finish, and none of its changes were kept. The message names the safety copy. | Report the message. Meanwhile, start the previous version, or restore the safety copy as above. |
| "The data store ... was upgraded by a newer version of Tuvima" | You started an older version on a data store a newer version has already upgraded. | Install the newer version again, or restore an older recovery point or safety copy. |
| "Upgrade step ... has been changed after it was applied ... Applied steps must never be edited" | This build was altered after the data store was upgraded. It happens with development builds, not releases. | Use an unmodified build, or restore an older recovery point. |
| "The record of applied upgrade steps in the data store ... is damaged" | The list of steps the data store has had is missing or out of order. | Restore a recovery point or safety copy. |
| "Not enough free disk space to make a safety copy ..." | There is no room for the safety copy, so the Engine stopped before changing anything. | Free up space, or set `TUVIMA_BACKUP_DIR` to a drive with more room, and start again. The copy is about the size of `library.db`. |
| "The safety copy of the data store could not be checked" or "Could not prepare the upgrade" | The Engine could not make a copy it trusts, so it did not start the upgrade. Nothing was changed. | Check the backups folder is writable and has room, then start again. |

Contributors adding a data store change: see [Changing the data store](../develop/changing-the-data-store.md).

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
6. Check **Settings → Library and Ingestion** (the library summary and live ingestion), **Settings → System**, a representative library page, playback, and Operations.

A digest identifies the exact image. Tags such as `latest` are moving update channels. The image workflow is configured to generate an SBOM and provenance and sign the image digest; verify the particular image you use.

## Roll back a container

Set the Compose image to the digest recorded before the update:

```yaml
image: ghcr.io/tuvima/tuvima-library@sha256:RECORDED_DIGEST
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

Use [secure remote access](remote-access.md) to configure Tailscale Serve or reverse proxy trust, and run **Test remote access**. Keep normal Tuvima authentication enabled.

<details>
<summary>Technical details: verify an image signature</summary>

Install Cosign separately, replace the digest below, and verify the workflow identity:

```bash
cosign verify \
  --certificate-identity-regexp='https://github.com/Tuvima/tuvima-library/.github/workflows/docker-publish.yml@refs/(heads/main|tags/v.*)' \
  --certificate-oidc-issuer=https://token.actions.githubusercontent.com \
  ghcr.io/tuvima/tuvima-library@sha256:DIGEST
```

Do not treat a tag alone as signature or provenance verification.

</details>

## Next steps

- [Review account recovery](account-security.md).
- [Set up secure remote access](remote-access.md).
- [Troubleshoot failed work](troubleshooting.md).
