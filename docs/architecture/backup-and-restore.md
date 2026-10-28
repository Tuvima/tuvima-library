---
title: "Backup and Restore: What a Backup Keeps"
description: "Which data store tables a backup keeps, which it leaves out, and how each table is classified."
audience: "developer"
category: "architecture"
product_area: "backup"
status: current
---

# Backup and Restore: What a Backup Keeps

## In this page

Every table in the Library's data store is classified as one of four kinds, in one code registry, and a test fails when a table is not classified. This page explains the kinds and lists every table.

## Where this lives in the code

- `src/MediaEngine.Storage/Backup/BackupTableClassification.cs` (the registry)
- `src/MediaEngine.Storage/Backup/BackupTableRule.cs` and `BackupDataKind.cs`
- `tests/MediaEngine.Storage.Tests/BackupTableClassificationGuardrailTests.cs` (the guardrail)
- `src/MediaEngine.Storage/Backup/PersonalStateBackupRepository.cs`, `TitleRecognitionReader.cs` and `BackupArchiveFormat.cs` (the exporter that follows the registry, the one place that reads a title's recognition data, and the file format it writes)
- `src/MediaEngine.Storage/Backup/BackupRestoreRepository.cs` (puts an export into the data store), `WaitingPersonalStateRepository.cs` (the waiting area and the re-attach pass), `LiveTitleResolver.cs` (finds the live row a recorded title now is), `WaitingStateTimePositions.cs` (which tables hold time positions) and `src/MediaEngine.Domain/Services/RestoredTitleRules.cs` (the pure rules for which file is the same title and cut); `src/MediaEngine.Api/Services/Backups/WaitingStateReattachHostedService.cs` runs the pass
- `src/MediaEngine.Api/Services/DatabaseBackupService.cs` (makes a backup file, stages a restore and, before the data store opens, puts the old store aside), `RestoreArchiveValidator.cs` (the one check every route into a restore uses), `RestoreMarkers.cs` and `PendingRestoreImportService.cs` (imports the staged backup into the fresh store at start-up), all under `src/MediaEngine.Api/Services/`
- `src/MediaEngine.Storage/Backup/UserExportScope.cs` (the one map of what a user export holds), `UserExportSummary.cs` (`UserExportReader`, which proves a file holds one profile with its sign-in or one household and nothing of anyone else), `UserImportRepository.cs` (imports the file's profiles into a chosen household in one transaction), `tests/MediaEngine.Storage.Tests/UserExportScopeGuardrailTests.cs` (the guardrail) and, in `src/MediaEngine.Api/Services/Backups/`, `UserTransferService.cs`, `UserImportStagingArea.cs` and the routes in `src/MediaEngine.Api/Endpoints/UserTransferEndpoints.cs`
- `src/MediaEngine.Storage/Backup/IBackupPortableValueCodec.cs` (the port that makes a machine-protected value readable for a backup) and `src/MediaEngine.Api/Security/DataProtectionBackupPortableValueCodec.cs` (the Engine's implementation for the two-step secret, registered with the exporter in `src/MediaEngine.Api/DependencyInjection/TuvimaBackupServiceCollectionExtensions.cs`)

## What a backup is for

A backup holds what is hard to recreate: your settings, your preferences and your own state (reading and watching positions, bookmarks, favourites). It does not hold your library's catalogue (titles, matches, enrichment results, artwork), which a rescan rebuilds (apart from the corrections you made to it yourself, such as a title field you locked or a chapter you renamed), and it never holds your media files or folders. Backing those up is a job for your computer's own backup tool, such as File History, Time Machine or a NAS.

After a restore and a rescan, your own state finds its way back to the right titles, even if a file has since changed. To make that possible, a kept row that points at a catalogue row does not rely on the row's id (ids do not survive a rescan). It is stored with recognition data instead: the title's external ids, running time and edition, with the file fingerprint as a fallback.

## The four kinds

**Configuration** is settings and access, kept as rows: accounts, passkeys, profiles, households, grants, view policies and preferences, provider settings without secrets, notification settings, document types, fields and rules, backup settings, plugin approvals, collections, playlists and View galleries a person built, values a person typed into document fields, and files a person chose to keep out of the Library. Rows that point at View items are re-attached by file fingerprint. Password hashes, passkey public keys and two-step data stay, because they are what lets people sign in after a restore. A person's name and description corrections for a fictional thing or a universe stay too (a fictional thing is found again by its Wikidata id). Collections the Library built (universes and series) are rebuilt, but a person's own switches on them (shown, featured, display changes) are kept.

**Personal state** is a person's own state tied to a title: bookmarks, highlights, reading statistics, audiobook bookmarks and listening history, music play stats, consumption history, reactions, saved items, taste overrides, personal activity and playback positions. It is kept and re-attached to titles by recognition data. Each rule names an owner column (the profile or user the row belongs to), so one profile's state can be exported on its own.

**Catalogue** is anything a rescan rebuilds: works, editions, media assets, canonical values, claims, artwork, fingerprints, subtitles, people, provider caches, search indexes, the Needs Review queue, and identity and enrichment jobs, and View items with what the Engine works out about them. It is not kept. The adult-video area's catalogue is in this group too. The exception is what a person did to it themselves: a person's own corrections on catalogue rows are kept, in two ways (see "A person's own choices and corrections" below).

**Transient** is not kept and not rebuilt from files: sessions and tokens, one-time codes and challenges, run, operation and audit logs, telemetry, device pairing requests, notifications already sent, job queues, backup copy records and data store bookkeeping.

Tables that mix kinds are split in three ways. By column, a rule can name catalogue references (columns that point at catalogue rows, swapped for recognition data), excluded columns (secret or machine-bound columns, never read: a column that may be empty is written as empty, any other is left out of the file) and excluded JSON properties (a named property removed, at any depth, from a JSON column, for example the machine-encrypted destination logins inside the backup settings). By row, a rule can carry a keep condition (for example, only collections a person built, or only provider settings that are not secrets). A rule can also name portable columns (see "Portable sign-in data" below).

A catalogue reference is normally an id. A reference stored as text is treated the same way when it parses as an id; any other text (a Wikidata id, a provider key) is an external key and is kept as written. A reference whose catalogue row no longer exists drops the row, and the count of dropped rows is recorded.

Provider keys and the Engine's own encryption keys are never kept: provider settings marked secret, the `config/secrets` and `config/.keys` folders and plugin secrets are left out. The deliberate exceptions are the two values that are protected with one computer's key, the two-step sign-in secret and a webhook's signing secret. They are kept readable so a restore works on a new computer (see "Portable sign-in data"). Nothing in a backup file is treated as a secret: backups are never encrypted, and keeping the file safe is the person's job.

## A person's own choices and corrections

A rescan rebuilds the catalogue, so anything a person changed on a catalogue row would be lost. Two kinds of rule keep it:

- **Choices** are single columns on a row that a rescan recreates: a favourite flag on a View item, a display change typed on a title, a switch on a collection the Library built. The rule names the choice columns (and, when they sit on a side table such as a View item's metadata row, the column that says whose choice the row is, otherwise the row's own id); the exporter writes them to `data/choices/<table>.json` as a reference to the row (recognised by its external ids, or its file fingerprint) plus the values, and only for rows where a choice differs from its default. A choice column that points at another catalogue row (the series a title was placed in) is written as a reference too. A column can be gated by a sibling flag: it counts only while the flag is set, so a value the Engine read itself (a capture date from the file, a match source the person never locked) is never written. A Configuration table that keeps only some of its rows (collections) can name choice columns as well; they apply to the rows its keep condition leaves out.
- **Corrections** are whole rows a person made on a Catalogue table: a title field they locked or edited, a renamed audiobook chapter, a name they gave to a face in a photo, a cover they chose from a provider. The rule carries a correction condition; matching rows are written to `data/corrections/<table>.json` like personal rows, with every catalogue reference turned into a reference to a title and machine-only columns left out. A row whose required reference no longer exists is dropped and counted. A correction or choice on a withheld title (one a backup may not name: a media asset in a Vault library, a work or edition reached from one, or a View item in a Vault image library) is left out and counted separately, and the title is not written for it. A profile backup holds no corrections or Library choices, because they belong to the Library.

Structural moves are not kept: a locked or rejected match, a locked series placement, which episodes a file covers, or an edition moved to another title. A rescan sorts these out, and it usually fixes what the move was for. Only what a person typed or picked stays.

The reader accepts a choices or corrections file only for a table whose rule allows it. Restoring them (re-applying each correction to the rescanned title) is part of restore, not of the export.

Artwork a person chose is kept only when it has a stable outside identity: a cover picked from a provider is found again by provider and image address (`entity_assets`) or by the artwork's provider, source address and provider reference (`entity_artwork_links`). Artwork a person uploaded is never kept, because artwork files are not in a backup.

## Portable sign-in data

The two-step sign-in secret is stored sealed with this computer's Data Protection key, so on its own it would be useless on a new computer. The same goes for a webhook's signing secret (`application_webhooks.secret_ciphertext`, sealed for that webhook's id; the column cannot be empty, so a restored webhook row needs it). The `account_two_step` rule names `secret_protected` as a portable column, and the `application_webhooks` rule names `secret_ciphertext`: the exporter asks the Engine's codec to unseal it and writes it readable, marked as portable in the file, and a restore seals it again with the new computer's key. Plainly: **backups are never encrypted, so the backup file can be used to sign in as you; keeping it safe is your job.** If the key is gone and the secret cannot be unsealed, it is kept as it is, counted in the backup's contents, and works only on this computer. Without a codec registered, portable columns are written unchanged. The codec handles exactly these two columns, with the same protector and purpose the sign-in code and the webhook code use (it is given the row's values because a webhook secret is sealed for the webhook's id), and refuses any other.

## One person or one household

A user file moves people to another Library (backup and restore spec D19, users spec D9 and D16). It is the same archive format with the same matcher and re-attach pass, but its scope is one profile with the sign-in that opens it (`BackupScope.ForAccount`) or a whole household (`BackupScope.ForHousehold`), not the whole Library. The profile holds the person (roles, access, limits, PIN, preferences and personal state); the user is only the sign-in attached to it.

**Whole backups carry the people.** A full backup holds the users (`accounts`, with `profile_id`), `profiles` (roles, content limits), `households` (including `is_default`), `profile_feature_grants`, `profile_library_hidden`, `library_profile_grants` and `profile_admin_protections` as configuration, so a restore into a fresh Library brings every user back opening their own profile with the same password, roles and access (`PeopleBackupRoundTripTests`). A backup or user file made with another data structure (a different `database_epoch` in its manifest, or none) is refused on every route with one sentence, "This backup is from an older version of the Library and can't be restored. Make a new backup with this version." (a file from a later version says it was made by a different version); a user file says "This file is from an older version of the Library and can't be imported. Export again with this version." (users spec D11).

**What a user file holds.** The map in `UserExportScope` is the single list of tables and the filter for each:

- **Sign-ins**: for each profile that has one, the user row, its password hash, its two-step secret (written readable through the portable-value codec, exactly as in a backup, but only in the exporting person's own sign-in; see below), passkeys, outside sign-in links and dismissed notices.
- **Profiles**: each profile (one for a person, every profile of the household for a household), PIN hashes, grid, sequence and View preferences and View policies (a View preference that points at another person's profile is left out), Vault settings (without who last changed them), playback settings and Read/Watch/Listen/View access (`profile_feature_grants`). The household row is held only for its name, a suggestion for a new household. Roles are written as 0.
- **Their own state**: every Personal state table, filtered by its owner column to those profiles (the owner is a BLOB id in most tables and lower-case text in the reader tables), and the items of their bookmark packs.
- **What they built**: collections and playlists their profiles own (`owner_kind = 'Profile'`), with their items, placements and audience limited to those profiles.

**What the file leaves out**, each with a reason in `UserExportScope.NotInAUserExport`: every other profile and sign-in, hidden libraries (library ids differ between Libraries), administrator settings, invitations, paired devices, sessions and recovery codes, server and Library settings, the catalogue, choices and corrections on catalogue rows, and typed catalogue corrections. View galleries are left out too: they hang off a View space and View items that belong to folders configured on one Library. A test fails when a Configuration or Personal state table is in neither list, so a new table forces a decision.

**The file.** `tuvima-user-<yyyyMMdd-HHmmss>.zip` for a person, `tuvima-household-<yyyyMMdd-HHmmss>.zip` for a household. Its `manifest.json` has `kind` `user` and `scope` `profile` or `household`, plus `profile_count`, `sign_in_count`, `two_step_left_out` and the export counts (no email and no name); `data/contents.json` says `user` or `household` and names the account or household id only. A user file is never a backup of the Library: `RestoreArchiveValidator` refuses kind `user` on every route into a restore with "This file is one user's data, not a backup of the Library. Use Import a user instead.", and the import refuses any other file with "This file isn't a user export.". A user file is read with its own limits (200 entries, 64 MB an entry, 256 MB in all), accepts only tables in the map, holds at most eight profiles, and every row must prove it belongs to one of the file's sign-ins or profiles (`UserRowCheck`), so a crafted file cannot write somebody else's rows. The manifest's `scope` is a claim: a file whose data holds another scope is refused.

**Who may export.** Only the server administrator, signed in at the Dashboard (users spec D16): their own profile and its sign-in (`POST /access/me/export`), any user (`POST /access/accounts/{accountId}/export`) or a whole household (`POST /access/households/{householdId}/export`). Everyone else, household administrators included, gets `403` on all three. A household that does not exist gets "That household isn't in this Library.". Each route asks a stale sign-in to confirm it is them and records `account.exported` or `household.exported`.

**Who gets the two-step secret.** A person exporting themselves carries their own two-step secret, portable through the codec. Anyone exporting someone else's sign-in leaves theirs out: a file for another person has no `account_two_step` row, and a household file keeps only the exporting person's own (`BackupScope.ForHousehold(id, keepTwoStepForAccount)`, `BackupScope.ForAccount(id, leaveOutTwoStep)`). The test is whose sign-in it is against the caller's own account id, not which route was used. The manifest says `two_step_left_out: true` when any sign-in lost its secret, and the import preview carries `two_step_to_set_up` so the administrator is told; those people arrive with two-step off and set it up again from Account > Security. The flag is a claim, not proof: a file for one person that says the secret was left out yet holds a two-step row is refused. Recovery codes are never in the file.

**Importing (users spec D9).** Only the server administrator imports. They upload the file (`POST /access/accounts/import/inspect`), which checks it the same way a restore is checked, keeps it for an hour in the backup folder's `user-imports` staging area under an operation id only that administrator can use, and returns the preview and the choices (the profiles in the file, each sign-in with whether its email is used here, the households that exist with how many people they hold, whether a second household can be added). Nothing is written until `POST /access/accounts/import/{operationId}` with the choices, which imports in one write transaction. A refused import keeps the file so a choice can be changed and tried again; a successful one removes it. The choices:

- **Where:** `target_household_id` (an existing household) or `new_household_name` (made by the import, never the default household), exactly one. All profiles arrive in that household (never `household_id` NULL). At most eight people fit (`Household.MaximumProfiles`); a new household is a second household only once every server administrator has an email and a password or passkey (D10, the same check as everywhere else), and needs `new_household_administrator_profile_id`: an imported profile that keeps its sign-in and is not a Kids profile (D15). An existing household gets no administrator from the import.
- **Sign-ins:** each sign-in keeps its password hash, passkeys and outside sign-in links, and gets the file's email unless `emails` names another (a changed email is never a reason to refuse: the preview says which emails are used here and the administrator types another). `without_sign_in` lists sign-ins to leave out: their profile arrives with no user and nothing about how they signed in. An email used here, a passkey already registered here, an outside sign-in linked to someone here, or a two-step secret that was not made readable for any computer refuses the import (or that sign-in is left out), with nothing written.
- **Roles are never carried.** `is_server_admin` and `is_household_admin` are forced to 0 whatever the file says, a profile role of Administrator becomes a standard user and Kids stays Kids, except the one profile chosen to look after a household the import creates. Vault access is switched off (`access_mode` `off`).
- **Access:** Read, Watch, Listen and View arrive as the file had them only when `keep_access` is true (off by default, so they arrive with none); hidden libraries never arrive (library ids differ between Libraries), so everything in the person's areas is visible.
- **New ids:** an id that already exists here (a household exported from this very Library, for example) is replaced by a new one everywhere it is written; nothing existing is overwritten. Rows are written with a plain INSERT, so any other clash fails the whole import.
- Every column a rule excludes (a profile's `avatar_image_path`, a collection's cover, background, banner and logo artwork paths, and ids that point at someone else) is emptied whatever the file says, and the reader of every route refuses a file that fills one. A collection's parent is kept only when the parent is in the same file. Collections arrive private and not featured; an audience limited to selected profiles stays limited to the imported profiles.
- The two-step secret is sealed again with this computer's key. A file with no two-step row imports normally, with two-step off.
- Rows with no title go live. Rows that point at titles wait in `waiting_personal_state` under a new `backup_restores` row (named after the uploaded file), and attach as their titles are found, exactly as in a restore.

Refusals are plain sentences with "Nothing was imported." (`409`): the email is used ("Someone in this Library already signs in with {email}."), the household is full ("A household can have up to 8 people, and this would be more."), a second household before the administrator has a password ("Set an email and password for your own sign-in first. A Library with several households must not be open to anyone who reaches it."), a new household with nobody to look after it, and the outside sign-in or passkey clashes. The same checks run again inside the import's transaction, so a clash that appears after the preview is still refused. A user who is not in this Library is `404` ("That user isn't in this Library."); a problem on this computer (a key that cannot be read) is `500` with "The file couldn't be made on this computer." and an Error log line, never the internal message. A second upload by the same administrator replaces and deletes their earlier kept file.

Deleting a user removes only the sign-in (users spec D5); deleting a profile removes its sign-in and everything personal in it (D7, D8), so the same file can be imported again afterwards.

## What a backup file holds

A backup is one zip file, `tuvima-backup-<time>.zip`, with three parts:

- `manifest.json`: `schema_version` `3.0`, `kind` `settings-and-personal-state`, `created_at`, `database_epoch` (which data structure it was made on), `includes_secrets` and `includes_keys` (both `false`), `leaves_out` (media files, folders, artwork, catalogue, adult-video catalogue, secrets) and the counts of what it holds (profiles, configuration rows, personal rows, correction rows, choice rows, titles, titles recognised by file only). It holds no Vault field and names no title.
- `config/**`: the safe configuration files, as before (never `config/secrets`, `config/.keys`, backups, `.bak` and `.tmp` files, restore markers).
- `data/**`: the export described above, written by `BackupArchiveFormat`.

There is no copy of the data store file and no write lock: the export reads its own consistent snapshot, so a backup does not pause the Library. A backup of the earlier layout (manifest version `1.0` or `2.0`, no `kind`, or a `database/library.db` entry) is refused everywhere with "This backup is from an older version of the Library and can't be restored. Make a new backup with this version."; a backup whose data cannot be read is refused with "This backup file is damaged and can't be restored.".

## How a restore runs

One check (`RestoreArchiveValidator`) is used by scheduling a restore, the monthly restore check, the test restore, a setup upload and a restore from a destination. It applies the zip safety rules (unsafe or duplicate paths, forbidden secret entries, compression ratio, entry count, size), then reads the manifest, then reads the whole `data/` part once. Scheduling copies the checked zip into a staging folder `pending-<id>` in the backup folder, unpacks its `config/` files beside it and writes `.restore-pending.json` in the configuration folder.

Scheduling gives the restore an id and writes it into the marker. At the next start-up, before the data store opens, `DatabaseBackupService.ApplyPendingRestore` checks the staged zip again, writes where the current data store will go (the pre-restore copy) into the marker, and only then puts the store aside, whole (the file and its `-wal` and `-shm` files), as `<db>.pre-restore-<time>.bak`. Leftover `-wal` or `-shm` files of a store that is no longer there are put aside too (`<db>.orphaned-<time>`), so SQLite never replays them into the new store. If any move, or the copying of the configuration that follows (each replaced file is kept as `.pre-restore-<time>.bak`), fails, everything is put back (the data store and the configuration files already replaced), the pending marker stays and the Engine stops with the error, so the Library is left exactly as it was. Otherwise the marker is replaced with `.restore-import.json`, which carries the restore id and the pre-restore copy. The Engine builds a fresh data store, and `PendingRestoreImportService` (run once after the Engine is built, before anything is served) reads the staged zip and imports it as described below, then deletes the staged folder and the marker and logs one line of counts. The import is recorded under the restore id, so if the Engine stopped after the import committed but before the clean-up, the next start sees the id, imports nothing twice, cleans up and logs that. A clean-up that fails after the import committed is only a warning ("Restore finished; couldn't remove the staged copy"). If the import itself fails, it logs an error naming where the previous data store was put aside, read from the marker (never its contents), renames the marker to `.restore-import-failed.json` so it is not retried on every start, keeps the staged folder, and the Engine carries on with the fresh store. Your current Library is therefore never deleted by a restore.

## Backup screen actions

`GET /system/backups/state` gives the Dashboard counts only: titles a restore can match by exact file only (`PersonalStateBackupRepository.CountTitlesRecognisedByFileOnly`), the waiting period, how many restored rows still wait (`WaitingPersonalStateRepository.CountWaiting`, the Vault's included, never named) and the latest restore (`GetLatestRestore`). `PUT /system/backups/waiting-period` validates the days against `MissingFileRetentionLimits` (7 to 3650) and saves `missing_file_retention_days` through the maintenance configuration. The restore test and a setup upload return a nullable `contents` read from the manifest's counts (`profiles`, `configuration_rows`, personal, correction and choice rows added together, `titles`, `titles_recognised_by_file_only`); it is a total and names nothing.

## Restore and re-attach

A restore imports an export into the fresh data store in one write transaction, so it either completes or leaves the data store as it was. It does three things.

- **Settings go straight back.** A Configuration row that points at no title is written into its table, replacing the built-in row a fresh data store already holds (the default profile, the provider list). Values keep their storage class. A portable value (the two-step secret) is sealed again with this computer's key through the Engine's codec; with no codec the restore refuses rather than write it unsealed. A personal row that points at no title (a taste override) is written only if the Library does not already have it.
- **State that points at a title waits.** Every row, in any group, that holds a reference to a title is parked in `waiting_personal_state` with the recognition data of its titles (ids, running time, edition, file fingerprints, name and year, copied into the row). Each row records when it started waiting. The waiting period (`missing_file_retention_days` in `config/maintenance.json`, 90 days by default; Safer deleting reuses the same setting) is read live when rows are cleared, so changing the setting affects rows already waiting. A row that hangs off a waiting row (the items of a bookmark pack) travels with it. A backup taken while rows still wait keeps them, with the time they started waiting, so restoring it does not restart the clock. Rows about a title a backup may not name (the Vault) are marked withheld and are not counted unless asked.
- **A record of the restore** is kept in `backup_restores` with counts of rows put straight back, rows that started waiting, rows that have since attached, rows that yielded to a row the person made since the restore, and rows cleared.

A pass over the waiting area looks every titled row up in the Library, oldest first, and writes the row back only when every title it holds resolves to exactly one candidate. The Engine runs a pass after start-up, whenever the ingestion queue goes from working to idle, and at least once a day (which is also when rows that have waited longer than the waiting period are cleared). A title resolves like this, and never by guessing:

- **A file**: the exact same file fingerprint is the same file. Otherwise the works that share an id with it (a Wikidata id, IMDb, TMDB and so on; for an episode or track, the parent found by its ids and the child at the same position) are the candidates, and each of their original files is compared with the shared matcher used for bookmark packs. Ids come in two levels (the list is `RestoredTitleRules.EditionLevelIdKeys`): title-level ids name the title (Wikidata id, IMDb, TMDB, a MusicBrainz release group), edition-level ids name one edition of it (ISBN, ASIN, Audible and Apple Books ids, a MusicBrainz release, an edition's Wikidata id). When both sides share a title-level id, those ids decide same title or wrong title, and an edition-level id that disagrees (a hardcover and an ebook of the same book) only makes it another edition, which is treated as a different cut. When they share no title-level id, an agreeing edition-level id means the same edition and a disagreeing one a wrong title. The running time must be within the same tolerance, and the edition label must agree. Exactly one file of the same cut is an exact match; with none, exactly one file of a different cut or edition is a different-cut match; anything else keeps waiting. A file with no ids and a different fingerprint is never matched.
- **A work or an edition**: exactly one work with the same ids (an edition also needs the same label or edition id). With no id, a work is found through an exact file fingerprint.
- **A collection** by its Wikidata id, or, for a collection a person built, by its exact name if that is unique. **A person or a fictional thing** by its Wikidata id (a person with none by exact name, if unique). **A View item** by the fingerprint of its file. **Artwork** by its provider and source address or provider reference. Anything else has nothing to recognise it by and waits until it is cleared.

On a different cut, a row that holds a time position in seconds (an audiobook or video bookmark, a listening history entry) is written only when it names a chapter the live file also has; the position then moves to the start of that chapter (a backup does not record how far into the chapter the position was, so it lands at the chapter's start; this is a known limitation). A position with no chapter name keeps waiting. The saved timing of a file (`user_states`) attaches on a different cut with its progress percent, but without the seconds that belonged to the other cut: `position_seconds` and `duration_seconds` are removed from its `extended_properties` and every other key stays; on the exact same cut it comes back whole. A row with no time position (a progress percent, a reaction, a saved item, a preference) attaches on a different cut as well. The tables that hold seconds are listed once in `WaitingStateTimePositions`, and a test fails if a kept table gains a seconds column that is not listed there or named as a length rather than a position.

Writing a row back never overwrites what the person did since the restore. Settings and personal state are written only if the Library does not already have the row; when it does, the waiting row yields: it is removed, together with the rows that hang off it (the items of a bookmark pack), and counted as yielded, not as attached. A choice (a favourite flag, a display change) is written only for the columns still at their default. A typed correction is written as a new row (with a new id if its own is taken), replacing what a provider put in the same place but not a correction the person made again since; a typed claim on a title also sets the title's winning value, exactly as saving it in Editing Mode does, so it wins over the provider. A row that cannot be written yet (a choice on a row the rescan has not made, a person who has since been removed) keeps waiting.

A run looks at every waiting row once, in batches of 200, however many there are: the write lock is free between batches, there is a pause of 100 ms after each so playback and ingestion writes are not starved, and the run stops early, to carry on when the ingestion queue is idle again, if ingestion becomes busy. The Engine logs one line per run that re-attached, yielded or cleared anything (counts only, never a title), and one Warning per run when rows could not be written, with the number of rows and the table names (never a title); per-row detail is at Debug. The lookups by file fingerprint, id kind and value and edition Wikidata id each use an index; fingerprints are compared in lower case because they are stored that way. Search showing waiting items under the Missing filter, and the restore summary on the Backup screen, come with the screens in the next step.

**Finding waiting items in search (D18).** Global search (`GET /display/search`, `UniversalSearchReadService`) also lists the searching profile's own waiting titles as a `file-missing` section ("File missing"), through `WaitingPersonalStateRepository.SearchWaitingTitles`. The query reads `waiting_personal_state` for the active human profile only (`profile_id`, indexed), `group_kind = 'personal'` and `is_withheld = 0` (so never a Vault title), and only titles whose target is a Work, Edition or Media Asset (never a View item: search code names no View table). It matches the title's name or parent name case-insensitively, lists each (name, parent, year, media type) once, at most 10. A row carries no link or artwork (`DetailRoute` is empty) and `MatchReason` is "File missing". The section sits after all others, is never the top result, and is counted only in its own `TotalCount`, not the response total. Nothing extra is needed when a title returns: its waiting rows are removed by the re-attach pass, so it stops appearing.

## Backup contents (manifest)

See "What a backup file holds" above. The restore screens come in the next step.

## Configuration tables (64)

| Table | Why | Points at the catalogue | Left out | Only rows where |
|---|---|---|---|---|
| `accounts` | Sign-in accounts and their administrator and household settings. | none | none | all rows |
| `account_credentials` | Password hashes, needed to sign in after a restore. | none | none | all rows |
| `account_external_logins` | Links between an account and an outside sign-in provider. | none | none | all rows |
| `account_passkeys` | Passkey public keys, needed to sign in after a restore. | none | none | all rows |
| `account_two_step` | Two-step sign-in data, needed to sign in after a restore; the secret is protected with this computer's Data Protection key, so the export writes it readable (portable column `secret_protected`), or keeps it as it is when it cannot be read. | none | none | all rows |
| `account_feature_grants` | Which features an account may use. | none | none | all rows |
| `account_profile_grants` | Which profiles an account may use. | none | none | all rows |
| `account_notice_dismissals` | Notices an account has dismissed, so they do not return. | none | none | all rows |
| `grant_admin_protections` | Optional extra lock (PIN hash and lock settings) on an administrator grant. | none | none | all rows |
| `profile_feature_grants` | Which features a profile may use. | none | none | all rows |
| `profile_library_hidden` | Which libraries an administrator hid from a profile. | none | none | all rows |
| `library_profile_grants` | Which profiles can see a Selected-profiles library. | none | none | all rows |
| `profile_admin_protections` | Optional extra lock (lock settings) on a profile's administrator rights. | none | none | all rows |
| `households` | Households that group accounts and profiles. | none | none | all rows |
| `profiles` | People using the Library, with their display, content limit and navigation settings. | none | `avatar_image_path` | all rows |
| `profile_credentials` | Profile PIN hashes, needed to switch profile after a restore. | none | none | all rows |
| `profile_grid_preferences` | How a profile sorts each grid. | none | none | all rows |
| `profile_notice_dismissals` | Notices a profile has dismissed, so they do not return. | none | none | all rows |
| `profile_sequence_preferences` | Whether a profile shows missing items in a series or sequence. | `container_key` (other, text) | none | all rows |
| `profile_view_policies` | What a profile may do in View (access, sharing, documents). | none | none | all rows |
| `profile_view_preferences` | A profile's View display preferences. | none | none | all rows |
| `user_playback_settings` | A profile's playback settings. | none | none | all rows |
| `client_devices` | Paired devices and their capabilities (their tokens are not kept, so a device signs in again). | none | none | all rows |
| `applications` | Registered outside applications and what kind of access each has. | none | none | all rows |
| `application_client_bindings` | Which client ids belong to which application. | none | none | all rows |
| `application_credentials` | Hashed application credentials, which are access settings. | none | none | all rows |
| `application_permission_grants` | Which permissions an application has been granted. | none | none | all rows |
| `application_webhooks` | Webhook destinations an application has registered; the signing secret is protected with this computer's Data Protection key, so the export writes it readable (portable column `secret_ciphertext`), or keeps it as it is when it cannot be read. | none | none | all rows |
| `approval_records` | Approvals an administrator has given, with the statement approved. | none | none | all rows |
| `plugin_lore_sources` | Lore sources a person approved or rejected; sources only discovered but never decided on are rebuilt. | none | none | `approved_at IS NOT NULL OR rejected_at IS NOT NULL` |
| `plugin_library_limits` | The libraries an administrator limited a plugin or source to. | none | none | all rows |
| `metadata_providers` | The list of metadata providers and whether each is switched on. | none | none | all rows |
| `provider_config` | Provider settings; rows marked secret are left out of a backup. | none | none | `is_secret = 0` |
| `notification_settings` | Notification channels and rules. | none | none | all rows |
| `backup_settings` | Backup schedule, destination and retention settings; the machine-encrypted destination logins are left out. | none | the `EncryptedSecret` property inside `value_json` | all rows |
| `doc_types` | Document types a person defined. | none | none | all rows |
| `doc_field_definitions` | Custom document fields a person defined. | none | none | all rows |
| `doc_field_choices` | The choices offered by a document field. | none | none | all rows |
| `doc_correspondents` | Correspondents (senders and recipients) a person defined for documents. | none | none | all rows |
| `doc_rules` | Rules that file documents automatically. | none | none | all rows |
| `doc_rule_conditions` | The conditions of a document filing rule. | none | none | all rows |
| `doc_rule_actions` | The actions of a document filing rule. | none | none | all rows |
| `tags` | Tags a person created for View items. | none | none | all rows |
| `view_personal_spaces` | Each profile's private View space. | none | none | all rows |
| `view_sources` | Folders and devices a View space reads from. | none | none | all rows |
| `view_source_policies` | Whether a View source appears in the timeline. | none | none | all rows |
| `view_devices` | Devices that back up into a View space. | none | none | all rows |
| `view_folder_pins` | Folders a profile pinned in View. | none | none | all rows |
| `view_folder_timeline_policies` | Whether a folder appears in the View timeline. | none | none | all rows |
| `view_storage_labels` | Names a person gave to View storage locations. | none | none | all rows |
| `view_shared_library` | Which library a household shares in View. | none | none | all rows |
| `view_galleries` | Galleries a person built in View. | `cover_item_id` (local item), `soundtrack_playlist_id` (collection) | none | all rows |
| `view_gallery_items` | The View items in a person-built gallery. | `item_id` (local item) | none | all rows |
| `view_gallery_shares` | Who a person-built View gallery is shared with. | none | none | all rows |
| `doc_field_values` | Values a person entered in custom document fields. | `item_id` (local item) | none | all rows |
| `local_item_tag_links` | Which tags a person put on which View item. | `item_id` (local item) | none | all rows |
| `doc_item_details` | Filing details a person entered for a document (machine suggestions are rebuilt). | `item_id` (local item) | `suggestions_json` | all rows |
| `excluded_source_files` | Files a person removed from the library and chose to keep out, so a rescan does not add them back. | none | `title`, `excluded_by_name` | all rows |
| `collections` | Collections and playlists a person built; collections built from providers (universes and series) are rebuilt, but a person's switches on them are kept: choice columns `is_enabled`, `is_featured`, `display_overrides_json`, for the rows not kept here. | `universe_id` (other), `parent_collection_id` (collection) | `cover_artwork_path`, `background_artwork_path`, `banner_artwork_path`, `logo_artwork_path` | `collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart')` |
| `collection_items` | The titles in a person-built collection. | `work_id` (work) | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart'))` |
| `collection_placements` | Where a person-built collection appears. | none | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart'))` |
| `collection_profile_audience` | Which profiles can see a person-built collection. | none | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart'))` |
| `collection_view_sources` | The gallery or rule feeding a person-built collection. | none | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart'))` |
| `fictional_entity_user_overrides` | A person's name and description corrections for a fictional thing, found again by its Wikidata id. | `entity_id` (fictional thing) | none | all rows |
| `narrative_root_user_overrides` | A person's name and description corrections for a universe (already keyed by its Wikidata id). | none | none | all rows |
| `folder_first_checks` | The first check of each newly added folder: a pause waiting for an administrator's choice, and which folders have already passed, so a restore neither releases a paused folder nor starts the check again. | none | none | all rows |
| `backup_restores` | Which backups were restored and when, with counts of what was put back and what is still waiting. | none | none | all rows |
| `waiting_personal_state` | State from a restored backup that is still waiting for its title, kept with the time it started waiting, so a backup taken before it attaches does not lose it or restart its waiting period. | none | none | all rows |

## Personal state tables (16)

| Table | Why | Owner column | Points at the catalogue | Left out |
|---|---|---|---|---|
| `reader_bookmarks` | Bookmarks a person placed in a book. | `user_id` | `asset_id` (media asset) | none |
| `reader_highlights` | Highlights and notes a person made in a book. | `user_id` | `asset_id` (media asset) | none |
| `reader_statistics` | A person's reading time and speed per book. | `user_id` | `asset_id` (media asset) | none |
| `audiobook_bookmarks` | Bookmarks a person placed in an audiobook. | `profile_id` | `work_id` (work), `asset_id` (media asset) | none |
| `audiobook_listen_history` | What a person listened to in audiobooks. | `profile_id` | `work_id` (work), `asset_id` (media asset) | none |
| `music_play_stats` | Play counts and last played time per album or track. | `profile_id` | `work_id` (work) | none |
| `consumption_history` | What a person watched, read or listened to, with progress. | `profile_id` | `asset_id` (media asset) | none |
| `personal_activity` | A person's own activity feed (what they played and for how long). | `profile_id` | `work_id` (work), `asset_id` (media asset) | `artwork_key` |
| `player_device_states` | Where a person stopped on each device (playback positions). | `profile_id` | `asset_id` (media asset), `work_id` (work) | `player_session_id` |
| `user_states` | A person's progress and extended state per file. | `user_id` | `asset_id` (media asset) | none |
| `user_taste_profiles` | A person's taste profile. | `user_id` | none | none |
| `profile_reactions` | A person's reactions (likes and dislikes) to titles and other items. | `profile_id` | `entity_id` (other) | none |
| `profile_saved_items` | Items a person saved (favourites and watch later). | `profile_id` | `entity_id` (other) | none |
| `profile_taste_overrides` | Genres and tastes a person switched on or off. | `profile_id` | none | none |
| `profile_work_preferences` | A person's own tags and preferences for a title. | `profile_id` | `work_id` (work) | none |
| `profile_person_preferences` | A person's own tags and preferences for a creator or performer. | `profile_id` | `person_id` (person) | none |

## Catalogue tables (72)

The rows a rescan rebuilds are not kept; the tables below say where a person's own choices or corrections on them are. The search indexes (`search_index`, `local_item_search`, `artwork_asset_search`) are listed; their hidden storage tables are not.

| Table | Why |
|---|---|
| `works` | Titles; rebuilt by a rescan, but display changes a person typed are kept: choice column `display_overrides_json`. Matching and placement choices (locked or rejected matches, a locked series placement) are left to the rescan. |
| `editions` | Editions of a title; rebuilt by a rescan. |
| `media_assets` | The files themselves; rebuilt by a rescan. |
| `media_asset_chapters` | Chapters read from a file. |
| `media_asset_coverage` | Which part of a work a file covers; rebuilt by a rescan, including coverage a person set by hand (moves are left to the rescan). |
| `media_artwork_writeback` | Artwork write-back state for files. |
| `media_inspection_holds` | Inspection holds on files. |
| `playback_inspection_cache` | Cached file inspection results. |
| `playback_segments` | Intro, credits and recap segments found in files. |
| `audio_fingerprints` | Audio fingerprints computed from files. |
| `file_hash_cache` | Cached file hashes. |
| `canonical_values` | Winning metadata values, rebuilt from claims. |
| `canonical_value_arrays` | Winning list-type metadata values, rebuilt from claims. |
| `metadata_claims` | What each provider said about a title; rebuilt by enrichment. Current claims a person made themselves are kept as corrections: `is_current = 1` and either locked by a person or written by the manual-edit provider. `entity_id` is a reference (work, edition, person or file). |
| `bridge_ids` | External ids found for a title. |
| `entity_events` | Identity and enrichment history of a title. |
| `entity_field_changes` | Field changes recorded in a title's history. |
| `entity_relationships` | Relationships between titles, people and characters from Wikidata. |
| `entity_relationship_qualifiers` | Details of those relationships. |
| `entity_capability_states` | What enrichment has done for each title. |
| `identity_jobs` | Identification and enrichment jobs. |
| `unmatched_assets` | Files the Engine could not match, with when each is tried again and whether it was kept as it is. |
| `retail_match_candidates` | Retail match candidates for an identification job. |
| `wikidata_bridge_candidates` | Wikidata candidates for an identification job. |
| `deferred_enrichment_queue` | Titles waiting for enrichment. |
| `enrichment_refresh_schedule` | When each title is next refreshed. |
| `ai_feature_artifacts` | Results of optional AI features. |
| `pending_person_signals` | People spotted in files, waiting to be matched. |
| `review_queue` | Needs Review items; a rescan raises them again. |
| `artwork_assets` | Artwork files known to the Library. |
| `artwork_asset_context` | Where each artwork file is used. |
| `artwork_asset_search` | Search index over artwork. |
| `entity_artwork_links` | Which artwork belongs to which title or person. A cover a person picked from a provider is kept as a correction (`is_user_override = 1`): `entity_id` is a reference to the title or person, and `artwork_asset_id` is a reference to the artwork, found again by its provider, source address and provider reference (never its content hash, because artwork files are not in a backup). A link to an uploaded artwork, or one with no source address and no provider reference, cannot be found again, so the correction condition leaves it out (it is simply absent, not counted as dropped). |
| `entity_assets` | Images attached to titles and people. A cover a person chose from a provider is kept as a correction (`is_user_override = 1`, a provider other than `user_upload`, an `http` image address), found again by provider and image address; `entity_id` is a reference, and the row id, local file paths and export flags are left out. Uploads are not kept. |
| `image_cache` | Cached downloaded images (not kept: a cache row has no owner to attach a choice to). |
| `image_cache_sources` | Where cached images came from. |
| `character_portraits` | Character portraits. |
| `character_performer_links` | Which performer plays which character. |
| `fictional_entities` | Characters, places and other fictional things from Wikidata. |
| `fictional_entity_work_links` | Where a fictional thing appears. |
| `narrative_roots` | Universes and franchises from Wikidata. |
| `persons` | Creators and performers; rebuilt from files and providers, but a person's display changes are kept: choice column `display_overrides_json`. |
| `person_aliases` | Pseudonyms of people. |
| `person_group_members` | Members of bands and groups. |
| `person_media_links` | Which person worked on which file. |
| `person_roles` | The roles a person has. |
| `collection_relationships` | Relationships of provider-built collections. |
| `series_manifest_hydrations` | Series information fetched from providers. |
| `series_manifest_items` | The items of a series fetched from providers. |
| `series_members` | Series membership fetched from providers. |
| `tv_episode_credits` | Episode credits from providers. |
| `audiobook_chapter_title_overrides` | Chapter titles; the ones a person renamed are kept as corrections (`title_source = 'Override'`, with `work_id` and `asset_id` as references), the ones read from the file are rebuilt. |
| `subtitle_index_assets` | Subtitle indexing state per file. |
| `subtitle_index_tracks` | Subtitle tracks that were indexed. |
| `subtitle_lines` | Indexed subtitle text. |
| `subtitle_name_tags` | Characters found in subtitle text. |
| `text_tracks` | Subtitle and lyric tracks found for files. |
| `local_items` | Photos, documents and other View items found on disk; a person's own choices on them (favourite, hidden, archived, trashed, capture date they set) are kept: choice columns `favorite`, `hidden`, `archived_at`, `trashed_at`, `captured_at_user_override`, `captured_at` (the date only while the person set it). |
| `local_files` | Files behind View items. |
| `local_file_sources` | Where View files were found. |
| `local_item_files` | Which files belong to which View item. |
| `local_item_metadata` | Metadata read from View items; a place a person set on a photo and a description they wrote are kept as choices on the item named by `item_id`: choice columns `description`, `location_user_override`, and the place columns `latitude`, `longitude`, `location_name`, `location_city`, `location_region`, `location_country`, `location_country_code`, `location_source` (only while `location_user_override` is set). The embedded coordinates, camera and lens details and document text are never kept. |
| `local_item_annotations` | Labels and detections found on View items; a name a person gave, or a detection they reviewed, is kept as a correction (`source = 'user_assignment' OR reviewed_at IS NOT NULL`, `item_id` as a reference), without the machine's own details (`confidence`, `model_name`, `model_version`, `provenance_json`). |
| `local_item_search` | Search index over View items. |
| `local_item_search_keys` | Row numbers linking View items to their search index entries. |
| `doc_item_field_sources` | Where document field values came from (tied to View items). |
| `view_shared_assets` | View items promoted to the shared library. |
| `personal_activity_artwork` | Cached pictures for the activity feed. |
| `provider_response_cache` | Cached provider answers. |
| `resolver_cache` | Cached identification answers. |
| `search_results_cache` | Cached search answers. |
| `qid_labels` | Cached Wikidata labels. |
| `search_index` | Search index over titles. |

## Transient tables (68)

None of these are kept.

| Table | Why |
|---|---|
| `auth_sessions` | Sign-in sessions. |
| `client_tokens` | Device and application tokens. |
| `service_credentials` | Service credentials; they are issued again. |
| `account_invitations` | One-time invitation tokens. |
| `password_recovery_codes` | One-time recovery codes. |
| `password_reset_challenges` | One-time reset links. |
| `two_step_challenges` | One-time sign-in challenges. |
| `setup_codes` | One-time setup codes. |
| `grant_admin_unlocks` | Temporary administrator unlocks. |
| `profile_admin_unlocks` | Temporary administrator unlocks on a profile. |
| `device_pairing_requests` | Pairing requests from devices. |
| `authorization_audit_events` | Access change audit log. |
| `identity_audit_events` | Sign-in audit log. |
| `system_activity` | System activity log. |
| `transaction_log` | Change log. |
| `ingestion_log` | Ingestion run log. |
| `folder_organise_log` | Folder organise run log. |
| `folder_kind_changes` | Plans of the change-what-this-folder-is jobs. |
| `scheduled_task_runs` | Scheduled task run log. |
| `media_operations` | Media operation queue. |
| `media_operation_events` | Media operation log. |
| `media_file_write_intents` | Pending file write-back jobs. |
| `folder_first_check_files` | The files counted in a folder's first check. |
| `file_write_runs` | When the file write-back schedule last ran. |
| `encode_jobs` | Encode job queue. |
| `alignment_jobs` | Book and audiobook alignment job queue. |
| `adaptive_hls_packages` | Streaming copies prepared for playback. |
| `offline_variants` | Offline copies prepared for devices. |
| `device_downloads` | What each device has downloaded. |
| `device_photo_downloads` | Which photos each device has downloaded. |
| `playback_telemetry_sessions` | Playback telemetry. |
| `audiobook_listen_active_segments` | Audiobook listening in progress. |
| `music_play_active_segments` | Music playing in progress. |
| `import_uploads` | Resumable uploads into the Import folder in progress, with the hidden working file each is written into. |
| `import_upload_pieces` | Which pieces of each upload into the Import folder have arrived. |
| `player_sessions` | Live player sessions. |
| `player_queue_items` | Live player queues. |
| `companion_sessions` | Companion screen sessions. |
| `personal_status_commands` | Recent status changes kept for undo. |
| `ai_usage_records` | AI usage telemetry. |
| `provider_health` | Provider health telemetry. |
| `provider_connection_checks` | Provider connection checks. |
| `application_events` | Application event outbox. |
| `application_webhook_attempts` | Webhook delivery attempts. |
| `application_webhook_deliveries` | Webhook delivery queue. |
| `plugin_event_cursors` | How far each plugin has read events. |
| `plugin_records` | Plugin working data. |
| `plugin_activity` | What plugins did and how they failed; fills again as they run. |
| `plugin_lore_entities` | Lore entities found by plugins. |
| `plugin_lore_relationships` | Lore relationships found by plugins. |
| `media_editor_commits` | Editor save records, kept for repeat requests. |
| `media_editor_commit_items` | Items of editor save records. |
| `media_editor_commit_artwork` | Artwork of editor save records. |
| `media_editor_edition_artwork_commits` | Edition artwork save records. |
| `media_editor_preferred_artwork_commits` | Preferred artwork save records. |
| `media_editor_music_pairing_commits` | Music pairing save records. |
| `media_editor_music_pairing_commit_items` | Items of music pairing save records. |
| `view_shared_contributions` | Shared library contribution requests. |
| `view_shared_contribution_items` | Items of shared library contribution requests. |
| `view_shared_contribution_events` | Shared library contribution log. |
| `view_shared_transfers` | Shared library transfers in progress. |
| `notifications` | Notifications already sent. |
| `notification_channel_state` | Notification channel health. |
| `notification_rate_marks` | Notification rate limiting marks. |
| `backup_copies` | Records of backup copies already made. |
| `ui_settings_cache` | Cached Dashboard settings. |
| `onboarding_workflows` | First-run setup progress. |
| `onboarding_steps` | First-run setup steps. |
| `onboarding_sessions` | First-run setup sessions. |
| `onboarding_restore_operations` | Restore operations started from first-run setup. |
| `data_store_upgrades` | Which data store upgrade steps have run. |
| `storage_metadata` | Data store bookkeeping. |

## The Vault

The Vault keeps its settings and each person's own state in a backup, and never its catalogue (a rescan rebuilds it). There is no Vault-specific backup step: the Vault folds into the one mechanism above. A Vault video that a person's state points at is written to `titles.json` only as its ids, its Stash-box record (`stash_box_id`, written `server|record`), its running time, its edition and its file fingerprints, flagged `withheld`, with no name, parent name or year; the same Stash-box record is read from the live video, so after a rescan a Vault bookmark finds the same video even when the file (and so its fingerprint) is different, and with no Stash-box record the exact file fingerprint still decides. A person's corrections and choices on a Vault title are left out. A restore brings the Vault libraries back empty, with Vault mode settings, PIN hashes and grants. The saved Vault bookmarks wait like any other state, marked withheld, and are counted only when asked (the Vault settings page asks, through `GET /api/v1/vault/restored-bookmarks`, which counts the withheld waiting rows and never names a title). The earlier Vault-only scrub, restore note and bookmark re-link (and the `vault_pending_bookmarks` table, dropped by upgrade step 0040) are gone.

| Table | Kind | Why |
|---|---|---|
| `profile_nsfw_settings` | Configuration | How each profile's Vault mode is protected and when it turns off. |
| `vault_libraries` | Configuration | Which libraries are Vault libraries and how they look things up. |
| `bookmark_packs` | Personal state | Bookmark packs a person imported for a title. |
| `bookmark_pack_items` | Personal state | The bookmarks inside a pack (owned through the pack's profile). |
| `nsfw_session_unlocks` | Transient | Temporary Vault mode sessions. |
| `vault_activity` | Catalogue | Quiet list of Vault ingestion activity. |
| `vault_artwork` | Catalogue | Vault artwork. |
| `vault_asset_performers` | Catalogue | Vault performers per video. |
| `vault_asset_studios` | Catalogue | Vault studios per video. |
| `vault_asset_tags` | Catalogue | Vault tags per video. |
| `vault_field_sources` | Catalogue | Where each Vault field came from. |
| `vault_file_fingerprints` | Catalogue | Vault file fingerprints. |
| `vault_matches` | Catalogue | Vault lookup matches. |
| `vault_performers` | Catalogue | Vault performers. |
| `vault_studios` | Catalogue | Vault studios. |
| `vault_tags` | Catalogue | Vault tags. |
| `vault_galleries` | Catalogue | Vault image galleries. |
| `vault_gallery_items` | Catalogue | Images inside Vault galleries. |
| `vault_image_folders` | Catalogue | Vault image folders. |
| `vault_image_items` | Catalogue | Vault images. |
| `vault_image_libraries` | Catalogue | Vault image libraries. |
| `vault_image_tags` | Catalogue | Vault image tags. |

## How to add a table

When you add a table through a numbered data store upgrade step, add one line for it in `BackupTableClassification`, choosing Configuration, Personal state, Catalogue or Transient, with a one-sentence reason. The guardrail test fails and names the table if you forget. Then:

- For Configuration or Personal state, list every column that holds the id of a catalogue row as a catalogue reference, and every secret or machine-bound column as an excluded column. Personal state also names its owner column.
- If only some rows are kept, give the rule a keep condition.
- If a secret sits inside a JSON column, name the JSON property to remove. If a person's own choice sits on a Catalogue row, name the choice columns on that Catalogue rule (and gate a column by its flag when only a person-set value counts). If a person makes whole rows on a Catalogue table, give it a correction condition, its catalogue references and its machine-only excluded columns. If a Configuration column is sealed with this computer's key, name it as a portable column and extend the Engine's codec (it refuses anything it does not know).
- Add the table to the matching list on this page. A second test fails if a classified table is missing here.
- If unsure whether a column holds a secret, exclude it and ask.
