---
title: "Libraries, Sources, and Intake"
description: "Target architecture for catalogued libraries, View Personal Spaces, universal intake, and folder safety."
audience: "developer"
category: "architecture"
product_area: "ingestion"
status: current
---

# Libraries, Sources, and Intake

## In this page

Target architecture for catalogued libraries, View Personal Spaces, universal intake, and folder safety.

## Where this lives in the code

- `src/MediaEngine.Domain/Configuration`
- `src/MediaEngine.Ingestion`
- `src/MediaEngine.Api/Endpoints`
- `config/libraries.json`

Tuvima has two runtime library kinds, but only catalogued libraries are
administrator-authored in `libraries.json`. The `personal` kind is an internal
bridge provisioned automatically for each View-enabled profile.

| Kind | Areas | Processing |
| --- | --- | --- |
| `catalogued` | Read, Watch, Listen | Local extraction followed by configured identity and metadata providers. |
| `personal` | View | Local extraction and indexing only. External identity and metadata providers are not called. |

`Photos` and `General` are not configured library kinds. Phone photos, short videos,
documents, artwork, home movies, audio notes, and other personal files belong
to the owning profile's Personal Space in View. A Personal Space has one
internal `personal` library bridge and may receive files from several sources
or devices. The Personal Space, source, and device records are persisted in the
database; they are not library folders in `libraries.json`. These are intake/provenance
details rather than separate user-facing destinations. Galleries organize
assets without changing library kind or moving source files.

## Processing flow

```text
Source
  -> Intake
  -> Destination Library
  -> Library and Source Policy
  -> Storage
  -> Index
```

An intake request records its origin, actor, and optional destination library.
Direct-to-space actions retain the owning profile, Personal Space, stable
destination library ID, and source/device provenance through the pipeline.
Browser upload is implemented. Drag-and-drop, mobile backup, connected-device
import, and API intake remain modeled producer types for future clients; their
presence in configuration or storage is not evidence of a working client.
Every catalogue file-system source belongs to one explicit library, so its
classification and safety policy are known before ingestion begins. Personal
browser uploads use the dedicated targeted View
indexer and never enter catalogue ingestion.

The destination library determines the processing branch:

- Catalogued libraries run local extraction and may continue into provider
  identity, canonical reconciliation, enrichment, and structured organization.
- Personal libraries run local extraction, exact-duplicate handling, compound
  asset grouping, thumbnail or preview generation, and local search indexing.
- Direct personal uploads are indexed through View's targeted local-asset path;
  they never create catalogue chains or identity-provider jobs.
- A local-only or manual metadata policy never calls external identity or
  metadata providers.

Staging and temporary upload paths are implementation details. Normal users do
not configure or browse them.

## Catalogued libraries and sources

A library owns policy and presentation. A source represents one concrete
folder or intake endpoint. Every source has a stable ID and declares its path,
management mode (managed or read-only), recursion behavior, filesystem access
(derived from the management mode), conversion state, health, and optional
device or profile association.

A catalogued library has at most one destination, the managed source new media is
filed into. When none is chosen the first managed folder added becomes the
destination; if it is removed or switched to read-only, the earliest-added remaining
managed folder takes over, and with none left there is no destination. Source order
means nothing else, and a read-only source is never a destination.

## View root and profile sources

View has one administrator-configured managed root:

```text
<view root>/profiles/<stable profile id>/sources/<stable source id>/
```

Display names never determine these paths. Enabling View provisions the
profile's one Personal Space and profile directory. Browser uploads and managed
imports are stored under source-ID directories. Importing an existing folder
copies its files and leaves the originals untouched.

An administrator may instead link an existing folder as an advanced read-only
source. Linked folders stay at their external path and are indexed in place;
Tuvima does not move, rename, write back, overwrite, or delete their files.
View source/device records live in the database so adding a phone, browser
upload stream, or family archive never creates another user-facing library.

### Read-only source (existing library)

A read-only source may be scanned, identified, and indexed, but Tuvima
must not move, rename, tag, overwrite, or delete its files, and it is never
a destination. Switching the source to managed is a separate conversion with its
own progress (`conversion_state`): the Engine's organising job (below) renames and
reorganises the files already there. The Engine owns `conversion_state` and
`conversion_operation_id`: a settings save keeps whatever the Engine has for a folder it
already knew, and saving a folder as Read-only clears both.

Deleting a title whose file sits in a read-only folder removes the title from the library but never the file. The file stays where it is and becomes an **excluded file**: scans skip it, so the title does not come back, until someone adds it back in Settings > Libraries. If a different file later appears at the same place, it is a new file and is scanned.

### Organising job (converting a folder to Managed)

`POST /settings/libraries/folders/convert` makes the folder Managed and queues one
durable `media_operations` row (`organise_folder`, queue `folder_jobs`, one job at a
time) in the same step; the folder's `conversion_state` is `converting` until it ends
and `converted` afterwards, whether it finished, was cancelled or failed. The
`FolderOrganiseJobWorker` leases the job and `FolderOrganiseRunner` walks the folder's
media files in path order:

- **Per file.** The file's standard path comes from the type's naming preset exactly as
  normal organising computes it, but the target always stays inside the folder being
  converted (files organise within the folder they live in). The move is a plain
  same-drive rename, never an overwrite. The data store points at the new path only
  after the rename succeeds, a History line (`PathUpdated`) records old and new path,
  artwork companions and sidecars move with the file, tags are written afterwards when
  the type has them on, and an old folder is removed only once it is empty (the
  converted folder itself never is). A file waiting in Review, or without a confirmed
  title, is not renamed (`needs_review`).
- **Shortcuts.** Symbolic links and Windows junctions inside the folder are never
  followed: the walk (`FolderMediaEnumerator`, `skipLinks`) leaves them out, so a link to
  a Read-only folder or another drive is not touched and a link that points back up the tree
  cannot loop. Anything still reached through one (a link made mid-run) is refused by the
  same check the merge uses (`IsReachedThroughLink`), so it is never moved, renamed or
  deleted. The preview counts the shortcuts it passed over (`skipped_links`) and says
  "Shortcuts inside this folder were skipped."
- **Sidecars.** Files that share the media file's base name travel with it, keeping a
  language suffix (`Movie.en.srt`, `Movie.pt-BR.forced.srt`): subtitles
  (`.srt .ass .ssa .sub .idx .vtt .sup`), `.nfo`, artwork (`.jpg .jpeg .png .webp`),
  `.cue`, `.lrc` and `.m3u/.m3u8` (whose lines naming the media file are updated). A
  neighbour with a longer name keeps its own sidecars. Anything else is left where it
  is and listed as `not_moved`.
- **Collisions.** A different file already at the standard name is never overwritten:
  the file takes a numbered name (`Name (2).ext`) and a Review item ("Two different
  files want the same name", trigger `NameCollision`) is opened. An identical file
  there follows the global rule: under *Keep one copy* this copy is removed (its
  sidecars move beside the kept copy unless the kept copy already has an identical
  one); under *Keep both* it takes a numbered name. Media files the library does not
  know yet are checked for being exact copies of a held file (the same rule) and
  otherwise sent through the normal identification pipeline, which names them when it
  finishes.
- **Durability.** Each file writes an intent to `folder_organise_log` before it moves
  and a final outcome afterwards. After a restart the job's lease is released, the
  unfinished intents are finished (file already at its new name) or retired (nothing
  moved), and files with a final outcome are skipped, so no file is lost, doubled or
  left half moved.
- **Control.** Pause stops after the current file and keeps the place; Resume puts the
  job back in the queue; Cancel stops after the current file and leaves the folder
  Managed with the work done so far ("Stopped after 1,240 of 8,310; the rest will be
  tidied gradually by normal scans"). Saving the folder as Read-only cancels a running job
  and changes nothing on disk. A file in use is skipped, retried at the end of the
  pass with a growing wait, and counted as failed only if it stays locked; the job
  continues past it.
- **Estimate.** Up front: files times the average measured by the last finished job, or
  about 5 seconds per video and 1 second per other file; replaced by the live rate
  once 50 files are done.
- **Safety.** The job only moves and renames, and deletes only exact duplicate copies
  and folders it has emptied. A folder that is not Managed denies every change through
  the same source-mutation gate; the job reads the saved folder settings again every
  few seconds and stops when the folder stops being Managed. Saving `libraries.json`
  refreshes the in-memory folder list, so a switch takes effect without a restart.

### Merge job (merging a folder into a managed folder)

Merge is for one existing folder, once; the Import folder is the way to keep feeding
files in over time. `POST /settings/libraries/folders/merge` (`{source_id,
target_source_id, mode}`) queues one durable `media_operations` row (`merge_folder`,
the same `folder_jobs` queue, one job at a time) and records the merge on the source
folder: `merge_state` (`none`, `merging`, `merged`), `merge_operation_id`,
`merge_target_source_id`, `merge_mode` (`copy` or `move`) and, once it ends, `merged_at`.
Like the conversion state these belong to the Engine: a settings save keeps what the
Engine has. `FolderOrganiseJobWorker` runs it with `FolderMergeRunner`, which reuses the
organising job's control, progress, retry and ledger plumbing (`folder_organise_log`,
exportable log, pause, resume, cancel, restart-resume).

- **Who can merge into whom.** The target must be a managed, writable folder of the same
  library (the same kind of media), not the source itself and not nested inside it. It
  does not have to be the New media folder, but that folder is preselected. The target is
  authorised through `FileSourceMutationPolicyFactory.Create(..., isMergeTarget: true)`,
  so `AllowAsDestination` is true for a managed target only; a Read-only or otherwise
  non-managed target is refused by the source-mutation gate and by the request check.
  The folder that is the library's New media folder cannot itself be merged away.
- **Copy (the default).** The source is never written: no rename, tag, sidecar move,
  deletion or temporary file lands in it, and that holds for a managed source as well. Each
  original is read once, written to a temporary file in the target (`.name.guid.merge-part`),
  compared by size and SHA-256 with what was read, and renamed into place without
  overwriting. Tags are written into the copy only.
- **Move.** Offered only when the source is a folder Tuvima manages. The copy is made and
  verified exactly as above; then the data store is pointed at the copy, and only then is
  the original deleted, after the copy is confirmed byte for byte once more. A copy that
  does not match is discarded and the original is never deleted. Sidecars follow the same
  rule; source subfolders left empty are removed, the source folder itself is not.
- **Naming and duplicates.** Files get the type's standard name inside the target (the
  same preset, the same rules as converting). A different file at that name is never
  overwritten: the copy takes a numbered name and a `NameCollision` Review item is opened.
  An exact duplicate follows the global rule: *Keep one copy* does not copy it (Copy) or
  removes the source duplicate (Move), and the item is catalogued once, as the copy the
  target already had; *Keep both* copies it under a numbered name.
- **Unmatched files.** A file that cannot be named yet (not identified, waiting in Review,
  no confirmed title) is copied or moved to `From merge/<source folder name>/` inside the
  target, and a Review item with trigger `FromMerge` (badge "From merge", root cause
  insufficient evidence) is opened unless something else is already waiting for that item.
  A file the library has never catalogued is put there too and sent through the normal
  identification pipeline.
- **After a Copy merge.** The source row reads "Merged into NAME on DATE" (`merge_state:
  merged`). The folder is no longer scanned or watched (`ScannableSources` and the entry's
  effective source paths leave it out), the items that were in it are catalogued once, as
  the copies in the target (their asset rows are pointed at the copy, not deleted), and
  the person removes the folder when ready. Tuvima never removes it automatically.
- **Cancel and restart.** Cancel keeps what was copied; the ledger lists every file the
  job had not reached as `left_in_source`, and the folder is just a folder again
  (`merge_state: none`). There is no automatic undo; the log is exportable. Each file
  writes an intent to the ledger before its copy and a final outcome after the data store
  is updated, so after a restart a verified copy is completed, anything else is retired,
  leftover temporary copies are cleared, and finished files are not repeated. The job reads
  the saved settings again every few seconds and stops (keeping what was copied) when the
  merge is cancelled, a folder is removed, or the target stops being managed. A cancelled
  merge reads "Merge stopped. Nothing was deleted. You can start it again." (a Move says
  instead that the files already moved stay in the target). Starting it again is a new
  operation: `FolderMergeOrganiser.TryCarryOnEarlierCopyAsync` looks up the earlier run's
  ledger line for each original (`IFolderOrganiseLogRepository.FindEarlierCopyAsync`) and,
  when that copy is still in the target and still has the original's exact bytes, records
  the file again without copying it (a Move removes the original after the same byte
  check), so a restart never adds numbered duplicates.
- **Shortcuts and busy folders.** Symbolic links and Windows junctions inside the source
  are never listed, copied or removed (a file reached through one may really live in
  another folder), and an original is never removed when it and its "copy" are the same
  file. A target that is itself being merged elsewhere, or was merged away, is refused.
  A Move reads the saved settings before every file and stops the moment its source is
  switched to Read-only.
- **Duplicate removal by normal ingestion.** While a folder is merging, or once merged, the
  ingestion pipeline's own "exact duplicate" removal never deletes from it, and never
  removes any duplicate whose kept copy is missing. A merged folder is dropped from the
  scan targets and from folder resolution. After a *cancelled* merge the folder is a
  normal folder again, but the originals the merge listed are still protected: the
  mutation gate (`SourceMutationPolicyGate`, the one place every delete, move and rename is
  decided) asks the ledger (`IFolderOrganiseLogRepository.IsMergeOriginal`) and refuses
  Delete, Move and Rename for any path with a merge line whose outcome leaves the original
  in place (`FolderOrganiseOutcomes.KeepsOriginalInPlace`: `copied`, `copied_unique_name`,
  `duplicate_skipped`, `sidecar_copied`, `merged_for_review`, `left_in_source`), whatever
  state the folder is in and from any caller (duplicate removal, `AutoOrganizeService`,
  reorganising, conversion). Only the merge job's own policy (`IsMergeJob`, set by
  `FolderMergeContext` through `FileSourceMutationPolicyFactory`) is exempt. With the copy
  and the original both present, Keep one copy therefore keeps both untouched and opens no
  Review item. The protection does not expire: the originals stay as they are until the
  person deletes them or merges the folder again as a Move, which removes each one only
  after its copy is checked byte for byte. A merge cut short and then cancelled before it
  could resume has its unfinished lines closed as `copied` (`KeepUnfinishedMergeCopiesAsync`)
  so the original beside a copy that landed is protected too. If the ledger cannot be read
  the gate refuses (fail closed).

### Change-kind job (changing what a folder is)

"Change what this folder is" (Library setup D40(c), D60 to D64, D69) moves one folder to a library of another kind in the same
area (Movies to Videos, Music to Other audio, Other videos to YouTube). `POST /settings/libraries/folders/{sourceId}/change-kind/preview`
and `POST .../change-kind` (`{kind_key, library_id?}`, administrators only) share `FolderKindChangeService`, which refuses a Vault
folder or target, an unknown kind, the folder's current kind (unless an earlier change of the folder was cancelled or failed half way,
when starting it again finishes the rest), a kind from another area, a library that is not a library of that kind, and a folder with
any folder job already open. A new named library (Other videos, Other audio, Other reading allow up to five) is created first through
the normal library action; the Engine never creates one here, and a kind with no library yet is refused with a plain message.

The start writes the plan to `folder_kind_changes` (one row per job, before anything changes) and queues one `media_operations` row
(`change_folder_kind`, the same `folder_jobs` queue, one job at a time). `FolderOrganiseJobWorker` runs it with
`FolderKindChangeRunner`, which reuses the organising job's control, progress and ledger plumbing (`folder_organise_log`: an intent
`moving` before each file and `kind_changed` or `failed` after it; pause, resume, cancel and restart-resume work as for the other
folder jobs; the folder row shows the job's state). The folder row carries no extra state.

- **Once, first.** The folder's `sources[]` entry moves to the target library in `libraries.json` (its management mode and access are
  unchanged; the old library stays even if it is left with no folders). Its old first check and the filing moves held for it end
  (`FolderFirstCheckService.EndBecauseKindChangedAsync`), and a target that looks details up starts a new first check
  (`StartForKindChangeAsync`, D35, D61). The old check ends only after the libraries file has been saved, so a failed save leaves the
  old check and its held moves in place. `config_applied_at` on the plan records that this is done, so it is never repeated; a repair
  skips it (the folder is already in its library).
- **Each file, recorded in the ledger, safe to repeat.**
  1. If the file's Work has a different media type, it is placed again the way ingestion places a new file:
     `HierarchyResolver.ResolveAsync` finds or makes the new Work and its parents (from the file's own canonical values),
     the file's Edition moves under it, the file's media type is recorded as a current claim, what people did with the old
     title (`WorkPersonalStateMerge.Carry`: plays, hearts, saves, bookmarks, queue places, collections, tags) and the details they
     set on it (locked and manual claims) go to the new one (whose values are worked out again), and the old title is removed
     (`LibraryItemDeletionRepository.DeleteWorksCore`, leaf only). The places it leaves empty go through the move's rule
     (`WorkHierarchyMaintenanceService.CleanupEmptyParentsCore`): a show, season, album or series that someone saved, reacted to,
     collected, tagged, or gave their own artwork or details stays. When this is the last file of a title that holds artwork a person
     uploaded or chose, or details they typed, those follow it: the artwork link rows and records are re-pointed (the uploaded files stay
     where they are), the person's preferred picture takes its slot (the new title's looked-up preferred picture for it is let go) and
     `display_overrides_json` is copied, before the old title is removed. If the title it joins already has its own such artwork or
     details, or a picture would collide with one it already has (the same picture in the same place), the file is left exactly as it was
     and logged `kind_change_refused` with a sentence naming both titles ("\"A\" and \"B\" both have your own artwork or details. Remove
     them from one of the two in Editing Mode, then start the change again."). That is final for the job (a resume does not retry it); the
     job ends as failed with "Finished, but N files were not changed" and those sentences as its message (the folder row's job message), and
     starting the same kind again looks at the file once more and refuses it once more. While such files remain, any kind other than the
     folder's original one is refused with the same sentences; changing back to the original kind is allowed and ends with the folder whole.
     A crash between the placement and the new title's values being worked out is finished on resume: the values are worked out again
     (files Tuvima wrote for a removed title that a crash left behind are not found again). With the same media type (Music to Other audio, Other videos to YouTube) the Work stays where it is.
     Asset-keyed state (watch progress) never moves because the asset row stays.
  2. A plain target drops the details looked up for the file and its Work (D69): claims from providers, bridge ids, the Wikidata id,
     external identifiers and provider artwork links. Claims from the file itself (tags, file name) and a person's own edits stay;
     the canonical values of the keys that lost claims are worked out again from the claims that remain (`ScoringHelper`), so the file
     shows by its file name and embedded tags and a refile plan still has a name to take. A YouTube target reads the file again so the
     video-ID lookup runs under the new library. Any other lookup kind queues a new `Quick` identity job with the new media type, the
     way the Unmatched retry does (once: a job a cut-short attempt already queued is not queued again).
  3. In a Managed, writable folder the file's filing into the new kind's pattern is queued (`IFilingQueue.RequestFilingAsync`), held
     while the first check runs. Filing waits for the batch (D62) and the file stays playable until then (D63); a file in use goes
     to the retry batch as it does today. A Read-only folder changes kind with nothing queued and nothing moved.
  4. `media_assets.library_id` becomes the target library after the steps that need not wait for it (the placement, the drop and the
     YouTube re-read), so a file that failed on those is still wholly what it was. The identity job and the filing request are written
     after it, never before (a worker that saw the file in the library it has left would close the lookup or hold the filing); the library,
     the job and the filing request live in three repositories and cannot share one transaction. A file that fails is logged `failed` and
     the job carries on; it retries those files when it resumes (every step can be repeated). A job that ends with any
     failed file ends as failed ("Finished, but N files were not changed ..."), not as a success, and starting the same kind again
     (the repair) retries exactly the files logged as failed and those still in the old library. A different kind is refused until
     that is done.
- **Re-filing template.** The refile planner takes its naming template from the file's media type (`AutoOrganizeService.PlanRefileAsync`),
  not from the kind's `naming_template`. A change that keeps the media type therefore finds a file already fitting (nothing moves);
  a change that re-places the file uses the new media type's template. The kind's `naming_template` is not yet read by the organiser.
- **Activity.** One `FolderKindChanged` entry when the job finishes, naming the administrator who started it (`started_by_profile_id`):
  "{folder} changed from {old kind} to {new kind}: {n} files".
- **Preview.** `files_to_refile` is the number of files queued for the batch's re-filing pass (0 for a Read-only folder); the batch
  moves only those not already at their planned path.
- **Not covered.** Details dropped from a file's Work do not reach parents it shares with other files (an album, a show). A file whose canonical values hold no
  show, album or series folder is placed under a stand-alone Work of the new type until identification fills it in.
- **Known limits.** These are recorded, not fixed:
  - `AutoOrganizeService` still moves files out of a managed folder that is not the
    destination into the destination's root; a merge into such a target is unaffected by
    this job, but later organising of that target may.
  - Starting a stopped merge again into a *different* target, or restarting a *Move* merge
    for files that went to `From merge`, cannot find the earlier copy and may add a numbered
    copy beside it. Restarting into the same target carries on without copying.
  - Side effect of the merge-ledger protection: after a *cancelled* merge of a Managed
    folder, ordinary scans and Organise now cannot rename or move the originals the merge
    listed (the folder is otherwise a normal Managed folder again). They are organised again
    only if the person merges the folder again as a Move, or deletes the originals.

### Managed source

A managed source is one Tuvima looks after: it may move, rename and delete (when
a caller asks) within the source, and write tags when the type's `write_tags` is
on and the format supports it. Only the library's destination, or a merge target
the person started, may receive files.

When a title is deleted from the library, its managed files and their sidecars (subtitles, `.nfo` files, posters, and other files that share its name or that Tuvima wrote) are deleted from disk straight away after the confirmation. Library media has no recycle bin, because films, TV, books, audiobooks, comics and music can be downloaded again. Folders the delete leaves empty are removed. A file that cannot be removed, for example because it is in use or Tuvima has no permission to remove it, stays on disk and becomes an excluded file, and the title still leaves the library. The original of a file that a merge left in place is never deleted: it stays and becomes an excluded file.

Every mutating operation passes through the shared source-mutation policy gate.
Code paths must not infer permission from a path, a global option, or a UI
selection alone.

### Moving an item to another album, season or audiobook

In a Managed folder a move always renames and moves the file to fit its new place; there is no option. The preview says exactly what will happen to each file (`file_change` with the full path before and after, or `file_note` with one plain line), and the Library changes first, in one transaction:

1. **Preview.** For each file the preview asks the organiser's re-file what it would do for the item's new place: the item's own values with the new place above it and the new position numbers laid over them, run through the library's naming template and the shared policy gate. The answer is the same code the job runs later, so the two cannot disagree. A file name depends on the number the person gives the item, so the preview names the file for the entry the person chose: just before the confirm dialog the Match step asks again with its choices (`choices` on the preview request), so the dialog names the file the move will really make, not the Engine's guess; with no choice yet it names the file for the entry the Match step pre-selects, and an item with no suggestion only learns whether its file may be touched ("The file will be renamed and moved to fit its new place"). A choice to add anyway names the unnumbered file. The commit must send the choices the preview was made for; a different choice is a change (409 with a fresh preview). A file is left alone, with a plain line, when it sits in a Read-only folder ("Read-only folder: the file stays where it is"), in a read-only folder, or is protected (the original of a folder merge: "Protected original: the file is not touched"). A name that another file already holds is shown as a warning and the file stays where it is; nothing is overwritten. An item with several files gets one summary line ("2 files will be renamed and moved to OK Computer"). Moving into a hand-picked collection never touches a file. The file lines are part of the preview fingerprint, so a changed target at confirm gives a fresh preview and moves nothing.
2. **Commit.** The Library change and one durable file-queue row per moved file (`media_file_write_intents`, trigger `library_move`, `move_step = requested`) are saved in the same transaction.
3. **Job.** The file write scheduler's move lane (it looks every couple of seconds, so a move never waits for the write interval) claims each row under a lease (so a row being worked on when the Engine stopped is claimed again) and re-files the file. The row then goes back to waiting, and its tags are written on the final file by the next scheduled write run (`run_interval_minutes` in `config/writeback.json`, or Write now). The re-file makes its plan again from the Library as it is now and asks the shared policy gate again: a file that became protected, or whose new name was taken, since the preview stays where it is, the Library change stands, and the reason goes to the activity history and the logs. The file, the companion files that share its name (subtitles, lyrics, notes, cue sheets, artwork) and the stored path follow together through the same code that tidies a filed asset after hydration, so the file watcher recognises the moved file by its fingerprint instead of ingesting it again. A move that fails (a file in use) leaves the file where it was and is tried again, up to the queue's attempt limit. The move is crash-safe: each step (`planned`, `moved`, `checked`) is written on the row before and after it happens, and when the Engine starts it looks at the disk for every move that had started. A file only at its new place is finished (the stored path follows), a file only at its old place is moved again, a file at both (an interrupted copy across drives) is kept at the new place only when it matches the recorded fingerprint, and a file at neither is marked failed with a plain reason, the item kept. While a move is in progress the folder watcher, the rescan and the daily missing-file check ignore both places, so the file is never marked missing or taken for a new file. A second move asked for meanwhile is planned again from the Library once the first is checked. With write-back off the move still happens (it is filing), and only its tag write is dropped. A later editor save does not drop a file move that is still waiting. An item added anyway has no number, so its file is named by its title alone (`Hidden Jam.flac` in the album folder, `Show - Pilot.mkv` in the season folder): the shipped templates write the number parts as optional tokens (`{TrackNumber?}`, `{EpisodeCode?}`), and a dash right after an optional token goes with it when it is empty.

### Filing on its own waits for the batch

A move Library makes on its own, filing a newly identified file in a Managed folder into its kind's pattern or filing a file out of the Import folder, is not done when the file is identified. `AutoOrganizeService` checks everything it checked before (a settled title, no open review, the shared policy gate, a free name) and then records the request on the file's row in `media_file_write_intents` with `move_step = waiting` and `move_origin = filing` (through `IFilingQueue`), and presents the file as before. Until its run the file stays exactly where it is and stays playable: its stored path, companion files and poster do not change, the watcher and the daily check treat it normally at its current path (only a move that is requested or running counts as in progress), and any tags asked for meanwhile join the same row and wait behind the move. When a scheduled run starts (or an administrator chooses Write now), `FileWriteScheduler` first promotes every `waiting` filing row to `requested`, so the move lane files them before any tag is written, then claims the tags in the same run: each file's tags are written at its final path. Filing goes through the same step-recorded re-file as a person's move (`AutoOrganizeService.FileOnScheduleAsync`), so a crash part-way is finished or undone from the disk at the next start, and an Import-folder file keeps its own filing journal. Before a file moves, `FileInUseProbe` opens it exclusively and lets go at once; a file that is in use (or whose move hits a sharing violation) is left untouched, goes back to `waiting` with the reason "In use; will try again at the next run", and is not counted as a failed attempt (on Linux and macOS the probe only sees programs that take a lock on the file; on Windows any handle that does not share it). A filing move that fails for any other reason goes back to `waiting` with the attempt counted and the reason recorded, so it is retried at the next scheduled run, never in between; after the attempt limit it is failed and listed with the failed writes. A filing that already carries pending tags keeps them pending after its move, even with write-back off. Its tags wait with it, so no tag is ever written at the old path and then moved. Filing does not depend on the tag-writing switch: with `writeback.json` `enabled` off the schedule still files waiting files (and writes no tags). A row nobody asked tags for (trigger `filing`) is removed once its move is checked. A `held` row is recorded for a folder whose first check is running or paused (`IFilingHold`, answered by `FolderFirstCheckFilingHold` from `folder_first_checks`): it is never promoted or moved until the check passes or an administrator chooses Keep going, which turn it into `waiting`. A person's own move (Editing Mode) and the folder jobs (Convert to Managed, Merge) are unchanged and still happen at once; a person's move asked for while a filing waits takes the row over.

### The first check of a new folder

A folder added to a library whose kind looks details up (not a plain kind, not a Vault library, and never a folder that existed before) gets a first check before the Engine changes anything in it (Library setup D35, D61). The save of `libraries.json` starts it (`FolderFirstCheckService.SaveLibrariesWithFirstChecksAsync`, before the file is saved, so the hold is in place before the folder is first scanned), and `folder_first_checks` holds its state. While it is `checking` the folder's files are identified as normal, but: `AutoOrganizeService` records their filing as `held` (above); the tag claim (`MediaFileWriteIntentRepository.ClaimBatchAsync`) skips every file inside a folder that is checking or paused (found by a range on `ix_media_assets_path_folded` against the check row's precomputed prefix, case-sensitive outside Windows), so tag writes stay pending; and a file's final retail outcome (matched, needs review or Unmatched, counted in `IdentityJobRepository.UpdateStateAsync`, and for a YouTube file with no video ID in `UnmatchedFileMarker`) is recorded in `folder_first_check_files`, in the order it arrived. The check ends, decided by `FolderFirstCheckHostedService`, when the first `first_check_files` files (50 by default) have an outcome, or, for a folder with fewer files, when nothing is waiting for its lookup and ingestion has settled (no file of the folder has had an open `ingestion.file` row for 30 seconds: the Engine records no completion for the scan of a new folder, so the state must hold that long). A folder whose files all failed to look up ends too, as passed: a failure is no evidence of the wrong kind, nothing matched so nothing moves, and files that match later are filed normally. A folder with no files stays checking, with nothing held, until files arrive. If more than `first_check_unmatched_share` (0.5 by default) of them are Unmatched the folder is `paused`: `IdentityJobRepository.LeaseNextAsync` no longer leases the jobs of its files, the held rows stay held and the tag claim still skips them. Otherwise the check `passed`, the held rows become `waiting` and the next run files them with their tags. An administrator reads the folders that are checking or paused from `GET /library/first-checks` (and on each source in `GET /settings/libraries`), and chooses **Keep going** (`POST /library/first-checks/{sourceId}/keep-going`, on a paused folder or, as an override, on one still being checked): identification resumes, the held rows of matched files become `waiting`, and Unmatched files stay where they are and are looked up again later. On the Settings Libraries kind page each folder row shows a muted "Checking the first files…" line while its check runs, and, when it is paused, an amber line ("Most files in this folder aren't matching: 30 of 50. This is often the wrong kind or the wrong folder.") with **Keep going** and **Remove the folder** (the same confirmation as the row menu's Remove folder); the library's row on the group page carries a short amber **Paused** note beside the health icon. Nothing of this shows in first-run setup. Removing the folder is the existing folder removal; it drops the check and the folder's held rows and moves no file. "Change what this folder is" ends the check with `FolderFirstCheckService.EndBecauseKindChangedAsync`, so a new one can start. While a folder is held, `TextTrackExportService` also writes no subtitle beside a file (it asks the same `IFilingHold`); when the check passes or Keep going is chosen, `HeldSubtitleExporter` exports the preferred subtitles that were held.

Library writes nothing into a file it could not match, in any Managed folder (Library setup D64, D34): the tag claim (`ClaimBatchAsync` and the Claimable count) skips every asset that has a row in `unmatched_assets`, kept as it is or not, for Library's own automatic writes. Its tag requests stay pending (and are not counted as waiting) and are written in the next run once a lookup identifies the file and the row clears. A write a person asked for with an explicit edit (triggers `MediaFileWriteTriggers.PersonsEdits`: the editor, a manual override, item preferences, an applied canonical choice) is written as normal.

## Reorganization

Existing files are reorganized only through an explicit plan:

1. Calculate current and proposed paths without filesystem mutation.
2. Report unchanged, renamed, moved, conflicting, unresolved, blocked, and
   invalid operations.
3. Validate source containment, destination containment, collisions,
   writability, and available space.
4. Require confirmation of that exact plan.
5. Revalidate each operation immediately before execution.
6. Record progress and support cancellation and safe retry.

The planner and executor use stable library and source IDs. A change to source
policy invalidates an unexecuted plan.

## Personal items

View stores a logical local item separately from its physical files. This lets
one item represent a Live Photo, RAW/JPEG/XMP set, original plus edited version,
or another reliable compound relationship. Physical files retain checksums,
paths, sizes, timestamps, and source identity. Generated thumbnails and previews
are stored separately and never overwrite the original.

Searchable metadata includes library, owner, filename, embedded title, item
type, capture and creation dates, device, dimensions, duration, document text
when safely available, GPS or normalized place fields, and user tags.

AI-generated captions, tags, embeddings, transcription, face recognition, and
object detection are not part of the core View implementation. Future
annotations may add provenance, confidence, and model version without becoming
required for ingestion or search.

Archive and Trash are reversible database states. They remove items from the
normal timeline but never rename, move, overwrite, or delete the original.
Gallery removal and Gallery deletion likewise remove only organizational state.
Empty Trash is the one permanent-file operation. Before it deletes anything it
separately proves ownership, source containment, managed/writable policy, and a
fresh explicit confirmation of the exact summary.

Deleting a personal file from disk is done by Empty Trash. A personal file, such as a photo or a document in View, always goes to Trash first, where Restore brings it back, and it leaves the disk only when its owner empties Trash on a computer and confirms the exact summary. Empty Trash empties the whole Trash of the owner's own Personal Space; it cannot delete chosen items. A personal item with a file in a linked read-only folder cannot be moved to Trash at all. Library media works differently: its files are deleted straight away after confirmation (see Managed source), and personal items cannot be deleted through Delete from library.

## Access

Every Personal Space has an explicit owning profile. Profiles independently
control whether View is enabled, whether they may browse Shared View, whether
their own Personal Space contributes to Shared View, and whether they may share
Galleries. Shared View is a virtual authorized aggregation; it never copies or
moves media into a physical master library.

The Engine resolves Shared, Mine, and permitted profile scopes from trusted
profile identity. The same resource authorization decision is enforced for
browse, search, thumbnails, originals, downloads, Galleries, People, Places,
and Collection projections. Administrator configuration rights are distinct
from ordinary content-browsing rights.

A selected-profile Gallery share is independent of Shared View. A caller may
read a specifically shared Gallery without gaining access to that profile's
Personal Space or other assets. Manual Gallery contribution requires its own
permission. Smart Galleries accept no manual membership.

Administrator-authored Collections may reference a whole Gallery or store a
versioned View rule. They never store a list of personal asset IDs. Gallery
membership and rule results remain dynamic, and every projection rechecks the
viewer's View authorization before returning metadata, counts, or previews.

## Pre-beta cutover policy

Before beta, application databases and configuration may be reset or rebuilt to
adopt the target architecture. The Engine does not maintain compatibility
readers, dual schemas, route aliases, or data-preserving migrations for obsolete
development state. Obsolete configuration fails fast and source media is
reingested.

This development-state policy does not weaken filesystem safety. Existing
library sources and user-owned originals remain protected from unintended
moves, renames, writeback, overwrite, or deletion.

## Visual validation and intentional differences

The references use a permanent left rail as primary app navigation. Tuvima
keeps Read, Watch, Listen, View, and Collections in its existing top navigation,
with the established Settings or View rail beneath it. View's rail contains
Photos, Folders, Galleries, People, and Places; filters and source management remain
in the content area or Settings. Folder browsing retains source and nested-path
breadcrumbs. Empty surfaces show truthful capability or
intake guidance rather than fabricated thumbnails, people, counts, or backup
progress.

Places is list-first and is derived only from real GPS or normalized local
location metadata. A future map must preserve that accessible list and must not
contact an unconfigured third-party tile provider. People is similarly
evidence-first: it can show named/reviewed provenance-aware annotations, but it
must not imply that face recognition exists or fabricate unnamed identities.
