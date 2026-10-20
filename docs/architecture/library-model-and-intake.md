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
management mode (managed or as-is), recursion behavior, filesystem access
(derived from the management mode), conversion state, health, and optional
device or profile association.

A catalogued library has at most one destination, the managed source new media is
filed into. When none is chosen the first managed folder added becomes the
destination; if it is removed or switched to as-is, the earliest-added remaining
managed folder takes over, and with none left there is no destination. Source order
means nothing else, and an as-is source is never a destination.

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

### As-is source (existing library)

An as-is source may be scanned, identified, and indexed, but Tuvima
must not move, rename, tag, overwrite, or delete its files, and it is never
a destination. Switching the source to managed is a separate conversion with its
own progress (`conversion_state`): the Engine's organising job (below) renames and
reorganises the files already there. The Engine owns `conversion_state` and
`conversion_operation_id`: a settings save keeps whatever the Engine has for a folder it
already knew, and saving a folder as As-is clears both.

Deleting a title whose file sits in an as-is folder removes the title from the library but never the file. The file stays where it is and becomes an **excluded file**: scans skip it, so the title does not come back, until someone adds it back in Settings > Libraries. If a different file later appears at the same place, it is a new file and is scanned.

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
  an As-is folder or another drive is not touched and a link that points back up the tree
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
  tidied gradually by normal scans"). Saving the folder as As-is cancels a running job
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
  so `AllowAsDestination` is true for a managed target only; an As-is or otherwise
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
  switched to As-is.
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
Any future permanent-file operation must separately prove ownership, source
containment, managed/writable policy, and a fresh explicit confirmation.

Deleting a personal file from disk is not built yet. The planned rule is that a personal file, such as a photo or a document in View, always goes to Trash first and leaves the disk only when Trash is emptied (Empty Trash), after its own confirmation. Trash today only hides items. Library media works differently: its files are deleted straight away after confirmation (see Managed source), and personal items cannot be deleted through Delete from library.

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
