# Spec: Backup and restore (settings and personal state)

> **Status:** In progress · **Version:** 5 · **Last updated:** 2026-10-22 · **Related docs:** [Product overview](../product/overview.md) · [Videos spec](videos.md) D43, D46 (bookmark packs) · [Developer tools spec](developer-tools.md) D-P, WP25, WP26 (destinations, schedule, passphrase) · [Encrypted plugin keys](encrypted-plugin-keys.md) EK2 · [Delete groups](delete-groups.md) · [Safer deleting in View](safer-deleting-in-view.md)

> **Build note (2026-10-22):** WP1, WP2 (#397), WP2b (#405), WP3 (#421) and WP4 (#424) are built and merged: backups hold settings, personal state and typed corrections against recognition data, with no encryption; restore loads a fresh Library and re-attaches state automatically; the screens in section 4 are in place. Not yet built, waiting on Shy: a warning when restoring a destination copy this Library did not record making, the "File missing" search result for waiting items (D5a, D5b; the Safer deleting Missing filter does not exist yet), and "Export my data" for one profile (D10) with a way for another Library to take it in. Local checks L1 to L4 (plus the waiting-period check) are in `engineering/validation/batch.md`.

## 1. Goal

A backup should hold what is hard to recreate: your settings, preferences and your own state (reading and watching positions, bookmarks, favourites). It should not hold your library's catalogue or your files. After a restore and a rescan, your personal state finds its way back to the right titles, even if a file has since changed (different encoding, file type or edition).

## 2. Scope

**In scope**
- What a backup contains and leaves out.
- How personal state is tied to a title so it survives a re-scan.
- What the person is told after a restore.
- The Vault's special case.

**Out of scope**
- Backing up media files, folders or artwork. That is an operating-system job (File History, Time Machine, a NAS), and the app says so.
- Community sharing of bookmark packs (deferred past launch, see Videos spec).
- Cloud backup or any Tuvima-hosted service.

## 3. Decisions made

- **D1. No media in backups (Shy, 2026-10-21).** Files and folders are never copied. They are an OS-level concern.
- **D2. Back up configuration, preferences and personal state only.** Library settings, provider settings (never secrets), profiles and access, preferences, reading and watching positions, bookmarks, favourites, ratings and similar personal choices. Not the catalogue (titles, matches, enrichment results, artwork), which a rescan rebuilds.
- **D3. Personal state is pinned to the canonical title (Shy, 2026-10-21).** Each saved item records how to recognise the title: its external IDs (for example Wikidata QID, IMDb, TMDB, a stash-box id), running time and edition. After a restore, when that title is added again, the state re-attaches even if the file's hash differs.
- **D4. File hash is the fallback.** With no canonical data, state re-attaches only to the exact same file (same fingerprint). Otherwise it cannot be matched and is dropped.
- **D5. Import everything, summarise, keep what is waiting (Shy, 2026-10-21).** Restore imports every saved item. The person is told counts only (matched, waiting for a file), never a list of each one. Items with no file yet are kept as waiting state and come back automatically if the file returns. They are kept for the same period as Missing files (90 days, or the configured setting) and then purged. Nothing is guessed.
- **D5a. Waiting items stay findable.** Search finds waiting items (using the "Missing" filter from Safer deleting) so a person can always locate something that was deleted or not yet re-added. The Recycle bin history shows items deleted before the backup was taken, so it is easy to check whether a gap was a deliberate delete.
- **D5b. No manual matching (Shy, 2026-10-21).** There is no "link to a file" button. Waiting items re-attach only automatically and quietly when the title or exact file returns, with a log entry. Search shows a waiting item with a plain "File missing" note, so someone can ask an administrator where it went.
- **D6. Same matching rules as bookmark packs.** One shared matcher, not two (IDs must agree, running time within the same tolerance, edition and cut handled the same way, "align by chapter" offered for a different cut).
- **D7. Vault.** The Vault's catalogue (titles, performers, studios, tags, matches, artwork, activity) is never in a backup. Vault settings, access and the person's own Vault state are kept, pinned by canonical id or fingerprint as above. After a restore the Vault shows empty until its folders are rescanned. No Vault title appears in a backup file.
- **D8. Same file format and handling as today's backups where they still fit** (one archive, manifest, no secrets, safe-read checks). The manifest says what kind of backup it is and what it leaves out.
- **D10. Export and backup are one thing (Shy, 2026-10-21).** "Export my data" produces the same kind of file as a backup, so there is one format, one matcher and one restore path. Export can be limited to one profile's personal state (to move a person to another Library). Community sharing stays deferred.
- **D11. Delete and restore agree.** Personal state is kept while an item sits in the Recycle bin (Delete groups, Safer deleting) and the backup includes it, so a restored Library does not lose the progress of items deleted but not yet purged.
- **D12. Other specs' backup work builds on this one.** Scheduled backups, destinations, passphrase encryption (Developer tools WP25, WP26) and "Include keys" (Encrypted keys EK2) keep working unchanged; they wrap whatever the backup contains. Only the *contents* change here, so there is one archive builder, not two.
- **D9. Pre-release: no compatibility shims.** Old whole-data-store backups are not migrated (CLAUDE.md rule). Restoring one shows a plain message that the backup is from an older layout.
- **D13. The waiting period is set here (Shy, 2026-10-22).** This spec adds the one Library setting for how long state waits for a missing file (90 days by default). Safer deleting reuses it for Missing files rather than adding a second one.
- **D14. Your own typed corrections are kept; moves are not (Shy, 2026-10-22).** Fixes a person typed or picked are kept as well as their progress: field edits and locks made in Editing Mode, renamed chapters, corrected universe and character names, face names they gave, a photo's place and description, covers picked from a provider, and the on/off, featured and display choices on collections the Library built. They are stored against the title the same way as personal state, re-applied when the title returns, and still win over provider data. Structural moves are not kept: a locked or rejected match, a locked series placement, which episodes a file covers, an edition moved to another title. The rescan sorts these out, and usually fixes what the move was for.
- **D15. Backups are never encrypted (Shy, 2026-10-22; replaces the optional password recorded earlier the same day).** There is no backup password or passphrase anywhere, including copies sent to another folder, a network share, S3 or WebDAV: nothing in a backup is secret enough to justify the extra step, and it raises the barrier to using backups. The file holds what is needed to sign in on any computer, two-step sign-in included, and keeping it safe is the person's job (the Backup screen says so). The sealed-copy encryption built for backup destinations (Developer tools WP25) is removed. Provider and plugin keys stay out of backups; how Encrypted keys EK2 changes is asked separately.
- **D16. Keys travel only when asked, behind a 4-digit PIN (Shy, 2026-10-22).** A backup has an optional "Include keys" choice, off by default. Turning it on asks for a 4-digit PIN; provider and plugin keys are locked with it inside the backup, and nothing else is. Restoring asks for the PIN only if the backup holds keys: the right PIN puts the keys back, no PIN or a wrong one skips just the keys and everything else restores. The PIN is a convenience lock, not strong protection (Shy, 2026-10-22): each guess is made deliberately slow, and the switch says in one line that the PIN only stops casual snooping, so keep the backup safe; providers let keys be reset if a backup is lost. Built as Encrypted keys EK2 with this PIN in place of the backup passphrase.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Backup screen | A plain note under the button: "Backups hold your settings and your own progress, bookmarks and favourites. They do not include your media files or folders. Back those up with your computer's own backup tool." |
| Backup screen, keeping it safe | One plain line: "Anyone with this file can see your settings and sign in as you. Keep it somewhere safe." No password option. |
| Backup screen, items matched by file only | A warning bubble with one short line: "38 items are matched by exact file only and may lose progress if the file changes." Hidden when the count is zero. |
| Creating a backup | A short progress state, then the file name and size. |
| Restore, first look | A summary of what the backup holds: profiles, libraries (settings only), number of bookmarks and positions waiting for titles. |
| After restore + rescan | One summary line: "X items got their progress and bookmarks back. Y are waiting for their files; they'll return if the files do, and are cleared after 90 days." No per-item list. |
| Waiting items | They attach automatically when a matching title or file appears, are findable through search's Missing filter, and are purged after the retention period. |
| Error (older layout, damaged file) | One sentence saying what is wrong and what to do. |
| Vault | Nothing about Vault titles anywhere. Vault progress only shows inside NSFW mode. |

## 5. Data / model changes

- A "waiting personal state" holding area keyed by canonical recognition data (IDs, running time, edition) and optionally a file fingerprint, scoped to the profile.
- Backup export reads personal state and writes it keyed by recognition data instead of copying the whole data store.
- Restore loads configuration, then fills the waiting area. A matcher runs after each ingestion and re-attaches waiting state.
- Pre-release: old backups are not converted; a reset is acceptable.

## 6. Work packets

### WP1 — Define what is kept
- **What it does:** Inventory every table and setting; classify each as configuration, personal state or catalogue. First-pass classification from the current data structure, for Claude Code to confirm: *Configuration and access* (accounts, passkeys, profiles, households, grants, view policies and preferences, provider config without secrets, notification settings, document types, fields and rules, saved collections the person built); *Personal state* (reader bookmarks, highlights and statistics, audiobook bookmarks and listening history, music play stats, consumption history, profile reactions and saved items, taste overrides, personal activity, playback positions); *Catalogue, rebuilt by a rescan* (works, editions, media assets, canonical values, claims, artwork, fingerprints, subtitles, caches, ingestion and job tables, local items and files). Tables that mix kinds are split by column. write the list into the engineering docs and the manifest schema.
- **Files likely touched:** `Api/Services/DatabaseBackupService.cs`, docs, a new classification test that fails when a new table is not classified (to be confirmed by Claude Code)
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test fails if any table is unclassified; the doc lists each group.

### WP2 — Canonical-keyed personal state export
- **What it does:** Export configuration and personal state keyed by recognition data; no catalogue; no Vault catalogue.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** a backup contains no titles other than the recognition data the person's own state needs; a raw-bytes search finds no Vault title or file name.

### WP2b — Keep your own corrections (D14)
- **What it does:** Adds a person's corrections on catalogue rows to the export, keyed by recognition data like personal state: user-locked and user-entered values from Editing Mode, chapter title overrides, universe and character name overrides, confirmed face names, a photo's place and description, covers picked from a provider, and the switches and display choices on collections the Library built (no structural moves, D14). Sign-in data needed on another computer (two-step) is exported in a form any computer can read (D15).
- **Depends on:** WP2
- **Size:** Complex
- **Done when:** an export of a library with a locked title, a renamed chapter and a hidden built collection holds those choices against the title's recognition data and nothing else from the catalogue; the two-step data in the export reads without this computer's key.

### WP3 — Restore and re-attach
- **What it does:** Restore configuration, fill the waiting area, re-attach after each ingestion using the shared matcher; report matched, waiting and unmatched counts. Re-applies kept corrections (D14). Adds the waiting-period setting (D13). Wires the export into backups and restore, and removes backup encryption and the passphrase from backups and destinations (D15): every copy, local or sent elsewhere, is the same plain backup.
- **Depends on:** WP2, WP2b, bookmark packs matcher (built)
- **Size:** Complex
- **Done when:** a test restores onto an empty library, adds a same-title file with a different hash and sees bookmarks and position return; a file with no canonical data and a different hash does not match; counts are reported.

### WP4 — Backup and restore screens, docs
- **What it does:** The screens in section 4, plain wording, docs and presentation rules updated.
- **Depends on:** WP3
- **Size:** Standard
- **Done when:** phone and desktop widths checked; copy matches section 4.

## 7. Local checks

- **L1.** Back up a small test library with bookmarks and positions, delete it, restore, re-add the files with one re-encoded: progress returns for the re-encoded file when it has canonical IDs, and is reported as unmatched when it has none.
- **L2.** A Vault test folder: after restore the Vault is empty until rescanned, then Vault progress returns.
- **L3.** Restore a backup with no password on a second Windows account or computer: signing in with two-step works; corrections (a locked title, a renamed chapter) come back after a rescan.
- **L4.** A copy sent to another folder or a network share is a plain backup: it restores without any passphrase, and the Backup screen shows no password option.

## 8. Risks and open questions

- **Coordination:** WP25 (Developer tools) and EK2 (Encrypted keys) both edit the backup builder. Build WP1 and WP2 here first, or agree one owner; do not let three threads change `DatabaseBackupService.cs` at once. This work is post-launch unless Shy says otherwise.
- **Risks:** a canonical id that changes upstream could orphan state (mitigate with the fingerprint fallback and the waiting list); personal state for titles with no canonical data is lost on a changed file (D4 accepts this and says so).
- **Q1 (answered by Shy 2026-10-21: no).** Backups stay small and light: configuration and personal state only; anything a rescan or the 30-day refresh can rebuild is rebuilt. Original question: ** Should the person be able to keep a backup of the catalogue too (an "include everything" switch for faster restores)? Recommendation: no for launch; a rescan is fast enough and it keeps the rule simple.
- **Q2 (answered: yes, but no per-item list on restore; see D5/D5a).** Keep a name and year in the backup for waiting items so the unmatched list is readable? Recommendation: yes, only for titles the person has personal state on.
- **Q3 (answered: no passphrase at all, D15, Shy 2026-10-22).** Should an optional passphrase be added later for the personal-state part? Recommendation: not now; there are no secrets in it.
- **Q4 (answered: yes).** Shy also wants Export to be the basis for a future cross-Library sync keyed to a Tuvima ID (watch to 40% on one server, continue on another). Not built here; the export format must carry the recognition data and a per-profile scope so it can be. Should Export (D10) also allow a single profile's state only? Recommendation: yes, it is the same code and covers moving a person to another Library.
- **Q5 (answered: yes, Shy 2026-10-21).** Shown as a standard warning bubble on the Backup screen (the app's existing warning style), one short line, no lists. Original question:  Document libraries with no canonical IDs (scans, personal files) match by file hash only (D4). Should the backup warn at creation time how many items are "hash only" so people know what a re-encode would lose? Recommendation: yes, one line on the Backup screen.

## Plain English summary

Backups will keep your settings and your own progress, bookmarks and favourites, never your media files. Your progress is remembered against the title itself, so if you re-add a movie later, even as a different file, the bookmarks come back. If the app cannot be sure which title something belonged to, it tells you instead of guessing. The Vault follows the same rules and never puts its titles in a backup.
