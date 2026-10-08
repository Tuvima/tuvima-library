# Real-media remediation: identity, availability, playback, and reading

Status: implementation and existing-catalogue repair applied on September 27, 2026. The product walkthrough below remains the acceptance contract. See [implementation and verification report](./real-media-remediation-verification-2026-09-27.md) for completed changes, observed results, and remaining limits. The repair preserves the existing real-media ingestion rather than repeating the initial wipe.

## Product walkthrough

The collection stays in its existing folders under `C:\Temp\real_import`. Read, Watch, Listen, View, detail pages, and personal progress retain their established roles. The change is that usable files appear promptly, their relationships are correct, and players and the reader behave reliably. Each numbered experience below maps to technical work packages later in this document.

1. **Descriptions read like descriptions.** When a provider supplies paragraphs or HTML entities, users see readable text with paragraph spacing, never literal `<p>` tags or executable provider markup. The same description is consistent on cards, heroes, details, search, and review. Acceptance: plain text, encoded HTML, and mixed provider descriptions display safely without tags. **W1.**

2. **An audiobook folder supplies useful evidence.** An audiobook source containing `Author/Book/Disc/Track` is interpreted as a recording with ordered parts. Folder hints supplement tags and sidecars; conflicting editions or recordings are not silently merged. Acceptance: missing track tags do not send every chapter to independent identity review. **W2.**

3. **One audiobook appears once.** Fahrenheit 451 has one library tile and one audiobook detail page, with its original chapter/track names in an ordered track list. A book series remains a separate sequence of books, not chapters. A failed match produces one book-level identity review with track diagnostics available beneath it. Acceptance: repeated scans, restarts, and late-arriving parts create no duplicate book tiles or chapter-sized review flood. **W2, W10.**

4. **Usable media appears before enrichment finishes.** After local checks establish a readable media item and its parent relationship, it appears with local information and a small **Updating details** status. Metadata and artwork improve in place without losing selection, changing the item's address, or stopping playback. Play/Read/Listen and personal progress remain available when supported. Metadata edits, rematching, renaming, moving, deletion, and structural changes are disabled with a reason while required ingestion work is active. A terminal **Needs review** state ends the worker lock and exposes correction tools; it does not make a usable original disappear. Corrupt, inaccessible, unauthorized, or explicitly rejected media remains excluded. Acceptance: a slow/offline provider does not block browsing local media or leave editing locked forever. **W3.**

5. **The music player links to the right identities.** The supplied compact music layout shows cover, song title, a linked artist, and a linked album beside transport controls. Remove the generic **View details** action for music. Artist links open the canonical person page; album links open album details. Unresolved identities remain readable text, with no fabricated links or track-as-book destination. Acceptance: navigation does not interrupt the song. **W5.**

6. **One Play action starts playback.** Selecting a song, album, movie, episode, or audiobook starts the requested item once it is playable, without a second click in the player. Pause means media is actually playing; buffering and preparation have their own states. A genuine browser autoplay denial presents one clear **Tap to play** recovery rather than pretending playback started. Acceptance: first play, rapid item changes, and returning to a paused item have predictable behavior. **W4, W7.**

7. **Song rows provide navigation as well as playback.** Clicking the song starts it; clicking its artist or album opens the corresponding detail page without also starting or replacing playback. The links work with keyboard navigation and fit compact/mobile layouts. Acceptance: every resolved artist and album in Songs has the correct destination. **W5.**

8. **Album dates belong to the identified album.** Radioactivity by Age of Days should use the correct release identity and its supported 2013 date, not another same-name album's 1975 date. Original release and edition/reissue dates remain distinguishable. Acceptance: album detail, song list, grouping, and timeline agree, and the correction survives re-enrichment. No title-specific date override is added to runtime code. **W6.**

9. **Video starts promptly and presents usable controls.** The supplied video reference guides a full-frame picture, restrained title/episode identity at the top, and an organized bottom transport bar. A compatible source starts directly; sources needing conversion start from usable initial segments rather than waiting for a complete film encode. Preparation, buffering, unsupported media, and errors are visible. Captions, audio, quality, speed, history, chapters, Next Up, volume, picture-in-picture, and fullscreen appear only as supported. Acceptance: real movie and TV samples play with advancing time, working audio, subtitles where supported, seeking, and resume. **W4, W7.**

10. **Reading is legible and resumes at the same text.** The supplied reader reference guides a quiet dark shell, centered high-contrast page, restrained top controls, clear previous/next navigation, and a truthful bottom progress row. Closing, reopening, resizing, or changing font size returns to the same textual location. Book progress is based on position within the whole book, not an equal share for each contents entry. Turning a page never creates a highlight. Acceptance: Project Hail Mary restores the same passage across sessions and font/viewport changes; chapter 1 is not arbitrarily 15% because front matter occupied several spine entries. **W8.**

11. **Every owned TV episode is reachable in both directions.** Episode rails expose visible previous/next controls when overflow exists, plus touch/trackpad and keyboard navigation. Users can return to earlier episodes, switch seasons, and return from episode details without losing their place. Acceptance: the first, middle, and last owned episodes are reachable on narrow and wide screens. Provider-only episodes remain outside owned lists. **W9.**

Representative journeys: add an audiobook folder and see one book develop its metadata; start a newly discovered song and follow its artist while audio continues; open a movie during enrichment and watch without waiting for every provider; read several pages, resize, close, and resume at the same sentence; browse to the final TV episode and navigate back to the first.

Scope boundaries: this work repairs identity/grouping, availability and edit eligibility, navigation, playback delivery, reader state, and the affected presentation surfaces. It does not reorganize originals, rewrite tags, perform another automatic full wipe, manufacture metadata or subtitles, add title-specific exceptions, remove DRM, or promise every ISO/codec can play. Existing source protection and the prior directory-timestamp incident remain visible and are not reset to a clean history.

## Screenshot contract

The attachments are visual references supplied for this plan, not separate execution instructions. Their displayed book text, page totals, percentages, time estimates, and media names are not implementation constants. Use the actual EPUB and media records, not text copied out of the screenshots.

| Reference | Preserve in the proposed experience | Adapt to real capability and screen size |
| --- | --- | --- |
| *Screenshot removed during documentation cleanup; no capture is retained in this repository.* | Centered readable paper, dark surrounding shell, top title/contents/search/type/bookmark/exit controls, side page arrows, bottom position and progress | Reflow text and controls; truthful whole-book progress; no fabricated `286` pages or reading-time estimates. Label chapter pages separately if whole-book pagination is unavailable. |
| *Screenshot removed during documentation cleanup; no capture is retained in this repository.* | Video dominates the surface; episode/show identity top left; minimize/close at top; seek row above grouped bottom transport/tools; prominent play/pause | Maintain video aspect ratio without stretching; avoid tool wrapping over the picture; overflow secondary tools on small screens; caption-safe margins; genuine loading/error states. |
| *Screenshot removed during documentation cleanup; no capture is retained in this repository.* | Compact horizontal player with square art, linked artist/album, central transport and seek, grouped queue/history/lyrics/shuffle/repeat/volume/display controls | Stack predictably on mobile; links stay distinct from play; hide unsupported lyrics/display actions; use shared visual tokens and accessible contrast/focus. |

Do not turn the audio bar into a new detail surface or add standalone song pages. Audiobook details retain the existing Overview/Details and track-table contract, source-authored titles, bookmarks, speed, sleep, history, and per-track progress. No copied mockup text, screenshot-as-background UI, or decorative controls without behavior.

## Evidence and remaining diagnosis

These findings are from read-only source/database inspection; suspected causes must still be reproduced before fixes are finalized.

| Area | Confirmed observation | Consequence for implementation |
| --- | --- | --- |
| Provider descriptions | `ValueTransformCatalog` already has `strip_html` and `sanitize_html`; some provider mappings use them. Sanitization currently retains selected tags. | Trace the actual claim-to-display path and encoded content. Do not assume one missing global transform or render raw markup to conceal the symptom. |
| Audiobook grouping | `AudioProcessor.AddAudiobookCompanionClaims` recognizes certain tags/sidecars, enumerates siblings in lexical path order, and emits part metadata. `HierarchyResolver.ResolveBookOrAudiobookAsync` groups multipart audio only when series is absent and part-count/book-title evidence exists. | Repair both evidence propagation and hierarchy semantics, including books within a series and natural numeric ordering. Existing grouping is partial, not absent. |
| Duplicate title | Current Fahrenheit 451 asset canonical rows repeatedly contain `album=Fahrenheit 451`. | Trace why shared evidence does not produce a single visible recording and why track identity is used for provider matching. |
| Early visibility | `HomeVisibilitySql` excludes pending review and terminal identity no-match states from normal display. | Availability must not be derived solely from metadata confidence or success. Apply consistent visibility across discovery, browse, search, and details. |
| Music navigation | `ListenNowPlayingBar` has **View Details** and a generic `/details/work/…` fallback; `ListenSongTable` renders artist and album cells as spans. | Add explicit canonical album/person references to contracts and share route resolution. |
| Radioactivity | The source path is `music/Age of Days/Radioactivity/Broken.m4a`; its asset has `year=2013` and `original_release_year=1975`. `MediaDateSql` gives explicit original-year claims precedence. | Trace the winning provider and album relationship. Identity contamination versus precedence is not yet established; fix the cause before projecting a new date. |
| Video | The previous harness run reached manifests but remained at 0:00 while adaptive packages were preparing. `AdaptiveHlsService` currently publishes a package after its preparation steps complete. | Measure direct-stream, preparation, first-segment, and browser attachment separately. Package completion and readiness to start are different events. |
| Reader | `EpubReader` stores chapter index and page-in-chapter; percentage is `(chapter index + chapter page fraction) / chapter count`. JS pagination depends on resized columns. | Stable text anchors and content-weighted book progression are required. Highlight symptoms need a focused reproduction, not an assumed CSS-only fix. |
| TV navigation | `SequencePlacementPanel` has multiple season/episode branches and some paging controls; detail styles also have overflow rules. | Exercise the failing branch and real overflow measurements rather than append a second independent carousel. |

## Technical work packages

### W0 — Reproduction baseline and source preservation

Record source manifest, current protection state, schema/config versions, representative asset/work/parent IDs, review reasons, provider claims, and playback/reader diagnostics in the existing ignored reports area. Keep original hashes and violation evidence immutable. For implementation, stop the development apps before builds and controlled repair operations, preserving current work and restart recovery. Do not stop/reset them merely to write this plan.

Use read-only real media for integration tests and isolated small fixtures for fault injection. All thumbnails, subtitles, segments, extracted EPUB data, logs, and temporary test files stay in approved Tuvima output locations. Monitor additions, deletions, renames, content/metadata changes and lost monitoring coverage. Compare against the original baseline and separately report the already-known three directory timestamp differences.

### W1 — A consistent description representation

Trace retail adapters, sidecars, claim scoring, canonical storage, display DTOs, and `DescriptionAttribution`/hero consumers. Preserve raw provider evidence separately, but make user-facing descriptions plain text with paragraph boundaries under a shared normalization contract. Decode entities in a bounded, deterministic way, parse markup safely, discard script/style content, and encode output; do not fix this with raw `MarkupString` or regex-only trust in upstream data. If a rich-text surface is explicitly retained, it must use a parser-backed allowlist independently of the plain-text projection.

Make normalization idempotent and apply it to newly ingested and existing affected descriptions. Backfill only derived/provider values, preserving user overrides and attribution. Test nested/encoded tags, entities, paragraphs, malformed markup, script payloads, and literal comparison symbols across all consuming surfaces.

### W2 — Recording-level audiobook ingestion and identity

Extend the shared intake context with source-scoped recording evidence: configured audiobook category, bounded book-folder identity, disc/part subfolders, sidecar book title/author/narrator/edition IDs, album tags, track/disc numbers, and filename fallback. Use the configured library type as strong type evidence and folder names as confidence-bearing hints, never cross-library identity keys. Do not scan unrelated folders or read/write companion files outside the protected source policy.

Represent a series as ordered books, each audiobook recording as one user-facing book/edition, and its files as playable ordered parts. Multiple recordings/abridgements/languages/narrators remain distinct editions when evidence warrants it. Retain current asset IDs and source-authored titles; parts must not masquerade as separate books or separate volumes in a series. Extend the existing hierarchy path rather than introduce a parallel audiobook catalogue.

Resolve one stable recording identity before provider matching. Use explicit identifiers first; within a source use a durable folder/recording key until trusted external evidence arrives. Never globally merge by normalized title alone. Order by reliable disc/track metadata, then natural numeric filename evidence, then deterministic fallback with ambiguity diagnostics; preserve gaps and do not lexically place 10 before 2. A late file updates the recording without changing its public identity or resetting progress.

Run book-level retail/Wikidata matching once per recording, with idempotent jobs and concurrent-discovery protection. Aggregate identity review at recording level; retain file-specific corruption or decode diagnostics separately. Project exactly one audiobook in Home, Listen, search, people credits, Continue, and collections, with parts beneath its detail/player. Test missing sidecars/tags, malformed sidecars, nested discs, partial arrivals, books in a series, same-title different authors/recordings, concurrent rescans, and interrupted registration.

### W3 — Early availability with durable edit locks

Add a shared per-item projection that separates local availability, required ingestion phase, metadata confidence/review state, playback capability, and allowed actions. Names below describe the proposed contract, not claimed existing fields:

| State | Normal library behavior | Metadata/structural editing |
| --- | --- | --- |
| Discovered / checking | Remains in Operations until source, media type, and stable identity are committed | Locked |
| Locally available / enriching | Visible using local metadata; **Updating details**; supported playback/reading enabled | Locked with phase/reason |
| Required work waiting/retrying | Still visible if usable; truthful waiting state; retry/cancel available to authorized administrators | Locked until safely settled or cancelled |
| Ready | Normal library experience | Allowed by normal permissions |
| Terminal needs review | Remains visible if usable; **Details need review** and correction entry point | Allowed once workers release the item |
| Corrupt, missing, inaccessible, rejected, unauthorized | Explicit diagnostic or exclusion according to existing policy; no fabricated playable item | Existing recovery/authorization policy |

Derive lock/completion from durable required operations and identity jobs, including resumed jobs and recordings with multiple parts. Optional deferred enrichment must not keep an item locked indefinitely. Cancelling or reaching terminal review must settle work and release leases safely; elapsed time alone must not claim completion. Re-enrichment acquires a bounded editing lease while playback remains usable.

Enforce action eligibility in API mutation handlers as well as UI. Recheck inside the write transaction/version boundary to prevent a stale editor racing ingestion; return a structured conflict with reason and retry guidance. Preserve personal actions such as progress, bookmarks, reactions and My List. Define the protected structural operations explicitly, including metadata edit, rematch, reparent, delete, move, tag writeback and organization.

Commit lightweight local projections early, then invalidate server caches and emit bounded SignalR updates using the same stable IDs. Update cards in place without resetting focus, scroll or playback; avoid flashing duplicates while parent grouping settles. Use one unobtrusive status line/badge and no misleading percentage or new inline card actions. Include artwork rendition sizes in updated contracts. Proposed performance gate: visible within 2 seconds of local registration commit on the reference machine, independent of provider delay; measure discovery/probing latency separately.

### W4 — One playback intent and truthful transport state

Trace the click through song/detail/Home actions, `PlaybackSessionController`, audio/video hosts, JS source attachment, and browser `play()` result. Preserve the initial user activation where possible, attach the chosen source once the element exists, and carry the pending play intent through asynchronous manifest/delivery preparation. Do not optimistically mark a session playing before browser media events establish it.

Use explicit preparing, buffering, playing, paused, blocked-by-browser, and failed states. Cancel superseded requests when another item is chosen or the player closes; a late manifest must not start an old item. Recover from actual autoplay rejection with a clear gesture control. Make album shuffle/queue, audiobook part transitions, episode Next Up, and restart/resume use the same transport contract. Instrument click-to-attachment, first media time advancement, stalls, and error category without logging credentials or signed stream URLs.

### W5 — Music links and player composition

Extend queue/song DTOs with canonical album ID/route and ordered primary artist person references. Resolve routes centrally for `ListenNowPlayingBar`, `ListenPlayerPopupPage`, `ListenSongTable`, album track tables, and queue/history surfaces. Do not derive routes from display names or reinterpret a song as a book. Unresolved references render as text until hydration supplies verified links; real coartists remain separately navigable in canonical order.

Remove music **View Details**. Apply the music screenshot composition using shared controls and tokens, bounded artwork renditions, responsive sizing, and persistent transport state. Artist/album links in rows stop row-play propagation and have accessible focus targets; avoid nested interactive elements. Test navigation while playing, mixed/compilation albums, long labels, missing IDs, keyboard operation, compact layouts, and popup/full-player parity.

### W6 — Identity-backed album dates and targeted repair

Trace Radioactivity's 1975 claim through provider candidate selection, identifiers, artist evidence, canonical winner, parent album, and `MediaDateSemantics`/`MediaDateSql`. Require sufficient artist-plus-album and/or stable identifier evidence for album matches and enrichment relationships. Treat incompatible artist identity as a reason to reject or review a candidate; title overlap alone is insufficient.

Define original album release versus edition release consistently. Project the verified album's date into album/song timeline grouping; a track's unrelated original composition year or an unmatched provider candidate cannot override it. If identity is unresolved, prefer supported local release evidence or unknown with provenance instead of an authoritative-looking unrelated year. Repair only claims/links demonstrated to be contaminated, invalidate derived projections, and retain manual overrides. Re-run enrichment to prove the bad value does not return. Use Radioactivity as a regression example, not a runtime special case; include same-name albums by different artists and reissues.

### W7 — Progressive video delivery and player layout

Trace `PlaybackCapabilitiesService`, `AdaptiveHlsService`, manifest DTOs, dashboard proxy/authorization, `WatchPlayerPage`, `VideoPlaybackHost`, and source attachment. Distinguish compatible direct playback, container remux/audio conversion, and full video transcoding based on actual browser capabilities and probed streams.

For adaptive delivery, publish a valid authorized live/event playlist once initial segments are ready while generation continues. Separate readiness to begin from a complete reusable package. Do not expose half-written segments or require every rendition/subtitle to finish before first playback. Add explicit preparation progress/readiness, cancellation, bounded concurrency, seek-to-unbuffered behavior, retry and cleanup. Preserve access expiry/authorization on manifests and segments. Do not silently fall back to an incompatible original when adaptive delivery is unavailable.

Make embedded/sidecar text captions available independently of full video encoding; normalize to managed outputs, preserve language/default/forced distinctions and offsets. ASS styling may be simplified with clear behavior; image subtitles require a supported rendering/burn-in path or an explicit unsupported state, never a false CC promise. Verify cue rendering, not merely extraction. Audio-track switching, duration, seek, resume, and Next Up must use the actual playback asset/episode.

Implement the video screenshot layout with content-safe overlays, usable mobile overflow, truthful enabled controls, and no main app chrome over fullscreen video. Proposed warm-local start targets: first advancing video within 2 seconds for direct play and within 5 seconds for remux; target 10 seconds for a reference transcode after capability benchmarking. Record cold/cached performance separately and do not mark success by meeting the target on generated media alone.

### W8 — Stable EPUB locations, progression, and presentation

Replace persistent page-in-chapter identity with a versioned locator tied to asset/content fingerprint, spine resource, and stable text/DOM anchor (CFI-equivalent supported by the current renderer), with text quote/context fallback. Layout page numbers become derived display state. Migrate existing chapter/page progress as a best-effort locator after layout, preserving the old value until a successful restore/save. Do not silently erase bookmarks or highlights.

Restore only after chapter content, fonts and layout are ready; coalesce repagination, suppress transient initial-page saves, and sequence saves so stale callbacks from earlier chapters/devices cannot overwrite a newer location. Preserve a real zero-percent position. Font, theme, viewport and orientation changes reflow around the current text anchor rather than keeping a now-unrelated page index. Flush progress on exit with recovery for disconnects.

Compute whole-book progression from stable content locations or normalized readable-content lengths across the ordered spine, with an explicit policy for front matter, navigation documents and non-reading resources. Never count each TOC entry as equal length. Display chapter-local pages with a chapter label; display whole-book page counts only if the same layout has a trustworthy complete count. Reading-time estimates use measured reading rate/content remaining and are omitted until sufficiently supported.

Apply the reader screenshot's geometry, contrast and hierarchy through `EpubReader.razor`, `epub-reader.js`, and `epub-reader.css`; isolate publisher CSS from shell/theme styles without stripping meaningful book structure. Fix page-turn focus/selection behavior separately from saved annotations. Persist highlights using stable ranges; temporary selection and focus rings must not become saved highlights, and intentional selection/bookmarking must remain accessible. Test real EPUB images, long chapters, different fonts, rapid page changes, last page/chapter transitions, reload, cross-device size changes, and intentional versus accidental highlights.

### W9 — Reliable bidirectional episode rails

Audit every season/episode branch in `SequencePlacementPanel`, `DetailPrimaryModule`, CSS overflow, and shared rail JS. Base arrow availability on actual scroll extent/window state after content and size changes, not only episode count or the current item's index. Keep both directions discoverable; disable only at their respective bounds. Scroll one useful viewport with overlap, support keyboard and touch, reveal the focused episode, and preserve season/position when returning from details.

Do not let an overflow-hidden ancestor clip controls. Keep owned-only season grouping, existing detail targets, episode stills, canonical numbering and current-item accessibility. Test short and long seasons, partial ownership/gaps, specials, direct episode entry, first/middle/last positions, resizing, and reduced-motion behavior.

### W10 — Repair existing data and verify the complete journeys

No automatic repeat full wipe. Produce a dry-run repair report before changing current data: audiobook groups and conflicting recordings, duplicate work aliases, affected review jobs, bad provider claims, and derived projections. Back up database/configuration and run repairs with workers stopped or explicitly quiesced. Use transactional, resumable, idempotent migrations with a rollback plan.

Preserve asset IDs and original paths; remap work/edition/part relationships, progress, track offsets, bookmarks, favorites, My List, collection memberships, history, and deep-link aliases. Resolve progress conflicts from recorded locations/history rather than summing chapter percentages or choosing a random duplicate. Cancel superseded identity jobs/reviews only after replacement recording-level state is durable. Do not coalesce genuine different editions. Rebuild affected search/display projections and requeue only necessary work. Reader locator migration is independently versioned and reversible.

Validate a fresh ingestion in an isolated database as well as repair of the existing corpus. A clean test reset remains explicit and uses the protected harness; no source relocation, deletion or tag rewriting is permitted. Completion requires reporting actual per-file outcomes, recording/title counts, unresolved reviews, identity jobs, playback capability, browser results, and original integrity separately.

## Delivery order and gates

| Stage | Packages | Gate before proceeding |
| --- | --- | --- |
| 1. Establish baseline | W0 | Reproductions, IDs, timing, screenshots, existing integrity incident and source protections recorded. |
| 2. Repair meaning and contracts | W1, W2, W3 contract, W6 | Recording hierarchy, date/description provenance, durable state and API edit eligibility tested before exposing more items. |
| 3. Publish early and navigate correctly | W3 rollout, W5 | One audiobook tile; no chapter review flood; early visibility with working server-side locks; canonical music links. |
| 4. Start and control real playback | W4 then W7 | One user intent starts supported audio/video; progressive delivery and explicit failures; screenshot layout verified. |
| 5. Reading and episode navigation | W8, W9 | Stable resume/progress and selection; bidirectional rails across desktop/mobile. These can be developed independently after the baseline. |
| 6. Repair and acceptance | W10 | Dry-run reviewed, safe data repair, fresh-ingestion and upgraded-data suites, complete real-media journeys, source audit. |

Highest-risk changes are audiobook identity migration, availability/edit-lock concurrency, progressive HLS delivery, and EPUB locator migration. Keep each reviewable with its own rollback boundary. UI polish does not precede fixing those contracts. No separate agents or threads are required by this plan.

## Acceptance matrix

| Reported issue | Required proof |
| --- | --- |
| 1. Provider HTML | Adversarial normalization tests plus real provider descriptions on card/hero/detail/review; no tags, entity artifacts or unsafe markup. |
| 2–3. Audiobook folder/duplicates | Fahrenheit 451 is one recording with all owned parts in correct order; one identity job/review scope; series and alternate-recording cases preserved; restart/rescan remains idempotent. |
| 4. Slow visibility/locking | Delayed/offline provider test shows local media promptly; stable IDs and live updates; playback stays available; direct API and stale-editor mutation attempts are blocked during active work and allowed after safe settlement. |
| 5, 7. Music routing | Music player has no generic details action; artist/album links resolve correctly from player and song rows; link clicks do not trigger row playback; audio continues while navigating. |
| 6. Double Play | Cold/warm starts from every entry point advance media time after one action; rapid selection cannot start stale media; real autoplay rejection is recoverable and honestly labeled. |
| 8. Radioactivity date | Verified Age of Days identity and supported 2013 date agree across detail/timeline; wrong 1975 claim cannot reappear after re-enrichment; same-title/reissue controls pass. |
| 9. Movie playback/layout | Real VC-1/H.264/H.265 samples classified by capability; supported samples play, seek and resume with sound; text subtitles visibly render; unsupported ISO/streams reported; measured startup stages and reference-layout checks. |
| 10. EPUB reader | Project Hail Mary same-text resume after reopen/font/viewport change; whole-book progression tracks content rather than chapter count; legible layout; navigation adds zero annotations; deliberate highlights survive. |
| 11. TV rail | First-to-last and last-to-first navigation with mouse, keyboard and touch; overflow controls visible and accurate after resizing; owned-only episodes and return position retained. |

Use behavioral unit/integration tests for contracts, identity, migration, locking, dates and locations. Use real browser tests for actual media time advancement, captions, focus, selection and layout; source-text guard tests alone do not prove these behaviors. Include the supplied desktop proportions, an intermediate tablet width and narrow mobile width, plus keyboard navigation and 200% text zoom. Use synthetic samples only for controlled failure cases, not as a substitute for the real-media acceptance pass.

Definition of done: every reported issue has evidence or a clearly documented unsupported-source outcome; fresh and repaired libraries behave consistently; no unfinished required ingestion work is presented as completion; editing eligibility is enforced server-side; real playback/reading journeys pass; original media remains intact, with the prior folder-timestamp incident still disclosed. Builds and relevant regression suites pass. Publish before/after counts, timings, screenshots, migration results and limitations in a single verification report.

## Plain-English completion summary

This plan makes real files useful sooner without hiding unfinished metadata work. Audiobooks become single books with tracks, music points to the right albums and artists, Play starts the experience, reading resumes at the same text, and all TV episodes remain reachable. The screenshots guide the reader and player design, while actual media determines the information shown. Existing files and personal activity are preserved throughout repair.
