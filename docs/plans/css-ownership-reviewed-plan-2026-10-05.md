# Dashboard style ownership: reviewed delivery plan

**Implementation update — 6 October 2026:** The user subsequently authorized every step. WP1–WP6 are implemented; WP7 evidence and acceptance are being completed in [the implementation report](../reports/css-ownership-2026-10-06.md). The line and priority gates pass. The isolated bundle reduction gate is unmet and has not been amended.


Status: Implementation authorized across all work packages and in progress. Reviewed October 5, 2026 against local HEAD `bbfb3b9e`. Source material: the supplied `css-ownership-codex-spec.md` from Claude. Its model assignments, execution commands, commit instructions, and stop conditions are proposals reviewed here; the user's subsequent request authorizes implementation of this reviewed plan.

The recommended delivery keeps the Dashboard's current appearance and behavior while moving styling to the components responsible for it. Retain Claude's ownership-first approach, but correct the scope, guardrails, and verification mechanics below before implementation. There is no proposed player or browse redesign.

## Plain-English product walkthrough

These numbered outcomes describe the complete experience and map to the technical work packages below.

1. **Browse the same Home and detail pages with more reliable styling.** Opening Home, a movie, an owned TV episode, a book, a comic, an album, an audiobook, a person, or a collection should show the same artwork, typography, controls, progress, and spacing. Home rotation and its pause conditions stay the same. Detail navigation, ordered series, missing-item controls, credits, and ownership counts keep their current meanings. The change makes each part's appearance easier to maintain without accidentally affecting another screen. **Observable acceptance:** paired desktop and phone views have the same visible layout; detail changes do not alter Home; keyboard focus, links, and scrolling still work. Maps to **WP1–WP3 and WP7**.

2. **Edit through the same single editor.** Opening Edit keeps the current page underneath one modal, at the same URL, with the same header, section navigation, permissions, Save/Cancel behavior, and warning for unsaved changes. Details, Artwork, Match & Identity, and History become internal presentation sections rather than a larger screen or additional editor. Existing File, Links, and other applicable sections remain available. Collection and Gallery editors keep their current navigation. **Observable acceptance:** editing a field enables Save; save, cancel, target selection, provider preview/apply, artwork selection/lightbox, and unsaved-change dismissal behave as before. Maps to **WP1, WP4, and WP7**.

3. **Use Settings and shared switches without a visual surprise.** Administrators see the same Settings navigation, breadcrumbs, review counts, and authorized actions. On/off switches retain their current size, label, checked, disabled, and focus appearance both inside Settings and in other screens such as View folders. The change gives shared controls one clear styling owner while retaining any proven existing contextual variants. **Observable acceptance:** switches remain readable and clickable on desktop and phone, and ordinary profiles retain the same permission-gated views. Maps to **WP1, WP5, and WP7**.

4. **Keep playlists and listening tools working as they do today.** Playlist pages retain their rail, editing, ordering, queue behavior, and playback actions. Desktop lyrics and phone/popout player surfaces remain regression checks because global CSS changes can affect them. Audiobook segments remain the existing tracks presentation. **Observable acceptance:** playlist links and edits work, and the checked player controls remain visible and usable without a changed playback session owner. Maps to **WP1, WP6, and WP7**.

5. **Make future changes easier to check.** Developers get an ownership inventory, limits on large component stylesheets, and checks that prevent styling overrides growing again. The maintenance result is measured honestly: moving a rule does not automatically make the downloaded stylesheet smaller. **Observable acceptance:** the completed files meet their size limits, visual comparisons pass, and the final report shows actual before/after size and override numbers with any outstanding limitations. Maps to **WP1 and WP7**, with every extraction updating its evidence.

Representative journeys: open Home and then the same book/movie/episode detail; open an album and follow a credited person; inspect an ordered series and toggle Show missing; edit a field and cancel with unsaved changes; preview a match and inspect artwork; visit library/network settings and a non-Settings switch; open a playlist and then desktop lyrics or the phone player. Use disposable QA data for editable journeys.

**Scope boundaries:** no new routes, Engine/Storage/Contracts changes, catalogue repairs, provider semantics, playback ownership changes, token-value changes, application dependencies, or new user features. Existing Home long-title clipping is recorded as a known baseline defect and is outside this delivery. Successful maintenance preserves the existing experience; it does not promise a perceptible speed improvement from byte counts alone.

## Review findings and corrections to Claude's proposal

| Finding | Evidence and consequence | Correction |
| --- | --- | --- |
| The supplied baseline is historical. | The attachment names `13a71803`; this checkout is `bbfb3b9e`. Fresh source measurements differ from the quoted report. | Rebuild and capture the exact implementation starting state in WP1; retain historical figures as reference only. |
| The initial line-limit exceptions omit Listen. | `ListenPage.razor.css` has 2,020 lines, alongside DetailPage at 6,861 and the shared editor at 4,997. | Include all three initial exceptions. A raw `--max-lines 2000` check is a final gate, not a passing WP1 check. Remove each exception when that file is finished. |
| Per-file override ratchets conflict with ownership moves. | Moving retained `!important` or `::deep` into a destination can raise that file's count without creating a new override. | Permit documented transfers within a package, with source/destination accounting and a non-increasing aggregate. Ratchet the final destination budgets; unknown/new files default to zero unless covered by the reviewed transfer. |
| Class references do not prove the element's scope owner. | `AppCssElement.cs` uses `BuildRenderTree`. `CinematicHeroSurface` uses it for the stage; `HeroBackdrop` uses it for its root. Shared buttons and MudBlazor also render markup behind component parameters. | Inspect emitted HTML, generated scoped CSS, render fragments, and C# renderers. Keep justified contextual descendant rules; do not move product-specific classes wholesale into a generic primitive. |
| Global-to-isolated moves can change matching and precedence. | Adding an isolation attribute changes specificity. Popup/dialog DOM may render outside the caller's subtree. | Prove DOM ancestry and compiled selectors before relocation. Keep the smallest documented global bridge for proven portal cases. |
| V3 and V4 have already been partly resolved. | `DetailPage.razor:73` directly renders the fully qualified Details `OverviewTab`. `CinematicHeroSurface` renders `HeroBackdrop`; the latter authors media markup. | Use these actual call paths. V4 still needs special handling for the C#-rendered backdrop root and child controls. |
| The legacy audiobook branch is unreachable through the current production routes. | `ListenPage` declares only two playlist routes. `IsAudiobooksView` requires `/listen/audiobooks`, which is served elsewhere. No production embedding of `ListenPage` was found. | Retain that markup and its CSS in this maintenance delivery. Do not make branch removal a prerequisite for the Listen line limit. Any retirement belongs in a separately scoped change. |
| The proposed browser CLI is not provided by its reference harness. | Existing `capture.mjs` exports `captureState` and consumes documented CUA browser/tab objects; it does not launch a browser as a standalone Node command. | Separate CUA-driven capture from an offline Node comparison CLI. Use supported viewport screenshots and scroll positions; do not promise full-page export or hover automation without a documented capability. |
| The matrix asks every detail type to show content it may not have. | People have one continuous page; albums/audiobooks have specialized Overview/Details content. | Declare applicable states per entity. Capture actual audiobook tracks, rather than introducing a chapters module or nonexistent person tabs. |
| The editor acceptance empties the whole allowlist too early. | Listen remains oversized until U6 in the supplied sequence. | WP4 removes only editor exceptions; the final allowlist becomes empty in WP7 after WP6. |
| The documentation replacement could overstate completion. | The current Follow-up also mentions broader interop work, which this delivery does not audit fully. | Record CSS completion only after evidence exists; preserve remaining interop work and historical limitations. |

### What was verified during this planning review

The working tree was clean before this plan was added. Read-only inspection covered the supplied spec, repository guidance, CSS audit, previous cleanup report, capture helpers, affected component paths, and representative test references. The existing audit and its six unit tests passed using `.tmp/docs-venv/Scripts/python.exe`; the bundled Python initially lacked `tinycss2`, so the existing repository environment was reused without installing anything.

| Measure | Current observation | Qualification |
| --- | ---: | --- |
| First-party CSS files | 233 | Excludes `bin`, `obj`, and `vendor`; includes global first-party CSS. |
| Raw source bytes | 1,572,137 | Actual checked-out bytes; line endings can affect this figure. |
| Source lines | 47,846 | Fresh inventory and audit agree. |
| Parsed declarations | 33,190 | Audit total, with its four unsupported files reported. |
| `!important` declarations/markers | 1,894 | Audit and raw marker inventory agree. |
| Removable exact duplicate declarations | 0 | Fresh conservative audit. |
| Existing Release isolated bundle | 1,526,997 bytes | Existing output inspected only; no fresh build was run for planning. |
| Oversized isolated stylesheets | 3 | DetailPage 6,861; SharedMediaEditorShell 4,997; ListenPage 2,020 lines. |

The fresh raw editor `::deep` occurrence count is 202, versus 201 in the attachment. Occurrences, comments, selector counts, and parsed declarations must be named separately in the implementation inventory. Do not mix raw and parsed counts in a ratchet.

Static searches found no `.razor`, `.cs`, or `.js` references to `tvdb-show-order` or `tvdb-match-result`. This is supporting evidence, not sufficient retirement proof: WP4 must still inspect other source types, generated markup, and dynamic prefix construction before deleting them. Four parser-unsupported files remain the same: `LibraryConfigurableTable`, `StageGate`, `TuvimaArtworkStack`, and `BookDetailContent`.

## Technical design rules

1. **Record ownership before moving rules.** An inventory entry includes source selector and conditions, current winning declaration, emitted target element, owning markup/render fragment/renderer, consumer contexts, proposed destination, compiled selector, and verification states. Static class scanning supplies candidates, not conclusive ownership. C# property strings, class builders, caller-supplied classes, render fragments, pseudo-elements, and generated markup need explicit review.
2. **Preserve shared-child context.** Detail-only rules keep the existing `.tl-detail-page` ancestry or a proven equivalent. The existing hero hook is `cinematic-hero-surface--detail`, not an assumed `cinematic-hero--detail`. A hook substitution requires equivalent matching evidence. Shared Home/Details artwork remains owned by the same components.
3. **Check generated CSS and the actual DOM.** Blazor scopes HTML elements, and `::deep` relocates the scope constraint onto an ancestor; component tags themselves are not ordinary scoped HTML targets. See [Microsoft's CSS isolation documentation](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/css-isolation?view=aspnetcore-10.0). Equal selector specificity alone does not prove equal matching or source-order precedence. Global-to-isolated moves also require a specificity review.
4. **Retain legitimate boundaries.** A contextual `::deep` is acceptable for third-party internals or a reusable renderer whose markup must inherit the consumer's presentation. Each surviving group in a touched file gets a concise owner/reason comment. Portal rules stay global when no suitable scoped ancestor exists. Do not add wrappers, shared scope identifiers, broad globals, or priority markers just to bypass the line limit.
5. **Remove priorities with evidence.** Identify the competing declaration and prove that the ordinary declaration wins at every relevant breakpoint/state. Record removals by selector/property. Merge identical owner rules only after preserving condition, source order, fallbacks, and precedence. No new `!important` declaration is allowed; moving a retained declaration is a documented transfer.
6. **Keep removals scoped.** The current audit's `--prune` can write across all first-party CSS inputs. Before using it here, add an explicit reviewed file selection and prove out-of-selection files remain untouched. Review dry-run output first; unsupported files and unrelated surfaces remain unchanged.
7. **Budget destinations as well as sources.** WP1 estimates each resulting file's lines before extraction. Every isolated stylesheet, including new owners, must finish at no more than 2,000 lines. Extract real cohesive markup sections when necessary; neither comment stripping, formatting compression, imports, nor cosmetic CSS shards satisfy the intent.

### Boundaries that remain outside implementation scope

Preserve `wwwroot/vendor/**`, `wwwroot/css/epub-reader.css`, `wwwroot/tuvima.tokens.css` values, and all four parser-unsupported stylesheets. Preserve Engine, Storage, Contracts, provider behavior, routes, persistence, playback controller/host ownership, and unrelated interop. Playback CSS is regression-only; there is no planned playback-selector deletion. New editor sections consume explicit presentation values and callbacks; they do not receive a cascading shell instance, own mutable editor state, inject API services, or perform data access.

## Ordered work packages

Implement sequentially: **WP1 → WP2 → WP3 → WP4 → WP5 → WP6 → WP7**. This plan does not assign models or authorize subagent work. Package boundaries are review checkpoints, not automatic permission requests. Build the authorized package fully and correct ordinary regressions within scope; seek a scope decision only if completion would require changing the accepted experience or protected areas.

### WP1 — Establish ownership, feasibility, and reproducible evidence

**Files:** `scripts/css/audit.py`, `test_audit.py`, and `README.md`; new `scripts/visual-qa/css-ownership/` capture/comparison helpers, `states.json`, and README; `tests/MediaEngine.Web.Tests/StyleOwnershipGuardrailTests.cs` and `Fixtures/style-ownership-baseline.json`.

- Stop only running Engine/Web processes before development or fixture startup, identifying the actual process/executable/command line. Do not kill unrelated `dotnet` processes or all build nodes. Record HEAD, working-tree changes, resolved SDK (`global.json` allows feature roll-forward), build configuration, parser version, browser, viewport, and DPR.
- Rebuild the Release bundle. Record raw bytes and, separately, UTF-8/LF-normalized source bytes for reproducibility; name exclusions, count methods, unsupported parser coverage, and bundle path. Include global stylesheet bytes separately from isolated bundle bytes.
- Add `--ownership` and `--max-lines 2000`. Inventory all isolated stylesheets even if parsing is unsupported. Report every deep selector, its target classes, source-reference candidates, emitted owner evidence, consumer multiplicity, and confidence/status. Distinguish literal HTML ownership from component Class parameters and C# renderer candidates. Unresolved entries remain visible and cannot be auto-pruned.
- Add narrow file selection for pruning. Tests cover child/shared ownership, MudBlazor internals, dynamic classes, renderer/fragment uncertainty, selector-function parsing, unsupported files, line-limit failures, and unchanged excluded files.
- Add initial line exceptions for all three oversized files, at their current counts. Counts may only decrease. Per-file priority and deep budgets update through reviewed transfer entries containing source, destination, selectors/counts, and reason; aggregate budgets never rise. Guardrail tests fail if a remaining exception grows or a new stylesheet breaches the cap.
- Build an ownership/destination line budget and identify any component that needs a genuine section extraction. Estimate whether the bundle-reduction gate is feasible when Settings globals move into the isolated bundle. If constraints conflict, record a concrete target/scope amendment before starting the affected extraction.
- Implement CUA-driven capture helpers accepting existing documented browser/tab surfaces. An offline `compare.mjs` CLI reads saved JSON; capture is invoked in the supported CUA runtime, not plain `node capture.mjs`. Adapt to the current documented API rather than copying stale examples from prior reports.
- Capture deterministic baseline data through normal fixture setup/sign-in. Extend only disposable fixture tooling/data as needed for person, collection, gallery/editor, history, candidate, and Settings states; inspect existing coverage first. Keep authentication/runtime data inside ignored fixture roots.

**Exit:** complete ownership inventory and feasible destination budgets; all baseline states actually reachable and evidenced; guardrail/audit/comparator tests pass; comparing a set with itself passes and deliberately changed geometry or missing targets fails. No application source or styling changes in WP1.

### WP2 — Move detail hero rules to proven owners

**Files:** `DetailPage.razor.css`, `DetailHero`, `DetailHeroContent`, `HeroBackdrop`, `HeroActionRow`, `DetailPrimaryModule`, and applicable `HeroMetadataPills`/`OverflowActionMenu` styles; `CinematicHeroSurface.razor.css` only for its proven markup/context; affected tests.

Move hero/media/person/watch/metadata/copy/action/title rules by inventory entry, not broad class-family replacement. Leave page/stage/tab/panel container rules in DetailPage. Preserve original media-query conditions and contextual matching, including render-fragment content authored by DetailHero and C#-rendered backdrop/stage roots. Merge with existing owner rules and remove priorities only after proving the new winner. Keep Home/detail foreground geometry, detail `95svh`, primary/utility target sizes, and all responsive behavior.

**Exit:** all applicable Home and detail hero comparisons pass; generated selectors match intended elements; destination line budgets hold; aggregate priorities do not increase; retargeted tests retain their meaning. Lower the DetailPage exception to the actual new count.

### WP3 — Move sequences, credits, tracks, and related content

**Files:** DetailPage and proven owners such as `SequencePlacementPanel`, `AudioItemTable`, `AudiobookTracksContent`, Details `OverviewTab`, `MusicAlbumOverviewContent`, credit/character components, related chips/tabs, and child lists identified by WP1; affected tests.

Move ordered-array, track, credit, person/child/media, overview, related, and character styling into the actual emitting or contextual owner. Do not assume all `tl-series-*` or `tl-media-*` classes belong to one component. Keep all current call contexts for shared sequence panels. Preserve connector layering, numbering, current-item glow/`aria-current`, missing controls, borderless tracks, square people cards, and content/tab eligibility. Run the reviewed, file-scoped duplicate dry run after merging.

**Exit:** DetailPage and every destination stylesheet meet 2,000 lines; remove the DetailPage exception. Relevant structural/track/credit/detail tests and comparisons pass, without weakening value assertions.

### WP4 — Extract cohesive shared-editor sections

**Files:** shared editor markup/styles and narrowly necessary presentation partials; new `Components/MediaEditor/Sections/EditorDetailsSection`, `EditorArtworkSection`, `EditorMatchSection`, and `EditorHistorySection` with owned styles; explicit presentation types/callback inputs where needed; editor tests.

Extract markup and its styling together. The shell retains loading, state, dirty tracking, permissions, selection, all existing API/provider operations, and save/apply/cancel flow. Pass immutable/read-only presentation inputs where practical and explicit callbacks; avoid callbacks or DTOs that expose the entire shell indirectly. Preserve existing IDs, associations, keys, focus order, and element references. Review asynchronous callbacks and target changes for stale-state behavior. Leave other sections and header/nav chrome in place unless the measured ownership budget requires a genuine further extraction.

If needed, extract cohesive header/context chrome with explicit inputs and callbacks. Budget the four proposed sections too; if a section exceeds 2,000 CSS lines, split a real existing subsection with its markup rather than creating CSS-only shards. Keep Collection/Gallery editor styling separate unless a shared owner is genuinely established; shared class spelling alone does not establish shared scope.

Retire old `tvdb-*` selectors only after broader static/dynamic/renderer proof, an explicit per-file manifest, and scoped dry-run review. Absence from screenshots is not proof.

**Tests and exit:** render/callback checks cover representative targets, dirty Save enablement, save/cancel, unsaved-close guard, permission restrictions, target selection, and retail preview/apply using existing fixtures/spies. Capture Details, Artwork/lightbox, Match candidates/episode-only TV matching, History, and Collection/Gallery editors. Shell and new section CSS meet the cap; remove only editor exceptions at this point.

### WP5 — Give Settings and switches clear styling owners

**Files:** `wwwroot/app.css`, `Settings.razor.css`, emitting Settings tab styles, `AppSwitchRow.razor.css`, `LibrariesTab`/`NetworkRemoteAccessSettings` styles, and relevant tests. Add a presentation parameter only for a proven existing variant that cannot be preserved through current context.

Classify each Settings global rule by actual emitted target, including shared form controls and popup roots. Move genuine Settings-only rules to their owner while keeping the existing ancestor context. Preserve necessary global portal bridges with ownership/reason comments; do not force them into isolation. Centralize switch-row HTML and scoped MudSwitch internals in `AppSwitchRow`, retaining any existing contextual differences instead of homogenizing screens. Use the full consumer list: Settings alone is insufficient; View folders, Universe cast comparison, and the design preview also use the switch.

**Exit:** baseline parity for admin/ordinary-profile navigation, libraries, network, switch-heavy Settings/review states, and representative non-Settings consumers; unchecked/checked/disabled/focus/label variants pass. Account for priorities transferred from globals. A 50% reduction in the combined Settings priorities remains a reported aspiration, not a reason to alter presentation.

### WP6 — Finish Listen ownership without removing dormant markup

**Files:** `ListenPage.razor.css`, `ListenNavigationSection.razor.css`, and affected tests; additional styles only for already proven owners.

Separate rail rules targeting `ListenNavigationSection`'s HTML from identical class names also authored inline by ListenPage. Keep shell/inline playlist rules with the page; preserve contextual rules for shared controls. Move the child-owned rules and safely merge duplicates. Retain the dormant audiobook branch and its styling. Verify that this ownership move alone reaches the line cap; if it does not, report the specific remaining owner/section proposal instead of deleting markup or compressing CSS.

**Exit:** ListenPage and destinations meet the cap; remove its exception. Playlist detail, rail, focus/navigation, editing/reordering, and downstream player regression states pass. No queue, dock, player, route, or audiobook behavior changes.

### WP7 — Final measurements, documentation, and acceptance

**Files:** implementation report, measurements JSON, selected sanitized before/after images; design-system ownership standard, Dashboard architecture, audit/capture READMEs; synchronized AGENTS/CLAUDE and relevant `.agent` guidance, including `features/LIBRARY-DASHBOARD.md`, `skills/DASHBOARD-UI.md`, and `SYNC-MAP.md`; final guardrail budgets. Update documentation navigation/exclusions if strict build requires it.

Rebuild and recapture from the same fixture snapshot/configuration, SDK, and encoding method. Report source/bundle bytes, declarations, priorities, deep rules, unresolved ownership, retained portal/context exceptions, per-file lines, and all approved comparison tolerances. Empty the line exception list and finalize transfer-aware per-file budgets. Document rules as implemented facts only after verification. Preserve still-open interop work when updating the existing Follow-up paragraph.

**Final required gates:** all isolated CSS at most 2,000 lines; no added priorities; final source priority count and same-configuration isolated bundle bytes both strictly below WP1; all mandatory states covered with no unexplained visible/computed differences; tests and documentation checks pass. The **900,000-byte bundle**, **60% source-priority reduction**, and **50% Settings-priority reduction** are reported stretch goals. If the strict bundle gate proves incompatible with ownership/fidelity, obtain a concrete scope/acceptance amendment; never claim it passed or sacrifice the experience to reach it.

## Visual and behavioral verification contract

Capture every applicable state at **1920×1080** and **390×844**, plus representative detail heroes at **1920×900**. Add **320×568** and the player's existing **420×780** presentation where global changes touch those controls. Record actual viewport and DPR; emulate at least one higher-density geometry where the documented browser capability supports it, otherwise state that coverage gap.

| Surface | Required applicable states |
| --- | --- |
| Home | Fixed hero identity, Continue contents, discovery rest/focus; hover when the documented tool supports it; rotation/pause smoke check. |
| Media detail | Landscape and artwork-fallback movie; book/comic covers; untouched and progressed owned episode/show; album and audiobook heroes; Overview and Details where available. |
| Structural/person detail | Ordered series with missing/current item, standard collection cluster, person continuous page with credits/owned works; Related only where actually available. |
| Tracks and credits | Album tracks/credits/More by; audiobook source-authored tracks; Show missing on/off; representative credit cards/linked identities. |
| Editor | Details clean/dirty, Artwork/picker/lightbox, Match candidates/preview, episode TV matching, History, restricted target, Collection/Gallery navigation; representative loading/error/empty states. |
| Settings/shared controls | Admin and ordinary-profile overview; libraries; network; switch-heavy tab; review queue; View folder and Universe switch consumers; popup/select open and disabled/focus states. |
| Listen/player | Playlist detail and rail, editing/reordering, desktop lyrics, phone artwork/lyrics, popout presentation when available. |

State entries specify actual fixture identity/route, normal UI setup actions, viewport/scroll position, applicable controls, mandatory selectors/expected element counts, and review criteria. A missing selector is a failure, not an empty passing capture. Do not require a person Details tab or a chapters module the product does not expose.

Use identical fixture data and reset controlled progress/preferences between runs; match hero slide, selected tab, editor target, and player time. Wait for fonts and visible artwork to settle using documented state checks. Stabilize animation/rotation through existing UI/state mechanisms; do not alter production CSS to make evidence match. Capture viewport JPEGs and additional documented scroll positions for lower content and modals; include pseudo-element styles and focus/active/checked/disabled states where rules target them.

Compare bounding boxes, typography, colors/backgrounds, borders/shadows, padding/margin/gap, grid/flex, sizing/object-fit, position/stacking, opacity/transform, and overflow. Keep ignore/tolerance entries narrow: exact state, selector/element, property, numerical bound or transient reason, and visual review result. A known time-dependent seek width does not permit ignoring all progress widths. Avoid a blanket font-width tolerance after fonts have settled. Image review remains required alongside computed-style checks, including tolerated differences. Unsupported hover/full-page/native-device capabilities are explicitly unverified.

Keep runtime JSON/full capture sets under ignored `.tmp/css-ownership/` or marked fixture outputs. Commit only selected sanitized review evidence and aggregate reports; omit credentials, raw DOM/private text dumps, signed grants, and fixture databases. Snapshot/restore only disposable QA state; do not alter configured user media/catalogues for this task.

## Test and measurement schedule

Per package, run the affected Dashboard/bUnit and CSS guardrail tests, audit/comparator tests when tooling changes, a build sufficient to regenerate isolation, the package's baseline comparisons, and `git diff --check`. Broaden only for new failures or concerns. Do not run the full solution/docs matrix after every small CSS move.

At final integration:

```powershell
dotnet restore MediaEngine.slnx
dotnet build MediaEngine.slnx --no-restore
dotnet test MediaEngine.slnx --no-build
dotnet build MediaEngine.slnx -c Release --no-restore
node --test tests/MediaEngine.Web.Tests/JavaScript
python -m unittest discover -s scripts/css -p test_audit.py
python scripts/css/audit.py --ownership --max-lines 2000 --output .tmp/css-ownership/final-audit.json
node scripts/visual-qa/css-ownership/compare.mjs --before .tmp/css-ownership/before --after .tmp/css-ownership/after
python -m mkdocs build --strict
git diff --check
```

Use the actual established Node/Python runtimes, not an assumed PATH command. The ownership/line/comparison commands were planned here and are now implemented. Capture the full matrix through documented CUA calls before running the comparator. Record baseline warnings/skips and new failures accurately; existing limitations cannot become silently passing checks. No fresh solution build/test or browser verification was performed for this planning-only change.

## Risks and scope decisions

The principal risks are altered Blazor matching/source order, portals losing styling, shared controls changing unrelated screens, oversized destination files, and editor extraction disrupting callbacks/focus. Ownership evidence, destination budgets, transfer accounting, deterministic fixtures, and package-local comparisons address these risks before final integration.

Investigate uncertainty with markup, compiled selectors, tests, and runtime DOM first. Keep an unresolved rule in its current home while completing independent work. Escalate with the exact conflicting rule/state and a reviewable option only when the accepted appearance, protected implementation boundary, or final gate cannot be preserved. Dormant branch retirement and broader interop work remain separate; neither should stop this delivery merely because they exist. The original planning request performed no implementation. Subsequent authorization covers implementation; commits, pushes and deployment remain outside this work.

## Plain-English completion summary

The maintenance plan has now been implemented with clearer styling owners and enforced size limits for individual component stylesheets. The Dashboard keeps its established experience. Final acceptance remains pending because the generated CSS bundle increased; actual validation and the proposed acceptance decision are recorded in the implementation report.
