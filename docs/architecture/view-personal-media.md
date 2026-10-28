---
title: "View Personal Media Architecture"
description: "Ownership, permissions, scopes, Galleries, queries, and extension boundaries for Tuvima View."
audience: "developer"
category: "architecture"
product_area: "view"
tags:
  - "view"
  - "personal-media"
  - "privacy"
  - "galleries"
status: current
---

# View Personal Media Architecture

## In this page

Ownership, permissions, scopes, Galleries, queries, and extension boundaries for Tuvima View.

## Where this lives in the code

- `src/MediaEngine.Contracts`
- `src/MediaEngine.Api/Endpoints`
- `src/MediaEngine.Storage`
- `src/MediaEngine.Web/Components/Pages/ViewPage.razor`

View is Tuvima's local-first home for personal photos, short videos,
documents, and audio. It is not a catalogue lane and never sends local-only or
manual media through retail metadata, canonical identity, Wikidata, or Review
Queue workflows.

The product presents one **Personal Space** per enabled profile. A Personal
Space may use several sources or devices, but those sources describe where
files came from rather than creating separate libraries. The system has one
managed View root. Each profile owns a directory beneath `Profiles` through a
collision-safe storage label that remains stable when a display name or login
changes. `Shared` is a separate household-owned originals tree and never
masquerades as a profile.

Managed folder import copies files beneath the profile's `Folders` directory and
preserves the original hierarchy and names. Browser uploads, Add files, phone backup and files copied
into the Import area all go through one filer (`ViewIntakeFiler`). Import and
Rejected areas sit outside every library folder; each file there has a
`view_intake_files` row that doubles as the filing journal. The filer waits for a
file to settle, checks it (readable, not empty, supported, storable name, room on
the drive), reads the capture date, skips an exact copy already in the destination
space, and files the file with its companions as
`Year/Year-Month-Day/original name` (the pattern is `config/photo_filing.json`).
Refused files wait in the Rejected area for 30 days. The duplicates job
(`view_duplicate_groups`) and Tidy this folder (`view_tidy_jobs` with a rename
ledger in `view_tidy_moves`) are durable and resume after a stop.
Administrators may link an external folder as an advanced read-only source;
the link is indexed in place and detaching it deletes only the source record,
not its files. Sources are persisted Personal Space provenance, not entries in
`libraries.json` and not separate libraries in View.

## Identity and ownership

Every personal asset resolves an owning profile, Personal Space/library,
source or device when known, and one or more physical files. Compound media
such as Live Photos and RAW/JPEG/XMP groups remain one logical asset.

Identical hashes do not merge logical ownership. Two profiles may independently
own the same bytes, retain distinct source paths, apply different flags, and
place the asset in different Galleries. Generated derivatives are cache entries
and never replace or modify originals.

## Permissions

Each profile has independent View capabilities:

- **View enabled** provisions and exposes its Personal Space.
- **Access Shared View** allows browsing the authorized shared aggregation.
- **Include in Shared View** allows eligible assets from its Personal Space to
  contribute to that aggregation.
- **Share Galleries** allows sharing Galleries with selected profiles.

Access and inclusion are deliberately independent. Administrators manage these
capabilities, but administrative configuration rights do not silently become a
normal profile's content-browsing rights.

The Engine derives the active profile from trusted request identity. A browser
supplied profile, library, Gallery, or asset identifier is only a requested
resource and never proof of access.

## View scopes

View supports three typed scopes:

- `Shared` resolves only accepted Shared Library assets when the active profile
  has Shared Library access.
- `Mine` resolves the active profile's Personal Space.
- `Profile(profileId)` resolves only the active profile; other profile IDs fall
  back without revealing whether that profile exists.

The scope resolver returns authorized internal Personal Space/library IDs. All
timeline, search, derivative, original, Gallery, People, Places, and Collection
queries consume this resolved scope rather than accepting arbitrary library ID
lists.

The last selected scope is stored server-side per profile. First use defaults to
Shared when permitted and Mine otherwise. A saved scope that becomes
unauthorized falls back to Mine.

Shared Library assets are server-owned records beneath `Shared`. Shared queries
filter on accepted membership instead of aggregating Personal Spaces, so the
original profile's other private assets remain unavailable.

## Asset states

Favorite, Hidden, Archive, and Trash are logical states. Archive removes an
asset from the normal Photos timeline. Trash is reversible: Restore puts the
asset back where it was, and the time it was trashed is kept. Moving an asset to
Trash never mutates an original file. An asset with any file in a linked
(read-only) folder cannot be moved to Trash at all, because those folders are for
looking at; its `can_trash` flag is false and the Dashboard hides the button.
Empty Trash is the only permanent removal of a personal file. It is owner-only,
runs from a computer, and removes everything in the owner's Trash after a fresh
confirmation of the exact summary. The safety rules are under Filesystem and
lifecycle safety below.

## Timeline and search

The main Photos timeline is date grouped and uses captured time when available,
then a stable import/creation fallback. It uses cursor pagination ordered by the
effective date plus stable asset ID. Deep `OFFSET` pagination and loading an
entire library for client-side grouping are not supported target behavior.

Metadata search uses the local FTS index for filenames, titles, dates, device,
place, tags, media kind, and indexed document text. The query boundary permits
future semantic results to be blended in, but semantic processing is never a
prerequisite for ordinary metadata search.

## Organising items: global tags, correspondents, types and fields

Tags (for every View item) and correspondents, document types and custom fields
(for documents) are one global list per install, kept in `tags`,
`doc_correspondents`, `doc_types` and `doc_field_definitions`, and linked to
items by `local_item_tag_links`, `doc_item_details` and `doc_field_values`. The
lists are global but never shown globally: `ViewCatalogueRepository` decides on
every read that a person sees a name only when an item that carries it is in a
library the request was approved for (the same library test the timeline query
uses) or they created it. Counts are the viewer's own, an id the viewer cannot
see is answered like an unknown id, and only the creator or an administrator may
rename or delete a name. Document text, notes and custom field values stay in
the View search document only and never reach Home, global Search or
Recommendations.

## Reading document text

A document is searchable by its words. Plain text files are read during the
scan. Every other readable format is read by a `view.document_text_read`
background operation (`DocumentTextReadOperationWorker`, `DocumentTextReadService`,
`DocumentTextReader`): PdfPig for a PDF's text layer and the Open XML SDK for
`.docx`, `.xlsx` and `.pptx`, within the `personal_library_policy.document_text_*`
limits. `ILocalAssetRepository.SetDocumentTextAsync` stores the words and
`text_source`, then rebuilds the item's search document. A PDF with pages that
have too little text of their own (`text_layer_min_characters` in
`config/ocr.json`) is then queued as `view.document_ocr`; a photo of paper is
queued by the scan.

`DocumentOcrOperationWorker` reads one file at a time and waits while
`IPlaybackActivityProbe` reports playback. `DocumentOcrService` draws each
low-text PDF page with PDFtoImage (PDFium) or prepares the photo, then
`OcrService` runs Tesseract as a child process (`OcrProcessRunner`, never
in-process) with an orientation retry for poor reads. Pages that already have
text are never re-read, a failed re-read keeps the earlier words, and
`text_source` becomes `ocr` or `mixed`. English data ships with the helper;
other languages are an explicit administrator download
(`OcrLanguageDownloader`, `POST /settings/ocr/languages/{code}`) stored under
`{DataRoot}/ocr/tessdata`, and that download is the only network use. After
either read, `DocumentRulesQueue` queues `view.document_apply_rules` so rules and
suggestions see the words. Document words are written only to the owner's
search document.

## Saved views, bulk editing and export

A saved Documents view is a Smart Gallery (`view_galleries`, `smart_rule_json`)
whose rule is one "all of these" group that includes `media_type` is `document`;
`ViewSmartGalleryRules.IsDocumentView` recognises it. The rule vocabulary gained
`tags` with `all` (every tag), `correspondent`, `document_type` (by id, which a
saved view stores so a rename never breaks it, or by name), `document_date`
(equals, comparisons, `between`, `known`/`unknown` and `within` a relative range
worked out when the rule is evaluated), `filing_state`, `text_contains` (words in
the document) and `field:<field id>` for one
custom field (equals, contains, a number or date range, known or unknown, judged
by the field's kind). All of it compiles in `LocalAssetSmartRuleSqlCompiler` with
values as parameters and the field id parsed as a GUID, so a rule can never
smuggle in a column or table name.

The Documents sort (`docSort`) orders by an expression the data store chooses
from a fixed list and pages by position; the cursor then carries an offset, not
a date boundary. The remembered sort lives in `profile_view_preferences`.

Bulk editing is `ViewDocumentBulkService` over `IDocumentBulkRepository`. The
service resolves every id (reachable, a document, in the Shared Library, in the
Inbox or the trash), asks the authorization service whether the person may manage
that document, and only then calls the data store, one transaction per document so
a failure never half-applies a change. The Shared Library is always refused (only
rules file there). Trash and restore reuse `SetLifecycleStateAsync` and mark
only; files are never deleted. A correspondent or type written (or confirmed) in
bulk records the person in `doc_item_field_sources`. A saved Documents view cannot
be shared, because its rule can hold search words. The export reads the same query as
the list through the query orchestrator (so access is identical), adds custom
field values for those rows only, and streams `ViewDocumentCsv`.

## Filing documents: Inbox, suggestions and rules

A new document gets a `doc_item_details` row in the `inbox` state when it is
first indexed. The `view.document_apply_rules` media operation (one per
document and text version, so a scan whose words are replaced by text
recognition runs again) loads the document and the rules that cover it
(`DocumentFilingRepository.GetApplicableRulesAsync`: the owner's rules for a
Personal Space item, a household administrator's rules for a Shared Library
item), plans them with the plain `DocumentRuleEngine` (all matching rules apply,
tags are united, the first rule to set a single value wins) and applies the plan
without ever replacing a value a person set (`doc_item_field_sources`). If the
document is still in the Inbox, `DocumentSuggester` stores suggestions from
filed documents of the same library only (rare shared words found with the
contents index, then a vote of the five most alike documents), plus the date
and amount found in the words. No AI, no network.

The Inbox list is the ordinary Documents list with `filing=inbox`; its count is
`GET /view/documents/inbox-summary`. Filing a document (`POST
/view/items/{id}/inbox/file`) is a person's action and records their values as
theirs. Every Inbox, rule and suggestion action checks the same Documents
permission as the document text route.

A document copied to the Shared Library keeps its tags, date, correspondent,
type, archive number, notes and custom field values as a filed document, all
referencing the copy through the same relations as the original.

## Galleries

A Gallery is a lightweight profile-owned personal-media container.

- A **Manual Gallery** stores unique asset memberships and may support explicit
  ordering and a selected cover.
- A **Smart Gallery** stores a typed rule definition and evaluates it against
  the viewer's authorized asset scope. It does not accept manual membership.

Gallery removal never removes an asset. Gallery deletion never deletes media.

While an asset is in Trash it leaves every Manual Gallery: it is missing from the
Gallery's items, counts and cover, and it cannot be chosen as a cover. Its place in
the Gallery is kept, so Restore brings it back. A Gallery whose cover is in Trash
reads as having no cover and falls back to its newest photo, as it does when the
cover is removed. Empty Trash removes the emptied items from every Manual Gallery
they were in. It never deletes a Gallery, so a Gallery left with nothing in it
stays. Smart Galleries and Collections simply stop showing the removed items.
Sharing is Private or selected-profile access; authorization is re-evaluated for
details, counts, cover previews, and every returned asset.

Smart Gallery and Collection rules use one versioned rule core with separate
field registries. Personal-media rule fields are capability-driven so future
processors can add people, objects, scenes, OCR, captions, or embeddings without
inventing a parallel rule format.

## Collections integration

Collections may reference an entire Manual or Smart Gallery, or contain their
own saved View rule definition. They do not manually contain individual local
asset IDs. Gallery references remain references rather than copied membership
rows, so later Gallery changes are reflected naturally.

Collection projections always reapply View authorization. Private thumbnails,
filenames, counts, people, and locations must not leak through Collection
summaries or previews.

## View surfaces

Ordinary View routes use the shared section shell and primary
destinations:

- `/view` — Photos
- `/view/galleries` — Galleries
- `/view/people` — People
- `/view/places` — Places

Shared View, Favorites, Videos, Archive, Recently Added, Hidden, and Trash are
scopes or filters in the content area. Source configuration, devices, uploads,
backup, duplicates, storage, AI configuration, and View administration remain
in Settings. There is no View Folders page: each View kind (Photos, Personal
videos, Documents) has a library page at Settings > Libraries > View with the
same Folders, Access and File handling tabs as any library, and each source
folder there has one Show in Photos timeline switch (`view_source_policies`).
The switch belongs to the whole source folder, so a folder with it off keeps all
its photos, subfolders included, out of the Photos timeline; there are no pinned
folders and no per-branch rules. Immersive
asset, Gallery, person, and place details may intentionally
omit the section rail.

## Shared Library contributions

Selecting owned items in Mine can preview a batch for Shared Timeline or a named
Shared folder. The preview states whether managed originals will eventually
move or linked read-only originals will copy, together with group count, bytes,
and destination directories. Submitting creates a pending, revision-bound
record and does not change files. Contributors can cancel pending work.

A household administrator of the contribution's household (or a server administrator) can
accept or decline only the explicitly submitted items. There is no per-profile curator
flag; reviewing follows household-administrator authority and the administrator PIN. A decline is non-mutating. Acceptance records the decision,
queues transfer work, and exposes per-item progress plus an append-only activity
record at `/view/contributions/{id}`. The list and detail routes retain the
`View > Shared Library > Contributions` breadcrumb. A hosted worker resumes
accepted recoverable work after restart.

The Engine persists source and destination manifests before copying. It copies
to excluded staging, verifies byte count and SHA-256, finalizes without
overwrite, and publishes Shared Library membership only after every group member is
usable. Managed-source cleanup occurs after publication and re-verifies both
copies immediately before deletion. A failed deletion stays `cleanup_pending`
and retry uses the same manifest; the Engine never deletes the last verified
copy. Named Shared folders preserve original names. Shared Timeline uses the
same readable calendar convention as personal uploads.

## People, Places, and AI

Places works from extracted GPS metadata and does not require AI. Map rendering
must have an accessible list fallback. The Places client first attempts
OpenFreeMap's dark map style (`https://tiles.openfreemap.org/styles/dark`), an
explicit external request that can load remote map resources; if it fails, the
map falls back to locally served country data (`/maps/world-countries.geojson`).
See [privacy and network access](../explanation/privacy-local-first.md).

People consumes provenance-aware local annotations when a real processor has
produced them. When processing is unavailable or incomplete, the UI presents a
truthful capability state. Face recognition, semantic search, OCR, object
detection, captions, and embeddings are future integrations (text recognition for documents and photos of paper is implemented, see Reading document text). If implemented,
they must run asynchronously through the existing Local AI architecture and
must never block ingestion or asset availability.

Current implementation truth: the Engine and Dashboard expose authorized Places
and People queries. Places aggregates only real GPS/place metadata and renders a
map (OpenFreeMap tiles when reachable, otherwise locally served country data)
plus an accessible list. People returns only named or reviewed provenance-aware
annotations and presents a truthful capability state when no capable producer
has created them. Face recognition, object/scene detection, captions,
embeddings, semantic search, mobile backup/sync and public-link sharing are not
implemented features.

## Scale and safety

View queries are designed for at least hundreds of thousands of assets. Common
filters use indexed owner/space, effective date, media kind, flags, location,
hash, Gallery membership, tag, and annotation fields. Query-plan checks and
large seeded datasets are part of release verification.

Existing and read-only sources remain immutable. Personal-media work must not
weaken path containment or source mutation policy. The pre-beta reset rule
allows obsolete application database and configuration state to be rebuilt; it
does not authorize modifying or deleting user-owned originals.

## Static implementation and release review

This section is the review checklist for the current foundation. It records
the boundary that static inspection can establish and the obligations that
still require runtime and visual evidence before release.

### Authorization boundary

- The Dashboard signs eligible `/view` and `/collections` Engine requests with
  profile, timestamp, and HMAC signature headers. The signature binds the exact
  HTTP method, path, and query, and the Engine enforces a short clock-skew
  window. A query-string profile ID is not authentication.
- Scope resolution turns Shared, Mine, or permitted Profile selections into
  authorized Personal Space/library IDs. Asset lists, search, content,
  thumbnails, Galleries, People, Places, and personal-media Collection sources
  must consume that result instead of caller-provided library arrays.
- Resource authorization is repeated for direct identifiers. Missing and
  unauthorized View resources share the same not-found behavior so IDs cannot
  be enumerated. Owned-item mutations do not become available merely because a
  caller can read Shared View.
- A selected-profile Gallery share authorizes that Gallery only. It does not
  grant Shared View, another Gallery, or arbitrary access to the owner's
  Personal Space. Collection projections reauthorize their Gallery or rule
  source and remain count-free when the viewer cannot resolve it.
- Browser media uses a short-lived, profile/library/asset/resource-bound grant
  on the Dashboard's same origin. The proxy supports range requests while
  keeping Engine API keys and trusted-profile assertions out of browser URLs.

### Filesystem and lifecycle safety

- Logical deduplication retains every physical source path; identical hashes do
  not collapse profiles or Personal Spaces into shared ownership.
- Favorite, Hidden, Archive, Trash, Restore, Gallery membership, and Gallery
  deletion modify SQLite state only. They do not mutate originals.
- **Empty Trash** is the only operation that deletes personal files, and it only
  ever empties the whole Trash of the signed-in person's own Personal Space. It is
  refused for everyone else (Shared View and Gallery-share viewers, administrators,
  paired apps and service applications) and for anything but a signed-in person at
  the Dashboard. Nothing is deleted until the person confirms a summary the Engine
  produced (items by kind, space freed, Galleries affected, items that stay and
  why). The request must send back that summary's fingerprint; if the Trash has
  changed, the Engine answers 409 with the new summary and deletes nothing.
- Before anything is removed, every file is checked again. Its path is followed
  through every link and the real file must lie inside the owner's own profile
  folder in the managed View root, and the shared source-file safety gate
  (`SourceMutationPolicyGate`) must allow the delete. Linked (read-only) sources are
  never deleted. A file that another item still uses on disk is kept, unless that item
  is removed too; the same content at a different path does not count as sharing.
- **All or nothing per item.** An item's files go together (a Live Photo's video,
  RAW, JPEG and XMP, an original and its edited version). They are first moved into
  a hidden `.tuvima-staging` folder in the owner's profile folder, with a manifest.
  Only after the item's records are removed in one transaction are the parked files
  deleted, followed by the item's thumbnails and previews. If anything fails first,
  every file is moved back and the item stays in Trash with the reason. A recovery
  sweep at startup and after each Empty Trash puts parked files back for an item
  that still exists, and deletes them for one that is gone. Scans and the folder
  watcher ignore the staging folder. Items that share a file on disk are removed as
  one group, so none is left in Trash with its file already gone.
- **Items that stay in Trash.** An item is skipped, with a reason, when a file is in
  a linked read-only folder; when it is waiting to be added to the Shared Library or
  is being copied there (that work protects the item, and an item already added keeps
  its contribution history, which no longer links to it); or when the safety gate or
  the folder check refuses a file. Shared Library items never appear in a Trash and
  cannot be moved to Trash.
- **Records.** Removing an item deletes its `local_items` row in one transaction. The
  cascades clear its metadata, file links, tags, document fields, Gallery memberships,
  annotations, search entries and phone download entries; its `local_file_sources`
  rows for removed files go too, and so does any file record left with no link.
  Thumbnails and previews are deleted from the derived cache. Galleries are never
  removed.
- **Activity.** Each Empty Trash that removes something writes one activity entry for
  the owner: the item count, the space freed and the number skipped. It holds no file
  names, titles or thumbnails.
- Existing/read-only sources remain immutable. Shared Library contribution copies them and
  retains the external original. Managed promotion requires an owned item,
  verifies destination containment beneath Shared, and requires an explicit
  consequence preview. The development database reset policy is not file authority.
- Derivatives and thumbnails are replaceable cache artifacts. They must never
  overwrite an original or become the only recorded copy of user media.

### Query and scale review

- Timeline paging is keyset/cursor based on effective capture/import time plus
  stable item ID. Gallery membership uses position plus item ID. Deep `OFFSET`
  paging and per-item follow-up reads are outside the supported design.
- SQLite indexes cover owner/space/library timeline scans, media kind,
  favorite/lifecycle filters, source/device provenance, hashes, tags, GPS,
  Gallery ordering/shares, Collection View sources, and annotation lookup.
  Metadata search uses `local_item_search` FTS with a GUID-to-rowid mapping.
- Release verification must include `EXPLAIN QUERY PLAN` checks and a seeded
  large-library scenario. Index presence alone is not proof that a changed
  predicate or sort order remains efficient.

### Responsive and accessibility obligations

- The standard View shell exposes primary links—Photos, Galleries, People,
  and Places (plus Documents for profiles that may use them)—with one
  current-page state. Shared View,
  Favorites, Videos, Archive, Hidden, Trash, and Recently Added remain content
  scopes/filters, not extra rail destinations.
- Keyboard users must be able to reach scope, search, filters, asset selection,
  Gallery actions, and the viewer; focus must remain visible, bulk selection
  must announce count/state, and dialogs/viewers must trap and restore focus.
- Date groups, asset buttons, Gallery cards, and people/place results need
  semantic names that do not depend on thumbnails, color, hover, or map markers.
  Places must always retain an accessible list alternative.
- Before release, capture and inspect Photos, Galleries, People, and Places at
  1920×1080, the normal 1440-pixel desktop width, a tablet width, and a narrow
  mobile width. Verify empty/loading/error states, long names, zoom/text scaling,
  overflow, selection toolbars, dialogs, and reduced-motion behavior. Static
  review does not substitute for this visual and assistive-technology pass.
