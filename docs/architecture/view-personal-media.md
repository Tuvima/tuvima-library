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
preserves the original hierarchy and names. Browser uploads use a built-in
managed Timeline source and organize new files by year, month, capture date,
time, and media-kind suffix.
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
asset from the normal Photos timeline. Trash is recoverable and records when the
asset was trashed. Neither operation mutates an original file. Permanent file
deletion is a separate explicit operation governed by source containment,
management mode, access mode, and filesystem mutation policy.

## Timeline and search

The main Photos timeline is date grouped and uses captured time when available,
then a stable import/creation fallback. It uses cursor pagination ordered by the
effective date plus stable asset ID. Deep `OFFSET` pagination and loading an
entire library for client-side grouping are not supported target behavior.

Metadata search uses the local FTS index for filenames, titles, dates, device,
place, tags, media kind, and indexed document text. The query boundary permits
future semantic results to be blended in, but semantic processing is never a
prerequisite for ordinary metadata search.

## Galleries

A Gallery is a lightweight profile-owned personal-media container.

- A **Manual Gallery** stores unique asset memberships and may support explicit
  ordering and a selected cover.
- A **Smart Gallery** stores a typed rule definition and evaluates it against
  the viewer's authorized asset scope. It does not accept manual membership.

Gallery removal never removes an asset. Gallery deletion never deletes media.
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

Ordinary View routes use the shared section shell and five primary
destinations:

- `/view` — Photos
- `/view/folders` — Folders
- `/view/galleries` — Galleries
- `/view/people` — People
- `/view/places` — Places

Shared View, Favorites, Videos, Archive, Recently Added, Hidden, and Trash are
scopes or filters in the content area. Source configuration, devices, uploads,
backup, duplicates, storage, AI configuration, and View administration remain
in Settings. Folders is the authorized browsing projection of configured
sources and Shared keeper files. Profiles can pin authorized folders as private
shortcuts. A source owner can set a Photos inclusion rule on any branch;
descendants inherit the nearest rule before falling back to the source default.
It keeps source and nested path in the URL and renders a breadcrumb from Folders
through every directory segment. Immersive
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
must have an accessible list fallback and must not make an unconfigured
third-party tile request.

People consumes provenance-aware local annotations when a real processor has
produced them. When processing is unavailable or incomplete, the UI presents a
truthful capability state. Face recognition, semantic search, OCR, object
detection, captions, and embeddings are future integrations. If implemented,
they must run asynchronously through the existing Local AI architecture and
must never block ingestion or asset availability.

Current implementation truth: the Engine and Dashboard expose authorized Places
and People queries. Places aggregates only real GPS/place metadata and renders a
local, privacy-safe coordinate cluster plus an accessible list; no third-party
map tiles are loaded. People returns only named or reviewed provenance-aware
annotations and presents a truthful capability state when no capable producer
has created them. Face recognition, object/scene detection, OCR, captions,
embeddings, semantic search, mobile backup/sync, public-link sharing, and
third-party map rendering are not implemented features.

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

- The standard View shell exposes five primary links—Photos, Folders,
  Galleries, People, and Places—with one current-page state. Shared View,
  Favorites, Videos, Archive, Hidden, Trash, and Recently Added remain content
  scopes/filters, not extra rail destinations.
- Keyboard users must be able to reach scope, search, filters, asset selection,
  Gallery actions, and the viewer; focus must remain visible, bulk selection
  must announce count/state, and dialogs/viewers must trap and restore focus.
- Date groups, asset buttons, Gallery cards, and people/place results need
  semantic names that do not depend on thumbnails, color, hover, or map markers.
  Places must always retain an accessible list alternative.
- Before release, capture and inspect Photos, Folders, Galleries, People, and Places at
  1920×1080, the normal 1440-pixel desktop width, a tablet width, and a narrow
  mobile width. Verify empty/loading/error states, long names, zoom/text scaling,
  overflow, selection toolbars, dialogs, and reduced-motion behavior. Static
  review does not substitute for this visual and assistive-technology pass.
