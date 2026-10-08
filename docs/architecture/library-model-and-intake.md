---
title: "Libraries, Sources, and Intake"
description: "Target architecture for catalogued libraries, View Personal Spaces, universal intake, and folder safety."
audience: "developer"
category: "architecture"
product_area: "ingestion"
---
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
role, management mode, recursion behavior, filesystem access, writeback
override, organization participation, intake role, health, and optional device
or profile association.

One catalogued managed source may be the explicit primary destination. Array order never
selects a destination.

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

### Existing library source

An existing-library source may be scanned, identified, and indexed, but Tuvima
must not move, rename, tag, overwrite, or delete its files. Changing the source
to managed changes policy only; it does not mutate existing files.

### Managed source

A managed source may accept new files and participate in organization when the
source is writable and both library and source policy permit the operation.
Organization and embedded metadata writeback remain separate permissions.

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
