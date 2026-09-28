# TV show and episode matching in the shared editor

Status: editor workflow implemented in the working tree, 28 September 2026. The reported Solo Leveling library record, live TMDB responses, and rendered editor have not been inspected.

## Plain-English product walkthrough

1. **Match the show once.** From any owned episode, open the existing editor and choose the Series scope in the Context Rail. Matching shows the current TMDB series, or offers a show-name search when there is none. A chosen show is previewed with its title, first-air year, synopsis, and poster before **Match series**. Confirming it updates the shared show identity and show artwork. It does not change which episodes the user owns. Maps to packages A, C, and D.
2. **Pick an exact episode.** Choose an owned episode in the Context Rail. Matching shows the inherited TMDB series, the local file's current season/episode labels, and a season selector populated from that TMDB show. Selecting a season loads its numbered episodes, including title, air date, synopsis, and still preview when available. The local S/E is suggested, not imposed; the user may choose a different season and episode. A text filter narrows the loaded season list, rather than pretending TMDB has a free-text episode search. Maps to packages A, B, and C.
3. **Review the correction before applying it.** Selecting an episode previews the old and new show/season/episode path, the exact file affected, and any occupied-position conflict. **Match episode** saves the TMDB show ID on the series and the selected TMDB episode ID and S/E metadata on that owned episode. A wrong-show selection requires an explicit show change or a reviewed structural move. Other owned episodes keep their matches. Maps to packages B and C.
4. **Repair a season without repetitive searches.** In the Season scope, **Review owned episodes** compares the user's files with TMDB's episode rows for that season. It proposes exact S/E matches, clearly sets aside missing numbers and conflicts, and applies only the choices the user confirms. The Season 2 poster and the confirmed owned episodes' stills are then refreshed, with a per-item result showing whether an image was stored, absent at TMDB, or failed to download. A single episode can still be corrected with the picker in step 2. Existing user-selected artwork remains preferred. For Solo Leveling, this makes a whole Season 2 repair practical without creating TMDB-only episodes in the library. Maps to packages B, C, D, and E.

The detail pages, Review Queue, and batch entry points continue to use `SharedMediaEditorShell`. The Context Rail, separate show/season/episode scopes, and owned-only episode lists remain. This work does not import unowned episodes, infer owned season totals from TMDB, or add a new management workspace. Success is observable when a user can match a TV series, choose a listed episode in any TMDB season, see the selected identity on reload, and see locally stored season/episode artwork when TMDB supplies it.

## Findings and cause

- `SharedMediaEditorShell` currently allows canonical search only on TV `episode`; `series` and `season` return no quick-search target. The displayed instruction says to select an episode within the matched series and season, but the screen provides one generic text box.
- `BuildSuggestedSearchQuery` creates a show/S/E string. `SearchCanonicalAsync` sends it as `QueryOverride`, and the API intentionally omits structured `SearchFields` when an override exists. Generic `SearchService` therefore uses TMDB's `tv_search` strategy, which returns **shows**, not episode rows. The configured `tv_season_episodes` strategy needs `tmdb_id` plus `season_number`, and its response is not exposed as a scoped editor picker. This explains why changing the query text cannot reliably find the episode.
- The ingestion TV worker already follows the right lookup shape: find the TMDB show, fetch a season's episodes, then associate an owned file with one result. That path is automatic and is not available as an editor selection workflow.
- The manual `retail-match` endpoint has `show` and `show_episode` policies and can write identity and preview structural moves, but its episode path trusts posted candidate fields and has no server-side, show-scoped episode selection contract. A show result passed through the episode policy risks treating the show's ID as an episode ID. The hierarchy request also passes the episode provider item ID into the show suggestion, so the selected parent ID must be made explicit.
- TMDB show, season, and episode artwork have different owners and fetch paths. `ImageEnrichmentService` handles show and represented-season images; grouped TV matching downloads episode stills. Manual replacement queues a harvest with `SkipRetailStage = true`, so the grouped still-download path is not a dependable consequence of an editor correction. The current season image mapping asks TMDB for `backdrops`, whereas TMDB's season images documentation describes posters; season thumbnail availability should therefore be reported truthfully, with a visual fallback rather than a fabricated asset.
- The source review establishes the editor/search-path defect. It does not establish whether Solo Leveling Season 2's TMDB record contains each desired still, whether the local files have correct parsed S/E hints, or whether a prior image request failed. Package E includes that diagnosis.

TMDB's documented endpoints support this hierarchy: [TV search](https://developer.themoviedb.org/reference/search-tv), [season details with episode rows](https://developer.themoviedb.org/reference/tv-season-details), [season images](https://developer.themoviedb.org/reference/tv-season-images), and [episode images](https://developer.themoviedb.org/reference/tv-episode-images).

## Technical work packages

### A. Series match in the existing editor (walkthrough 1–2)

- Enable the `show` target on the TV Series scope in `SharedMediaEditorShell` and retain the existing `show_episode` target only for an owned episode. Use the current TMDB show bridge on the root work as the default episode context; when absent, direct the user to Series matching first. Season scope displays its TMDB context and artwork but does not invent a separate season identity that conflicts with the show's ID and the episode's number.
- Route Series searches through the existing show search and `show` policy. Preview the show candidate and apply it to the root work using the existing identity/hierarchy transaction. Require a clear confirmation if replacing a series ID already shared by owned episodes. Identify child episodes whose episode IDs were established under the old show and mark them for review; do not silently rewrite their S/E, stills, or ownership.
- Expose the resolved show ID and name in episode Matching after editor retarget/reload. Keep Wikidata/canonical identity separate from the TMDB retail choice.

Primary areas: `SharedMediaEditorShell.razor(.cs)`, editor context/navigation DTOs, `ItemCanonicalEndpoints`, `CanonicalCandidateBuilder`.

### B. Typed TMDB season and episode picker (walkthrough 2–3)

- Add Engine endpoints scoped to the current owned TV editor target: list seasons for a validated TMDB show ID and list episodes for a selected season. Reuse `TmdbRetailClient`, configured credentials, locale, rate limiting, and cancellation. Include season 0 when TMDB supplies specials, label it clearly, and bound/cancel requests on rapid selection changes. Return provider errors separately from valid empty seasons.
- Return typed IDs and presentation fields: series ID, season number, episode number, episode ID, title, air date, synopsis, runtime, and bounded image previews. Do not flatten these into `ItemCanonicalRetailCandidateDto` results from `/search/tv`. Avoid loading all seasons and all episodes on initial editor open; cache a selected season for a bounded period.
- Preselect a season and highlight an episode from the owned file's existing S/E, but never auto-apply. Permit a different season/episode. Keep query text as an optional filter within the fetched episode list. If the episode is absent, show **No TMDB episode in this season** with a way to pick another season or retry; do not substitute an arbitrary show hit.
- In Season scope, offer a bounded owned-episode review against that season list. Propose only unique exact S/E pairs, show each owned file and proposed TMDB row, and require confirmation for the selected proposals. Exclude duplicates, missing ordinals, cross-season results, and already-confirmed contradictory episode IDs for individual correction.
- Validate the chosen episode again at apply time against the chosen show and season. The server derives the episode ID, number, title, and artwork path from TMDB, and rejects stale or inconsistent posted IDs. Preserve `MetadataMatch` authorization and access to the concrete owned target.

Primary areas: new TV editor contracts under `MediaEngine.Contracts`, `TmdbRetailClient`, `ItemCanonicalEndpoints` or a focused TV matching endpoint, `IEngineApiClient`/`EngineApiClient`, `SharedMediaEditorShell`.

### C. Correct identity, placement, and atomic apply (walkthrough 1 and 3)

- Use a typed show ID and episode ID in the apply request. Treat `tmdb_id` as root-series identity and `tmdb_episode_id` as exact episode identity; do not infer one from a generic `ProviderItemId`. Update candidate/bridge mapping and hierarchy preview so the parent suggestion receives the **show** ID, never the episode ID.
- Reuse `HierarchyAlignmentService` to preview old path → target path and block an occupied owned episode position. Commit the selected identity, S/E/title fields, stale provider-ID cleanup, and structural placement in one durable operation. Do not clear another owned episode or a user-authored display override. Keep the full enrichment job queued after commit and refresh the editor/detail state.
- If the chosen episode belongs to a different show, present the structural move explicitly with its impact; avoid updating a shared parent as a side effect of changing one leaf. For a series-level rematch, leave existing owned episodes attached but visibly flag conflicting episode IDs until each is reviewed or a safe reconciliation is explicitly confirmed.
- Apply a confirmed season review as independent per-owned-episode decisions with a truthful summary of applied, skipped, and failed rows. Never create a provider-only episode as an owned work. Resume safely after partial failure without repeating successful identity or artwork writes.

Primary areas: `ItemCanonicalEndpoints`, `CanonicalCandidateBuilder`, `HierarchyAlignmentService`, claim/bridge scoping, editor hierarchy preview.

### D. Scoped artwork repair and truthful status (walkthrough 1 and 4)

- After series match, refresh root show posters/backdrops/logos and posters for **represented owned seasons** through `ImageEnrichmentService`; expose a targeted retry for the selected season. A season with no TMDB poster remains clearly empty. Do not treat TMDB's season listing as proof of local ownership or guarantee a season backdrop.
- After episode match, fetch the selected episode's `still_path`; if absent, check the documented episode images endpoint for a usable still. Persist a managed `EpisodeStill` on that owned episode work, generate its appropriate renditions, and retain user-selected preferred artwork. Remove or demote stale provider-managed stills from a replaced match without deleting shared cached bytes that other entities use.
- The confirmed season review refreshes stills for only its confirmed owned episode rows. Bound and rate-limit the work, keep row-level progress, and make a retry target only failed rows. A season poster refresh does not depend on every episode having a still.
- Make artwork work durable and idempotent after identity commit. Show separate states for queued, stored, no TMDB image, provider error, and download/validation error. A failed image request must not roll back a correct identity; Retry artwork should resume only the affected scope. Refresh the editor and detail surface when the managed asset is ready. Follow the existing artwork sizing contract for picker previews, cards, and heroes.

Primary areas: `ImageEnrichmentService`, `ArtworkScopeService`, TV still persistence currently in `RetailMatchWorker.TvBatch`, artwork refresh endpoints, editor artwork state, image rendition helpers.

### E. Verification with the reported case (walkthrough 4)

- Before changing code, capture a read-only snapshot for one affected Solo Leveling Season 2 asset: local parsed S/E and title, parent show/season work IDs, root `tmdb_id`, episode `tmdb_episode_id`, selected provider claims, owned-only hierarchy, artwork owner/variant/path, and latest artwork/identity job result. Keep provider credentials and private file paths out of the written report.
- With the configured TMDB account, inspect the selected show's seasons, the affected season's episode rows, season posters, and the exact episode's still availability. Compare with a working Season 1 episode. This distinguishes a lookup defect, wrong series/season mapping, missing TMDB image, and local persistence/rendering failure.
- Verify automated contract/API tests for show vs episode IDs, season 0, alternate season choice, provider error vs empty result, stale selection, authorization, duplicate owned ordinal, cross-show moves, and user artwork overrides. Add an integration test that matches one owned S2 episode and verifies its root show ID, exact episode ID, managed still, and unchanged sibling episode. Exercise show-level artwork refresh for represented seasons only.
- Run the focused .NET tests and a build, then manually check the editor and detail at desktop and mobile sizes. Acceptance evidence should include the picker path, confirmation preview, saved identity after reopen, artwork status, and owned-only episode list. If TMDB supplies no poster/still for the exact record, the accepted result is an accurate **No image available from TMDB** state with the existing upload/URL artwork options.

## Delivery order and acceptance

Implement A and B before replacing the generic episode search. Implement C before enabling Apply in the picker. Land D with the correction path so matching does not appear complete while stills remain unaddressed. Run E against the real example before closing the issue. No database migration is expected if the existing bridge IDs, scope identities, and `EntityAsset` types suffice; confirm that during implementation.

Acceptance checklist:

1. Series scope finds and confirms a TMDB show, and every owned episode displays that shared show context.
2. Episode scope lists seasons and exact episodes from that show, including Season 2 and season 0 when present; text search filters those returned rows.
3. Choosing a different S/E previews the path and affects only the selected owned file after confirmation; conflict and wrong-show cases are handled explicitly.
4. Saved `tmdb_id` and `tmdb_episode_id` remain distinct and correct after reopening, and the selected episode's local S/E and display metadata agree with TMDB.
5. A Season 2 review previews only uniquely matched owned files, skips conflicts, and reports every applied/skipped/failed row. Show/owned-season artwork and available confirmed-episode stills are stored in the correct scopes and appear on detail/cards; user-selected artwork stays preferred.
6. Missing TMDB art and provider failures produce different, actionable states. No unowned TMDB episode appears as owned, and no provider total is shown as an owned count.

## Implementation and verification record

- The Series scope now searches for a TMDB show. The Episode scope browses that show's seasons and exact episode rows, suggests the local S/E without applying it, and previews the selected structural path. The server rechecks an episode against TMDB and the currently matched show when saving.
- The Season scope now reviews only owned episodes. It proposes unique exact episode-number matches, skips contradictory matches, applies selected rows individually, and reports row results.
- Series matching refreshes show and represented-season artwork. Episode matching refreshes a managed still from TMDB's episode row or images endpoint. Artwork refresh in Episode scope can retry that still. User-selected preferred images remain preferred.
- The solution builds with zero warnings and errors. Focused API tests passed (35), as did focused image-enrichment tests (18). The full API suite passed 1,096 tests and failed 14. These failures are in existing source guardrails, success metadata on three untouched routes, and endpoint inventory blocked by an inferred `database` parameter outside the TV matching routes. The problem-details guardrail passes after the new routes were corrected.
- Live acceptance remains open: no Solo Leveling library database was present in this checkout, so its exact show ID, Season 2 episode IDs, and TMDB image availability could not be confirmed. The editor has not been checked visually at desktop or mobile sizes. The route and artwork tests cover policy and no-image handling, but not a full owned-episode match with a downloaded still.

## Plain-English completion summary

The editor now lets someone match the TV show, browse its TMDB seasons, and choose an exact episode for an owned file. A season review can confirm several clear owned-episode matches and refresh available artwork. The Solo Leveling record still needs a live check before the cause of its missing Season 2 images can be stated with certainty.
