---
title: "Performance and Large Libraries"
description: "Rules for keeping Tuvima Library responsive with large local media collections."
audience: "developer"
category: "architecture"
product_area: "performance"
tags:
  - "performance"
  - "paging"
  - "sqlite"
status: current
---

# Performance and Large Libraries

## In this page

Rules for keeping Tuvima Library responsive with large local media collections.

## Where this lives in the code

- `tests/MediaEngine.Performance.Tests`
- `src/MediaEngine.Api/Services/ReadServices`
- `src/MediaEngine.Web/Components`

Large library surfaces must be bounded by default. API endpoints that can return works, assets, people, ingestion rows, watch-folder files, activity, reviews, or search results should use shared paging contracts and clamp caller-provided limits on the server.

Use server-side filtering and sorting whenever practical. Avoid loading all rows into memory and then applying search, type filters, or status filters in C# for request-time screens.

Blazor Server pages must not render unbounded rows or cards. Use first-party `App*` controls with server paging, Blazor `Virtualize`, or explicit "load more" paging for large grids and track lists. Add stable keys for repeated rows where row identity matters.

Avoid N+1 repository loops. When a page needs related people, collections, canonical values, or artwork for many parent rows, add a batch read method and map the result in memory.

SQLite indexes should be added for new high-volume query patterns. Prioritize joins and filters over `media_assets`, `editions`, `works`, `canonical_values`, `canonical_value_arrays`, `person_media_links`, `ingestion_log`, and `identity_jobs`.

SQLite operation connections run in WAL mode with `synchronous=NORMAL`, a 5 second busy timeout, memory temp storage, a 16 MiB page cache target, and a 256 MiB mmap cap. This favors local ingestion throughput while accepting that an OS or power crash can require reingesting the most recent media changes; the library is local-first and media files remain the source of truth.

Large-list read paths should emit debug timing logs with operation name, elapsed milliseconds, offset or cursor, limit, returned item count, and `has_more`. Slow reads over one second should log a warning. Do not log sensitive paths, full queries, secrets, or user media metadata.

Performance tests should use generated temp SQLite data or in-memory fixtures. They must not depend on the user's real database, watch folder, media files, local AI models, or network.

## Implemented performance controls

The August 2026 remediation pass established the first repeatable baseline and
added `tests/MediaEngine.Performance.Tests`. Its generated SQLite fixture verifies
indexed collection paging at 100,000 works without touching user data. Runtime
measurements are exposed through the `Tuvima.Library.Performance` meter, including
interactive request duration, active interactive work, database read duration,
and background-AI admission, deferral, and preemption counts.

The triggering live baseline was a background series-alignment run loading the
1.17 GB Qwen model on CPU. Engine memory reached roughly 2.65 GB, Home latency
rose from about 47 ms to 4.6 seconds, and one detail request ended as HTTP 499
after 254 seconds. With the AI batch idle, comparable TV and book details took
about 270 ms and 250 ms. Background local AI is therefore opportunistic: it is
blocked until onboarding completes, waits for an interactive quiet period and
resource admission, reserves CPU cores, and is cancelled for retry when a
user-facing request arrives. Invalid generated JSON does not trigger an immediate
second full inference.

Other enforced hot-path rules are:

- Artwork GETs stream stored renditions directly with ETag, Last-Modified, and
  public cache headers. They do not decode/re-encode or write renditions.
- Dashboard live events are coalesced; browse parameters are dirty-checked and
  media-added refreshes are debounced. Terminal events still flush immediately.
- Blazor prerendering is disabled because the Dashboard is an interactive server
  application and duplicate initialization doubled Engine reads. Engine clients
  use `IHttpClientFactory` handler pooling.
- Detail repository reads are database-only. `ffprobe` results are persisted by
  source fingerprint for playback decisions, and ffmpeg output pipes are drained
  concurrently.
- Home uses a 1,000-work recent projection cap and a 30-second projection cache;
  display responses also receive a short output cache and Brotli/Gzip compression.
- Complex card, artwork-stack, Home-shelf, and timeline projections are computed
  once per parameter change. Direct browse grids use browser-native render
  containment for off-screen cards because variable-width flex grids cannot use
  Blazor `Virtualize` without breaking layout and scroll semantics.
- Known file extensions route directly to one processor; moved files retain their
  path-keyed hash cache entry; unsupported video containers are rejected before
  any backup copy.

Microsoft.Data.Sqlite executes its asynchronous command methods synchronously.
Consequently, a repository-wide mechanical conversion from Dapper synchronous
calls to `*Async` calls is not a performance fix for this provider. Prefer bounded
queries, batching, indexes, fewer statements, and short-held connections; only
write-lock waiting and genuinely asynchronous file/network operations should be
awaited.

