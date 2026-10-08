# Audiobook details, Watch playback, and review recovery

Status: implementation underway; fresh reset supersedes targeted recovery at the user’s request. Evidence collected September 27, 2026 from the current source, runtime logs, and a read-only SQLite connection. Existing player layout changes remain untouched. Counts are a point-in-time snapshot of a running system.

## Plain-English product walkthrough

1. **Open any detail page using stored library information only (WP2).** Ingestion owns media inspection and stores the results. Opening Fahrenheit 451 must neither inspect nor prepare any of its 42 files, including in the background. Pages read stored identity, artwork references, tracks, chapters, technical facts, and profile progress. Independently running ingestion may publish updates, but visiting a page never starts that work. Track names, order, grouping, and saved listening position remain intact. Acceptance: basic details and known tracks appear within two seconds at the 95th percentile on this workstation; every detail request performs zero source-file inspections, hashes, provider requests, playback preparation, or inspection-job scheduling, including on a cold application start.

2. **Press Watch once and hand the selected asset directly to playback (WP1, WP3).** The player uses technical facts already recorded at ingestion and the current browser's capabilities to stream the file or prepare a compatible stream. It does not repeat catalogue identification or media inspection before handing off. Normal file opening, decoding, authorization, and necessary remuxing/transcoding belong to the playback service. Leaving, closing, or choosing another item cancels the old request; an old response cannot replace the new choice or crash the player. A missing file or failed stream produces a useful error with Retry. Acceptance: no index exception under repeat-click, close-during-load, navigation, and audio-to-video switching; the selected episode remains selected and uses video controls. No repeat FFprobe/MediaInfo inspection for an unchanged, fully ingested asset. Direct-play and transcoded samples have separately measured startup targets.

3. **Keep usable local media available while metadata is incomplete (WP4).** A provider failing to identify a real file should not, on its own, mean that the file is invalid. Safely classified local items can remain visible with incomplete-metadata status. Genuine conflicts or uncertain classification still require Review. Provider outages and retryable timeouts belong in Operations while automated work remains possible. Acceptance: every remaining review item explains the unresolved decision and why a person is needed; no active retry is simultaneously advertised as a final no-match decision.

4. **Start the library over from the real source files (WP5).** Remove the managed test library and database, then import only `C:\Temp\real_import`. Old catalogue decisions and progress are discarded instead of repaired. Original names, folders, and file contents remain untouched. Acceptance: the old database is removed, new ingestion starts with an empty catalogue, and source monitoring reports no changes.

5. **One click starts listening or watching (WP1, WP3).** Play, Listen, Watch, Resume, Restart, Shuffle, and direct track/episode start actions initiate playback themselves across music, audiobooks, movies, and TV. Opening or mounting the player must not discard that intent and leave it paused. Any unavoidable buffering or conversion shows progress and automatically proceeds to playback; it never asks for a routine second Play click. An explicit pause, close, or newer selection cancels the earlier start intent. Acceptance: from each supported entry point, one user action produces advancing media time and the playing state without another click, including first use in a fresh browser session and audio/video switches. A genuine browser permission restriction or playback failure is reported accurately with a recovery action, not presented as the normal workflow.

Scope: these failures, shared playback/identity state, single-action playback, and a complete managed test-library/database reset followed by fresh real-media ingestion. Source originals are excluded from deletion. No repair or historical backfill will run.

## Evidence and confidence

### Watch: confirmed exception site; triggering mutation still to reproduce

`logs/codex-run-dashboard.out.log:9790` records `ArgumentOutOfRangeException` from `List<T>.set_Item`, in `PlaybackSessionController.EnsurePlayableAsync` at line 1537, through `PlayQueueItemCoreAsync`, `PlayVideoAsync`, and `WatchPlayerPage.LoadPlayerAsync`.

The preparation loop checks the queue index and asset **before** awaiting `GetPlaybackManifestAsync`, then writes `_queue[index] = item` **after** the await without revalidating. A queue clear/replacement during that request can invalidate the index or overwrite a different item. This establishes the unsafe state transition; the exact user action or restore callback that mutated this particular queue is not yet proven. A roughly 20.2-second HTTP response appears immediately before the exception, but interleaved logs do not establish which request it was.

The Watch route also waits for legacy details, full detail presentation, and asset resolution, then profile and manifest resolution, before completing launch. Its loading path has no encompassing cancellation/error/finally handling. These are distinct possible sources of delay and stuck loading, separate from the queue exception.

Earlier browser verification also observed a Watch movie route ending with Music tools and a short audio-like duration. Treat this as a reproduction lead for stale snapshot/media-mode contamination, not as a proven explanation of the reported episode failure.

### Audiobooks: confirmed expensive request structure; timing contribution to measure

`DetailCompositionOrchestrator.MediaGroups.cs`, `BuildAudiobookChapterGroupAsync`, loops through every asset and awaits `BuildManifestAsync` sequentially, followed by per-asset resume loading. `PlaybackCapabilitiesService.BuildManifestAsync` uses an inspection cache for FFprobe but still constructs a `MediaInfoWrapper` for existing audio/video sources. Thus a detail request can do repeated file inspection and playback work proportional to the recording's file count. Measure cache hits, inspection time, SQL, and optional recommendation loading before assigning the entire delay to this path.

### Review: actual persisted reasons

There are **26 Pending review rows**:

| Reason | Media | Count |
|---|---|---:|
| RetailMatchFailed | Audiobooks | 8 |
| RetailMatchFailed | Books | 2 |
| RetailMatchFailed | Movies | 5 |
| RetailMatchFailed | Music | 3 |
| RetailMatchFailed | TV | 3 |
| LowConfidence | TV | 5 |

The five low-confidence entries explicitly say 70% is below the 85% organization threshold. The other 21 say no retail provider returned a match. At least one TV entry has a related queued identity job reporting that season identification exceeded the configured 45-second timeout: the review text and current automated state need reconciliation.

There are also 389 Dismissed retail-failure reviews and 72 Resolved low-confidence reviews, so prior repair did change historical review state. These are not additional pending items. Identity-job counts include legacy per-track jobs and multiple jobs per entity; do not equate job counts with the number of current titles, and do not multiply review rows through an unrestricted job join.

## Technical work packages

### Ownership contract — applies across all work packages

- **Ingestion:** inspect each new or changed source version and persist container, codecs, duration, streams, language/subtitle descriptors, chapters, and stable asset identity. Persist an inspection schema/tool version and outcome; retry incomplete work through ingestion/Operations. Provider enrichment is separate from local playback readiness.
- **Detail and browse reads:** use database projections and stored artwork references only. No source probing, source existence checks, hash verification, provider hydration, manifest construction, or scheduling of those operations from a page request. Normal authorization and profile-state reads remain required.
- **Playback:** accept the selected asset/track identity, read persisted technical facts, enforce access, open the source, and select delivery for the client's capabilities. File opening may fail normally; handle that failure without a separate preflight inspection pass. Decoder/container reads and necessary stream conversion are playback, not a second ingestion pipeline.
- **Change detection and repair:** independent source monitoring schedules ingestion only for an actual source change; an explicit versioned migration/reinspection handles missing or obsolete inspection facts. Application restart, page navigation, or Play must not automatically re-inspect an unchanged ingested file. This run discards legacy facts through the fresh reset; no request-time fallback or historical backfill runs.

### WP0 — Capture a reproducible baseline

- Preserve timestamped relevant logs and an SQLite-consistent backup/snapshot before any repair. Use read-only queries for the initial inventory; no raw copying of an active WAL database as a backup.
- Choose one single-file audiobook, Fahrenheit 451, a larger multi-file recording, one direct-play episode, and one transcoding episode/movie. Record exact work/asset IDs and codec/container information.
- Add correlation IDs and stage durations from click through Dashboard request, Engine projection, inspection, manifest, conversion readiness, media loaded/canplay, and first advancing playback time. Log queue revision and stable item identity without credentials or signed stream URLs.
- Measure cold inspection cache and warm cache independently, with ingestion idle and active. Identify database contention and duplicate requests rather than hiding them with longer timeouts.
- Before implementation/build work, stop the project runtime per AGENTS.md; restart through the normal protected launcher for validation.

### WP1 — Correct playback state ownership first (priority P0)

- Inventory all queue mutations: clear, replace, reorder, next, snapshot restore, popup sync, dismiss, and media-mode switches.
- Introduce a playback request/session generation and cancellation ownership. Capture stable queue-entry identity plus asset identity; asset ID alone is insufficient when the same asset is queued twice.
- Revalidate after every await before changing queue, current item, mode, error, or stream URL. Superseded work returns without affecting the current session. Do not solve this with an index bounds check alone or by holding a lock across network requests.
- Ensure unsuccessful/superseded preparation cannot fall through into playback of a different current item. Inspect all post-await callers as well as `EnsurePlayableAsync`.
- Add route-load cancellation, navigation/disposal handling, and guarded try/catch/finally. Keep retry/error state associated with the current request.
- Regression tests use deliberately delayed manifest responses, then clear/replace/reorder/switch the queue before completing them. Verify no crash, no stale write, no wrong media host, and successful subsequent playback.

### WP2 — Make all detail pages stored-data reads; persist inspection at ingestion (P1)

- Build detail track rows from persisted asset metadata, recording membership, inspection/chapter metadata, and batched profile progress.
- Remove the playback-service dependency from detail composition and audit equivalent paths for every media type. No page-triggered background inspection fallback is permitted.
- Move missing inspection work into ingestion and persist reusable technical facts durably per source version. Treat these as ingested records, not a disposable cache whose loss forces page or playback requests to inspect again. Keep profile resume data and signed playback grants separate.
- Avoid an unbounded `Task.WhenAll` over every file as the performance fix. Prefer batched reads; investigate and cap any necessary concurrency.
- Preserve chapter-to-asset identity and exact source track titles, ordering, cross-file starts, and resume semantics. Missing duration is unknown, not zero-length or a fabricated completion percentage.
- Test cold and warm detail reads with inspection, source-filesystem access, provider calls, and job scheduling configured to fail if invoked. The pages must still render stored details. Verify one inspection per new source version and reuse after application restart.

### WP3 — Single-action playback across audio and video (P1; after WP1)

- Audit every start entry point: detail hero, Home hero, song row, album Play/Shuffle, audiobook track, TV episode, Resume/Restart, and queue selection. Use one start-intent contract rather than separate navigation-only and playback paths.
- Trace the original browser user activation through Blazor event dispatch, asynchronous requests, route changes, media-host mounting, source assignment, and the actual media element `play()` call. Preserve the gesture through a browser-supported approach; do not assume activation survives server round trips or that a muted start satisfies audible playback.
- Keep the media host available for the initial interaction where necessary. Resolve persisted playback information without inspection and honor the current start intent when a source becomes ready. Prevent snapshot restoration, inactive-host events, and later renders from resetting that intent to paused or requiring another gesture.
- Handle the `play()` promise explicitly. Distinguish buffering/conversion from a real `NotAllowedError`, unsupported source, or network failure. Show a manual recovery action only after an actual failure; do not use a generic “Tap play to start this source” prompt as the normal start path. Do not repeatedly autoplay after a user pauses or closes the player.
- Browser acceptance tests must start with a fresh session without relying on prior autoplay permission. One click must produce advancing `currentTime`, `paused == false`, and the correct playing UI for music, audiobook, movie, and episode fixtures. Repeat with delayed source readiness, route transitions, Resume/Restart, and rapid switching; prove superseded requests never start later. Check supported browsers' actual behavior rather than only controller unit tests.

- Resolve the exact selected episode asset with a minimal launch contract. Full recommendations, artwork composition, and unrelated detail sections must not block playback.
- Construct delivery manifests from durable ingested facts and current session/client information. Remove repeat FFprobe/MediaInfo calls from manifest creation and adaptive delivery selection; pass the stored technical description into the playback mechanic. Audit deeper helpers so reinspection is not merely moved behind another call.
- Trace repeated manifest requests and inspect HLS readiness, first decodable audio/video segments, codec selection, conversion failures, resume seeks beyond prepared segments, and caption extraction dependencies.
- Preserve progressive stream readiness; do not wait for full conversion or captions before starting playable video. Deduplicate concurrent preparation for the same compatible output without sharing unauthorized grants.
- Render the video surface immediately with truthful stage/error state and a bounded recovery path. Use negotiated client capabilities and preserve the selected profile throughout refreshes.
- Proposed workstation targets: video shell within one second; direct-play first advancing frame within three seconds; cold transcoding within ten seconds for selected supported fixtures. Record baseline and hardware conditions; if a codec cannot meet the target, document the bottleneck and adjust delivery rather than claiming success based only on an HTTP 200.

### WP4 — Explain and correct review eligibility (P1)

- Produce one row per pending review with canonical work/recording, source path, detected type, provider query/candidate evidence, score components, current job and retry state, and prior repair history.
- Separate: uncertain media classification, confident local identity with unavailable provider metadata, contradictory identity candidates, retryable provider timeout, stale pre-fix decision, and genuinely unreadable/unsupported media.
- Examine why the five TV items score 70%, including whether an organization/move confidence rule is incorrectly blocking linked read-only catalogue use. Do not lower the global threshold to make the queue disappear.
- Check folder/recording hints reach existing audiobook jobs, and that matching runs once per recording. Check filename normalization for codec suffixes and show/season matching without changing source names.
- Use existing durable states where possible to distinguish usable local items from incomplete enrichment. Keep unknown identity clearly labeled; never invent provider identities or silently accept conflicting candidates.
- Resolve obsolete reviews only after authoritative successful re-evaluation; retain human decisions and audit history. Do not automatically approve everything merely because it is playable.

### WP5 — Fresh reset and import (supersedes targeted repair)

- Stop runtime processes and retain diagnostic evidence outside the managed library.
- Capture source inventory and hashes; configure only read-only existing-library sources with original folder organization.
- Validate that the managed library and database are separate from `C:\Temp\real_import`, with no reparse-point traversal. Remove the old managed library and database.
- Start with a newly initialized database and let ingestion discover the real sources. Do not repair, replay, or backfill old catalogue records.
- Check new asset counts, durable inspection facts, playback, and meaningful review reasons. Keep source-change monitoring active and preserve prior historical violation evidence separately.

## Order and completion gates

WP0 → WP1 → WP2/WP3/WP4 → WP5. Work packages may be scheduled independently after the state-safety fix; this document does not request additional agents.

Completion requires a before/after timing table, deterministic state-race test results, browser checks of actual episode playback and audiobook track navigation, a review disposition report, and source-integrity evidence. Test cancellation and rapid switching explicitly, not just the happy path. Keep layout regression checks from the earlier player work.

## Plain-English completion summary

Ingestion records what a file is and how it can be played. Detail pages display those stored results; one click on Play hands the asset to playback and starts it without a second routine prompt. The fixes remove repeated inspection, correct the playback crash, and rebuild the catalogue through a fresh import instead of repairing old records. Original media stays in place throughout.
