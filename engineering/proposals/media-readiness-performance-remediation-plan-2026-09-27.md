# Media readiness, playback, setup, and UI performance remediation

Status: proposed implementation plan, based on read-only source, SQLite, and runtime-log investigation on September 27, 2026. No media, catalogue records, settings, or runtime processes were changed for this review. No reset, repair, or reingestion is included in this planning task.

## Plain-English product walkthrough

1. **Choose language and services before importing (WP1).** Setup offers display language, metadata language, and country/region, suggested from the server’s system locale and explicitly confirmed by the owner. Accepted content languages remain separate. All supported providers are offered, including Comic Vine before a Comics library exists. Existing preferences remain selected on resumed setup. Acceptance: choices survive restart, apply before import, and services are not hidden because a library type is absent.
2. **Play before metadata finishes (WP2–WP3).** Newly discovered movies, episodes, songs, and audiobooks can play once ingestion has registered and technically inspected the source. Descriptions, cast, artwork, and matching do not hold playback up. One Play action retains its intent through buffering or conversion. Catalogue editing can remain locked while required ingestion work runs. Protected originals remain read-only; only explicitly writable libraries can defer permitted file changes until playback releases them. Acceptance: delayed providers do not block otherwise playable media, and encoding failures are reported as failures rather than endless preparation.
3. **See consistent processing indicators without repetitive text (WP4).** Songs rows, media/album/group cards, episode lists, people portraits, author/actor credits, and network identities share a thin yellow animated outline while queued or processing. It occupies no extra space and does not replace names with “Updating details.” Accessible text and hover/focus explain the actual state; reduced-motion users see a static outline. A person whose enrichment finished without a photograph gets a normal fallback portrait, not endless animation. Acceptance: indicators agree with durable job state and never obscure content or keyboard focus.
4. **Browse an incomplete library without false errors (WP5).** Albums and Artists default to cards; Songs remains a list. If there are no eligible artists or networks yet, show the normal empty view and refresh as renderable groups arrive. Retain previously loaded content during refresh. Real network, authorization, and server errors remain distinct and retryable. Acceptance: a successful empty response cannot become an error because another request failed.
5. **Open pages quickly and fetch appropriately sized images (WP6).** People, media, groups, and View first render stored identity and visible content, then secondary sections. Image sizes match device, tile geometry, and display density; originals are reserved for explicit full-size viewing/download. Opening an author must not rebuild the entire catalogue before displaying the name and owned books. Acceptance: the budgets below hold during ingestion and while idle, with no media inspection or provider work triggered by page visits.
6. **Trust matching and dates (WP7; validation review V1–V5).** Keep Wikidata album matching, but require evidence that it is the same album and artist before saving any result. Radioactivity must keep the identity of Age of Days’ album; Kraftwerk’s similarly titled album must be rejected. Artist enrichment remains independent when Wikidata has no album match. Movies such as A Time to Kill and American History X must get useful title/year search hints without changing filenames. A real no-match remains available with truthful incomplete-metadata state; a failed lookup is not mislabeled as successful completion. Acceptance: every resolution path applies the same identity checks, missing optional Wikidata coverage does not block a correctly identified item, and later reingestion validates the result without title-specific rules or source changes. Current records remain untouched during investigation.
7. **View Live Photos and start gallery videos once (WP8).** Live Photo cards display a browser-readable still; playing motion is a separate action. Opening an ordinary gallery video starts it automatically. Changing or closing the item stops the old media. Acceptance: the three observed HEIC failures no longer show broken image icons; unsupported media gets an honest fallback, and there is no routine second Play click.
8. **Separate incoming work from completed additions (WP9).** Rename the broader status view **Library activity**, with **Processing**, **Needs review**, and **Ready** filters using the shared ingestion browser/Operations data. Home’s finished-content shelf becomes **Ready to enjoy** and excludes items whose required identity/ingestion work is pending. A provisional filename title can remain playable in its lane with a processing outline. Acceptance: “A Time to Kill (1996) - VC1 TrueHD” does not appear as a finished addition while unidentified; later refreshes do not make old media look newly added.
9. **Make personal actions work (WP10).** Mark read/watched/listened, mark unstarted, and hide/show in Continue update the correct profile, survive reload, and refresh affected shelves. They do not modify files. Acceptance: every More action works across supported media, failure is visible, and Undo has defined behavior.
10. **Receive useful summaries without interruption (WP11).** Notifications summarize runs and meaningful operations, never each title. Background notifications remain in history while watching, listening, reading, or playing gallery video. A problem with the current media is shown in that player. Acceptance: importing hundreds of files does not create hundreds of toasts or interrupt playback.

11. **Understand and recover from stalled ingestion (WP12).** When required work is waiting to retry, Operations explains the reason, affected count, and next attempt instead of appearing idle at a fixed percentage. Completed file intake stays visibly completed, and optional cast biographies cannot restart an episode's identification. Acceptance: provider season-number differences become a bounded matching outcome; repeated optional enrichment cannot trap the entire run.

Scope boundaries: this is systemic remediation across catalogue media and View where applicable. View stays outside catalogue matching/Review Queue. No direct source repairs, title-specific overrides, new synthetic seed import, provider purchases, or account changes are included. Source protection, profile authorization, canonical contributor eligibility, shared navigation, and artwork geometry remain binding. A later authorized reingestion validates the fixes; it is not a shortcut around root-cause analysis.

### WP12 — TV retry cycle and misleading stalled progress (P0 follow-up)

Read-only snapshot at 16:54 CDT on September 27: all 479 files were registered; 85 identity jobs were Ready/ReadyWithoutUniverse, 19 RetailNoMatch, and six TV jobs Queued. These are different units: identity jobs include grouped identities and must not be compared directly with the file count. All six queued jobs concern Solo Leveling season 2 (episodes 3, 8, 9, 10, 11, 12), each with 22 attempts, a 45-second season-identification timeout, and next retry approximately five minutes later. No job held a lease at that snapshot. This is an active retry/backoff cycle, not evidence of a dead Engine.

At 16:53:02 the log shows TMDB successfully resolving show 127532, followed by HTTP 404 for `/tv/127532/season/2`. The worker receives zero episodes, enters its no-match path, and synchronously enriches people. Logs show the same actors being looked up and rejected repeatedly. `RetailMatchWorker.TvBatch.cs` wraps the whole group, including `EnrichPeopleWithoutMediaMatchAsync`, in the identification timeout; cancellation schedules every group job back to Queued, even after a no-match state was written. Transient timeouts do not consume the poison-job budget in `IdentityJobRetryPolicy`, permitting this cycle to continue beyond five attempts. A missing season is not itself proof of wrong files or of a transient provider outage; investigate provider episode-order conventions before selecting a replacement season.

The dashboard's overall percentage aggregates stage counts (`IngestionLiveDashboardState.Projection.cs:879`), rather than measuring copied bytes or elapsed time. Page polling continues to return HTTP 200. The exact numerical decomposition of the user's 17% was not captured in this snapshot; do not label it 17% of files or invent an ETA.

- Separate episode identification from independently scheduled, deduplicated contributor enrichment; preserve completed decisions/checkpoints across timeouts.
- Distinguish absent season/episode, different provider numbering, genuine no-match, rejected candidate, and transient HTTP failure. Use verified alternate-order mapping rather than assuming a season offset or adding a show-specific rule.
- Bound repeated no-progress attempts with a visible diagnostic/suspended outcome and deliberate recovery conditions; do not exhaust transient jobs merely because a provider temporarily goes offline.
- Record/show retry reason, attempts, next attempt, last useful progress, phase, and affected count. Use consistent phase-specific units and keep file intake separate from identity/enrichment completion.
- Regression test a season 404 plus slow cast enrichment, partial group completion before cancellation, repeated terminal person no-results, and successful alternate-order resolution. Verify the run settles without a source edit or library reset.
- Separately fix the observed once-per-minute `ProviderHealthMonitorService` invalid request URI error. It is confirmed diagnostic noise, but not established as the cause of these six timeouts.

## Findings and evidence

“Confirmed” means persisted evidence or inspected code, not that every user symptom has been reproduced in the browser. Log timings are observational samples from active ingestion, not controlled benchmarks. Adjacent interleaved lines do not establish request causality.

| Issue | Evidence and confidence | Remaining diagnosis |
|---|---|---|
| TV compatible-stream failure | **Confirmed:** package `56b7561d-330e-4060-bbf6-44571a45c9b2` fails at 15:49:17 and 15:51:17 with `main profile doesn't support a bit depth of 10`. The player polls up to 30 iterations, then emits a generic message when no stream URL exists. | Capture the selected source’s stored pixel format/color facts, exact encoder command, audio status, state transitions, and first-segment timing. Failed package preparation can be entered again; validate retry behavior. |
| Slow detail pages | **Confirmed:** unified detail requests load the whole display-work projection before composing one entity. Person composition then performs sequential credits, character roles, aliases, memberships, links, artwork, and description reads. Observed requests reach about four seconds. | Attribute time to SQL, authorization, waits, allocations, projection, transport, and browser rendering before declaring a dominant cause. |
| Artist/network errors | **Confirmed code defect:** `LoadGroupsAsync` interprets empty groups plus shared `ApiClient.LastError` as failure. `GetSystemViewGroupsAsync` returns empty on cancellation/error and does not clear LastError on success. | Reproduce successful empty groups after an unrelated failed heartbeat, cancellation, and overlapping navigation. Do not hide actual failed responses as empty. |
| Album/artist default layout | **Confirmed:** Music’s tab default is List. Songs is forced to List, while Albums and Artists inherit the same tab default. | Verify explicit URL/user choice precedence and per-group defaults. |
| Radioactivity 1975 | **Confirmed:** local tags say Age of Days / Radioactivity / 2013. MusicBrainz also supplies 2013 and release group `24054974-5181-4df2-ae31-3510945d8269`. Wikidata supplies Q1139716, conflicting release group `104cc34f-b4f9-3fb1-b2a0-fce32b36ec1f`, and `original_release_year=1975`. The first logged resolution is TextSearch for “Broken” at 15:57:21; collection assignment uses the conflicting group at 15:58:42. Later album resolution uses the propagated bridge ID. **Follow-up confirmed:** Q1139716 is Kraftwerk's album; the artist guard excludes the observed `retail_text` strategy, and the generic-year check misses scoped date claims. Dependency fallback bypasses application policy. | Preserve raw request/candidate evidence and prove the shared acceptance boundary against the packaged resolver. See the linked Wikidata validation review. This is not bad local year metadata; do not fix it by globally preferring local year. |
| A Time to Kill / American History X | **Confirmed:** both have identity jobs, one attempt, and terminal `RetailNoMatch`; no selected candidate, QID, retry, or explanatory last error. At 15:58:24 logs report zero candidates evaluated. Local claims retain full filename stems including year/codec/audio suffixes and lack a separate year claim. Artifacts record `no_match`. TMDB search requests and a zero-result response appear in the relevant batch, but request logs do not correlate each query to each asset. | Distinguish malformed search hints, provider skips, genuine zero results, credential/rate/timeout failure, and candidate rejection. Capture exact sanitized query and response/candidate evidence per entity. Do not claim no lookup ran. |
| Slow initial availability | **Confirmed:** initial processing of American History X took 55.1 s, including 54.9 s hashing; A Time to Kill took 93.9 s, including 93.7 s hashing. | Separate discovery, registration, inspection, dedupe/hash, identity scheduling, and playback-ready publication. Evaluate safe provisional registration/readiness before a full content hash, without losing duplicate protection or source-change checks. |
| Live Photo thumbnails | **Confirmed:** 18 HTTP 204 thumbnail responses concern three HEIC primary items, including two verified Live Photo pairs. JPEG Live Photo examples return images. Thumbnail generation tries Skia then FFmpeg and returns null on failure; the endpoint returns 204. Nonzero FFmpeg exit can be silent. | Determine decoder availability, HEIC variant support, correct still selection, and exact failure. Do not infer that all Live Photos are unsupported. |
| Gallery video second click | **Confirmed:** shared viewer renders video with controls and metadata preload, but ordinary opening has no automatic-start behavior. View uses the original URL for video. | Test user activation transfer and browser codec compatibility separately. |
| Premature completed additions | **Confirmed:** Home builds fresh candidates from works/group cards without a completion filter at that point. | Audit upstream projections, Discover/recent routes, group semantics, and completion timestamps together. |
| More menu actions | Handler exists but silently returns when `Model.PersonalStatus` is absent. Otherwise it calls progress/status, refreshes detail, and offers Undo. No matching status requests appeared in the inspected log window. | **Unproven root cause:** trace action key, callback, null state, request, target expansion, revision/profile, persistence, and invalidation. Do not assume a write occurred. |
| Heartbeat errors | **Confirmed:** 37 player heartbeat failures in the inspected window have foreign-key errors in `PlaybackTelemetryRepository.Create`. | Identify stale session/asset/profile or reset epoch and prevent a repeated failure loop. Do not assume it explains the More menu. |
| Processing text / toast spam | Songs and media tiles explicitly render processing text. Snackbar providers exist in main/reader/popup layouts and direct producers are widespread. | Inventory event-to-toast producers; the exact per-title background emitter is not yet identified. |
| Language/setup | **Confirmed:** settings use English display/metadata and US region, but setup does not ask. Fahrenheit’s Portuguese claims originate in local file metadata. Provider setup filters by configured media types. | Separate content language, metadata display language, matching locale, and provider relevance. |

### Observed API baseline (milliseconds)

Successful completed GET requests in the current log; small samples are directional. Stream-transfer duration is excluded because it is not page latency. Median shown is the upper middle observed sample; p95 is nearest-rank.

| Route family | Samples | p50 | p95 | Maximum |
|---|---:|---:|---:|---:|
| Display browse | 47 | 40 | 3,504 | 3,671 |
| Work detail | 21 | 440 | 3,909 | 3,931 |
| Person detail | 5 | 1,766 | 3,969 | 3,969 |
| Content groups | 2 | 4,055 | 4,055 | 4,055 |
| People list | 3 | 782 | 4,667 | 4,667 |
| People role counts | 2 | 4,443 | 4,443 | 4,443 |

## Shared state contract

One `IsUpdatingDetails` flag must not decide playback, editing, publishing, artwork, and review.

| Independent state | Meaning |
|---|---|
| Source availability | Registered/readable, missing, unsupported, or denied. Only authorized sources can play. |
| Playback readiness | Awaiting first technical inspection, direct playable, compatible stream preparing, playable stream available, or terminal failure. Enrichment is independent. |
| Identity/enrichment | Queued, processing, identified, completed with limited metadata, needs review, retry scheduled, or failed. Derive from durable operations, not elapsed-time guesses. |
| Artwork readiness | Queued/generating, ready, unavailable, or failed. Missing portrait after terminal enrichment is not still processing. |
| Edit/write eligibility | Catalogue edit lock and source write permission are separate. Profile progress/favorites/Continue actions are not metadata edits. |
| Completed addition | A once-per-owned-work transition after required work completes; review-blocked items are excluded. Unrelated optional biographies/artwork refresh cannot keep a correctly identified title pending forever. |

An album/show can be playable with one ready owned child while more children process. Count distinct works, not audiobook segments. People status must come from actual jobs, not absence of a QID/photo alone. Define required stages per media type and a truthful terminal outcome for valid local-only media. “No match” is not “identified,” and “provider unavailable” is not “no match.”

## Technical implementation

### WP0 — Reproduction and measurement foundation (P0)

- Preserve timestamped read-only evidence outside the library: logs, claim provenance, identifiers, job/package outcomes, and baseline timings. Use a consistent SQLite backup if later needed, never a raw active WAL-file copy.
- Correlate browser navigation, Dashboard call, Engine authorization, SQLite queries/waits, projection, image fetch/decode, and first video frame/audio. Include catalogue epoch, request generation, cache state, and category; exclude credentials and personal filenames from metric labels.
- Test cold process/derived cache and warm cache, idle and loaded ingestion, desktop/mobile, overlapping navigation, and representative library scale. Record hardware, storage, codecs, worker concurrency, and network conditions.
- Capture failed grouped views and More-menu traces before choosing repairs. Tests that only assert source strings do not establish these behaviors.

### WP1 — Setup locale and complete provider catalogue (P1)

- Add language/region choices before first import via validated settings APIs. Suggest system locale; preserve explicit values on resumed setup. Normalize language codes and show a supported fallback for invariant/unsupported OS locales. Country is independent, not inferred permanently from display language.
- Keep accepted media languages independent. Do not silently translate source titles or infer recording language from UI language. Prefer descriptions in selected metadata language with explicit fallback provenance and an unavailable-translation policy.
- Show all supported providers with media applicability and recommended/optional grouping; relevant services may sort first. Disabled supported providers need an explicit enable path rather than disappearing.
- Extend protected-harness setup policy narrowly: safe locale/provider saves must validate source separation and update any protected configuration snapshot atomically. Never broadly disable protection to save settings; verify provider credential actions remain usable.
- Test fresh/resumed setup, non-English OS, explicit English with Portuguese-tagged content, zero libraries, Comic Vine without Comics, and protected configuration invariants.

### WP2 — Early playback readiness and safe deferred writes (P0)

- Prioritize cheap source registration and durable technical inspection ahead of provider queues. Separate full hashing/deduplication from early availability where safe: provisional source identity includes path/source revision; validate unchanged source and reconcile duplicates atomically when hashing finishes. Do not publish two independent playback/progress identities for the same recording.
- Publish readiness immediately and throttle downstream UI updates without delaying it. Every launch entry retains one play intent through asynchronous mount and buffering; newer selection/pause/close cancels earlier intent.
- Playback and detail consume stored facts. Missing first-pass facts are ingestion state; page visits and Play never initiate FFprobe, hashing, or matching. Opening/decoding an authorized source and preparing necessary compatible streams are still legitimate playback operations.
- Metadata-only changes must not interrupt playback or replace the selected source. Missing/removed files produce a useful terminal error.
- Add renewable playback/read leases honored by all application writers: tags, sidecars, organization/rename/move, replacement, and cleanup. Queue only already-permitted writes durably, then revalidate source revision, policy, and all active readers. Pause retains a lease while a source is open; crash recovery expires abandoned leases safely; external locks back off. Protected read-only sources never acquire queued writes.
- Test active playback through identification, artwork updates, duplicate reconciliation, source changes, multiple clients, crashes, and queued mutations in a separate writable fixture library.

### WP3 — Compatible video and truthful failures (P0)

- Correct 10-bit input versus output pixel-format/profile selection. Persist bit depth and color facts at ingestion. Choose tested SDR conversion/tone mapping or supported HDR delivery; blindly truncating HDR information is not acceptable.
- Validate software/hardware FFmpeg command contracts. Publish first compatible video/audio segments independently of later renditions and subtitles. Prioritize requested playback over background derivatives with bounded CPU/I/O concurrency.
- Model queued/preparing/streaming/ready/failed explicitly. Propagate terminal encoder error immediately; do not overwrite failed with preparing or restart failed work on every manifest poll. Retry must be explicit or a bounded classified transient retry.
- Replace opaque polling cutoff with observable preparation progress and a defined timeout/recovery policy. Cancel obsolete client waits; retain shared preparation only while useful.
- Fix heartbeat FK failures with validated session/asset/profile references and reset epoch handling, never disabled foreign keys. Telemetry failure cannot halt decoding or flood the service.
- Exercise real failing TV, 8/10-bit H.264/HEVC, VC-1/TrueHD and DTS movies, audio tracks, subtitles, seek, software fallback, first-segment failure, and rapid selection switches.

### WP4 — Shared processing presentation (P1)

- Shared state DTO/component for rows, all artwork shapes, episode/chapter lists, contributors, people, network cards, search and detail. Yellow outline has no geometry change; preserve purple hover and accessible keyboard focus. Reduced motion is static.
- Remove repeated visible processing copy in list cells; retain status in accessible labels/tooltips. Avoid hundreds of live-region announcements.
- Distinguish actual pending work from review/error (static marker), retry wait, completed limited metadata, and unavailable art. Never infer pending merely from a blank image.
- Apply item/group-scoped updates with stable keys, batching, and viewport awareness. Do not repaint all shelves for each claim/chapter event; measure animation cost on long Songs lists.

### WP5 — Browse defaults and request-local outcomes (P0/P1)

- Replace shared mutable LastError as a result discriminator with typed success/empty/cancelled/error per request. Fix system views and content groups together. Obsolete cancellation cannot clear newer content.
- Return successful empty arrays when there are no eligible groups. Omit unrenderable structural groups; stable partially enriched people may appear with processing indicators. Never promote non-primary contributors to fill a view.
- Per-group defaults: Songs list; Albums, Artists, authors, networks, and comparable groups cards. Respect supported explicit URL/user choices scoped to grouping.
- Test all lane/group combinations, filters and timelines during ingestion, including an unrelated failing heartbeat followed by successful empty artists/networks.

### WP6 — Fast read projections and appropriate images (P0, throughout)

- Replace whole-catalogue loading for one detail with indexed entity-scoped authorization/projections, preserving all visibility and canonical contributor constraints.
- Batch person credits/relationships/artwork reads, eliminate duplicate member queries, bound initial owned-work lists, and fetch secondary sections independently. Use query plans/counts to justify indexes.
- Profile write contention and worker saturation. Keep transactions short, prioritize interactive reads/playback, and bound background work. Neither unlimited parallelism nor warm caching alone fixes cold-request cost.
- Version projection caches by entity revision, profile/access scope, language, filters and catalogue epoch. Invalidate affected projections only; prevent cross-profile leakage and stale reset data.
- Audit every image surface: song thumbnail, album/artist card, person portrait, network logo, episode still, cover/poster, hero, search, View grid and immersive preview. Shared purpose presets select smallest adequate src plus truthful srcset/sizes for DPR 1/2/3. Reserve dimensions/aspect ratios, lazy-load below-fold images, prioritize only visible critical art; compact cards must not fetch originals.
- Add repeatable page/interaction performance tests reporting distributions, payloads, image sizes, query counts, lock waits and long tasks. Include active ingestion and cache invalidation correctness.

### WP7 — Identity, release-name parsing, dates, and language (P0)

**Wikidata decision, updated after owner review:** retain album and work matching, with a shared evidence-validation boundary. See [Wikidata match validation review](./wikidata-match-validation-review-2026-09-27.md) for the product walkthrough, exact Radioactivity failure chain, cross-media tradeoffs, and V1–V5 acceptance tests. The investigation found a strategy-gated artist safeguard bypass (`retail_text` versus `music_album`), dependency fallback that escapes application policy, a generic-year check that misses scoped original-release dates, and propagation of a conflicting release-group ID. No blanket removal of album matching is proposed.

**Confirmed movie query-construction defect:** `VideoProcessor` strips a movie year only at the end of the title. `ConfigDrivenAdapter.CleanTitleForSearch` likewise strips only trailing `(YYYY)` and trailing `SxxExx`; `ExtractYearFromTitle` is also end-anchored. Thus `A Time to Kill (1996) - VC1 TrueHD` and `American History X (1998) - VC1 DTS-HD MA` retain both the year and technical suffix in the search title, and fail to extract the year. TMDB's movie strategy uses `{title}` and `{year}` directly. This establishes a malformed-query path consistent with the zero-candidate outcomes; capture correlated provider responses to prove how much of each outcome it explains. Do not conflate provider lookup execution with successful identification.

- Add deterministic, media-aware search-hint parsing before provider lookup, independent of optional AI/SmartLabeler availability. Separate original filename, source-authored display title, parsed search title, year, technical/edition hints, and confidence/provenance. Never rewrite the source filename or tags as a lookup prerequisite.
- Prefer a verified external ID of the correct scope, then compatible embedded title/year and parent-folder evidence, then conservative filename parsing. Conflicting folder/file/embedded values remain explicit evidence, not whichever happened to run last.
- Use bounded provider strategies: clean title + known year; controlled retry without an uncertain year; credible alternate/original title or parent-folder title. Log which strategy ran and why. Do not progressively remove arbitrary words until something matches, select the first result, or lower global matching thresholds to hide failures.
- Distinguish no configured provider, invalid credentials, timeout, rate limit, provider error, zero results, rejected candidates, ambiguous match, and accepted identity. Transient errors stay retryable operational work; only genuine unresolved ambiguity goes to human review. Store query/candidate counts and sanitized rejection reasons per entity, including zero-result requests. Keep usable local files playable while metadata remains explicitly incomplete.
- Validate candidate media type, title, year tolerance, creator/artist/show, stable IDs, and episode/edition scope together. Movie remakes need year/context. Original-release years and reissue/edition years are separate facts.
- Apply the validation review’s V1–V5 across Radioactivity’s first text-search candidate, bridge propagation, canonical scoring, collection assignment, and date display. Incompatible authoritative release-group IDs or primary-artist identities must veto automatic propagation; an uncertain text match cannot manufacture a trusted bridge that later confirms itself. Run the same validation for text, identifier, batch, retry, and reused identities before persistence. Preserve aliases, compilations, collaborations and legitimate edition dates using scoped identity evidence. Keep source/user-locked facts intact.
- Dates must come from the correctly resolved identity and use the same semantics in filters, timeline, card, and detail. Do not hardcode a title, force 2013 manually, or globally prefer local year to mask a wrong entity match.
- Separate content language from metadata display language. The Portuguese Fahrenheit description is local evidence, not proof of a Spanish provider lookup. Prefer selected-language metadata and expose fallback provenance; do not rename embedded chapter titles or silently translate source-authored fields.
- Leave current records unchanged. Produce expected-match evidence for later authorized reingestion: title, year, artist/creator, provider IDs, groups, review outcome, and duplicate counts.

#### Filename/release-name edge-case matrix

Downloaded/torrent-style release names are parsing inputs, not trusted metadata. No downloading from those sites is part of this work.

| Cases | Expected behavior |
|---|---|
| `A Time to Kill (1996) - VC1 TrueHD.mkv`; `American History X (1998) - VC1 DTS-HD MA.mkv` | Search clean title with 1996/1998 separately; retain technical labels as source/format hints. |
| Dots, underscores, repeated spaces, hyphens, Unicode punctuation | Normalize separators conservatively without destroying real punctuation or words. |
| `1080p`, `2160p`, `4K`, `UHD`, `HDR10`, `DV`, `10bit` | Recognize bounded technical tokens; do not use them as title words. Do not infer codec/color truth solely from filename. |
| `VC-1`/`VC1`, `x264`, `x265`, `H.264`, `HEVC`, `AV1` | Keep codec hints separate; technical inspection remains authoritative. |
| `TrueHD`, `DTS-HD MA`, `DDP5.1`, `Atmos`, `AAC`, channel counts | Remove recognized release suffixes from search hints without stripping legitimate title text. |
| `BluRay`, `WEB-DL`, `WEBRip`, `BDRemux`, `REMUX`, `HDTV`; bracketed tags and trailing release group | Parse technical source/group spans only in release-name context; preserve original name and diagnostic provenance. |
| `REPACK`, `PROPER`, `MULTi`, `DUAL`, language/subtitle tags | Treat as release/content hints, not identity or chosen metadata language. |
| Director's Cut, Extended, Unrated, Remastered, theatrical, anniversary editions | Preserve edition hints separately; do not merge distinct works blindly or use reissue year as original year. |
| `1917`, `1984`, `2001: A Space Odyssey`, `Blade Runner 2049`, `Se7en`, `1917 (2019)` | Preserve numbers that are genuinely part of titles; extract only context-supported release years. |
| Remakes and conflicting/multiple years | Use bounded year evidence and candidate comparison; do not choose whichever year appears first. |
| `S01E02`, `S01E02E03`, `1x02`, daily-date episodes, specials, anime absolute numbering | Separate series/season/episode identity; preserve multiple-episode scope. Ambiguous absolute numbering requires evidence rather than guessed seasons. |
| `CD1`, `CD2`, `Part 1`, disc/track prefixes, chapter-only names | Group with folder/embedded evidence without erasing a real title such as Part II; no audiobook work per chapter. |
| `{imdb-...}`, `{tmdb-...}`, provider IDs in brackets | Validate identifier syntax and entity scope; IDs are hints, never arbitrary URLs/commands. |
| Non-Latin titles, accents, translated titles, meaningful brackets | Preserve Unicode and original title; use explicit aliases and locale-aware strategies. Do not strip all bracketed text. |
| Ambiguous words such as `Web`, `True`, `HD`, `IT`, artist names containing tag-like text | Require token boundaries and release-context evidence; never use a blanket global replacement list. |
| Missing embedded title; clean parent folder / noisy child filename; conflicting folder | Use scored folder context, preserving the conflict in diagnostics. No source-folder changes. |

Tests must assert exact outgoing query hints and accepted/rejected identities with controlled provider responses, plus later real-corpus checks. Include negative tests preventing over-cleaning and false-positive matches, not just successful removal of tags.

### WP8 — View derivatives and one-action video (P1)

- Inspect the three failing HEIC items read-only; capture decoder support and FFmpeg stderr/exit status. Verify primary still/companion selection without changing files.
- Generate browser-supported thumbnails/previews in managed storage through bounded View indexing jobs. Expose queued/ready/unavailable/failed state. Warm image delivery must not repeat costly decode work; deduplicate any bounded lazy fallback.
- Do not send an empty image response without a UI fallback. Keep stable placeholders while generating; retry only after meaningful state change. Unsupported formats get a terminal explanation. A motion-frame fallback must have an explicit, truthful policy.
- Preserve video-start intent through viewer mount/source readiness; handle rejected play promises and unsupported originals accurately. Stop old media on close/navigation. Opening a Live Photo still remains distinct from playing its motion.

### WP9 — Library activity and completion-based additions (P1)

- Reuse durable ingestion/job state for Processing / Needs review / Ready filters and distinct-work counts. Keep source discovery/registration timestamp separate from first-ready timestamp.
- Completed-addition shelves require required-stage completion, not asset insertion or file-completion events. A provider failure or an unidentified title does not count as identified. Define how explicitly accepted local-only items become Ready without falsely claiming provider identification.
- First-ready timestamp is set once; metadata refresh does not reorder old additions. Partially ready containers remain truthful; no separate addition per audiobook segment.
- Reuse the shared browser and Operations data, preserving permissions. Ordinary users see only accessible media and relevant status; diagnostics remain administrative. No duplicate Activity/Audit admin destination.

### WP10 — Personal status commands and refreshed projections (P0)

- Trace each More action: rendered key → callback → status load → request → profile/target expansion → revision/idempotency → persistence → detail/shelf refresh. Capture a controlled reproduction before assigning root cause.
- Do not render an actionable command that silently returns when personal state is missing. Keep personal state independent of metadata edit locks.
- Return updated status/revision and affected identities; invalidate detail, Continue, Home, For Me and browse for that profile. Subsequent heartbeats must not silently reverse manual completion or HideContinue.
- Verify books, comics, movies, TV parents/episodes, audiobook segments, albums, profile switches, reload, concurrent sessions, repeated clicks, conflicts and Undo after later progress. Use reversible test state; do not edit source media.

### WP11 — Activity notification coordinator (P1)

- Inventory server events, direct Snackbar producers and every main/popup/reader host. Trace the reported per-title emitter. Route background notifications through a scoped coordinator; local validation stays inline.
- Envelope includes stable event/run/operation IDs, category, severity, audience/profile, dedupe key, timestamp/expiry, action link and presentation policy. Persist meaningful unresolved events; bound the queue, prioritize and coalesce. Reconnect/replay cannot duplicate old toasts.
- Proposed default: one visible toast at a time, at most one background summary per 30 seconds, and at most one start/terminal summary per ingestion run. On leaving playback, show at most one current summary, not every deferred event; expire informational noise. Keep persistent actionable warnings in history/banner state.
- Suppress unrelated overlays/sounds during video, audio, reading and gallery playback across main/popup hosts. Current playback errors remain inline. User-initiated operations get nearby feedback and Undo where applicable.

| Activity | Notification policy |
|---|---|
| Explicit scan/import starts | One run confirmation. Automatic scans normally update activity indicator/history silently. |
| Ingestion completes | One truthful Ready / Needs review / Failed summary after required work finishes. |
| Run pauses/fails/needs intervention | One actionable warning linked to Operations; retries update it rather than add toasts. |
| Review items become available | Coalesced count, no titles. |
| Provider unavailable/recovered | Notify only sustained meaningful impact/change, not every request/retry. |
| Source missing/read denied/protection failure | Persistent scoped warning/history. Unsafe work stops independently of toast suppression. |
| Authorized backup/export/restore finishes/fails | One operation summary with appropriate audience. |
| User save/status/list action | Immediate local feedback and Undo; bulk operations aggregate. |
| Current playback preparation/failure | Inline player state, never a global background toast. |
| Item identified, chapter scanned, artwork/person enriched, progress/heartbeat | Silent item/status update and diagnostics only. |

## Performance acceptance budgets

Initial local/LAN targets on the recorded workstation and real corpus. Report p50/p95, not one fast sample. Establish idle and ingestion-loaded baselines, then at least 30 navigations per key route/scenario and repeated sessions that expose tail latency. Larger-scale tests use metadata fixtures outside protected originals.

| Experience | Target |
|---|---|
| Action feedback/skeleton | Within 100 ms; no blank pending page. |
| Warm browse/detail API | p95 <= 300 ms, no unbounded full-library work per detail. |
| Cold or ingestion-loaded browse/detail API | p95 <= 750 ms; documented exceptions must be fixed or explicitly accepted. |
| First useful content, local desktop | p95 <= 500 ms warm, <= 1 s cold/under ingestion; secondary sections do not block. |
| Mobile/throttled LAN | First useful content p95 <= 1.5 s; LCP <= 2.5 s and INP <= 200 ms under the declared profile. |
| Layout stability | CLS <= 0.1; processing outlines and delayed art do not resize rows/cards. |
| Direct-play first audio/frame | p95 <= 1 s warm, <= 2 s cold under normal local conditions. |
| Compatible-stream first frame | Target p95 <= 5 s for supported representative sources; acknowledge action within 100 ms. Publish codec/hardware limits rather than promise zero conversion time. Never wait for full-file encode. |
| Ready item visible | Within 2 s of its durable playback-ready event; provider enrichment continues independently. |
| Cached thumbnails | Endpoint p95 <= 100 ms, with no decode in the warm path. |

Record navigation-to-content, LCP, interactivity, first image/frame/audio, JSON/image bytes, selected rendition dimensions, DB query count/plans, waits and CPU/I/O. Test desktop/mobile viewport and DPR 1/2/3, cold/warm caches, concurrent ingestion and profile authorization. Separate startup source verification from page timings and transfer lifetime from playback-start latency. Gate both absolute budgets and meaningful relative regressions; do not omit slow/error samples to pass.

## Implementation sequence and completion gates

1. WP0 establishes evidence and reproducible cases. Prioritize WP3 video/state handling, WP5 request-local empty results, WP10 action wiring/persistence and WP7 matching correctness.
2. WP2 readiness/write leases and WP6 read-path performance establish shared contracts consumed by WP4 processing indicators, WP8 View, WP9 activity and WP11 notifications. WP1 must be ready before later fresh ingestion.
3. Validate contracts and browser journeys under provider delay/outage, stalled/retried jobs, missing portraits, empty groups, rapid navigation/cancellation and concurrent playback/ingestion. New tests must exercise behavior, not merely mirror implementation text.
4. Perform reingestion only when later authorized. Compare expected identity/date/query outcomes, title grouping, duplicates, review decisions, completed-addition counts and source integrity. Do not patch Radioactivity, Fahrenheit, or the two movies to manufacture a passing demonstration.
5. Deliver before/after timing distributions, processing/layout/Live Photo screenshots, actual playback startup measurements, persistent More-action tests, notification flood/quiet-mode results and source-preservation evidence. A build alone does not complete a work package; report remaining limitations explicitly.

## Source map

- Setup: `SetupProvidersStage.razor`, `SetupPage.razor`, `SetupEndpoints`, `CoreConfiguration.LanguagePreferences`, protected `RealMediaEndpoints`.
- Playback: `PlaybackSessionController.EnsurePlayableAsync`, `AdaptiveHlsService`, `PlaybackInspectionWriter`, `PlaybackTelemetryRepository`, writeback/organization paths.
- Browse/status: `ListenSongTable`, `MediaTile`, `MediaBrowseShell.LoadGroupsAsync`, `EngineApiClient.SearchMisc`, `BrowseQueryBuilder`, `ListenBrowseConfiguration`, contributor/portrait components.
- Performance: `DetailEndpoints`, `DetailCompositionOrchestrator.Entities`, display/read repositories, shared artwork sizing helpers.
- Matching: `VideoProcessor.BuildClaims`, `ConfigDrivenAdapter.CleanTitleForSearch`/`ExtractYearFromTitle`, `tmdb.json`, `WikidataBridgeWorker.JobResolution`, reconciliation/canonical scoring, `CollectionAssignmentService`, `MediaDateSemantics`.
- View: `ViewEndpoints`, `ViewThumbnailService`, `ViewLibraryService`, `ViewImmersiveViewer`, `MediaViewerShell`.
- Personal state: `DetailPage.HandlePersonalStatusAsync`, `EngineApiClient.PersonalStatus`, progress endpoints/repositories and Continue projections.
- Notifications: `UIOrchestratorService`, `UniverseStateContainer`, `ShellActivityState`, direct Snackbar callers and main/popup/reader hosts.

## Plain-English completion summary

This plan makes technically ready files playable before metadata finishes, makes pending work visible without repeated text, keeps unfinished items out of completed-addition shelves, and protects playback from background notifications. It prioritizes confirmed video, matching, thumbnail and request-state defects while measuring remaining page and action failures before implementation. Original media and existing catalogue records remain unchanged during planning.
