---
title: "TV Episode Consistency Plan"
summary: "Correct TV episode identity, state-driven heroes, short descriptions, missing entries, scoped credits, and shared More actions across media."
audience: "developer"
category: "proposals"
product_area: "watch"
tags:
  - "tv"
  - "details"
  - "metadata"
---

# TV Episode Consistency Plan

Status: implementation in progress, September 6, 2026. Core changes are implemented; fresh-ingestion and rendered UI acceptance remain open.

## Implementation and validation status

Implemented: typed TV hierarchy resolution and removal of competing Wikidata episode materialization; no Wikipedia episode extraction; episode still selection without unrelated image variants; a shared profile-aware continuation policy; short episode synopsis projection; ownership-first missing entries; a scrolling episode rail without the four-item cap; stored TMDB episode credits with episode/season/role filtering; and shared personal status, history, Undo, Continue placement, collection/playlist, queue, copy-link, and file-information actions where supported.

Status changes are profile-scoped and revision checked. They preserve real consumption history and bookmarks, do not fabricate music plays, and reject stale saves. Playback asset resolution prefers the same profile's most recently used eligible variant. Full credits currently describe owned episode evidence, explicitly labelled in the UI; provider-wide season aggregate browsing and normalized provider-to-local person identity links remain follow-up work. Existing song overflow reuses status commands; other specialized playlist/track overflow surfaces still need parity review.

The original Breaking Bad display was reproduced before changes at desktop size, including duplicated entries and long article text. A fresh generated media set and isolated validation configuration were prepared without changing original media. Automatic approval review rejected the development-state deletion and isolated runtime launch commands with “blocked by policy.” No deletion or launch occurred from those commands. Consequently, fresh ingestion, after screenshots, responsive verification, and live reset/completion flows have not passed their release gate. Existing corrupted pre-beta state is not repaired by these code changes: a fresh data store and ingestion are required.

Validation completed: solution restore and build (zero warnings/errors), full automated suite (all executed tests passing; 37 provider integration tests skipped), and strict documentation build. Added focused coverage for continuation states, profile isolation, reset/Undo conflicts, retained history, typed TV hierarchy, credit replacement/scoping, and recent playback-variant selection.

The sections below remain the acceptance plan, including broader consistency audits that must not be inferred as verified from a successful build alone.

Updated product direction: never-watched and reset TV shows must use exactly the same managed series hero artwork as the Watch root surface. Episode stills belong to an active viewing cycle and explicit episode details. The shared More menu will expose personal status controls across media types, with media-appropriate labels and capabilities.

The central change is to resolve one trustworthy episode context in the Engine and use it for artwork, synopsis, facts, credits, progress, selection, and playback. Show metadata, episode metadata, and provider catalogue entries currently cross those boundaries in several independent paths.

## Findings and root causes

The initial investigation covered production code and a read-only inspection of development SQLite state. The findings below record the pre-change behavior; implementation and validation status is tracked above.

| Issue | Evidence | Root cause and implication |
| --- | --- | --- |
| Wrong hero after finishing an episode | `DetailCompositionOrchestrator.CollectionBuilder.cs`, `SelectInProgressTvEpisode` and `SelectFirstOwnedTvEpisode`, select highest progress percentage or the first owned episode. `Collections.cs` changes the backdrop only for an in-progress episode. | There is no completion-to-next-episode resolution. Most-watched also does not mean most recently watched. Action, artwork, and progress are selected independently. |
| Wrong hero on a directly selected episode | `Work.cs` already prefers an episode still through its artwork reader. However, `DetailViewModelBuilder.BuildArtwork` chooses generic background/banner size variants before episode variants, and `HeroArtworkResolver.Resolve` lets `largeUrl` replace the selected backdrop. `HeroBackdrop.razor` also renders those variants in `srcset`. | A correct base still can be replaced by a show-sized variant. The current database has managed stills and size variants for owned Breaking Bad episodes, as well as a separate show backdrop. Confirm the exact returned URL family and browser `currentSrc` before changing the renderer. |
| Duplicate Season 1 episodes with different descriptions | TMDB manifest entries for Pilot (62085) and Cat's in the Bag... (62086) are already linked to their owned works. Separate Wikidata-derived rows for the same titles sit directly beneath the show, have episode numbers but no season number, and have no attached media assets. | The duplicate is not simply an unlinked TMDB manifest row. The collection reader includes structural/catalogue children; deduplication distinguishes `:1` from `1:1`, while presentation defaults a missing season to 1. Both become visible as Season 1 episode 1. |
| Confused structural identity | Those extra episode-labelled rows currently have `work_kind = parent`, `ownership = Owned`, `is_catalog_only = 0`, and season artwork. The stored child payload correctly places their unassigned episode data separately from numbered season entries. | Episode and season ordinal spaces are colliding somewhere in child creation/reuse. `CatalogUpsertService` matches by parent plus ordinal, then title; `ImageEnrichmentService` looks up a season by parent plus ordinal. Trace the exact writer that changed the structural rows before implementation. These lookups do not adequately prove entity kind or season scope. |
| Long Wikipedia episode text | The unassigned Pilot row has a 1,011-character `episode_description`; owned Pilot assets have the 236-character TMDB synopsis. `ReconciliationAdapter.EntityEnrichment.cs` explicitly fetches Wikipedia extracts for TV children; `CatalogUpsertService` writes `child.Description` into canonical `episode_description` with Wikidata as winner. | This is an upstream content-policy violation. Removing an Overview paragraph or truncating it would leave the contaminated canonical field intact. Generic description fallbacks and global Wikipedia-first field priorities provide additional routes for leakage. |
| Missing cards display descriptions | `CreateMissingManifestWork` copies provider descriptions, and `SequencePlacementPanel` displays any TV item description without checking ownership. | Missing and owned items share a presentation path without the required ownership distinction. |
| Only four episode cards | `SequencePlacementPanel.razor` sets `LandscapeWindowSize = 4` and renders `Skip(...).Take(VisibleCount)`. CSS also fixes landscape card sizing. | This is an explicit rendering cap, not a shortage of available episodes. |
| Repeated cast and crew | `PersonCreditReadService.BuildForWorkAsync` merges root-show credits into work credits. Retail TV matching combines aggregate show cast with episode crew/guest claims. Later generic TMDB enrichment uses show aggregate credits. | The data and readers lack a strict episode-credit boundary. Show membership is being used as episode participation evidence. |
| Incomplete “full cast” | The reader caps cast at 24, contributor mapping caps entries at 24, and TV presentation retains only Directors and Cast. The full panel has no season/role controls. | Writers and other supported crew disappear before rendering; a full view cannot recover them or filter by season. |
| Wrong profile context | Collection episode progress and owned-format progress readers join `user_states` using `DefaultOwnerUserId`, despite the route passing an active profile. | Episode continuation can reflect another profile. Fix this alongside target selection. |
| More has no personal-status controls | `BuildOverflowActions` emits only Edit, and `DetailActionAuthorizationContext.Allows` authorizes only that key. `OverflowActionMenu` and `ManageActionsMenu` already provide a shared renderer. | Extend the action model and command policy across media; adding a TV-only menu item would leave other media and Consumer profiles without consistent personal controls. |

The current database also has multiple physical assets for Pilot and owned episodes in other seasons. These must remain valid variants/episodes; the fix must not assume that the entire show consists only of the two Season 1 examples.

## 1. Establish episode identity and ownership first

Create a shared TV identity resolver used by ingestion, catalogue projection, artwork targeting, and detail composition. Keep internal work/asset IDs separate from provider IDs. A provider episode ID, backed by its trusted show identity, is the primary external episode identity. A complete show + numbering scheme + season + episode tuple provides structural matching when a provider episode ID is unavailable. An episode Wikidata QID is optional enrichment, never a prerequisite.

Require entity-kind evidence when resolving show, season, and episode records. Replace parent-plus-ordinal and title-only TV matches with typed, scoped lookups. Preserve season 0 as Specials; an unknown season stays unknown and never silently becomes Season 1. Do not merge different numbering schemes, remakes, reused episode titles, or partial tuples without evidence. Resolve unknown catalogue entries through trusted provider IDs; unresolved entries do not enter a numbered season rail.

Keep TMDB season manifests as the episode-list backbone where TMDB identifies the show. Join owned works onto that backbone once; additional trusted identity evidence can enrich the same entry but must not create parallel episode lists. Provider catalogue entries should stay in the manifest representation unless a real owned work needs a library record. Remove TV catalogue-child materialization that creates a competing hierarchy. Retain real show/season structure where required by owned media.

Compute ownership from an eligible local playback asset and library access, not a structural row's default `Owned` value. Deduplicate logical episodes before counts, actions, progress, and rail construction. Multiple files/formats remain selectable playback variants of one episode. Explicit multi-episode files need coverage mapping; do not duplicate the physical file or guess its coverage from a show QID.

Implementation touchpoints: `CatalogUpsertService`, `ReconciliationAdapter` TV child projection, work repository child lookup/creation, `ImageEnrichmentService`, provider manifest linking, and the collection/sequence readers and builders. Inspect the sibling Wikidata package only if the boundary fixture proves it supplied incorrect scope; do not change package references speculatively.

Acceptance: with only S1 E1 and S1 E2 owned in a seven-entry season manifest, show missing off gives exactly two entries; on gives exactly seven, with five missing. Counts remain two owned in both states. The existing development dataset produces the same result for Season 1 without losing owned episodes in other seasons.

## 2. Resolve one episode context for details and continuation

Add an Engine application policy returning the selected episode, playback asset, selection reason, profile progress/completion, current viewing-cycle state, artwork scope, and next playable episode. Map its result explicitly into `MediaEngine.Contracts`; do not duplicate JSON models in Web or expose persistence records. Historical playback alone must not classify a reset show as in progress.

| Entry/state | Required behavior |
| --- | --- |
| Direct episode selection | Always show that episode's still, short synopsis, facts, and credits, including if unstarted or completed. Explicit selection wins over automatic continuation. |
| Show never watched, or marked unwatched/reset | Use the exact selected managed series backdrop and image variants used for that show on Watch, through the same show-artwork resolver. Use the short show synopsis and show identity. Target the first owned regular episode for Watch; its action label identifies the target. Specials do not silently precede S1 E1. This is required behavior, not an optional fallback. |
| Show has resumable progress | Resume the most recently active resumable episode for this profile. Show that episode's still and synopsis. Use activity timestamps, not highest completion percentage. |
| Target episode finishes | Once completion is persisted, advance automatic show context to the next uncompleted owned episode in canonical order. Its still, synopsis, facts, and action update together, even before it has progress. |
| Next numbered episode is missing | Never target a missing file. Offer the next available owned episode with its actual S/E label and a concise gap indication. Do not imply it immediately follows the completed episode. |
| No later owned episode remains | Show a truthful end-of-available-library state and an explicit rewatch option. Distinguish “all owned episodes watched” from “series complete.” If all owned episodes are completed, return to series artwork because there is no active continuation target. If earlier owned episodes remain uncompleted, expose those without silently starting a rewatch. |
| Show marked watched in More | Complete the currently owned episode set for this profile, remove its active continuation, and use series artwork. This does not claim missing/future episodes were watched or create fictitious playback sessions. |

Reuse the playback service's completion semantics, including explicit completion and rewatch state, rather than adding another percentage threshold. Define deterministic tie handling for multiple resumable episodes and files. Persist and read progress under the active profile. Recompute after playback return, completion, profile switching, relevant metadata updates, and browser navigation; cancel/ignore obsolete detail requests.

“In progress” describes the show's current viewing cycle, not just a nonzero percentage on the target episode. After E1 finishes, E2 can be the active target with zero progress and must still supply the hero still. An explicit whole-show reset ends that cycle; old history cannot reactivate it. Starting playback again starts a new cycle. Marking one episode unwatched resets only that episode and does not reset the entire show; an explicitly selected episode retains its still even immediately after that reset.

Validate that an episode query belongs to the requested show and is accessible/owned. Preserve the existing show-scoped detail route; all episode cards, Continue cards, and search links should produce the same context. Keep Home/Watch discovery artwork show-level according to the existing product rule; episode detail and Continue artwork remain episode-level.

## 3. Select artwork as one managed asset family

Resolve the episode's selected managed still once, including its base URL, small/medium/large variants, dimensions, and scope. Every hero URL and `srcset` candidate must belong to that asset. Never combine an episode base URL with show background variants. Show logo/branding may remain separate show-level identity.

For the series-artwork states above, use the same canonical asset family as Watch's show backdrop, including user artwork changes and the same unavailable-artwork fallback. The detail hero may have its established geometry, but it must not independently choose a different series image. Make the resolved state choose between a complete series asset family and a complete episode asset family before rendering.

Preserve deliberate user artwork choices. If the provider has no still, use an existing episode-scoped extracted frame where available, then a settled artwork-unavailable treatment. Do not substitute a different episode or present show artwork as an episode still. Reset image failure state when episode/artwork identity changes.

Validate response payloads, managed image responses, and browser `currentSrc`, at desktop and mobile sizes. Share the same resolver for directly selected, resumed, and next-episode details.

## 4. Enforce one short episode-description policy

Use one attributed episode synopsis selection policy everywhere: episode hero, owned episode card, Overview where needed, Continue preview, search preview, and editor preview.

Priority is an explicit episode synopsis edit, then the matched TMDB episode overview, then eligible episode-scoped embedded/local synopsis text. If none exists, use a concise unavailable state. Wikipedia extracts, Wikidata entity descriptions, generic show descriptions, parent descriptions, and AI summaries of encyclopaedic text are ineligible. A short description means the provider's synopsis, not the first paragraph of a long article.

Remove TV-child Wikipedia extract fetching and prevent child upsert/deferred enrichment from writing those texts into episode synopsis fields. Apply policy at canonical selection as well as presentation, so later enrichment cannot reintroduce it. Keep raw provider identity/source evidence separate from display copy. Preserve source, language, URL, retrieved time, and edit status through projection and attribution.

Normalize whitespace and markup consistently; allow responsive line clamping without changing which text wins. Episode details must not expose a second long Wikipedia overview. Keep the short TMDB show synopsis in the separate Series Description block under the owned summary. Remove TV Overview's concatenation of container and item prose, and do not label mixed-source text with one Wikipedia attribution.

Missing entries expose title, canonical episode position, and optional trustworthy air date, with the existing “Not in library” thumbnail. Their description and artwork are omitted from the display contract; render ownership first as an additional safeguard. They have no play, edit, progress, or owned-detail affordance.

## 5. Store and display episode-scoped cast and crew

Fetch credits keyed to the matched show/season/episode. TMDB provides an [episode credits endpoint](https://developer.themoviedb.org/reference/tv-episode-credits), [episode details](https://developer.themoviedb.org/reference/tv-episode-details), and [season aggregate credits](https://developer.themoviedb.org/reference/tv-season-aggregate-credits). Use episode credits as the source for episode participation; season aggregates support season-level views but do not prove appearance in any particular episode.

Store normalized credits with provider person/credit identity, local person link when available, show/season/episode scope, exact job/department or character, order, and provenance. A Wikidata person or episode link is optional. Preserve regular cast, guest cast, multiple characters, and multiple roles without ordinal alignment between unrelated arrays. Cached provider responses and existing rate-limit/retry infrastructure should keep UI reads local and prevent per-render fetches.

Stop promoting aggregate show cast/crew into every episode. Make quick retail hydration, deferred hydration, and rematch use the same scope rules. Retain genuinely show-level creator credits as show-level credits. Replace broad job substring classification: for example, “Assistant Director” must not become the episode's primary Director.

The selected episode's Overview peek shows its own cast and primary crew. Full cast/crew defaults to **This episode** when entered from an episode, with **All seasons** and concrete season options to broaden scope. Entering from a show defaults to All seasons. Display only seasons supported by actual credit evidence; do not invent season coverage from show membership or aggregate episode counts.

Extract the reusable role-filter control from the person works surface and reuse it for **All roles / Actor / Director / Writer / ...**. Use the shared `AppSelect` family for scope/season selection. Filters intersect, show truthful counts, and preserve all roles on one person card. Support complete paged results; keep small limits only for the Overview peek, never as a hidden truncation of “full cast.” Scope changes in the credit panel do not change the hero's selected episode.

Distinguish provider-wide season credits from credits on owned episodes. Broader full-cast browsing must not add catalogue-only participation to a person's Works in Your Library, People counts, contributor browse groups, or person collections. Missing provider credits produce an honest empty/unavailable state rather than inherited show credits.

## 6. Make the episode rail use available space

Remove the four-card logical window. Use the shared sequence rail with all eligible items reachable through native horizontal scrolling and, if needed, viewport-based virtualization. Let actual container width, gaps, and a consistent readable 16:9 card size determine visible capacity. At 1920x1080, use the full structural content width and show more than four when that width accommodates them. Do not shrink text or distort images to hit a fixed count.

Keep season selection, missing preference, Jump to, selected-episode focus, scroll position, and keyboard behavior coherent. Recalculate on resize and inspect lower-height CSS overrides. Missing toggling must not move the selected owned episode out of reach or change its identity.

Fix a related interaction inconsistency: the current episode artwork contains a playback link, while the title opens details and an edit button is embedded alongside it. Make the episode card one semantic detail link, with hero actions for playback and the shared detail editor for corrections. Restore visible completed state on TV episode cards; the current completion-badge condition excludes season containers. Keep completion separate from the current-item highlight and expose `aria-current`.

## 7. Build a shared More menu across media types

Keep one shared action policy, renderer, and command dispatcher for media details, with the same commands reused by existing song/track and playlist overflow menus. Menus depend on entity capabilities, ownership, active profile, state, and permission. Personal status changes must be available to profiles allowed to consume the media, independently of metadata-edit permission. People, characters, and other non-consumable entities must not acquire watched/read/listened controls.

Recommended menu contents, in a consistent order:

| Action | Availability and behavior |
| --- | --- |
| Mark as watched / Mark as unwatched | Movies and TV episodes. Show/season contexts use explicit bulk labels such as **Mark owned episodes as watched (12)** and **Mark owned episodes as unwatched (12)**. Partial state offers both completion and reset; fully completed state offers unwatched; unstarted state offers watched. |
| Mark as read / Mark as unread | Books and comic issues; homogeneous series may offer the same action for their owned titles, with the affected count. |
| Mark as finished / Mark as unfinished | Audiobooks, including their owned-series scope. Unfinished clears the resume position and track completion for that audiobook. |
| Mark as listened / Mark as unlistened | Music tracks and albums, with album scope clearly covering its owned tracks. Store an explicit listened status separately from qualified play counts. This neither invents a play nor erases real listening history. |
| Hide from Continue / Show in Continue | Started/resumable media and TV shows. Hide changes placement only, preserving progress and the active episode/hero. Show restores eligibility; completed/unstarted items are not fabricated as Continue entries. Use the existing surface's user-facing Continue wording consistently. |
| Viewing / Reading / Listening history | Opens profile-specific history for the item, or an owned-child aggregation for a show/series/album. Distinguish manual status changes from actual consumption. Build this as a shared scoped history view, not a separate management workspace. |
| Play next / Add to queue | Supported Listen tracks/albums, reusing `PlaybackSessionController` and the shared queue. Album insertion uses owned tracks in order. Do not expose unsupported Watch queues or queue audiobook segments as independent music tracks. |
| Add to playlist | Supported owned music selections, through the existing Listen playlist membership flow. Audio-book track/chapter rows do not implicitly become playlist songs. |
| Add to collection | Administrators only, through a picker for editable manual curated collections. Dynamic collections remain rule-driven; use their existing editor to change rules. Respect existing membership, scope, and eligibility rather than silently rewriting a collection's mode. |
| Copy library link | Accessible detail entities. Copy the canonical library route, retaining explicit episode selection. This is a library link subject to normal access checks, not a public sharing feature. A song links to its album/track context, never a new standalone song detail page. |
| Edit | Existing permission-aware shared editor. Artwork selection, match correction, and metadata refresh belong inside that flow; use the existing collection editor for curated collections. Label the target clearly: Edit episode versus Edit show. |
| File information | Owned file-backed media, via the existing Details/file surface and a file selector when multiple assets exist. Remote browsers must not be offered a nonfunctional local-folder command. |

Keep Play/Read/Listen/Resume and Restart where already required as primary actions. My List, reactions, format tools, and existing parent navigation keep their established hero/detail placements; do not duplicate them in More. Add no new menus to artwork-led library cards. Permanent file deletion, library reset, and provider administration stay in their established administration/editor workflows rather than expanding this everyday menu.

Apply status commands to a clearly defined target. Episode details change that episode; automatic show continuation details still represent the show, so bulk status labels name the owned episode scope rather than silently acting on its current playback target. Season actions apply only to that season. Series actions include eligible owned titles only. Mixed-media collections and playlists have no ambiguous bulk “watched” status; their members retain their own media-specific status. Across formats, preserve the existing separation between reading and listening progress: marking an ebook read must not finish the audiobook edition of the same work.

### Reset, history, and command semantics

Mark unwatched/unread/unfinished returns the selected experience to its unstarted state: clear its active resume position, explicit completion, and relevant current-cycle progress for the active profile. For TV whole-show reset, apply that to all currently owned episodes and playback variants, reset the show continuation pointer, and immediately restore the Watch series hero. Do not provide a redundant “Reset progress” entry with indistinguishable behavior; the status action is the reset. Explain this effect in the action's concise supporting text.

Keep historical sessions, bookmarks, annotations, ratings, My List membership, and music play counts. They describe different user intent and must not be erased by a status toggle. Starting over in the player remains distinct from resetting the entire show's status. New episodes acquired after a bulk mark-watched action start unviewed; the bulk action covers the owned set at the time of the command.

Use typed, server-authorized, profile-scoped, idempotent status commands with explicit entity, media/format scope, and expected revision. Show a brief result with **Undo** for status/Continue changes. Bulk results disclose the affected owned count. Make bulk changes atomic and make undo revision-aware so it cannot overwrite new playback. Coordinate with an active reader/player: invalidate older progress revisions and refresh its session state so a delayed autosave cannot resurrect pre-reset progress. A new deliberate consumption session may then record progress normally. This runtime concurrency control is not a compatibility migration.

Refresh detail, hero, episode rail, Continue shelves, status filters, and history after success. Preserve prior state or roll back optimistic UI on failure. Unknown or unsupported actions must not silently do nothing; hide inapplicable capabilities and provide meaningful failure feedback for available actions. Reuse shared menu grouping, icons, loading state, keyboard navigation, focus restoration, and mobile sizing.

Implementation touchpoints: `BuildOverflowActions`, `DetailActionAuthorizationPolicy`, contract action/status types, shared status/history application services and repositories, `ProgressEndpoints` and playback integrations, `DetailPage` action dispatch, `OverflowActionMenu`/`ManageActionsMenu`, and existing Listen row/playlist menus. Introduce distinct personal-state and metadata-management permissions and enforce them again at command endpoints. Do not solve this with per-page switch statements or labels without working commands.

## Delivery order and verification

1. Capture the current show/episode API payloads and desktop screenshots before modifications. Build deterministic fixtures from the observed identity shape, without full copyrighted provider prose. Prove duplicate unknown-season rows, responsive asset-family selection, and episode-credit contamination.
2. Fix identity, ownership, catalogue reconciliation, source policy, and typed credit storage together. Remove obsolete TV writers/readers instead of introducing compatibility paths. Add repeat-hydration and race-order tests so TMDB and Wikidata arrival order cannot change the result.
3. Implement shared profile-aware episode context, viewing-cycle/reset semantics, artwork-family selection, and the canonical synopsis/credit projections. Add the shared cross-media personal-status and history commands; update contracts and boundary snapshots.
4. Update the rail, credit filters, route validation, More menus, and refresh behavior using the existing detail/control components. Ship applicable More actions with working commands across Read, Watch, and Listen, including existing song/playlist surfaces. Update product documentation and AGENTS/CLAUDE guidance in the implementation change where behavior changes.
5. Validate a fresh ingest according to repository rules. Stop Engine/Web before development runtime work. Reset only verified disposable development state and the explicitly allowed test destinations; protect source originals. Reingest fixtures rather than add repair scripts, migration shims, or title-specific Breaking Bad fixes. No reset is part of this planning task.
6. Run `dotnet restore MediaEngine.slnx`, `dotnet build MediaEngine.slnx --no-restore`, and `dotnet test MediaEngine.slnx --no-build`, plus documentation and relevant contract guardrails. Existing TV composer tests often assert source-code strings; add behavioral fixtures that demonstrate the actual outcome instead of preserving incorrect implementation strings.

Required regression cases:

- Direct unstarted/completed episode, resume, completion-to-next, season boundary, missing gap, no next owned episode, multiple resumable episodes, and rewatch.
- Never-watched and reset show details resolve the same series artwork family as Watch; reset while explicitly viewing an episode keeps that episode's still. Finished E1 with unstarted E2 uses E2's still until the whole show is reset or no continuation remains.
- Mark watched/unwatched at episode, season, and show scope; mark read/unread, audiobook finished/unfinished, and music listened/unlistened; partial and completed menu states; bulk counts exclude missing items and new acquisitions remain unstarted.
- Reset with retained history/bookmarks, late player autosaves, simultaneous devices, retry, undo after intervening playback, and show artwork changes. Verify no fake sessions/play counts and no cross-profile or ebook/audiobook status leakage.
- More action capability/permission matrix across media and roles, including Consumer personal actions, administrator-only curated membership, complete history, queue ordering, playlist deduplication, canonical links, multiple file variants, and visible error handling.
- Two profiles with different histories; two files for one episode; inaccessible/unavailable assets; invalid cross-show episode query; rapid episode switching and browser Back.
- Duplicate catalogue/owned entries, missing season, Specials, same title in different seasons, alternative numbering, duplicate provider records, partial manifest totals, and late Wikidata hydration.
- Short TMDB synopsis versus longer Wikipedia text in both work and asset storage; absent synopsis; explicit edit; source attribution and language consistency.
- Episode still with conflicting show size variants; unavailable still; image failure and subsequent episode change; user-selected still; show-level discovery artwork remaining intact.
- Different episode directors/writers, guest appearances, one person in multiple roles/seasons, an assistant director, missing credits, more than 24 credits, season/role intersections, and correct owned-person attribution.
- API/DB count checks followed by before/after visual checks at 1920x1080, a shorter desktop, tablet, and mobile. Verify full-width rail capacity, no clipping, keyboard focus, card navigation, placeholder consistency, and actual displayed image identity.

The release gate is a fresh ingestion producing a single coherent TV model before UI validation passes. Fixing only the displayed duplicate count, clipping long descriptions, increasing four to six, or hiding repeated cast would leave the root causes active.

## Product-owner summary

Never-watched or reset shows will use the familiar series artwork from Watch. Shows being watched will use the current or next episode's still, while selecting an episode always shows that episode. More will provide consistent watched/read/listened status, history, and other relevant actions across media, with resets limited to the current profile and clear scope. Missing episodes will appear once, wider screens will use their space, and full credits will support season and role filters. The underlying fixes will keep these behaviors consistent after fresh ingestion and later enrichment.
