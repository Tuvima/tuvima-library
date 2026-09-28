# Wikidata matching: retain coverage, require identity validation

Status: investigation and proposed remediation, September 27, 2026. The owner's latest direction is to **retain album matching**, with checks that prevent incorrect identities. This review changes documentation only; it does not repair media, change database records, reset the library, or start ingestion. It supplements WP7 of the media readiness/performance plan.

## Plain-English product walkthrough

1. **An album keeps its own identity (V1–V3).** Importing Radioactivity by Age of Days retains the album identified by MusicBrainz. Wikidata may add information only about that same album. A similar title by Kraftwerk is rejected before it can change the date, artist, album grouping, or related collections. Acceptance: the observed conflicting candidate cannot be accepted through text search, direct identifier lookup, a batch, or a retry.
2. **Missing information does not mean a broken library (V1, V4).** If Wikidata has the artist but no album, the artist can still receive its biography and relationships. The album remains correctly identified through its music provider and playable. A completed lookup with no supported match stops showing a processing indicator. Acceptance: a missing optional album match neither blocks playback/completion nor automatically creates a Review Queue item.
3. **Keep meaningful connections across media (V3–V4).** Verified album, book, film, show, and comic identities can still supply relationships unavailable from their creators alone. Artist and author pages remain independently enrichable. Acceptance: shared people produce people/credit connections; a shared actor or author does not itself create a fictional universe or an adaptation relationship.
4. **Explain and test decisions (V2, V5).** Diagnostics state which evidence supported or rejected a match, including different artists, conflicting identifiers, incompatible dates, and insufficient evidence. The later authorized fresh import demonstrates the correction without renaming or editing originals. Acceptance: regression cases reject false matches and retain legitimate aliases, reissues, editions, and collaborations.

What stays the same: music providers identify albums/tracks; Wikidata remains available for verified work and contributor enrichment; existing library navigation, playback, read-only source protection, and trusted relationship requirements remain. No blanket removal of work matching, title-specific override, or forced user review for ordinary Wikidata coverage gaps is proposed.

## What actually matched

The persisted local evidence identifies **Age of Days / Radioactivity / 2013**. MusicBrainz independently supplied release group `24054974-5181-4df2-ae31-3510945d8269`, also identifying that artist and album. Its public record lists an April 9, 2013 Canadian release: [MusicBrainz release group](https://musicbrainz.org/release-group/24054974-5181-4df2-ae31-3510945d8269).

The engine accepted **Q1139716**, which is **Radio-Activity by Kraftwerk**, published in 1975. That entity explicitly names Kraftwerk and identifies a different MusicBrainz release group, `104cc34f-b4f9-3fb1-b2a0-fce32b36ec1f`: [Wikidata album](https://www.wikidata.org/wiki/Q1139716).

Age of Days itself has Wikidata identity **Q4691875**, with MusicBrainz artist ID `e12b2e9b-c0a7-4b9e-b370-519be05ece46`: [Wikidata artist](https://www.wikidata.org/wiki/Q4691875). The retrieved artist record does not establish a Radioactivity album entity. This investigation found no verified Wikidata entity for the correct album; that is not an exhaustive proof that none exists. It is conclusive that the entity actually accepted belongs to the wrong artist.

Read-only log evidence in `logs/codex-run-engine.out.log`:

| Time / line | Observed event |
|---|---|
| 15:57:21 / 32400–32402 | The track Broken resolves to Q1139716 via `TextSearch`; the worker records `retail_text`. The adapter uses the album title for a MusicAlbum request, despite the log displaying the track title. |
| 15:58:42 / 34888–34889 | Collection assignment creates Radioactivity using Kraftwerk's release-group identifier. |
| 16:12:27 / 51363–51421 | A later Radioactivity album job resolves Q1139716 via `BridgeId`. |

The later identifier match is therefore not independent corroboration of the first match. The wrong identifier was already present in the catalogue. Both persisted claims and these events support the propagation path. The original raw resolver request and complete candidate payload were not captured in the inspected logs, so the precise original score and provider lookup response remain unavailable.

## Root cause: several checks fail to form one acceptance boundary

1. **Configured fallback restrictions stop at the adapter boundary.** `WikidataBridgeWorker.JobResolution.cs` builds MusicAlbum requests with constrained text fallback disabled; `config/providers/wikidata_reconciliation.json` also disables it. `ReconciliationAdapter.FictionalAndEditions.cs:689` nevertheless supplies the album title whenever real external IDs are present. The dependency request has no explicit fallback-policy field. In the sibling Tuvima.Wikidata 3.9.1 source, `Services/BridgeResolutionService.cs:346` falls back to title search when identifier candidates are absent. The application's setting governs whether a request can be built and its own second pass, but does not prevent the dependency's first-pass fallback.
2. **Artist context is a bonus, not a required compatibility check.** The dependency searches the title with media-type hints. `Internal/BridgeCandidateScorer.cs:221` compares linked creator labels; a weak match adds a warning and returns zero, without rejecting the candidate. Year disagreement likewise returns zero. `BuildTextCandidate` adds these bonuses to the text score; `BuildResolvedResultAsync` selects the first ranked candidate and marks it resolved. Creator properties also combine performers, composers, producers, and other roles; those must not all be interchangeable evidence of an album's primary artist.
3. **The application's existing artist safeguard is bypassed by the returned strategy.** `WikidataBridgeWorker.Finalization.cs:47` calls `MusicAlbumIdentityEvidence.Corroborates` only when `MatchedBy == "music_album"`. `JobResolution.cs:147` and `BatchResolution.cs:257` map TextSearch to `retail_text`, and BridgeId to `bridge_id`. The observed first match takes `retail_text`; the later match takes `bridge_id`. Neither enters that guard. The helper's isolated tests therefore do not demonstrate protection of the actual pipeline.
4. **Even that helper is not a complete conflict check.** `MusicAlbumIdentityEvidence.cs` accepts either one matching release ID or an exactly matching artist name. It does not establish an explicit veto for contradictory same-scope release-group IDs, distinguish a missing credit from a different artist, or resolve aliases through stable artist IDs. Those concerns need a shared validation result rather than another route-specific Boolean.
5. **The date safeguard reads the wrong claim key.** `ReconciliationAdapter.FictionalAndEditions.cs:65` checks generic `year`, treating a missing resolved year as compatible. `ReconciliationAdapter.ClaimMapping.cs:166` maps P577 to `original_release_year` or `edition_release_year`, then skips generic mapping. Consequently the returned 1975 original-release year can escape the existing comparison with 2013. Date validation must respect original-work versus edition dates; merely comparing every date with every local year would wrongly reject reissues.
6. **Accepted output becomes trusted input too early.** `WikidataBridgeWorker.Finalization.cs:85` persists claims and routes them into the work hierarchy, then upserts collected external IDs. No shared contradiction gate precedes those writes. `MarkBridgeSucceededAsync` also publishes confidence 1.0 regardless of the evidence path. Date presentation subsequently prefers original-release year, explaining why 1975 wins over the correct generic 2013. Changing display precedence alone would conceal the wrong identity and leave its relationships/grouping corrupted.

The Engine build manifest references Tuvima.Wikidata 3.9.1; the inspected sibling source declares the same version. These source findings agree with the observed TextSearch/BridgeId sequence. A recorded-response integration test remains necessary to prove the complete correction against the packaged dependency.

## Why keep work-level matching, and where it adds value

Artist-only music matching reduces lookup volume and exposure to album-title collisions. It can retain album playback, music-provider artwork, dates, tracks, artist biographies, and contributor-based cross-media links. However, it cannot by itself prove album-specific soundtrack, adaptation, sequence, or fictional-world relationships. Such facts may be recoverable from other providers, but equivalent coverage is not established here.

Keeping work matching preserves those opportunities, at the cost of more requests, incomplete coverage, and stricter identity validation. The chosen approach is to retain it as optional background enrichment with bounded requests and verified identity, independent of playback readiness. Artist matches cannot substitute for work matches when asserting work relationships.

| Media scope | Keep Wikidata for | What creator-only matching would lose or weaken | Required context |
|---|---|---|---|
| Music album | Verified album-specific relationships; artist/group enrichment independently | Album-specific links and sequence facts available only on the work | Release-group/release scope, primary artist IDs/credits, original versus edition date; compilations and collaborations handled explicitly |
| Music track | Optional verified recording/composition relations when useful; no forced per-track match | Recording/composition-specific links | Recording and composition are distinct; never assign an album QID as a track identity |
| Book | Underlying work, series, adaptations, fictional universe; author separately | Which particular book a film adapts, book sequence, work-specific universe links | ISBN-to-edition-to-work lineage, author identities, title/translation and publication scope |
| Audiobook | Underlying book reused where verified; narrator independently | Underlying work relationships if only narrator/author is resolved | Separate book author from narrator and recording edition; do not conflate publication dates |
| Movie | Film relationships, adaptations, franchises; cast/director separately | Specific adaptation/franchise identity | Verified IMDb/TMDB film IDs, title/year and relevant creator evidence; actor overlap alone is insufficient |
| TV | Show relationships; episode matching optional and scoped | Show-specific universe/franchise connections | Root show identity; season/episode identifiers cannot silently become unrelated show identities |
| Comics | Issue identity where supported, otherwise explicit series/run scope | Work/run-specific relationships and ordering | Publisher/run/creator/year/issue context; a series QID must never be represented as an issue QID |
| Personal photos/videos | None: View stays outside catalogue identity | No intended catalogue capability lost | Preserve personal-media separation |

The code already supports contributor enrichment from unlinked names and retained retail identities (`PersonEnrichmentWorker`, `RetailMatchWorker.EnrichPeopleWithoutMediaMatchAsync`, `WikidataBridgeWorker.TryOrganizeRetainedRetailIdentityAsync`). Removing the album match is therefore not intrinsically required to enrich the artist. The current person search adapter passes name/role/work hints; extending it to use verified artist identifiers first is part of the proposal, not a claim about current behavior.

## Technical work packages

### V1 — Explicit resolution policy and independent completion

- Retain MusicAlbum matching. Propagate allowed strategies through the dependency contract and enforce them again on returned results. Do not rely on the presence or absence of a title as a security/identity policy switch. Until the dependency supports this, reject disallowed returned strategies before any persistence and suppress unwanted fallback dispatch through a documented adapter path.
- Keep contributor jobs independent of album QID success. Reuse/deduplicate verified artist identities, including bands; prefer provider artist IDs over bare name search.
- Represent optional Wikidata outcomes as matched, no supported match, conflict rejected, or retryable operational error. A confident retail identity survives an optional conflict rejection; human review is reserved for unresolved core identity requiring a decision. Optional work must not keep playable, identified media pending indefinitely.

### V2 — One validation boundary for all paths

- Validate before QIDs, claims, bridge IDs, work lineage, cache acceptance, collections, or relationship rows are persisted. Cover single/batch, text/identifier, sibling reuse, rollup, retry/resume, and candidate previews/manual choice. Explicit user choices must display conflicts rather than silently bypass scope rules.
- Build a typed evidence object retaining input provider/source, entity scope, artist IDs, ordered primary credits, candidate IDs, candidate performer identities, date semantics, and observed contradictions. Fetch the candidate's actual P175 performer evidence; its album-level P434 is not a substitute for the performers' artist IDs.
- Distinguish **corroborated**, **unknown**, and **contradictory**. Reject explicit different primary artists or incompatible same-scope authoritative IDs. Missing optional metadata is not a disagreement. Text candidates require positive artist corroboration; title similarity alone cannot pass. An exact verified release-group bridge can support a candidate with missing artist data, but must not overrule an explicit artist contradiction.
- Compare release to release and group to group; validate release-to-group relationships before treating different-scope identifiers as equivalent. Support multi-artist releases, credited aliases, transliterations, collaborations, and Various Artists without replacing stable identity with string equality. Do not arbitrarily require a song's guest artist to equal the album artist.
- Compare original to original and edition to edition dates. Strong contradictory original dates reject a text match; a 2013 reissue of a 1975 album remains possible only with supported edition lineage and compatible artist identity.
- Preserve actual match strategy, evidence confidence, rejection reasons, and candidate diagnostics. Remove unconditional certainty from accepted-but-inferred results.

### V3 — Prevent contaminated identities and relationships

- Never use a candidate's returned identifiers to prove that same candidate matched. Freeze the independently supplied evidence snapshot before resolution. A propagated ID retains provenance and cannot be upgraded to independent evidence by another job.
- Validate claims and relationships as one acceptance decision before publication. Any multi-store persistence needs retry-safe consistency so partial writes cannot seed later false confirmation.
- Scope album claims to albums, track claims to tracks, and creator claims to contributors. Keep provider/local shelf finalization when optional Wikidata finds no acceptable entity. Broader automatic collections still require trusted work relationships; matching people is not enough.

### V4 — Preserve useful coverage and performance

- Deduplicate album lookups by verified release-group identity and artist lookups by artist identity. Avoid repeating an album search per track.
- Retain verified work enrichment across media according to the table. Bound retries and cache terminal no-result outcomes with policy/version-aware invalidation; a later new provider ID can justify another attempt.
- Keep all provider lookup and identity checks in background ingestion/enrichment, never page loads or playback actions. Catalogue readiness and player start remain independent of optional Wikidata coverage.

### V5 — Regression proof and later fresh import

- Add recorded-response integration coverage for the actual adapter/dependency/worker boundary, not just `Corroborates`. Require the observed Radioactivity candidate to fail through TextSearch, BridgeId, batch, resumed job, and propagated-ID paths with zero rejected-candidate claims or relationships written.
- Negative cases: same album title/different artist; same artist/different album with conflicting group ID; same title/year but different group; wrong media kind; missing artist on text candidate; inconsistent exact bridge; artist-name homonyms; different book authors; film remakes; wrong TV show/run.
- Positive cases: artist aliases backed by IDs, band name changes, multi-artist albums, compilations, legitimate reissues/remasters, book translations/editions, audiobook book-work reuse, and explicit comic run scope.
- Prove an artist can enrich and an album can finalize/play when the album has no Wikidata match. Prove a failed lookup cannot erase a valid provider identity or create a user-review item solely for missing Wikidata coverage.
- A later authorized clean ingestion must preserve Age of Days / Radioactivity / 2013 and the correct MusicBrainz group without a title-specific rule. No forced album QID is expected unless verified evidence exists. Source names, folder layout, bytes, and protected metadata remain unchanged.

## Completion summary for the product owner

The engine found the wrong album, and existing safeguards did not run effectively on that route. We can retain Wikidata's useful connections while requiring evidence that each match describes the same work and artist. If no safe match exists, the library keeps its correct music-provider information and stays usable. This review records the cause and proposed fix; no media or catalogue records were modified.
