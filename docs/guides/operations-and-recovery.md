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
5. Confirm the test passed: it reads the whole recovery point and checks it, and it does not change anything.

Above the recovery points, administrators see a read-only line such as "Data store version: step 3, upgraded on 2026-10-18". It names the latest upgrade step applied to the data store, which helps when asking for support. On a brand-new install it briefly says "setting up", and it is hidden when the Dashboard cannot reach the Engine.

**What a recovery point holds.** Your settings and access (accounts, passkeys, profiles, grants, preferences, notification and backup settings, the collections and playlists you built), each person's own state (reading and watching positions, bookmarks, favourites, saved items) and the corrections you typed yourself, plus a manifest and the safe configuration files. It does **not** hold your media files or folders, artwork, the catalogue the Library rebuilds by scanning, provider credentials, the Engine's own encryption keys, model files or the adult-video catalogue. Back up your originals with your computer's own backup tool. A recovery point is not a copy of the data store file, and making one does not pause the Library.

**What you see on the screen.** Under **Create backup** a plain note repeats what a backup holds and says media files and folders are not included, and one line says to keep the file somewhere safe because anyone with it can see your settings and sign in as you. There is no password option. When some of your items are known by their exact file only (no external ID), a warning bubble says how many ("38 items are matched by exact file only and may lose progress if the file changes."); it is hidden when there are none. After a backup is made the screen shows its file name and size. **Test restore** shows a short first look from the backup's own counts: how many profiles and your settings, how many items of progress, bookmarks and favourites (which come back as their titles are found again), and that media files and folders are not included. First-run setup shows the same first look when you choose a file. A file the Library refuses (an older layout, a damaged file) is explained in one sentence, as it is.

**Keep the file safe.** Recovery points are never encrypted and have no password. Because the two-step sign-in secret and webhook secrets are kept readable (so a restore works on a new computer), a recovery point can be used to sign in as you. Keep it, and any destination you copy it to, as private as the Library itself.

**The Vault catalogue is never in a recovery point.** A recovery point never holds a Vault library's videos, images, titles, file names, performers, studios, tags, artwork, matches or activity, and no log, activity note or search entry naming them. It keeps what you set up and what is personal to each person: which Vault libraries exist and their settings, each profile's Vault access mode and PIN (as a one-way hash, never the digits), account access grants, each person's view preferences, and each person's saved Vault bookmarks (kept against how to recognise the video, never its name). `libraries.json` is kept as it is, so the Vault library's name and folders come back and can be scanned again; that file therefore still names those folders.

Test restore uses temporary storage, checks the recovery point exactly as a real restore would, then removes the temporary files. It does not schedule or apply a restore.

**A recovery point from an older version can't be restored.** You will see "This backup is from an older version of the Library and can't be restored. Make a new backup with this version." A file that is damaged says "This backup file is damaged and can't be restored."

## Bookmark pack files

A bookmark pack is a small `.zip` file made from **Bookmarks → Saved → Export** in the video player. It follows the same conventions as a recovery point: a ZIP with a `manifest.json` (`schema_version`, `created_at`, `includes_secrets: false`) and one `pack/bookmarks.json`, and it is checked on the way in with the same refusals (unexpected entries, unsafe paths, oversized or over-compressed contents). Like a recovery point it has no password, because it holds nothing secret: only how to recognise the title (IMDb, TMDB, TVDB, Wikidata or YouTube IDs, running time and edition) and, for each bookmark you choose, its time, optional end, chapter name, short note and longer description. It never holds file paths, profile names or what anyone watched, and a Vault title cannot be exported at all. **Import** (in the same Saved list) checks the pack is for the title, tells you when it was made on a different cut and offers **Align by chapter** or **Skip**, then adds read-only pack bookmarks labelled with the pack's name and author. Each pack can be switched off or removed on its own.

## Move a person to another Library

A user file moves one person's sign-in, profiles and what is personal to each profile (progress, bookmarks, favourites and collection items) to another Library. It is not a recovery point: it holds one person, not your media or settings.

1. **Export.** The person can open **Settings → Security → Account details → Export my data** and choose **Export**. An administrator can instead open **Settings → Manage → Users and Sign-in → Users**, open the "..." menu on the person's row (on a computer or tablet) and choose **Export**. The Library asks you to confirm it's you, then downloads a `.zip`.
2. **Keep the file safe.** Anyone with the file can sign in as that person. Delete it once the move is done.
3. **Import.** On the other Library, an administrator opens **Settings → Manage → Users and Sign-in → Users**, chooses **Import a user** and picks the file. The preview shows the email, household, profiles and how many items come with them. Choose **Import** to add them.
4. **Give access.** The person can sign in straight away but sees no libraries until you choose what they can see. The permissions editor opens as soon as the import is done.
5. **Progress returns as titles are found.** Progress, bookmarks and favourites attach to a title when the other Library finds that same title. Until then they wait.

What is not carried: the person's access rights (you choose them on the new Library), photo galleries, and items still waiting for files on the old Library. Administrator rights never travel: a profile that was an administrator arrives as a standard profile. If the Library cannot import the file (for example because that email already signs in there), the preview says why in one sentence and nothing is added.

## Copy backups to other places

Under **Settings → Backup and recovery → Where backups go**, **Add a destination** copies each backup somewhere besides this computer: **Another folder** (such as an external drive), a **Network share** (a path such as `\\nas\backups`, or a mounted folder), **S3-compatible storage** (Amazon S3, MinIO, Backblaze B2, Wasabi) or **WebDAV** (Nextcloud and many NAS devices). Each destination receives the same plain backup file and each copy is read back and checked. Backups are never encrypted, so keep the destination somewhere only you can reach. A copy made by an older version (a `.tvbk` file) can no longer be restored. A web destination asks you to agree to the exact address backups will be sent to ("Send backups to ..."). Its login is kept encrypted on this computer and is never shown again. Press **Test** to check a destination; for a network share the Library uses the access your computer already has, so mount or sign in to the share first.

## Restore a recovery point

1. Choose **Restore** and confirm the recovery point. The Library checks it first; nothing changes yet.
2. Wait for "Restore checked and scheduled".
3. Restart the Engine or container.
4. Check the Dashboard, library access, and Operations.

**What happens at the restart.** Your current Library is never deleted: the Engine puts its data store aside as a `pre-restore` copy beside the original (the file name ends `.pre-restore-<time>.bak`), puts back the safe configuration (each file it replaces is kept as `.pre-restore-<time>.bak`), starts a fresh Library and fills it from the recovery point. If filling it fails, the Engine still starts on the fresh Library, the log says where your previous data store was put aside, and the recovery point is kept so it can be tried again; nothing is lost.

**What comes back at once.** Your settings, accounts, profiles, grants, PIN hashes, Vault libraries (empty) and preferences, and the people who can sign in, including two-step sign-in on a new computer.

**What comes back as the titles come back.** Your progress, bookmarks, favourites, saved items and the corrections you typed belong to titles, and the restored Library has no titles until it has scanned your folders. The Engine keeps this state in a **waiting area** and, after each scan, puts it back on a title only when that title is clearly the same one (by its IDs, then by the exact file when it has no IDs). Nothing is guessed: a title that is not found, or that could be two things, keeps waiting. Saved Vault bookmarks wait the same way, by the video's Stash-box record or its exact file, and the Vault settings page (administrator, Vault mode on) shows how many still wait, never which. Imported bookmark packs on Vault videos are not kept (a pack can be imported again from its file). Vault mode being on is not kept, so everyone starts with it off after a restore.

**After a restore.** Backup status shows one line while anything is still waiting and for a week after a restore: "12 items got their progress and bookmarks back. 4 are waiting for their files; they'll return if the files do, and are cleared after 90 days." When nothing is waiting it says only how many items got their progress back, and when nothing has come back yet it says only how many are waiting ("3 items are waiting for their files; they'll return if the files do, and are cleared after 90 days."). When both are zero there is no line. It never lists items. It stays quiet about the Vault: the counts are totals.

**The waiting period.** State that is still waiting after 90 days is cleared (the same setting as Safer deleting, `missing_file_retention_days`; changing it affects state already waiting). Administrators on a computer or tablet can change it under **Settings → Backup and recovery → Restored progress → Keep restored progress waiting for**: 30, 60, 90 (the default), 180 or 365 days, saved as soon as you choose. A value set another way (7 to 3650 days) is shown as one more choice. A recovery point made while state is still waiting keeps it, with the time it started waiting, so a second restore does not restart the clock.

A restore does not replace the media files excluded from the recovery point.

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
