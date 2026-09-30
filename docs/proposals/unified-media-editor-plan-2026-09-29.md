---
title: "Unified Media Editor Implementation Plan"
summary: "Unify editor navigation, parent artwork, owned-child inspection, and identity correction across supported media without replacing existing artwork or ingestion services."
audience: "product and engineering"
category: "proposals"
product_area: "media editor, artwork, identity, write-back"
status: "in progress"
---

# Unified media editor

## Planning scope and design inputs

The initial request was to **build an implementation plan**, with Sol coordinating Terra workers. The user subsequently authorized implementation with “proceed.” The pasted Work handoff and the Solo Leveling image remain design inputs, not independent instructions. This document records the intended experience and release checks; each behavior still needs verification against the implementation.

The image supplies the desired hierarchy and visual rhythm: a stable parent header and navigation; an artwork overview with series roles, seasons, and a compact child-status area; and a main workspace that remains usable at desktop width. Its variant counts, TVDB identity, file write-back switch, progress, and episode states are examples, not verified local data or proof that those capabilities already exist. The existing movie artwork editor and `ArtworkWorkspace` remain the functional baseline.

## Product walkthrough

1. **Open one recognizable editor from a detail page.** An administrator editing a movie, TV show, album, audiobook, or series sees the same header, spacing, section treatment, and four primary destinations: Details, Artwork, Match & Identity, and History. The parent title and source remain visible while the user examines a child. Closing returns to the originating detail context. Existing details, playback, and library browsing routes stay as they are. **Work packages A and B.**
2. **Manage artwork where it has meaning.** On the TV show Artwork page, a user first sees available series artwork roles and variant counts, then season cards showing custom/provider/inherited state, then a compact list of owned episodes showing what artwork they inherit and whether supported files are current. Selecting a role or season opens the existing full variant controls inside this workspace: preferred image, upload, library/URL/provider sources, removal, image metadata, and zoom. Albums and audiobooks show their release/book art with track/part inheritance; movies show their own art; structural book/comic/movie series use the same visual frame through their collection-backed editor target. Provider episode stills continue to appear on episode details and Continue cards. **Work packages A, B, and C.**
3. **Find a local child before correcting it.** Opening Match & Identity from a parent starts with an owned-item browser, with no child preselected and no provider candidates shown. Search checks both the original filename/path and current matched title/number; season, disc, volume, match status, and file status are filters where applicable. Selecting a row opens an inspector showing its local file, present match, provider ID, and canonical hierarchy. A movie with one file uses the same model in a compact form. **Work package D.**
4. **Change one match with a clear preview.** The inspector's Change Match action opens a separate provider-candidate search. Before applying, the user sees the proposed child identity and any parent move. A confirmed child match derives its parent hierarchy where provider evidence is sufficient. The editor refreshes the row and inspector in place; if the item moves to another parent, the original review list, search, filters, and scroll position remain until the user chooses to leave. Existing provider-specific ordering and cross-show safeguards still apply. **Work packages D and E.**
5. **Understand file synchronization without mistaking it for matching.** Artwork shows an honest library/source-policy state and per-file outcome: current, pending, unsupported, failed, or disabled. When supported and enabled, changed inherited art can queue a bounded, retryable file update. When disabled or unsupported, the preferred artwork still works inside Tuvima, and the UI never claims it was embedded. File name, path, technical details, reread, subtitle/lyrics management, and write-back remediation remain accessible from the selected item's inspection/Details area after the Files tab is retired. **Work packages B and F.**
6. **Use the same experience on smaller screens.** The child browser keeps its width; the inspector becomes a drawer or stacked panel. Keyboard users can search, select, inspect, change a match, and return to the same list position. Details retains editable display facts and source information; History records actual edits and processing events. **Work packages B, D, F, and G.**

Observable acceptance: an administrator can perform these journeys on a movie, a 10-track album, a comic run, an audiobook, and a show with at least 1,000 owned episodes; manage show and season variants; distinguish inherited art from an episode still; correct a same-parent and a cross-parent child match; and see truthful file-update status at desktop and mobile widths.

### Scope boundaries and decisions

- Use the present editor launch and permission model. This plan does not change Home, lane browse, playback, detail routes, owned-only TV counts, TVDB-for-TV/TMDB-for-movies routing, or canonical contributor rules.
- A child can have source-specific imagery, such as an episode still or comic issue cover, without gaining a general purpose child artwork-management page. Inheritance governs parent presentation and eligible embedded file cover; it must not overwrite a correct episode still used by episode detail or Continue.
- Keep show, season, and episode provider identities independently addressable. A child match may propose a parent move, but it must never silently renumber an episode, overwrite a sibling, or change an unrelated season match.
- The image's **Banner** card is conditional. The canonical artwork model currently exposes Primary, Background, Portrait, and Logo, with scope-specific slots. Package C must establish a real supported Banner slot and delivery contract before displaying Banner as an editable role; otherwise omit that card rather than relabel another role.
- Multi-item **batch matching** is a subsequent phase. The first release may prepare selection and contracts, but must not present one provider result as a match for every selected file. A later batch action needs individual proposed mappings and explicit review of ambiguity.
- Podcasts are named in the handoff but have no established editor hierarchy/schema in the inspected implementation. Treat them as a capability-gated follow-on after their owned show/episode model and provider support are audited; do not route them through the Books fallback or promise a working podcast editor in this release.
- Artwork file embedding requires actual format and library-policy support. Existing metadata write-back and optional sidecar export do not, by themselves, establish that preferred inherited art has been embedded in every child file.

## Current implementation and gaps

| Area | Existing foundation | Planned gap |
| --- | --- | --- |
| Editor | `SharedMediaEditorShell` has context scopes and Details, Artwork, Match & Identity, Files, and History; TV show/season/episode and music album/track are modeled. | Present one four-destination navigation model; make the parent Match & Identity default a child browser; move valuable Files controls into the relevant inspector or Details surface. |
| Structural series | `CollectionEditorShell` handles collection-backed book/comic/movie series and already embeds `ArtworkWorkspace`. | Share the visual shell and artwork overview through an adapter without replacing collection membership/authorization behavior. |
| Artwork | `ArtworkWorkspace`, role catalog, scoped TVDB images, preferred variants, library picker, upload, URL import, and renditions already exist. | Add parent overview and season selection, explicit effective-artwork provenance, child inheritance state, and truthful Banner capability. |
| Identity | Current show/season/episode matching and hierarchy-preview machinery exist. | Page/search owned children; separate inspection from candidate search; atomically apply supported reparenting with a reviewable impact preview. |
| Files and write-back | A Files tab contains file/processing facts, reread, text-track tools, and retry. Metadata write-back, format taggers, and optional artwork sidecar export exist. | Preserve those tools after tab removal; add policy-aware inherited-art embedding, per-asset state, retries, and event/read models where format support is real. |

Relevant entry points: `src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor`, its `.razor.cs` and `.razor.css`, `MediaEditorTabState.cs`, `src/MediaEngine.Web/Components/Collections/CollectionEditorShell.razor`, `src/MediaEngine.Web/Components/Artwork/ArtworkWorkspace.razor`, `src/MediaEngine.Contracts/Metadata/MediaEditorContracts.cs`, `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs`, `src/MediaEngine.Api/Services/ReadServices/MediaEditorNavigationReadService.cs`, `src/MediaEngine.Ingestion/Services/WriteBackService.cs`, and `src/MediaEngine.Api/Services/AssetExportService.cs`. The existing `docs/proposals/tvdb-setup-tv-season-editor-plan-2026-09-28.md` records TV matching already implemented locally and must be treated as a compatibility constraint.

## Technical work packages

### A. Contract and baseline decisions (walkthrough 1, 2)

- Record the supported editor-target matrix for movie, TV show/season/episode, music album/track, book/comic/movie series, comic issue, and audiobook release/part. Each target states its parent, owned leaf, optional intermediate level, artwork owner, provider identity level, and file capabilities. Keep actual hierarchy as data, not branching markup for every media type.
- Extend `MediaEditorContextDto` or a dedicated read model with a stable launch parent, target hierarchy, effective artwork owner/provenance, supported roles, child collection capability, file-policy state, and a revision token. Keep existing clients working during migration.
- Audit existing movie artwork behavior, TVDB season/episode match behavior, collection-backed series launch, authorization, and file tools as regression baselines. Resolve whether comic issues and audiobook chapters are separate owned files in each storage shape before defining labels.
- Decide Banner from actual provider, storage, and image-delivery support. Specify empty, missing, provider-only, and inherited states; never infer an available role from the screenshot.

### B. Shared editor shell and Files migration (walkthrough 1, 2, 5, 6)

- Extract the parent header, four-item navigation, section/panel rhythm, spacing tokens, action placement, and responsive inspector layout from the oversized `SharedMediaEditorShell` into reusable components. Adapt `CollectionEditorShell` for structural series presentation while retaining its collection-specific membership tools.
- Replace `file` tab routing and `MediaEditorTabState` return behavior with selected-item inspection and applicable Details subsections. Keep deep links and review launches resolving to the relevant new section; do not discard file path, technical facts, reread, subtitles/lyrics, audiobook chapter editing, or retry actions.
- Make all selects use the existing shared select controls. Keep role cards and child rows sized consistently, and use the common artwork sizing/rendition helpers rather than raw originals.
- Establish responsive/keyboard behavior with a browser-width acceptance matrix. The parent context and filters persist when opening and closing an inspector.

### C. Parent artwork overview and inheritance (walkthrough 2)

- Compose a role-summary layer above `ArtworkWorkspace`; clicking a role opens its existing variant picker and metadata. Show actual preferred art, count, source, and dimensions from the artwork API, with bounded thumbnail renditions.
- Add a season section driven by owned show hierarchy and scoped season art. Selecting a season swaps the active `ArtworkWorkspace` target in the same Artwork pane. State whether the effective art is custom, provider, or inherited; Specials follow the same rule.
- Implement a server-side effective-artwork resolver: explicit season preference, then series preference for inheritable presentation/file-cover roles, with recorded source scope and asset ID. Keep `EpisodeStill` independent. Album-to-track and audiobook-to-part use the same rule through target capabilities; comic issue imagery remains source-scoped where appropriate.
- Add an owned-child inheritance/status read model with filtering/paging, using only owned assets. Do not fetch every child image or full record merely to render the overview. Preserve show-level root artwork on Watch landing slides and exact episode stills on episode-specific surfaces.
- Reuse central role/slot definitions and the existing artwork picker; add any new Banner role only after role catalog, persistence, API, responsive rendition, and UI contracts are covered together.

### D. Owned-item browser and contextual inspector (walkthrough 3, 4, 6)

- Add a paged or keyset `GET` read endpoint for owned children under an editor parent. Index/search both source filename/path and current metadata title/number. Return stable ordering, total/result counts, grouping/filter facets, match state, file state, asset ID, work ID, and canonical hierarchy without listing provider-catalog children as owned.
- Use season/disc/volume as filters, not a giant sidebar or mandatory drill-down. Debounce/cancel searches; preserve URL or editor state for filters, selected row, and scroll position. Validate performance with at least 1,000 episodes and an appropriate database query plan.
- Selecting a row opens a default read-only inspector; **Change Match** is the only transition to candidate search. Label local search and provider search separately. Reuse existing TVDB episode/season candidate controls, retail/canonical candidate services, and source-to-target preview where they fit.
- Move selected-item file facts and operations from Files into inspection/Details. Do not expose unavailable controls when an item has no file or the format is unsupported.

### E. Individual match apply and reparenting (walkthrough 4)

- Introduce a preview/apply contract carrying the selected owned asset/work, candidate provider and child ID, expected revision, current and proposed hierarchy, destination identity evidence, affected files/containers, and any conflict. Require explicit confirmation for a parent move.
- Reuse the existing candidate and hierarchy-alignment services where possible. Applying a child match validates provider, level, order scheme, source ownership, and revision; updates child identity and required parent links in one durable transaction; queues existing enrichment/organization jobs; refreshes artwork relationships and read models; and retires an obsolete derived container only when empty and safe. On partial downstream failure, retain a recoverable pending state rather than claiming completion.
- Do not regress independently matched TV show/season records. Cross-show episode changes currently have guards; replace only the intentionally approved move path with an explicit preview. A track-to-release or issue-to-run move follows the same contract where provider IDs give reliable parent evidence; ambiguous identity stays in review.
- Return the new canonical location and job state. Refresh the origin list without losing the working session; show a moved-item notice and a link to the destination. Same-parent corrections update the selected row and inspector in place.

### F. Artwork write-back, status, and history (walkthrough 5, 6)

- Audit `WriteBackService`, `RetagSweepWorker`, format taggers, source mutation policies, and `AssetExportService`. Record a tested per-format support matrix for audio, video, ebook, comic, and audiobook files before offering embedding. Reuse existing queue, retry, and source-protection boundaries. Add an artwork-specific desired-version key per eligible file (effective preferred asset plus policy/format version) and durable current/pending/unsupported/failed outcome. Metadata-tag success or sidecar export must not be reported as embedded artwork success.
- Enqueue affected owned files when series/season/album/audiobook preferred art or an inheritance override changes. Make work idempotent and bounded; check read-only/linked-source policy and format support before mutation. Read back the physical file tag after writing before reporting **Embedded**. Preserve existing file-specific art where policy says it wins, and never replace the provider episode still relationship as a side effect.
- Provide a library-settings link and status/retry controls only when policy permits. The image's inline switch is not authority for a new per-series policy toggle; use the real library/source configuration.
- Log preferred-art changes, match/parent changes, provider refresh, file embed success/failure, and user overrides through the existing activity/history infrastructure. Present only events actually recorded, with source and timestamp.

### G. Integration and release verification (walkthrough 1–6)

- Add focused contract, storage, API, and Blazor tests for target capabilities, ownership, inherited-art precedence, episode-still preservation, search over wrong metadata/source filename, paging, inspector/candidate state, apply conflict and rollback, cross-parent move, file-policy outcomes, and migrated Files actions.
- Run restore/build and the relevant .NET test projects. Use disposable media fixtures for actual file mutation; never use a personal library as a write-back test target. Verify representative desktop, tablet, and mobile renders; keyboard/focus behavior; compact/large artwork URL selection; authorization; and a 1,000-episode query and UI interaction.
- Record any existing unrelated test failures separately. Release only after an authenticated end-to-end pass with a movie, album, audiobook, comic run, and TV show; validate both successful and unsupported/failed file outcomes.

## Sol/Terra execution model

**Sol orchestrator** owns the cross-media contract, task sequencing, integration branch, interface decisions, review, and acceptance evidence. Sol checks the current `AGENTS.md` and baseline before work, keeps the product walkthrough in sync with material plan changes, and resolves contract conflicts before assigning edits. Sol integrates one vertical slice at a time; it does not ask two workers to edit `SharedMediaEditorShell` concurrently.

| Sequence | Terra worker assignment | Dependency and handoff to Sol |
| --- | --- | --- |
| 1 | Read-only baseline audit: editor targets, collection editor, artwork capabilities, Files tools, provider match rules, and write-back formats. | Package A target/capability matrix and regression list. |
| 2 | UI worker: shared shell, structural-series adapter, Files migration, responsive styles. | A contracts; Package B with screenshots and focused component tests. |
| 2 | Artwork worker: effective art resolver, season overview, inheritance/read status, format-support audit. | A contracts; Package C, then F after Sol accepts resolver semantics. |
| 2 | Identity worker: paged owned-child endpoint, browser/inspector, explicit candidate state. | A contracts; Package D with 1,000-item evidence. |
| 3 | Identity worker: preview/apply reparent transaction and recovery path. | D selection contract; Package E and cross-provider regression tests. |
| 3 | Artwork worker: policy-aware embedding, durable per-file status, retry and history. | C effective-art contract; Package F and disposable-file tests. |
| 4 | Sol integrates UI, artwork, identity, and source-policy behavior, runs Package G, and resolves acceptance gaps. | All prior packages; one coherent four-section editor. |

Workers use isolated worktrees or nonoverlapping files when tasks run in parallel, submit small reviewable changes, and report the observable behavior and tests. Sol owns any shared-shell merge and any change to identity/artwork contracts that crosses worker boundaries. Sol should stop a slice for a concrete decision if provider identity cannot reliably derive a parent or a file format cannot safely embed art; it should not substitute a guessed mapping or a false status.

## Completion criteria

- Four primary editor destinations appear consistently for supported media, with no separate Files tab and no lost file-management capability.
- Parent Match & Identity opens an owned-child browser with fast filename and metadata search, filters, selection, and a read-only inspector; provider candidates appear only after Change Match.
- Existing artwork variants remain fully manageable; TV show and season art share one pane; effective inheritance is truthful; episode-specific stills remain available where required.
- Applying an individual child match derives supported parent hierarchy, previews a move, preserves review context, and produces recoverable job/status information. Provider and season-order safeguards remain enforced.
- Artwork write-back status is derived from actual policy, format support, and per-file processing evidence, including disabled, pending, unsupported, and failed cases.
- The named representative journeys and responsive/keyboard checks in the walkthrough pass with focused tests and a successful solution build.

## Implementation and verification status (September 30, 2026)

The unified four-section shell, parent artwork overview, owned-child browser and inspector, migrated file actions, guarded individual match preview, and policy-aware artwork write-back are implemented in this checkout. An administrator used a disposable, authenticated Engine and Dashboard with synthetic media to open a movie, book, audiobook, 10-track album, comic run, and 1,000-episode TV show. Desktop, 768-pixel tablet, and 390-pixel phone layouts were checked. The TV browser found `Episode 999` in about 340 ms in the browser after a query fix; the direct authenticated API probe took about 296 ms including test-helper startup. The comic issue editor now opens within the same dialog and returns to the selected issue in the origin browser. Artwork status correctly reported **Disabled** for the fixture's read-only, artwork-write-back-disabled policy; no real media was changed.

The full solution build passed with zero warnings and errors. Focused API editor/TVDB/scale tests passed 27 of 27. The full Dashboard test suite passed 1,199 of 1,200; the remaining provider-icon fixture failure predates this editor work and is tracked separately. Focused artwork write-back tests passed in the API and ingestion projects. The disposable fixture used temporary config, database, credentials, and synthetic rows; it did not copy personal secrets or trigger AI downloads.

**Release gates still open:**

- A live provider-backed TVDB cross-show episode preview and apply was not performed in the disposable fixture because it had no TVDB credentials. The implementation has remote show/season/episode/default-order verification, optimistic revision checks, and focused tests, but the authenticated end-to-end apply still needs a provider-enabled test environment.
- ComicVine issue matching deliberately keeps a confirmed issue in its existing run when the provider evidence cannot safely verify a different destination. Provider-derived cross-run comic moves therefore remain outside this release; the UI describes this limit rather than promising a move.
- The fixture verified truthful **Disabled** artwork state and disposable MP3 write/read-back tests, but it did not visually exercise every Pending, Unsupported, Failed, and Embedded state across all media types. Video, comic, and EPUB embedding remain Unsupported under the conservative format matrix.
- The named provider-icon Dashboard test failure and a final provider-enabled cross-parent journey must be resolved or explicitly accepted before marking all completion criteria met. Podcasts remain a capability-gated follow-on under the scope boundary above.

The plan remains **in progress** until these release gates are closed. The implementation in this checkout is reviewable and the local UI journeys above are verified, but those observations are not evidence of a provider-backed cross-parent apply.

## Plain-English completion summary

Tuvima now has one consistent editor for the supported media tested here. People can find an owned episode, track, or issue, inspect its current identity, and start a deliberate correction without losing their place. Artwork remains managed at the appropriate parent level, and the app reports whether eligible child files received it. The local journeys and large-library search work in the isolated test library. Provider-backed cross-show matching and the remaining artwork states still need release verification, so the plan remains in progress.
