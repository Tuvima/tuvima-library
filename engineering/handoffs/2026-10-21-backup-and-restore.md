# Handoff: Backup and restore (settings and personal state)

Run this handoff end to end per `.claude/rules/end-to-end-runs.md`.

**Spec:** `engineering/specs/backup-and-restore.md` (Locked, v2, 2026-10-21). Read it first; it is the source of truth. Built in the cloud, validated locally in one batch (`.claude/rules/cloud-first.md`). Post-launch priority unless Shy says otherwise.

## Thread (one thread, WP1 to WP4 in order)
Prompt for the build session:

> Build `engineering/specs/backup-and-restore.md` end to end. Set its Status to In progress and update `engineering/README.md`. Work packets WP1 to WP4 run in order. Today's backup (`src/MediaEngine.Api/Services/DatabaseBackupService.cs`) copies the whole data store; replace the contents with configuration, access and personal state only, keyed to the canonical title (IDs, running time, edition) with the exact file hash as fallback. No media, no catalogue, no Vault titles, no secrets. Restore imports everything, shows one summary line, keeps unmatched state as waiting for the configured missing-file period (90 days default), purges after, and re-attaches quietly and automatically with a log entry. No manual matching, no per-item lists. Search shows waiting items with a "File missing" note (reuse the Missing filter from Safer deleting). Show a standard warning bubble on the Backup screen for the count of file-hash-only items. Reuse the Videos bookmark pack matcher (D46/WP15); do not write a second one. Pre-release: no compatibility shims; old backups get a plain "older layout" message. Follow CLAUDE.md, merge your own PRs once CI is green, post a short hourly update, and finish with the Local checks appended to `engineering/validation/batch.md`.

## Dependencies and collisions
- Developer tools WP25 (schedule, passphrase, destinations) and WP26, and Encrypted keys EK2 (sealed keys) also edit `DatabaseBackupService.cs`. Check `list_sessions` and unmerged branches first. If WP25 is not merged, build WP1 and WP2 first and agree one owner for the archive builder with the Developer tools thread. Do not run two threads on that file at once.
- Delete groups and Safer deleting (Locked): personal state is kept while items sit in the Recycle bin (D11); search's Missing filter is reused (D5a).
- Videos bookmark pack matcher must exist (built in Videos Wave 3 WP15). If it is not merged yet, say so and park WP3 only.

## Needs from Shy
Nothing before starting. Local checks L1 and L2 on SHY-DESKTOP afterwards.
