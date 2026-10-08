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

The edition- and release-aware extension is tracked in [Unified Media Editor: edition and release extension](./unified-media-editor-edition-extension-2026-09-30.md). Its ownership matrix and Work/Edition/Asset boundaries refine the implementation packages below; the existing completion evidence on this page applies only to the earlier editor scope.

The initial request was to **build an implementation plan**, with Sol coordinating Terra workers. The user subsequently authorized implementation with “proceed.” The pasted Work handoff and the Solo Leveling image remain design inputs, not independent instructions. This document records the intended experience and release checks; each behavior still needs verification against the implementation.

The image supplies visual rhythm and density for a selected-item editor. It does not establish a persistent parent banner, file checklist, duplicate artwork dashboard, or batch workflow. Its variant counts, TVDB identity, file write-back switch, progress, and episode states are examples rather than verified local data. The existing movie artwork editor and `ArtworkWorkspace` remain the functional baseline.

## Product walkthrough

1. **Open one editor with one clear target.** The header changes to name the selected item while adjacent scope selectors provide its parent and sibling context. Exactly one item is highlighted as the editing target. Existing detail, playback, and browse routes stay the same. **Work packages A and B.**
2. **Edit the target in four familiar sections.** Details, Artwork, Match & Identity, and History follow the highlighted target. File facts and applicable maintenance controls live in Details; there is no separate Files destination. Match status appears at the top of Details rather than in selector badges. **Work packages A, B, and D.**
3. **Choose artwork directly.** Artwork roles come from Engine capabilities. Clicking a saved tile makes it preferred immediately; upload, library or URL import, provider import, and confirmed unlink also persist immediately. Owner-scoped artwork remains attached to its declared owner, and inherited artwork is identified without creating a second editing target. **Work packages B, C, and F.**
4. **Correct only identities the Engine can apply safely.** Match search retains the current identity until the user explicitly saves a result. Same-owner and supported same-show TV corrections use reviewed server evidence. A selected music file can move to one exact MusicBrainz release Track ID through a bound review and atomic transaction. Other cross-owner moves remain blocked. **Work packages D and E.**
5. **Keep specialized editing in its established place.** Audiobook chapter-title overrides remain under Chapters. Structural shelf editing targets the structural owner Work. Universe and entity editing remain separate authorized content inside the shared shell and do not become a media-editor shortcut. **Work packages A, B, and C.**
6. **Preserve the same target and actions on smaller screens.** Parent context, selectors, section navigation, and the highlighted target remain understandable at desktop, tablet, and mobile widths. Buttons align with their controls, tile trash stays independently keyboard accessible, and no region overlaps another. **Work packages B and G.**

Observable acceptance: an administrator can open a movie, album, comic run, audiobook, and TV show; identify the single highlighted target; see the header update when a child is selected; persist and remove artwork from its tiles; apply a supported same-owner correction; see a truthful blocked state for unsupported relocation; and use the same controls at desktop, tablet, and mobile widths.

### Current handoff reconciliation

The current product surface is the single-target editor described above. It has no persistent file browser, checklist, selection count, or batch editor UI. One exact, reviewed music release-track move is supported for a selected file; cross-show TV and other unverified cross-owner moves remain blocked.

### Scope boundaries and decisions

- Use the present editor launch and permission model. This plan does not change Home, lane browse, playback, detail routes, owned-only TV counts, TVDB-for-TV/TMDB-for-movies routing, or canonical contributor rules.
- A child can have source-specific imagery, such as an episode still or comic issue cover, without gaining a general purpose child artwork-management page. Inheritance governs parent presentation and eligible embedded file cover; it must not overwrite a correct episode still used by episode detail or Continue.
- Keep show, season, and episode provider identities independently addressable. A child match may propose a parent move, but it must never silently renumber an episode, overwrite a sibling, or change an unrelated season match.
- The image's **Banner** card is conditional. The canonical artwork model currently exposes Primary, Background, Portrait, and Logo, with scope-specific slots. Package C must establish a real supported Banner slot and delivery contract before displaying Banner as an editable role; otherwise omit that card rather than relabel another role.
- Multi-item matching is outside this editor plan. Do not add a persistent checklist, selection counter, or shared batch Save/Discard surface to the single-target editor.
- Podcasts are named in the handoff but have no established editor hierarchy/schema in the inspected implementation. Treat them as a capability-gated follow-on after their owned show/episode model and provider support are audited; do not route them through the Books fallback or promise a working podcast editor in this release.
- Artwork file embedding requires actual format and library-policy support. Existing metadata write-back and optional sidecar export do not, by themselves, establish that preferred inherited art has been embedded in every child file.

## Current implementation and gaps

| Area | Existing foundation | Planned gap |
| --- | --- | --- |
| Editor | `SharedMediaEditorShell` has context scopes and Details, Artwork, Match & Identity, Files, and History; TV show/season/episode and music album/track are modeled. | Present one four-destination navigation model; make the parent Match & Identity default a child browser; move valuable Files controls into the relevant inspector or Details surface. |
| Structural series | `CollectionEditorShell` handles collection-backed book/comic/movie series and already embeds `ArtworkWorkspace`. | Share the visual shell and artwork overview through an adapter without replacing collection membership/authorization behavior. |
| Artwork | `ArtworkWorkspace`, role catalog, scoped TVDB images, preferred variants, library picker, upload, URL import, and renditions already exist. | Present one owner and role gallery for the selected target, persist supported actions immediately, show effective provenance, and avoid a second parent or season dashboard. |
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

- Extract the selected-item header, four-item navigation, section/panel rhythm, spacing tokens, action placement, and responsive inspector layout from the oversized `SharedMediaEditorShell` into reusable components. Adapt structural series presentation while retaining its ownership and membership safeguards.
- Replace `file` tab routing and `MediaEditorTabState` return behavior with selected-item inspection and applicable Details subsections. Keep deep links and review launches resolving to the relevant new section; do not discard file path, technical facts, reread, subtitles/lyrics, audiobook chapter editing, or retry actions.
- Make all selects use the existing shared select controls. Keep role cards and child rows sized consistently, and use the common artwork sizing/rendition helpers rather than raw originals.
- Establish responsive/keyboard behavior with a browser-width acceptance matrix. The parent context and filters persist when opening and closing an inspector.

### C. Parent artwork overview and inheritance (walkthrough 2)

- Use one `ArtworkWorkspace` gallery for the selected owner and role. Role controls switch that gallery in place; do not add a second role summary, parent overview, season dashboard, or child-status panel above it. Show the preferred image, variants, source, and dimensions with bounded renditions.
- Season, episode, album, edition, and structural artwork become available by selecting that editing target through the shared context selectors. The header and gallery update together, and effective provenance states whether the result is explicit or inherited.
- Implement a server-side effective-artwork resolver: explicit season preference, then series preference for inheritable presentation/file-cover roles, with recorded source scope and asset ID. Keep `EpisodeStill` independent. Album-to-track and audiobook-to-part use the same rule through target capabilities; comic issue imagery remains source-scoped where appropriate.
- Resolve effective provenance for the selected target without rendering a second child-status dashboard. Preserve show-level root artwork on Watch landing slides and exact episode stills on episode-specific surfaces.
- Reuse central role/slot definitions and the existing artwork picker; add any new Banner role only after role catalog, persistence, API, responsive rendition, and UI contracts are covered together.

### D. Context selectors and target inspector (walkthrough 2, 4, 6)

- Return bounded parent, sibling, Edition, and Asset context for the current target. Large child sets use searchable, viewport-bounded selector popovers rather than a persistent file browser or checklist.
- Selecting context updates the header, highlighted target, Details, Artwork, Match & Identity, and History together. Preserve target identity across asynchronous loads and discard stale responses.
- **Change Match** is the only transition to candidate search. Label current local identity and provider search separately, and reuse existing provider-specific controls where they fit.
- Move selected-item file facts and operations from Files into Details. Do not expose unavailable controls when an item has no file or the format is unsupported.

### E. Individual match apply and reparenting (walkthrough 4)

- Use a preview/apply contract for the selected target, expected revision, candidate identity, current owner, and any unsupported owner change.
- Same-owner correction validates provider, level, order scheme, ownership, and revision before applying. It queues established enrichment or organization work and returns the refreshed target state.
- Cross-show, cross-run, and other unverified cross-owner moves are blocked. Music release-track relocation uses a dedicated one-file workflow with exact MusicBrainz release Track ID, source revision, destination conflict checks, and atomic audit and job recording.
- Same-owner corrections update the target in place. A blocked relocation leaves the current identity and owner unchanged and explains the unsupported boundary.

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
- Match & Identity follows the selected target and retains its current identity until Apply. Provider candidates appear only after Change Match; no persistent owned-file browser or checklist is part of the editor.
- Existing artwork variants remain fully manageable; TV show and season art share one pane; effective inheritance is truthful; episode-specific stills remain available where required.
- Applying an individual child match preserves the current owner unless a dedicated atomic relocation workflow supports the move. The editor previews available evidence, preserves review context, and blocks unsupported cross-owner moves without mutation. Provider and season-order safeguards remain enforced.
- Artwork write-back status is derived from actual policy, format support, and per-file processing evidence, including disabled, pending, unsupported, and failed cases.
- The named representative journeys and responsive/keyboard checks in the walkthrough pass with focused tests and a successful solution build.

## Implementation and verification status (September 30, 2026)

The unified four-section shell, selected-item context navigation, one owner/role artwork gallery, migrated file actions, guarded individual match preview, and policy-aware artwork write-back are implemented in this checkout. A disposable authenticated Engine and Dashboard exercised representative media and responsive widths. The comic issue editor opens within the same dialog and returns to the selected issue in the origin context. Artwork status correctly reported **Disabled** for the fixture's read-only, artwork-write-back-disabled policy; no real media was changed.

The full solution build passed with zero warnings and errors. Focused API editor/TVDB/scale tests passed 27 of 27. The full Dashboard test suite passed 1,199 of 1,200; the remaining provider-icon fixture failure predates this editor work and is tracked separately. Focused artwork write-back tests passed in the API and ingestion projects. The disposable fixture used temporary config, database, credentials, and synthetic rows; it did not copy personal secrets or trigger AI downloads.

**Release gates still open:**

- Cross-show TV apply is intentionally unavailable. The current transaction is same-show only and rejects a cross-show move without mutation. A future dual-owner transaction needs provider-backed acceptance after it can authorize and update both shows atomically.
- ComicVine issue matching deliberately keeps a confirmed issue in its existing run when the provider evidence cannot safely verify a different destination. Provider-derived cross-run comic moves therefore remain outside this release; the UI describes this limit rather than promising a move.
- The fixture verified truthful **Disabled** artwork state and disposable MP3 write/read-back tests, but it did not visually exercise every Pending, Unsupported, Failed, and Embedded state across all media types. Video, comic, and EPUB embedding remain Unsupported under the conservative format matrix.
- The named provider-icon Dashboard test failure and the remaining supported provider-backed journeys must be resolved or explicitly accepted before marking all completion criteria met. Cross-parent relocation is a separate future capability, and podcasts remain a capability-gated follow-on.

The plan remains **in progress** until these release gates are closed. The implementation in this checkout is reviewable and the local UI journeys above are verified; cross-parent apply is outside the currently supported workflow.

## Plain-English completion summary

Tuvima now presents one clear editing target, and the header changes with that selection while keeping parent and sibling context nearby. People manage one owner and artwork role at a time directly from its gallery. Safe same-owner corrections and one-file exact music release-track moves are available. Other unverified cross-owner moves remain blocked. Desktop and mobile browser review confirmed the main layout; live provider-backed move verification remains outstanding.
