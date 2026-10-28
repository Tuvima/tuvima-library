---
title: "Product Status"
description: "A clear Early Access view of what Tuvima Library can do today and what is still planned."
audience: "user"
category: "reference"
product_area: "product"
tags:
  - "status"
  - "early-access"
  - "roadmap"
status: current
---

# Product Status

Tuvima Library is in **Early Access**. Use this page to check whether a feature meets your needs before setting it up.

- **Ready today:** local browsing, reading and playback, inline editing, personal files, accounts and library access, with the limits below.
- **Partial:** discovery quality, some local AI features, plugins and native-client delivery depend on data, configuration or further verification.
- **Next:** broader device releases, automation, photo intelligence and cross-format story continuity.

**Installation:** [Docker Compose](../install/docker.md) is the recommended setup. The [Windows installer](../install/windows.md) is Early Access and is published with tagged releases; [running from source](../install/from-source.md) suits developers and evaluators.

For the row-by-row implementation truth table, see the [Feature Truth Inventory](feature-truth-inventory.md).

## Built Today

| Area | Status | What this means |
|---|---|---|
| Engine and Dashboard | Live | The API host and Blazor Dashboard run locally and communicate over HTTP and SignalR. |
| Home, Read, Watch, Listen | Partial | Browse surfaces render real Engine display data when available. Quality depends on ingested library data. |
| For Me | Live | Your saved items, Favorites and Continue state remain separate. Personal collections, playlists and galleries appear in their own shelves. |
| Collections | Partial | Browse trusted automatic groups and custom collections. Lane-level shelves stay in their media lanes; playlists belong in Listen. |
| Search | Partial | Cross-library search is wired to Engine display/search APIs. Results depend on indexed metadata. |
| Detail pages | Live | Item detail surfaces show metadata and launch inline editing through the shared editor. |
| Review Queue | Live | Uncertain or blocked items can be reviewed, dismissed, skipped for universe/QID, or resolved through Engine-backed actions. |
| Durable operations | Live | Ingestion, Wikidata bridge work, and plugin jobs have restart-safe operation rows with status, stage, progress, retry, and failure detail. |
| Capability readiness | Partial | Media assets can expose explicit readiness rows for identity, enrichment, text tracks, commercial skip, writeback, AI, and plugin work. More workers will be wired over time. |
| Ingestion dashboard | Partial | Active operations, recent batches, folder health, provider health, progress, and review reasons are visible from durable Engine data where available. |
| Helper tools check | Live | The Engine checks FFmpeg, FFprobe and Tesseract at first run, at every start-up and when you choose **Check again**. Setup lists a missing required tool under "A few things to install" and never blocks you from continuing. |
| Tools and requirements page | Live | **Settings > Tools and requirements** shows Ready or missing for each tool, with **Locate it** and **Check again**. AI model files download on request from the same page. |
| Waiting for media tools | Live | Video and audio files that need FFmpeg show **Waiting for media tools** and finish by themselves once FFmpeg is found. Nothing fails. |
| Install for me (Windows, from source) | Live | On a source checkout, the Tools page downloads a pinned, checksum-verified FFmpeg build into the Library's data folder, and downloads the official Tesseract installer for you to run. |
| Settings > Libraries | Live | Catalogued libraries, stable catalogue sources, incoming locations, the single View storage root, source safety, organization, metadata, intake, and duplicate policies are backed by Engine/config APIs. |
| Folder picker | Live | An Explorer-style address bar (clickable folder names, a small arrow after each to jump to a folder inside it, and a click on the empty end to type or paste a full path, then Enter or Esc), single-click to select and double-click to open, Back, Forward, Up, folder search, and a Selected path with a copy button. It is the Folder step of the Add folder dialog and also opens on its own for the Import folder. Media locations are listed before drives, and paths are shown as the server sees them. |
| Network and Remote Access | Live | New installs are local-network-only. Authenticated remote access is gated behind verified Tailscale Serve or a trusted HTTPS reverse proxy. Docker bridge topology is explained without attempting router discovery; PCP/NAT-PMP/UPnP and manual forwarding remain Advanced options for an explicit local TLS listener. The Engine is not exposed. External reachability and bandwidth remain explicitly unknown until they can be proven. |
| View Personal Spaces | Live | Each enabled profile resolves one automatically provisioned Personal Space beneath the managed View root. Any number of persisted sources or devices may feed it; eligible folders are watched and reconciled in the background. Managed imports copy originals beneath stable profile/source paths, while optional external links remain read-only. Local-only/manual personal media bypasses retail providers, Wikidata, canonical claims, and Review Queue. |
| View: Photos | Live | `/view` uses the five-item View shell and same-origin media grants. Trusted Shared Library and Mine scopes drive a cursor-paged mixed-media timeline with search, filters, calendar-organized browser uploads, selection, favorites, hidden state, archive, trash, restore, Empty Trash (desktop, own Personal Space; the only step that removes files), Gallery placement, managed thumbnails, and an accessible immersive viewer. Browsing and thumbnail generation never modify originals. |
| View: source folders and Shared Library | Live | Each View kind (Photos, Personal videos, Documents) opens the same page shape as any library at Settings > Libraries > View (Folders, Access, File handling tabs); each source folder has one Show in Photos timeline switch. Shared scope includes only accepted items in the physical Shared Library root. `/view/contributions` adds batch preview, pending submission/cancellation, independent submit/review grants, curator decisions, direct curator add, asynchronous verified transfer, restart recovery, per-item state, and durable activity. Managed originals move only after Shared verification; linked originals copy and remain. Contribution routes preserve `View > Shared Library > Contributions` breadcrumbs. |
| View: Galleries | Live | Manual and rule-driven Smart Galleries support creation, editing, deletion, item paging, duplicate-safe membership, drag/drop placement, ordering, and owner-selected profile sharing. Policy-gated recipients receive view or contribute access; deleting a Gallery never deletes media. Public-link sharing is not implemented. |
| View: Documents | Live | `/view/documents` lists, searches and opens PDFs, Office, text and scanned files. Words are read on the host in the background (PDF and Office text layers, plain text, and local text recognition with Tesseract for scanned pages and photos of paper). Global tags, correspondents, document types and custom fields; an Inbox with local suggestions; auto-filing rules; saved views; bulk editing of up to 500 documents; sorting; CSV export; and a bundled PDF viewer. The Documents permission is on by default and off for child profiles. Originals are never changed, and document words never reach Home, global Search or Recommendations. |
| View: People and Places | Partial | People shows named or reviewed annotations, not automatic face recognition. Places plots available locations in Atlas, with place lists and a local geographic fallback. The current map can request OpenFreeMap styles and tiles; it is not a guaranteed offline-only view. |
| View in Collections | Live | Administrators can attach a whole Gallery or a versioned View smart rule in the Collection editor. Individual local asset IDs are rejected, saved sources remain dynamic, and every projection reapplies View authorization without leaking unauthorized counts. |
| Backup and recovery | Live | Administrators can create, list, download, validate, stage, and apply backups containing the data store and non-secret configuration. |
| Provider key connection | Live | Connecting a provider key happens on one screen. |
| Settings > Providers | Live | Provider catalogue/status/config, credential state, health, tests, and pipeline priority are backed where the Engine exposes them. |
| Settings > Local AI | Live | Status, Model and hardware, Budget (the default plugin allowance) and Usage are connected to Engine endpoints. The AI features themselves are Official plugins managed at Settings > Plugins. |
| Playback and reader preferences | Live | Personal playback, reading, subtitle, resume, audiobook chapter cleanup, audiobook history/bookmarks, chapter display-title overrides, and progress preferences persist through the playback settings and player APIs. |
| Adaptive video delivery | Live | Incompatible or remote video sources can be prepared as source-aware HLS with bitrate variants, alternate audio, WebVTT captions, seek/resume support, expiring package-scoped access, bounded storage cleanup, and optional hardware encoding. Compatible sources remain direct play. |
| Plugins | Partial | Plugin list, enable/disable, settings JSON, dynamic manifests, health, jobs, and approved-catalog lookup are available. |
| Users and access | Live | One account can hold explicit profile grants, switch only among granted profiles, and invite an optional direct email/password login into an existing profile. Credentials are hashed and account invitations, recovery, rate limiting, sessions, and revocation are Engine-backed; profile storage identity remains stable when login details change or are removed. |

## Still Outstanding

These items are not presented as complete user workflows yet:

- Advanced direct-play, delivery, subtitle/audio policy, and automated offline-download controls.
- Plugin marketplace install/update flows.
- Some Local AI job controls, deletion actions, and per-feature runtime integrations.
- Full worker coverage for every capability row. The durable model exists; individual enrichment, AI, text track, and writeback workers will continue moving from artifact-only writes to operation/capability updates.
- Richer playlist editing, recommendation automation, smart collections, and broader discovery intelligence.
- Additional secure-connectivity integrations and device interoperability. Current access uses accounts, profile grants and revocable Application credentials, not the retired profile/API-key authority model.
- Interoperability targets such as OPDS, Audiobookshelf-compatible APIs, import wizards, webhooks, and PWA behavior.
- Post-beta photo intelligence: face recognition, object/scene detection,
  captions, embeddings, semantic search, memories, and AI-assisted organization.
  (Text recognition for scanned documents and photos of paper is live; see View: Documents.)
- Public Gallery links and a complete mobile backup/sync experience. Places already renders coordinates and can use an external map service with a local fallback; richer local geocoding and map privacy controls need separate acceptance.
- Device-specific mobile-backup and connected-device producers. Source/device
  records and intake policy vocabulary exist so future clients have a safe
  target, but they are not working backup products today.
- Automatic shared-incoming routing into a profile's Personal Space. Mixed
  local candidates are classified and left for attention today; explicit
  browser uploads already resolve the caller's Personal Space and bypass
  catalogue processing.

## Product Guardrails

- Normal media corrections happen inline from the page, card, row, album, track, movie, show, book, comic, or detail view where the issue appears.
- Review Queue is only for terminal or actionable issues that need human/admin confirmation. In-progress uncertainty belongs in Operations, Capabilities, retry backlog, or blocked work views.
- Settings/Admin is for configuration and operational state.
- The retired all-in-one correction workspace must not return as a current product surface.
- Future-state documents must say they are future-state documents.

## Next steps

- [Getting Started](../tutorials/getting-started.md)
- [How File Ingestion Works](../explanation/how-ingestion-works.md)
- [Privacy and Local-First Behavior](../explanation/privacy-local-first.md)
- [Target State](../architecture/target-state.md)
- [Beta Roadmap](beta-roadmap.md)
