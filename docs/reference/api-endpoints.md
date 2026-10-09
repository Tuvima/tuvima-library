---
title: "Engine API Reference"
description: "Look up the Engine's HTTP routes, authentication rules, and endpoint responsibilities."
audience: "developer"
category: "reference"
product_area: "api"
tags:
  - "http"
  - "api"
  - "endpoints"
status: current
---

# Engine API Reference

## In this page

Look up the Engine's HTTP routes, authentication rules, and endpoint responsibilities.

## Where this lives in the code

- `src/MediaEngine.Api/Endpoints`
- `src/MediaEngine.Web`
- `src/MediaEngine.Contracts`

Public client base URL: the Dashboard origin plus `/api/v1` (for local development, `http://localhost:5016/api/v1`).

The Engine origin (`http://localhost:61495` in development) is an internal service boundary. Native clients must discover and call the Dashboard origin and never receive the Dashboard service credential or connect to the Engine port.

Interactive documentation: `http://localhost:61495/swagger`

Administrative Engine endpoints require live effective administrator authority (with any configured surface unlock), or the exact registered Application permission where supported. Public v1 client endpoints use scoped bearer tokens issued to a paired device. Every route also enforces its resource/profile scope; the Auth column below is a summary, not a substitute for the mapped endpoint policies. See the [Access implementation status](../../engineering/plans/access-architecture-2026-09-08/execution/status.md) for final cutover gates.

## Public client API v1

API v1 is the frozen native-client contract. Breaking request or response changes require a new major path. Additive fields may be introduced in v1; clients must ignore fields they do not understand.

### Discovery and pairing

1. `GET /.well-known/tuvima` on the Dashboard origin returns the public API base, supported versions, device-authorization and token endpoints, verification URL, and server capabilities.
2. A television or other input-constrained client posts form data or JSON to `POST /api/v1/oauth/device_authorization` with a stable public `client_id`, display names, version, device class, requested space-delimited scopes, and capabilities.
3. The user opens the returned `verification_uri` on an authenticated browser, reviews the exact client/device/scopes, and approves or denies it. Device codes expire after 10 minutes and polling starts at the returned interval.
4. The device polls `POST /api/v1/oauth/token` using grant type `urn:ietf:params:oauth:grant-type:device_code`. `authorization_pending`, `slow_down`, `access_denied`, and `expired_token` are normal OAuth error responses.
5. Access tokens last 10 minutes. Refresh tokens rotate on every successful `refresh_token` grant and expire after 30 days. Reuse of a consumed refresh token revokes its token family.

Tokens are opaque bearer credentials and must be stored in the platform secure credential store. Device and token identifiers in request bodies or query strings are never authority; the Engine uses the profile, device class, client, and scopes bound to the bearer token. `GET /api/v1/devices` lists grants for the active profile, `GET /api/v1/devices/current` describes the caller, `PUT /api/v1/devices/current/capabilities` replaces negotiated capabilities, and `DELETE /api/v1/devices/{deviceId}` immediately revokes that device and all of its tokens.

### Scopes and resources

| Resource | Primary routes | Required scope |
|---|---|---|
| Discovery | `GET /.well-known/tuvima` | None |
| Browse/home | `GET /api/v1/display/home`, `/browse`, `/shelves/{key}`, `/groups/{id}` | `library.read` |
| Search | `GET /api/v1/display/search?q=` | `library.read` |
| Details | `GET /api/v1/details/{entityType}/{id}` | `library.read` |
| Artwork/media bytes | URLs returned by display/detail/manifest models; `/api/v1/stream/{assetId}` supports ranges and signed `/api/v1/stream/hls/...` paths serve adaptive packages | `artwork.read` or `playback.read` as applicable |
| Progress | `GET/PUT /api/v1/progress/{assetId}`, `GET /recent`, `GET /journey` | `progress.read` / `progress.write` |
| Queues | `GET /api/v1/player/state`, mutations under `/api/v1/player/queue` | `queue.read` / `queue.write` |
| Playback session | `/api/v1/player/command`, `/heartbeat`, `/session/takeover` | `playback.write` or `progress.write` |
| Playback decision | `GET /api/v1/playback/{assetId}/manifest` | `playback.read` |
| Downloads | encode/offline routes under `/api/v1/playback` | `downloads.read` / `downloads.write` |

Capability negotiation declares containers, video/audio codecs, subtitle formats, protocols, maximum dimensions/bitrate/channels, HDR, speed, and offline-download support. The playback manifest compares those registered capabilities with inspected source media and returns `directPlaySupported`, `recommendedDelivery`, source/alternate URLs, tracks, chapters, resume state, warnings, and a conversion reason. For HLS it also returns `hlsStatus`, `hlsUrl`, and `hlsExpiresAt`. `preparing` means the client should retry the manifest; `ready` supplies the playable path. Clients must follow the manifest rather than independently guessing whether a file can direct-play.

The HLS URL is a bearer capability scoped to one asset, package, and expiration time. It is intentionally usable by native HLS media stacks that cannot attach application authorization headers to every segment request. Clients must not log, persist, or share it. Expired or modified paths return `403`; missing and traversal-rejected package resources return `404`. Relative URIs in the master and media playlists preserve the same signed path for variant, alternate-audio, caption, and segment requests.

Offline encode jobs created by paired clients are owned by the authenticated
profile and device. Job listing and cancellation use those trusted token claims;
one client cannot enumerate, cancel, discover, or download another device's
work. Public JSON never includes the Engine's local output path. Clients
download only the API v1 URL exposed by their ready offline-variant model.

Most paginated GET endpoints accept `offset`/`limit` query parameters. View
timeline and discovery endpoints use opaque/keyset `cursor` plus `limit`, and
Manual Gallery membership uses position/item keysets. Limits are clamped
server-side so a caller cannot force an unbounded read.

JSON errors use RFC 7807 Problem Details through the shared `ApiErrors`
factories. JSON success bodies use named `MediaEngine.Contracts` types, and
every production route declares its 2xx response metadata for Swagger. Untyped
success metadata is reserved for reviewed bodyless, redirect, file, stream, and
direct-response routes.

`MediaEngine.Contracts` is the sole owner of non-frozen HTTP and SignalR wire
types. Engine endpoints map internal projections explicitly into those
contracts, and the Dashboard may add presentation wrappers without redefining
the JSON payload. The only frozen exception is the exact Universe/Chronicle
SignalR pair `LoreDeltaDiscoveredEvent` and
`UniverseEnrichmentProgressEvent`; Universe HTTP responses are not exempt.

The canonical contracts intentionally include fields that existed on only one
side of the former API/Dashboard mirror boundary. Notable examples are
ingestion `count_unit`; person and person-backed collection `roles`; complete AI
settings plus resource `cpu_pressure`; complete managed-collection, placement,
rule, permission, and artwork fields; playback manifest, player, settings,
chapter, bookmark, text-track, and offline-delivery fields; and plugin
manifest, capability, permission, tool, AI-permission, health, job, and approved
catalog fields. These are contract completeness corrections, not separate
compatibility versions.

---

## System

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/system/status` | Service health, version, uptime | None |
| GET | `/system/activity-status` | Sanitized leased/running durable operations used to seed the Dashboard navbar activity indicator; excludes media titles and filesystem paths | Required |
| GET | `/system/watcher-status` | File watcher diagnostic - shows monitored folders and last event | Required |
| POST | `/maintenance/sweep-orphan-assets` | Scan `.data/assets/` for managed files with no database reference and remove them. | Administrator |
| POST | `/maintenance/storage/run` | Run storage maintenance on demand. Supports `dryRun`, cache retention, image retention, and claim compaction batch query parameters. | Administrator |

---

## Library

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/library/works` | All works with their canonical values | Required |

## Media Management

Library configuration uses schema `4.0`, including explicit server-visible folder-picker roots. Older flat-folder and Photos/General
schemas are rejected during the pre-beta cutover rather than migrated or served
through compatibility routes.

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/settings/libraries` | Return the complete library configuration, including stable sources and universal incoming sources | Administrator |
| PUT | `/settings/libraries` | Validate and replace the complete schema-3 library configuration | Administrator |
| GET | `/settings/incoming-sources` | Return universal unassigned intake folders | Administrator |
| PUT | `/settings/incoming-sources` | Validate and replace universal incoming folders | Administrator |
| POST | `/settings/libraries/{libraryId}/reorganization/plan` | Produce a read-only, expiring move/rename plan with stable source IDs, conflicts, blocked work, and an exact confirmation fingerprint | Administrator |
| POST | `/settings/libraries/{libraryId}/reorganization/execute` | Execute one confirmed plan once, revalidating source policy, files, collisions, and free space immediately before each operation | Administrator |

Reorganization execution requires both the returned `plan_id` and its exact
`fingerprint`. A plan expires after 15 minutes and cannot be reused. Partial
outcomes are reported per item; an existing-library source is always blocked
from mutation.

## View

Personal View routes require the trusted profile assertion established by the
Dashboard. The server-to-server `X-Tuvima-View-Profile`,
`X-Tuvima-View-Timestamp`, and `X-Tuvima-View-Signature` headers bind the active
profile to the exact method/path/query through HMAC-SHA256 using the already
validated API key. They are accepted only for `/view` and `/collections`, must
be within the configured short skew window, and are not browser credentials.

Browser-supplied profile, library, Gallery, scope-profile, or asset IDs are
never treated as authority. Friendly Shared, Mine, and permitted Profile scopes
are resolved by the Engine before any asset, Gallery, People, Places,
thumbnail, original-file, or personal-media Collection query runs. Direct
unauthorized identifiers return the same not-found shape as missing resources.

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/view/scopes` | Resolve the selected/default scope and return only authorized labeled scope options | Required + trusted profile |
| GET, PUT | `/view/preferences` | Read or persist the active profile's last scope and timeline density | Required + trusted profile |
| GET | `/view/assets` | Cursor-page authorized mixed local assets by scope, search, kind, state, or Gallery | Required + scope read |
| GET | `/view/folders` | Page authorized source roots or one indexed folder hierarchy with URL-backed path breadcrumb, scoped search, and optional descendants | Required + scope read |
| PUT | `/view/folders/pin` | Pin or unpin an authorized folder for the active profile | Required + scope read |
| PUT | `/view/folders/timeline-policy` | Set or clear the source-owner branch override inherited by the Photos timeline | Source owner or administrator |
| POST | `/view/uploads` | Upload to the caller's server-resolved Personal Space | Required + Personal Space owner |
| GET | `/view/items/{id}` | Read authorized personal-asset metadata | Required + asset read |
| GET | `/view/items/{id}/content` | Stream an authorized original with range support | Required + asset read |
| GET | `/view/items/{id}/thumbnail` | Return a bounded local image preview when supported | Required + asset read |
| PUT | `/view/items/{id}/favorite` | Set favorite state on an owned asset | Required + asset owner |
| PUT | `/view/items/{id}/hidden` | Set hidden state on an owned asset | Required + asset owner |
| POST | `/view/items/{id}/archive` | Archive an owned asset without changing its file | Required + asset owner |
| POST | `/view/items/{id}/trash` | Soft-delete an owned asset without deleting its original | Required + asset owner |
| POST | `/view/items/{id}/restore` | Restore an archived or trashed owned asset | Required + asset owner |
| POST | `/view/shared/contributions/preview` | Preview a selected batch, compound-file totals, destination, and eventual move/copy behavior | Shared submit grant + asset owner |
| POST | `/view/shared/contributions` | Create an idempotent, revision-bound pending contribution without changing files | Shared submit grant + asset owner |
| GET | `/view/shared/contributions` | Page the caller's submissions or the curator review queue | Required; curator grant for review mode |
| GET | `/view/shared/contributions/{id}` | Read an authorized contribution, item states, decision, and activity timeline | Contributor or Shared curator |
| POST | `/view/shared/contributions/{id}/cancel` | Cancel a caller-owned pending contribution with optimistic revision checking | Contributor |
| POST | `/view/shared/contributions/{id}/decision` | Accept or decline a pending contribution and queue accepted transfers | Shared curator |
| POST | `/view/shared/contributions/{id}/retry` | Requeue recoverable accepted item transfers | Shared curator |
| POST | `/view/shared/items/direct` | Create an auto-accepted contribution for curator-owned items using the same transfer worker | Shared curator + asset owner |
| GET, POST | `/view/galleries` | List owned/shared Galleries or create an owned Gallery | Required + trusted profile |
| GET, PUT, DELETE | `/view/galleries/{id}` | Read or manage an authorized Gallery | Required + Gallery permission |
| GET, POST, DELETE | `/view/galleries/{id}/items` | Page or mutate Manual Gallery membership | Required + Gallery permission |
| GET, PUT | `/view/galleries/{id}/shares` | Read or replace selected-profile Gallery shares | Owner + Gallery sharing enabled |
| GET | `/view/share-targets` | Return only enabled profiles the caller may select as Gallery share recipients | Required + trusted profile |
| GET | `/view/people` | Cursor-page named/reviewed, provenance-aware people annotations within an authorized scope; no face-recognition claim | Required + scope read |
| GET | `/view/places` | Cursor-page real GPS/place aggregates within an authorized scope; no map/tile request | Required + scope read |
| GET | `/view/admin/profiles/{profileId}/sources` | Return an administrator-safe source/device summary without paths, secrets, client identifiers, or invented capacity | Administrator |
| POST | `/view/admin/profiles/{profileId}/reconcile` | Recovery/admin reconciliation for a profile's Personal Space | Administrator |

## Works

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/works/{id}` | Work detail with canonical values, editions, and owned assets | Required |
| GET | `/works/{id}/editions` | Editions and owned assets for a work | Required |
| GET | `/works/{id}/cast` | Actor and character credits for a single work | Required |

---

## Collections

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/collections` | Browse-oriented collection list with works and canonical metadata | Required |
| GET | `/collections/catalog` | Collections catalog: system/user/managed collections plus broader rollups where trusted relationships connect multiple shelves | Required |
| GET | `/collections/{id}/summary` | One Collections summary for a detail page without loading the full catalog | Required |
| GET | `/collections/{id}/items` | Items for a collection detail page, including generated rollup aggregation | Required |
| POST | `/collections/reconcile` | Dry-run or run collection shelf repair for already-ingested media. Body: `dry_run`, `batch_size`, `max_items`. Returns candidate, processed, assigned, skipped, failed, and elapsed counts. | Effective administrator or precise Application permission |
| GET | `/collections/{collectionId}/series-manifest` | Ordered Wikidata series checklist with total, owned, missing, provisional, ambiguous counts and named entries | Required |
| GET | `/collections/search?q=` | SQL-backed search across visible library works, canonical values, and collection names. Returns up to 20 work results. | Required |
| GET | `/collections/personal-media/galleries` | List Galleries eligible for the administrator's Collection editor without exposing individual assets | Administrator + trusted profile |
| GET | `/collections/{id}/personal-media` | Return count-free Gallery/rule sources after reapplying the viewer's View authorization | Required + trusted profile |
| POST, PUT, DELETE | `/collections/{id}/personal-media[/{sourceId}]` | Manage whole-Gallery references or a versioned View rule; individual asset identifiers are rejected | Administrator + trusted profile |
| GET | `/persons?catalog=true&q=&role=&lane=&sort=&offset=&limit=` | Paged canonical primary contributors on owned works for Collections / People | Required |
| GET | `/persons/role-counts?catalog=true` | Canonical owned-library contributor counts grouped by role | Required |

---

## Library Item

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/library/items` | Paginated item list for current browse/detail surfaces. Includes projection-backed fields such as `pipelineStep`, `libraryVisibility`, `isReadyForLibrary`, `artworkState`, `artworkSource`, and `artworkSettledAt`. Supports filtering by status, media type, collection, and search term. | Required |
| GET | `/library/items/{entityId}/detail` | Full item detail including claims, canonical values, pipeline projection fields, artwork truth, and linked persons | Required |
| GET | `/library/items/{entityId}/editor-preferences/{profileId}` | Profile-owned notes, local tags, hidden/recommendation flags, and optimistic editor revision | Required |
| PUT | `/library/items/{entityId}/editor-preferences/{profileId}` | Atomically save supported display overrides and profile-owned editor preferences; returns `409` for a stale revision | Effective administrator or precise Application permission |
| GET | `/library/items/counts` | Status counts for tab badges and compatibility counters such as review, auto-approved, duplicate, staging, and missing-image counts | Required |
| GET | `/library/items/state-counts?batchId=` | Four-state counts scoped to a specific ingestion batch | Required |

---

## Library Overview

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/library/overview` | Aggregated library health and readiness view, including `hidden_by_quality_gate`, `art_pending`, `retail_needs_review`, `qid_no_match`, and `completed_with_art` | Required |

---

## Metadata

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/metadata/claims/{entityId}` | All claims for an entity, grouped by field, with source and confidence | Required |
| GET | `/metadata/conflicts` | All unresolved metadata conflicts across the library | Effective administrator or precise Application permission |
| GET | `/metadata/{entityId}/canon-discrepancies` | Field-level mismatches between the canonical value and file-embedded metadata | Required |
| GET | `/metadata/{entityId}/artwork` | Artwork context for the media editor, including variants by artwork type and preferred selections | Effective administrator or precise Application permission |
| GET | `/metadata/{entityId}/artwork/{scopeId}` | Artwork variants for a specific editor scope | Effective administrator or precise Application permission |
| POST | `/metadata/{entityId}/artwork/{scopeId}/{assetType}` | Upload a user-owned artwork variant for the selected type | Effective administrator or precise Application permission |
| POST | `/metadata/{entityId}/artwork/{scopeId}/{assetType}/from-url` | Add an artwork variant from a provider or user-supplied image URL | Effective administrator or precise Application permission |
| POST | `/metadata/{entityId}/artwork/{assetType}` | Compatibility upload route for an artwork type | Effective administrator or precise Application permission |
| PUT | `/metadata/artwork/{variantId}/preferred` | Make an artwork variant the preferred image for its artwork type | Effective administrator or precise Application permission |
| DELETE | `/metadata/artwork/{variantId}` | Remove an artwork variant from the item. Shared provider/image cache files are retained when still referenced elsewhere. | Effective administrator or precise Application permission |

---

## Streaming

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/stream/{assetId}` | Stream media file. Supports HTTP 206 byte-range requests for seeking. | Required |
| GET | `/stream/{assetId}/cover` | Full-size cover art (JPEG) | Required |
| GET | `/stream/{assetId}/cover-thumb` | 200px-wide thumbnail (JPEG, quality 75, SkiaSharp-generated) | Required |

---

## Ingestion

| Method | Path | Description | Auth |
|---|---|---|---|
| POST | `/ingestion/scan` | Dry-run scan of configured library folders. Reports what would be ingested without making changes. | Administrator |
| POST | `/ingestion/library-scan` | Scan library folders and update known file paths. Triggers ingestion for new files. | Administrator |
| POST | `/ingestion/upload` | Upload a file to the explicit managed, writable primary destination identified by multipart `destinationLibraryId`; catalogued files retain stable intake IDs, while personal files are indexed directly into View without catalogue/provider work | Administrator |
| GET | `/ingestion/operations` | Dashboard snapshot backed by durable ingestion operation counts, numbered `stage_progress` rows, current activity, review state, provider health, and recent batch summaries. | Effective administrator or precise Application permission |
| GET | `/ingestion/batches` | Recent ingestion batches. | Effective administrator or precise Application permission |
| GET | `/ingestion/batches/{batchId}` | Single ingestion batch summary. | Effective administrator or precise Application permission |
| GET | `/ingestion/batches/{batchId}/items` | Durable per-file item ledger for a batch, sourced from `media_operations`. | Effective administrator or precise Application permission |
| GET | `/ingestion/watch-folder` | Returns the derived current watch folder view from configured library source folders. | Required |

`/ingestion/operations.stage_progress` contains numbered ingestion stage rows with `stage_number`, `stage_key`, `label`, `completed_files`, `total_files`, `count_unit`, `percent_complete`, `active_count`, `queued_count`, `status_label`, `active_item_label`, `active_group_label`, `active_group_count`, `label_accuracy`, `artifact_label`, `artifact_count`, `detail_items`, `last_updated_time`, and `is_stale`. `count_unit` identifies what the counts measure, such as files, albums, people, artwork assets, or links, so clients do not relabel grouped work as files. `detail_items` is an optional list of `{ label, value, tone?, icon? }` rows populated by the Engine so clients do not hardcode provider math. Grouped provider work, such as batched Wikidata resolution, uses group labels instead of fake exact file labels unless per-file correlation is available.

The Dashboard renders Stages 1-8 as compact progress rows. Review/attention state remains in the snapshot for API consumers, but the Dashboard surfaces it through the top **Need Review** metric and the `recent_batches` review count rather than as another progress bar. `recent_batches` rows include batch id, status, timing, file totals, media-type counts, registered/review/failed counts, and artifact totals such as people, artwork, metadata, matched, and review.

---

## Operations And Capabilities

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/operations` | Durable work queue ordered by queue priority and position. Supports `queueName` and `limit` (default 200, capped at 250). | Effective administrator or precise Application permission |
| GET | `/operations/{id}` | One operation plus its event timeline. | Effective administrator or precise Application permission |
| GET | `/operations/summary` | Counts by durable operation status. | Effective administrator or precise Application permission |
| POST | `/operations/{id}/retry` | Requeue a durable operation for another attempt. | Effective administrator or precise Application permission |
| POST | `/operations/{id}/cancel` | Cancel a durable operation. | Effective administrator or precise Application permission |
| GET | `/assets/{id}/capabilities` | Explicit capability/readiness states for one media asset. | Effective administrator or precise Application permission |
| GET | `/capabilities/summary` | Counts by capability and status. | Effective administrator or precise Application permission |

---

## Search

| Method | Path | Description | Auth |
|---|---|---|---|
| POST | `/search/universe` | Search Wikidata for entity candidates by title, author, and media type | Effective administrator or precise Application permission |
| POST | `/search/retail` | Search configured retail providers for matching candidates | Effective administrator or precise Application permission |
| POST | `/search/resolve` | Unified resolve search - queries all active providers and returns ranked candidates | Effective administrator or precise Application permission |

---

## Review Queue

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/review/pending` | All items currently in the review queue | Effective administrator or precise Application permission |
| GET | `/review/count` | Count of pending review items. Used for media library badge. | Required |
| GET | `/review/{id}` | Full detail for a single review item including candidates | Effective administrator or precise Application permission |
| POST | `/review/{id}/resolve` | Resolve a review item by selecting a candidate or confirming corrected local metadata | Effective administrator or precise Application permission |
| POST | `/review/{id}/dismiss` | Dismiss a review item without resolving it | Effective administrator or precise Application permission |
| POST | `/review/{id}/skip-universe` | Accept the item without a Wikidata QID. The item can still remain browse surfaces-visible if it passes the browse readiness gate. | Effective administrator or precise Application permission |

---

## Persons

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/persons/{id}` | Person detail - biographical data, roles, library presence, social links | Required |
| GET | `/persons/{id}/aliases` | Pseudonym list for a person, including resolved Wikidata aliases | Required |

---

## Universes

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/universes` | All narrative roots (Universes) in the library | Required |
| GET | `/universe/{qid}` | Universe detail - Series, People, and asset counts | Required |
| GET | `/universe/{qid}/health` | Health score - completeness, enrichment freshness, missing metadata indicators | Required |
| GET | `/universe/{qid}/graph` | Graph data in Cytoscape.js format for the Chronicle Explorer visualization | Required |
| GET | `/universe/{qid}/paths` | Find shortest paths between two entities within the universe graph | Required |
| GET | `/universe/{qid}/family-tree` | Character family tree rooted at a specified character entity | Required |
| GET | `/universe/{qid}/cross-media` | Entities that appear across more than one media type within the universe | Required |
| GET | `/universe/{qid}/cast` | Characters with their linked performers, including era-correct actor data | Required |
| GET | `/universe/{qid}/adaptations` | Adaptation chain - all works derived from or adapted into each other | Required |
| GET | `/universe/{qid}/lore-delta` | Check for Wikidata revision changes since the last enrichment pass | Required |
| POST | `/universe/entity/{qid}/deep-enrich` | On-demand deep enrichment of a character/entity and its immediate neighbors | Effective administrator or precise Application permission |

---

## AI

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/ai/status` | AI subsystem health - model load state, hardware tier, resource pressure | Required |
| GET | `/ai/models` | Status of all configured models (loaded, unloaded, downloading, unavailable) | Required |
| POST | `/ai/models/{role}/download` | Trigger download for a model role (`text_fast`, `text_quality`, `text_scholar`, `text_cjk`, `audio`) | Administrator |
| POST | `/ai/models/{role}/load` | Load a model into memory | Administrator |
| POST | `/ai/models/{role}/unload` | Unload a model from memory | Administrator |
| GET | `/ai/config` | Full AI configuration | Administrator |
| PUT | `/ai/config` | Update AI configuration | Administrator |
| GET | `/ai/profile` | Hardware profile - benchmark results, tier classification, GPU backend | Required |
| POST | `/ai/benchmark` | Re-run hardware benchmark and reclassify tier | Administrator |
| GET | `/ai/resources` | Live system resource usage - CPU load, RAM pressure, active transcoding tasks | Required |
| GET | `/ai/enrichment/progress` | Background enrichment batch progress | Required |
| GET | `/ai/enrich/tldr/{entityId}` | Generate a TL;DR summary for a work using its description | Effective administrator or precise Application permission |
| GET | `/ai/enrich/vibes/{entityId}` | Generate vibe tags for a work | Effective administrator or precise Application permission |
| POST | `/ai/enrich/search/intent` | Parse a natural-language search query into structured field filters | Required |
| POST | `/ai/enrich/extract-url` | Extract metadata from a URL (book page, IMDB entry, etc.) | Effective administrator or precise Application permission |

`/ai/config` preserves the complete settings graph, including `models`,
`model_catalog`, `operational_roles`, `role_requirements`, feature flags, and
scheduling/resource policy. `/ai/resources` includes numeric `cpu_pressure`;
clients should use that contract value rather than derive pressure from labels.

---

## Enrichment

| Method | Path | Description | Auth |
|---|---|---|---|
| POST | `/metadata/pass2/trigger` | Manually trigger the optional Pass 2 deferred enrichment flow for one or more entity IDs | Effective administrator or precise Application permission |
| GET | `/metadata/pass2/status` | Current Pass 2 deferred-enrichment status, including whether two-pass mode is enabled | Required |

---

## Activity

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/activity/recent` | Recent system activity entries, newest first | Required |
| POST | `/activity/prune` | Prune activity entries older than the configured retention period | Administrator |
| GET | `/activity/stats` | Aggregated activity statistics - counts by type and outcome | Required |
| PUT | `/activity/retention` | Update the activity retention period (days) | Administrator |
| GET | `/activity/by-types` | Filter activity log by one or more activity type codes | Required |
| GET | `/activity/run/{runId}` | All activity entries for a specific ingestion or enrichment run | Required |

---

## Plugins

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/plugins` | List built-in and dynamic plugins loaded by the Engine | Administrator or application with `plugins.read` |
| GET | `/plugins/approved` | Fetch the approved plugin discovery catalog from the configured GitHub source | Administrator or application with `plugins.read` |
| GET | `/plugins/{pluginId}` | Plugin detail, manifest metadata, settings, permissions, and load state | Administrator or application with `plugins.read` |
| POST | `/plugins/{pluginId}/enable` | Enable a plugin | Administrator or application with `plugins.manage` |
| POST | `/plugins/{pluginId}/disable` | Disable a plugin | Administrator or application with `plugins.manage` |
| PUT | `/plugins/{pluginId}/settings` | Save plugin user settings JSON | Administrator or application with `plugins.manage` |
| GET | `/plugins/{pluginId}/manifest` | Read dynamic plugin manifest JSON. Built-in manifests are compiled and are not returned here. | Administrator or application with `plugins.read` |
| PUT | `/plugins/{pluginId}/manifest` | Save dynamic plugin manifest JSON without changing plugin id | Administrator or application with `plugins.manage` |
| DELETE | `/plugins/{pluginId}` | Delete a dynamic plugin folder and saved plugin configuration | Administrator or application with `plugins.manage` |
| POST | `/plugins/{pluginId}/health` | Run plugin health checks | Administrator or application with `plugins.read` |
| GET | `/plugins/{pluginId}/jobs` | List recent durable plugin operation rows for one plugin. | Administrator or application with `plugins.jobs.read` |
| POST | `/plugins/jobs/segment-detection/run` | Run scheduled playback segment detector plugins immediately | Administrator or application with `plugins.jobs.run` |

Plugin responses preserve manifest capabilities, permissions, tool
requirements and per-platform artifacts, AI permissions, settings schema,
health checks, durable job counters, and approved-catalog provenance. Dashboard
presentation types must not narrow that payload.

---

## Admin

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/admin/provider-configs` | List all provider configurations | Administrator |
| PUT | `/admin/provider-configs/{providerId}` | Update a provider configuration | Administrator |
| DELETE | `/admin/provider-configs/{providerId}` | Remove a provider configuration | Administrator |

---

## Settings

| Method | Path | Description | Auth |
|---|---|---|---|
| POST | `/settings/test-path` | Test whether a filesystem path is accessible by the Engine | Administrator |
| GET | `/settings/providers` | Provider configuration status - enabled state, last health check, rate limit info | Required |
| GET | `/settings/server-general` | Core server settings (name, language preferences, country) | Required |
| PUT | `/settings/server-general` | Update core server settings | Administrator |
| POST | `/settings/organization-template/preview` | Validate an organization template and return a sample preview without saving | Administrator |
| PUT | `/settings/organization-template` | Save the organization template after validation | Administrator |

---

## Profiles

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/profiles` | List profiles granted to the current account. Each item includes `has_pin` so the *Who's using Tuvima?* page can show a lock; the other profile endpoints return `has_pin: false` | Human session |
| POST | `/access/profiles` | Create a profile and its explicit account grant | Identity users write |
| GET | `/profiles/{id}` | Profile detail | Required |
| PUT | `/profiles/{id}/experience` | Save name, avatar color, and navigation preferences without role fields | Exact active profile |
| DELETE | `/access/profiles/{id}` | Delete through account/grant safety rules without deleting originals | Identity users write |

---

## UI Settings

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/settings/ui/global` | Global UI defaults | Required |
| PUT | `/settings/ui/global` | Update global UI defaults | Administrator |
| GET | `/settings/ui/device/{class}` | Device profile settings for a device class (`web`, `mobile`, `television`, `automotive`) | Required |
| PUT | `/settings/ui/device/{class}` | Update device profile settings | Administrator |
| GET | `/settings/ui/profile/{id}` | Per-user UI preference overrides for a profile | Required |
| PUT | `/settings/ui/profile/{id}` | Update per-user UI preferences | Required |
| GET | `/settings/ui/resolved` | Effective resolved settings for the current request context (global + device + profile merged) | Required |
| GET | `/settings/ui/library-preferences` | Current per-media library display preferences (missing-item visibility defaults) | Required |
| PUT | `/settings/ui/library-preferences` | Save per-media library display preferences to `config/ui/library-preferences.json` | Administrator |

---

## Reader

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/read/{assetId}/metadata` | EPUB metadata - title, author, language, cover | Required |
| GET | `/read/{assetId}/toc` | Table of contents | Required |
| GET | `/read/{assetId}/chapter/{index}` | Chapter content by index | Required |
| GET | `/read/{assetId}/resource/{path}` | Embedded resource (CSS, image) by path within the EPUB | Required |
| GET | `/read/{assetId}/search` | Full-text search within EPUB content | Required |
| GET | `/reader/{assetId}/bookmarks` | List bookmarks for an asset | Required |
| POST | `/reader/{assetId}/bookmarks` | Create a bookmark | Required |
| DELETE | `/reader/{assetId}/bookmarks/{bookmarkId}` | Delete a bookmark | Required |
| GET | `/reader/{assetId}/highlights` | List highlights | Required |
| POST | `/reader/{assetId}/highlights` | Create a highlight | Required |
| DELETE | `/reader/{assetId}/highlights/{highlightId}` | Delete a highlight | Required |
| GET | `/reader/{assetId}/statistics` | Reading statistics - time spent, completion percentage, sessions | Required |
| POST | `/read/{assetId}/whispersync` | Sync reading position across devices | Required |
| GET | `/read/{assetId}/whispersync` | Retrieve last sync position | Required |
| DELETE | `/read/{assetId}/whispersync` | Clear sync position | Required |

---

## Player

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/player/state` | Current playback session, queue, position, and audiobook history | Required |
| POST | `/player/queue/replace` | Replace the active playback queue | Required |
| POST | `/player/queue/items` | Add items to the active playback queue | Required |
| POST | `/player/command` | Apply transport commands such as play, pause, seek, volume, or speed | Required |
| POST | `/player/heartbeat` | Persist current playback timing and resume progress | Required |
| GET | `/player/audiobooks/{workId}/history` | Recent audiobook listening checkpoints | Required |
| GET | `/player/audiobooks/{workId}/bookmarks` | Saved audiobook playback bookmarks | Required |
| POST | `/player/audiobooks/{workId}/bookmarks` | Save an audiobook playback bookmark at a position in seconds | Required |
| DELETE | `/player/audiobooks/bookmarks/{bookmarkId}` | Delete an audiobook playback bookmark | Required |
| POST | `/player/audiobooks/{workId}/chapters/suggest-names` | Suggest display-only audiobook chapter names using local AI | Required |
| GET | `/player/audiobooks/{workId}/chapter-overrides` | List saved display-only audiobook chapter title overrides | Required |
| POST | `/player/audiobooks/{workId}/chapter-overrides` | Save one display-only audiobook chapter title override | Required |
| DELETE | `/player/audiobooks/{workId}/chapter-overrides/{assetId}/{chapterIndex}` | Delete one display-only audiobook chapter title override | Required |

Player and playback contracts carry the complete queue/session state, delivery
manifest, resume data, tracks/subtitles/chapters, audiobook history and
bookmarks, chapter-title provenance, offline variants, diagnostics, and user
playback settings. Text-track contracts retain source and normalized formats,
timing mode, ownership/preference/export flags, confidence, and URL.

---

## Progress

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/progress/{assetId}` | Playback or reading progress for an asset | Required |
| PUT | `/progress/{assetId}` | Update progress | Required |
| GET | `/progress/recent` | Recently accessed assets with progress | Required |
| GET | `/progress/journey` | Full reading/watching journey across all assets for the current profile | Required |

---

## Reports

| Method | Path | Description | Auth |
|---|---|---|---|
| POST | `/reports` | Submit a metadata quality report for an entity | Required |
| GET | `/reports/entity/{id}` | All reports for a specific entity | Effective administrator or precise Application permission |
| POST | `/reports/{id}/resolve` | Mark a report as resolved | Effective administrator or precise Application permission |
| POST | `/reports/{id}/dismiss` | Dismiss a report | Effective administrator or precise Application permission |

---

## Library Character and Asset Data

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/library/characters/{id}/portraits` | Portrait images for a fictional character | Required |
| PUT | `/library/characters/{id}/portraits/{portraitId}/default` | Set the default portrait for a fictional character | Effective administrator or precise Application permission |
| GET | `/library/persons/{id}/character-roles` | Characters a person has performed, linked to works | Required |
| GET | `/library/universes/{qid}/characters` | All characters in a universe | Required |
| GET | `/library/assets/{entityId}` | Shared assets for an entity (Cover Art, Headshot, Banner, Logo, Backdrop) | Required |
| POST | `/library/enrichment/universe/trigger` | Trigger universe enrichment for a specific QID | Effective administrator or precise Application permission |

---

## SignalR

| Collection | Path | Direction | Description |
|---|---|---|---|
| Intercom | `/intercom` | Server -> Client | Real-time push events: ingestion progress, enrichment completion, `MediaOperationChanged`, `CapabilityStateChanged`, pipeline state changes, and review queue updates. Authentication required. Server-push only - clients do not send messages to the collection. |

SignalR payloads use `MediaEngine.Contracts.Realtime` except for the exact
frozen `LoreDeltaDiscoveredEvent` and `UniverseEnrichmentProgressEvent`
Universe/Chronicle pair. New events and new payload fields are not covered by
that exception.

---

## Development

Available in development environments only. These endpoints are removed in production builds.

| Method | Path | Description |
|---|---|---|
| POST | `/dev/reset-and-seed` | Preserve application configuration, reset catalogued library state, create selected Standard or Stress fixtures, and queue normal ingestion. |
| POST | `/dev/reset-library-data` | Clear catalogued media and derived state without deleting source media or application configuration. |
| POST | `/dev/factory-reset` | Clear the development database and generated state without deleting configured source media. |
| POST | `/dev/seed-library` | Seed the library with 22 EPUB test cases covering edge cases (pen names, foreign languages, series grouping, multi-author) |
| POST | `/dev/wipe` | Lower-level automated harness reset; generated-state scope preserves configuration and unrelated source media. |
| POST | `/dev/full-test` | Run full ingestion and enrichment pipeline on the seeded test library |
| POST | `/dev/reingest-library` | Pause file watching, reset generated database/cache/artwork state, scan every configured library source path, and leave watching paused until restart or explicit resume. |
| POST | `/dev/integration-test` | Run the integration test suite and return an HTML report |

## Related

- [How to Build, Test, and Verify Changes](../guides/running-tests.md)
- [Database Schema Reference](database-schema.md)
- [Security Architecture](../architecture/security.md)

## Access, application events, and telemetry

These routes belong to the Access replacement under final integration. Inbound credentials resolve the current Application; they do not carry a household role. Metadata, provider, and other outbound service secrets remain separate.

| Method | Engine path | Responsibility |
|---|---|---|
| GET | `/auth/sign-in-methods` | Dashboard-only (service credential). Answers "what can this visitor use to sign in from where they are?" before anyone has signed in. Sends `X-Tuvima-Client-Ingress` (missing or unknown means `remote`) and `?https=true` when the visitor came over HTTPS. Returns `{ "password": bool, "passkey": bool, "external_providers": [{ "id", "display_name" }], "invitation_code": bool }`, computed with the same rules the sign-in actions enforce (who can connect, HTTPS for outside visitors, the one public address for passkeys and providers). It never returns account emails, profile names or counts. Nothing is cached: the Dashboard asks once per sign-in page load. |
| POST | `/auth/session/validate` | Dashboard-only (service credential). Sends `X-Tuvima-Client-Ingress` (`this_computer`, `home_network` or `remote`; missing or unknown means `remote`). A session that was made at home or on this computer answers `401` with `{"reason":"sign_in_again_here"}` when the header says `remote`; the session is not revoked and works again at home. A session made from outside stays valid anywhere the door rule allows. A session of an account that works only on this computer is refused with the same `sign_in_again_here` answer from `home_network` as well as `remote`. |
| GET | `/auth/this-computer` | Dashboard-only (service credential) and only when `X-Tuvima-Client-Ingress` is `this_computer`. Returns `{ "display_name" }` of the account that works only on this computer, for the sign-in page. Any other caller, or no such account, gets the same plain `404`, so home and remote visitors never learn the account exists. |
| POST | `/auth/this-computer` | Same conditions as the `GET`. Body: `device_id`, `device_name`, `client`. Signs in the this-computer-only account without a password and returns the usual session response with `authentication_method` `ThisComputer`. `404` from anywhere else or with no such account. |
| POST | `/auth/confirm` | Signed-in caller. Body: `password`, or `credential_json` + `state` from `POST /auth/confirm/passkey-options`. Marks the current session as just authenticated (`auth_sessions.authenticated_at`). Wrong secrets answer `401`; a locked credential is refused. |
| GET | `/auth/confirm/recent` | Signed-in caller. Returns `{ "recent": bool }` for the current session; the Dashboard calls it before a redirect-based action (linking a provider). |
| POST | `/auth/confirm/passkey-options` | Signed-in caller. Starts a passkey assertion used to confirm. |
| GET | `/auth/device-profile` | Signed-in caller. Returns `{ "profile_id": guid|null }`: the profile this browser or device "always opens as" for the caller's account. A saved profile the account no longer has reads as `null`. Always scoped to the caller's own account and the device of the caller's own session; no other account or device can be named. |
| PUT | `/auth/device-profile` | Signed-in caller. Body: `{ "profile_id" }`. Saves "always open as" for this device. `403` when the account has no access to that profile. |
| DELETE | `/auth/device-profile` | Signed-in caller. Clears "always open as" for this device. `204` whether or not one was set. |
| POST | `/auth/account/secure` | Signed-in this-computer-only account. Body: `password` and/or a passkey (`passkey_credential_json`, `passkey_state`, `passkey_name`), plus `device_id`, `device_name`, `client`. Adds the credential, clears `this_computer_only`, replaces recovery codes, revokes old sessions and returns a new session. |
| POST | `/setup/v1/begin` | Dashboard-only (service credential). Body: `original_client_ingress` (the Dashboard's own classification; missing or unknown means `remote`) and optional `setup_code`. From `this_computer` no code is needed; from `home_network` the one-time code from `tuvima-admin setup code` is required and is used up on success. `remote` is refused even with a valid code. Success returns the setup session token (header `X-Tuvima-Setup-Session`) and ends any earlier setup session. Refusals are `403` with `{"reason","message"}` where `reason` is `setup_code_required`, `setup_code_invalid` (wrong, expired, used, replaced, or too many wrong tries) or `setup_remote_refused`. `409` once an administrator exists. |
| POST | `/setup/v1/administrator` | Setup-session protected. Creates the first administrator. Body adds `sign_in` (`password`, the default, or `this_computer`) and `original_client_ingress`. With `this_computer`: `email` and `display_name` are required, `password` must be empty, and the request is refused with `409` and `code: "not_available_here"` unless `original_client_ingress` is `this_computer` **and** the Engine is not in a container. The account is created with no password (`accounts.this_computer_only = 1`), its session is issued from `this_computer`, no recovery codes are returned, and `network.who_can_connect` is set to `this_computer`. The password path is unchanged. |
| GET/POST/PUT | `/access/accounts` and `/{accountId}` | Managed account lifecycle and explicit account grants |
| POST | `/access/accounts/{accountId}/temporary-password` | Administrator only, and only once the administrator's own account is secured. Body: `temporary_password` (12–128 characters, the usual password rules). Gives the account that password (it works only to choose a new one), signs the account out everywhere and returns the account with `must_change_password` and `temporary_password_expires_at`. `POST /access/accounts` also accepts `temporary_password`. |
| POST | `/access/households/{householdId}/people` | Administrator only, once their own account is secured. Body: `display_name`, `avatar_color`, `is_child`, `pin` (optional, 4–12 digits). Adds a person to the household (up to 8; refused with "A household can have up to 8 people." when full). A child person is a restricted profile. The household's main sign-ins can open them, not as default. Returns the profile (`is_restricted` shows the child role). |
| POST | `/access/profiles/{profileId}/own-sign-in` | Administrator only, once their own account is secured. Body: `email` and optionally `temporary_password`. Creates the person's own sign-in in their household: one default grant to that person, never an administrator, following the household's main sign-in for feature and library access (`grants_inherit_from_account_id` on the account). Without `temporary_password` the response carries an `invitation` like `POST /access/invitations`. Refused if the person already has one or the email is taken. |
| DELETE | `/access/accounts/{accountId}/own-sign-in` | Administrator only. Deletes a person's own sign-in, ending its sessions and paired devices. The person and their data stay. Refused for any account that is not a single-person own sign-in, and when nobody else in the household could open the person. |
| POST | `/access/invitations` | Administrator only. Creates an invitation and returns `{ account_id, code, expires_at, public_address }`: `code` is the one-time `XXXXX-XXXXX` code (ten characters without 0, O, 1, I or L; only its SHA-256 is stored), `public_address` is the saved public address or null. The code is not returned again. |
| POST | `/auth/invitations/preview` | Dashboard-only (service credential, rate limited). Body: `code`, `original_client_ingress`, `original_client_is_https`. Returns `{ email, expires_at }` for a code that can still be used, otherwise a plain `404`. Used to show the email, read-only, before choosing a password. |
| POST | `/auth/invitations/accept` | Dashboard-only (service credential). Body: `code` (dashes, spaces and case are ignored), `password`, device fields. Creates the sign-in for the invited account and returns a session. A bad, expired or used code answers `401` with the same message. |
| POST | `/auth/password/change-temporary` | A signed-in person whose password is temporary. Body: `current_password`, `new_password`, `device_id`, `device_name`. Replaces the password, ends every session of the account and returns a new normal session. Refused with the administrator message once the temporary password has expired. While `must_change_password` is true, every other Engine action except `POST /auth/session/validate` and sign-out answers `403` with `code: "password_change_required"`. |
| GET | `/access/devices` | Paired phones and TVs for **Settings > Network > Apps & devices**. A human session only. An administrator sees every active device; anyone else sees only their own account's. Each item: `id`, `device_name`, `platform` (`mobile`, `television`, `automotive`, `web`), `client_name`, `account_id`, `account_display_name`, `profile_id`, `profile_name`, `paired_at`, `last_seen_at`, `backs_up_to` (empty until phone backup exists). |
| DELETE | `/access/devices/{deviceId}` | Revokes the device and all its tokens, then sends `ApplicationEventRevoked` to its live connection so the app signs out at once. `204` on success, `403` when a non-administrator names another account's device, `404` when the device is unknown or already revoked. |
| GET/POST/PUT/DELETE | `/access/applications` and `/{applicationId}` | Application identity and registered permissions |
| POST/DELETE | `/access/applications/{applicationId}/credentials` and `/{credentialId}` | One-time credential issuance and revocation |
| GET/POST/PUT/DELETE | `/access/applications/{applicationId}/webhooks` and `/{webhookId}` | Validated destinations, event selection, and delivery status |
| POST | `/access/applications/{applicationId}/webhooks/{webhookId}/rotate` | Rotate the protected signing secret; display it once |
| SignalR | `/application-events` | Bounded subscription/replay with current principal and resource authorization |
| GET | `/api/v1/playback/sessions` | Current scoped playback sessions |
| GET | `/api/v1/playback/history` | Durable scoped playback history |
| GET | `/api/v1/analytics/playback` | Scoped playback totals |
| GET | `/api/v1/analytics/users`, `/libraries`, `/devices` | Aggregation only after scope filtering |

Telemetry read operations have separate registered permissions; cross-session control remains unavailable. Delivery mode and transformed media properties remain unknown without trustworthy observed stream facts. Webhook retries use the same signed envelope and stable delivery identity; receivers must handle repeated deliveries idempotently. A replay gap requires a fresh authorized snapshot before resuming the stream.
