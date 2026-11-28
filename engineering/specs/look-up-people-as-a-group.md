# Spec: Look up people as a group

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-28 · **Related docs:** [Product overview](../product/overview.md) · [Ingestion flow map](https://claude.ai/artifact/CZfyPXs6srQz1Qfkg7CGxh) · issue #1360 (ingestion speed)

## 1. Goal

After a library is added, titles appear quickly but the cast and crew behind them keep filling in for hours. This spec makes that background work finish in roughly half the time by asking outside sources about a title's people together instead of one person at a time, and by never doing the same person twice. Nothing changes in what the person using Library sees, only how soon it is complete.

**Why (measured, run of 27 Nov 2026, 2,304 files):** matching finished at 2 h 31, but the people work was still going at 3 h 32 and projected to about 10 hours. Wikidata was asked 31,194 times at a steady 2.4 requests per second, close to its limit of 3, so the only way to go faster is to ask less. Each new person cost about 4.8 Wikidata requests. 63% of person saves were repeats (one actor was saved 86 times). TMDB was asked 19,183 times and 12,373 of those were repeats (the same settings page 8,161 times).

## 2. Scope

**In scope**
- Films, TV, music, books, audiobooks and comics: the background step that links cast, crew, authors, narrators and artists to Wikidata and fetches their pictures.
- Doing a show's, album's or Series' people once for the whole group rather than once per episode, song or book.
- Bundling Wikidata questions (up to 50 people per request where Wikidata allows it).
- Saving each person once per run of work.
- Remembering TMDB's settings page and each person's picture list instead of asking again.
- A small addition to the Tuvima.Wikidata library so several provider IDs can be looked up in one request.

**Out of scope**
- What is shown on person pages, cast rows or the editor (no screen changes).
- Which sources are used. Shy's rules stay: Wikidata for music is the artist (album nice to have), never per song; Apple once per album; first 10 billed cast during the scan, the rest in the background.
- Matching of titles themselves (retail match, Wikidata bridge) and the Universe queue.
- The other findings of the ingestion review (priority lanes, "already own this" shortcut, MusicBrainz per album, remembering answers across restarts). Each gets its own spec if Shy wants it.
- NFO files and TV source choice (reported separately).

## 3. Decisions made

- **D1. One people job per group, not per file.** A TV show, a music album and a book or comic Series each get one background people job covering every person credited on any of its owned files. A film stays one job. Reason: 60 episodes of one show share most of their cast; today each episode queues its own job and repeats the work. (Performance)
- **D2. First 10 billed still come first.** The existing two passes stay: the first 10 billed people of a title are done during the scan, the rest in the background pass. D1 applies to both passes. Reason: Shy's decision of 26 Nov; the top of the cast row must not wait. (Reliability of the experience)
- **D3. Guest stars stay attached to their own episode.** Grouping changes when the lookups happen, not where a credit is shown. An actor credited on one episode still appears only on that episode. (Reliability)
- **D4. Ask Wikidata about many people in one request.** Finding people by their TMDB or MusicBrainz ID is done as one combined search for a group of IDs, followed by one fetch of up to 50 people, instead of one search and one fetch per person. A person the combined search does not settle falls back to today's one-by-one name search, unchanged. Reason: these two request kinds were 9,738 of the run's requests. (Performance)
- **D5. A person is saved once per job.** If the same person appears under several roles or on several files of the group, their Wikidata details and picture are fetched and saved one time, and each credit is only linked. A person already complete in the data store from an earlier title is linked without any outside request. (Performance)
- **D6. TMDB settings and picture lists are remembered.** TMDB's settings page is asked once per Engine start (refreshed daily). A person's picture list is asked once per person and remembered for the session. (Performance; also kinder to TMDB)
- **D7. Same answers as today.** The set of people linked, their Wikidata IDs, roles and pictures must be the same as before for the same library. Any difference is a bug in this work, not an accepted trade-off. (Reliability)
- **D8. Limits stay.** Wikidata stays at about 3 requests per second and three titles' people at once. This spec asks less; it does not ask faster. (Reliability, good citizenship)

## 4. UI behaviour

No screen changes. The same states as today, reached sooner.

| Situation | What the person sees |
|---|---|
| Normal | Cast, crew, authors and artists with pictures and Wikidata links, as today |
| Empty (nothing to show yet) | As today: the first 10 billed appear during the scan |
| Loading | As today: remaining people fill in over time, about twice as fast |
| Error (source unreachable) | As today: credits stay as names without pictures; the job retries later |
| Partly ready | As today: a show's cast row can be complete before every episode's guest list is |

## 5. Data / model changes

No new information is stored or shown. The background job list keeps one people job per group (show, album, Series) instead of one per file; jobs already queued in the old shape are simply run as they are. No reset needed.

## 6. Work packets

### WP1 — Count requests per person in tests
- **What it does:** Adds a test harness that runs the people step for a fixture show (3 episodes sharing most of a 15-person cast) against canned source answers and counts outside requests by kind and person saves. Records today's numbers as the "before".
- **Files likely touched:** `tests/MediaEngine.Providers.Tests/` (new `PeopleGroupLookupTests.cs`, fixtures)
- **Depends on:** none
- **Size:** Standard
- **Done when:** the test passes and prints the before counts (requests by kind, saves per person).

### WP2 — Tuvima.Wikidata: look up several provider IDs at once
- **What it does:** Adds `Entities.LookupByExternalIdsAsync(propertyId, values)`: combined `haswbstatement` searches in groups that fit the search length limit, then one `wbgetentities` to map each result back to the ID it carries. Returns one entry per input value (found item, none, or ambiguous). Minor version bump, README, AGENTS/CLAUDE and docs page per that repository's rules.
- **Files likely touched:** tuvima-wikidata `src/Tuvima.Wikidata/Services/EntityService.cs`, `Internal/WikidataSearchClient.cs`, tests, both `.csproj`; tuvima-docs `content/libraries/wikidata/`
- **Depends on:** none
- **Size:** Standard
- **Done when:** offline tests show 20 IDs resolved with at most 3 requests and ambiguous IDs reported as ambiguous.

### WP3 — Remember TMDB settings and person picture lists
- **What it does:** `ResolveImageBaseUrlAsync` result cached per Engine start (24 h); person picture lists cached per TMDB person ID for the session.
- **Files likely touched:** `src/MediaEngine.Providers/Workers/PersonImageEnrichmentWorker.cs`, tests
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** a test enriching 10 people (2 repeated) makes 1 settings request and 8 picture-list requests.

### WP4 — Resolve and save a job's people as a group
- **What it does:** In the people step, collect all person references of the job first; resolve provider IDs through WP2 in one go (`PersonReconciliationService`), fetch details in batches of up to 50, save each distinct person once, then link credits. Fallback to today's per-person name search for anyone unresolved (D4, D5, D7).
- **Files likely touched:** `src/MediaEngine.Providers/Workers/PersonEnrichmentWorker.cs`, `Services/PersonReconciliationService.cs`, person identity/harvest services they call, `Directory.Packages.props` (Tuvima.Wikidata version)
- **Depends on:** WP1, WP2
- **Size:** Complex
- **Done when:** WP1's harness shows the same people, QIDs and roles as before, each person saved once, and Wikidata requests per new person at or under 2.0 (from about 4.8).

### WP5 — One people job per show, album or Series
- **What it does:** The quick pass and the remainder pass queue one people job keyed by the group (show, album, Series) instead of by file; the job covers the credits of all the group's owned files and links guest credits to their own file (D1, D3). Files arriving later for a known group add a job only for people not yet done.
- **Files likely touched:** `src/MediaEngine.Providers/Services/EnrichmentService.cs`, `Workers/PersonEnrichmentWorker.cs`, `src/MediaEngine.Api/Services/PeopleEnrichmentHostedService.cs`, tests
- **Depends on:** WP4
- **Size:** Complex
- **Done when:** ingesting the 3-episode fixture queues one first-pass and at most one remainder job, and every episode shows the right credits.

### WP6 — Docs
- **What it does:** Updates the public pipeline page and the performance page with how people are looked up, and the Tuvima.Wikidata changelog.
- **Files likely touched:** tuvima-docs `content/products/library/architecture/ingestion-identity-enrichment-pipeline.md`, `performance-and-large-libraries.md`
- **Depends on:** WP5
- **Size:** Mechanical
- **Done when:** the docs site check passes.

## 7. Local checks

- **L1.** Ingestion run on the same 2,304-file sample used on 27 Nov, recorded fresh (a replay of the old recording cannot be used, because the new bundled questions are not in it). Pass: the run settles fully, total Wikidata requests are at least 40% lower than 31,194 at the same point, and people work finishes within 6 hours. Fail: it takes longer than before or does not settle.
- **L2.** Compare people between the 27 Nov record and the new one: same linked people and roles for 20 spot-checked titles (5 films, 5 shows, 5 albums, 5 books). Fail: any person missing, added or pointing at a different Wikidata entry (allowing for Wikidata edits in between).
- **L3.** Open one show with guest stars: a guest appears on their episode only, not on every episode.

## 8. Risks and open questions

- **Risks:**
  - A combined ID search can return an item that carries the ID on a different property or twice. WP2 confirms every result against the fetched item and reports unclear ones as ambiguous, so they fall back to the name search.
  - Grouping makes one job larger; a failure part-way must not lose the people already saved. Jobs save per person and are safe to retry.
  - The speed-up (about 60% fewer Wikidata requests in the tail, settle time from about 10 hours to about 5) is an estimate from one run. L1 is the proof.
  - WP2 changes a separate published library; it ships first and Library picks up the new version.
- **Open questions:** none. Q1 (does a book or comic Series count as a group for people?) was answered by Shy on 2026-11-28: yes, shows, albums and Series (D1).
