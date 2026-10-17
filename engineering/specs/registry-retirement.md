# Spec: Registry retirement — Needs Review is the home for exceptions

> **Status:** Draft (as-built) · **Version:** 4 · **Last updated:** 2026-10-17 · **Rebuilt from code on 2026-10-17** · **Related docs:** [Product overview](../product/overview.md) · [Item Detail](item-detail.md) · [ADR 0006 — management workspace retired](../architecture/decisions/0006-management-workspace-retired.md) · [Resolving reviews guide (public)](../../docs/guides/resolving-reviews.md)

> This replaces the March 2026 "Registry spec v3". The reference copy was not available, so this version is rebuilt from the code. Per Shy (2026-10-17), "Registry" is to be **retired entirely** so it no longer exists in the app, the docs or Claude's context. Sections 1–5 describe what exists today; section 6 is the removal plan; section 8 lists gaps. Not Locked.

## 1. Goal

There is one, clearly named place to deal with library items the Engine isn't sure about — **Needs Review** — and no leftover "Registry" screen, tab, wording or dead Engine actions that confuse people or future work.

## 2. Scope

**In scope:** the remnants of the spring-2026 Registry (a single all-in-one screen to see and fix every item): the diagnostic "Registry" tab on detail pages, the leftover item-state names and Engine actions nothing uses, stale docs and labels; plus describing the current Needs Review flow so the retirement doesn't lose anything.

**Out of scope:** redesigning Needs Review, bringing back bulk editing, the shared editor's own layout. Those would be separate specs.

## 3. Decisions made

- **D1.** (2026-04-14 → 04-25) The all-in-one Registry screen was retired. Its jobs moved: fixing one item → the shared editor; exceptions → Needs Review; item history → the editor's History section. Recorded in [ADR 0006](../architecture/decisions/0006-management-workspace-retired.md).
- **D2.** (2026-10-17, Shy) Retire the name and every remnant. New docs call the exceptions place **Needs Review**.
- **D3.** Bulk editing stays switched off until a spec brings it back; the editor launcher refuses multi-item launches today and a guardrail test enforces it.

## 4. UI behaviour (as built today)

### Where exceptions live: Settings → Recently Added → Needs Review

- **Who:** administrators only. Kids profiles never see it.
- **How people get there:** the account menu shows **Needs Review** (with a count) when the session has administration rights; it opens Recently Added with the Needs Review section expanded. Old addresses (`/settings/review`, `/operations/needs-review`, other `/operations/*` pages) redirect here.
- **What it shows:** a pinned **Needs Review** list (every pending item, oldest first, no paging), then **Library activity** history (25 per page). Filters: search, media type, status (All / Added / Needs Review), date range, sort.
- **Row actions:** **Review** (opens the shared editor in review mode), **Find in recent history**, **Keep provider match**, **Dismiss**. Films that look like TV episodes get a "Move to TV" suggestion card. History rows offer Edit (not on phones), Open details, View ingestion run.

### What sends an item to Needs Review

- A background step for that item ends **blocked**, **missing**, or **failed for good**.
- Ingestion is unsure: identity confidence below 0.40 (unidentifiable) or below the auto-link threshold (low confidence); a placeholder title; the item sits in an "Other" folder; the media type is uncertain.
- Organising files hits a problem: name collision, a merge, or a failed write-back.
- An item is only queued once; a second problem doesn't add a duplicate.
- 20 reason codes exist; people see plain labels such as "Low confidence match".

### What each action does

| Action | Result |
|---|---|
| Resolve (save in the review editor) | Saves the person's locked values and chosen identity, re-runs matching without re-queuing, marks the item resolved, logs it |
| Dismiss | Marks it dismissed; metadata unchanged |
| Keep provider match | Marks the item as "not part of a universe" and dismisses |
| Move to TV | Files a film as Season 0 Episode 1 of the matching show (the file on disk doesn't move) |
| Keep as film | Same as Dismiss |

### The leftover "Registry" tab on detail pages

A read-only list of technical facts: entity ID, type, presentation context, artwork mode and source, raw identity status. No actions. It appears only when the page address includes `?context=admin`; nothing in the app links there, and there is no role check.

### States

| Situation | What the person sees |
|---|---|
| Normal | Needs Review list, then activity history |
| Empty | "No items need review"; "No review items match the search" when filtered |
| Loading | A progress bar |
| Error | History shows a warning; **the review list wrongly shows "No items need review"** when it fails to load (G2) |
| Action fails | Dismiss and Keep provider match fail silently (G3) |

### Settings that change what's flagged

Auto-link 0.85, conflict 0.6 (`scoring`); auto-review confidence 0.60, retail auto-accept 0.90, retail ambiguous 0.65, disambiguation 0.7, post-hydration organise 0.70 (`hydration`); media-type auto-assign 0.70, review 0.40 (`disambiguation`); rejected items kept 30 days (default). The 0.40 "unidentifiable" cut-off is fixed in code.

## 5. Data / model changes

Today: the review list is stored in the review queue (status Pending / Resolved / Dismissed, with reason, confidence and candidates). Each work also carries a lifecycle state left over from the Registry (Identified / In review / Provisional / Rejected) that is calculated on the fly; the named constants for it are unused.

Retirement removes unused names and actions only. No stored data is migrated; if a column is dropped, pre-release libraries may need a reset (acceptable).

## 6. Work packets (removal plan — pending Shy's answers in section 7)

### WP1 — Remove the Registry tab and its address switch
- **What it does:** delete the Details "Registry" tab and the `?context=admin` presentation path; drop its identity badge. If Shy chooses (Q1), move the useful diagnostics into the editor's Match & Identity section, visible to administrators only.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/RegistryTab.razor`, `DetailPage.razor:149-151`, `DetailPage.razor.css`, `HeroMetadataPills.razor:83-86`, `src/MediaEngine.Api/Services/Details/Internals/DetailCompositionOrchestrator.ViewModelBuilder.cs:879-882`, `…Core.cs:99`, `…DetailPresentationPolicy.cs:19-22`, `src/MediaEngine.Contracts/Details/DetailDtos.cs` (`IsAdminView`, `DetailTab.IsAdminOnly` if unused), `tests/MediaEngine.Web.Tests/UnifiedDetailComponentTests.cs:298`, Contracts wire snapshots
- **Depends on:** Q1 · **Size:** Complex (wire contract change)
- **Done when:** no detail page can render a Registry tab; Contracts snapshot updated deliberately; Web and Contracts tests pass.

### WP2 — Remove unused item-lifecycle names
- **What it does:** delete the unused `LibraryItemLifecycleState` constants and fix stale comments (state-count summary wording, 14-vs-30-day purge comment).
- **Files likely touched:** `src/MediaEngine.Domain/Constants/LibraryItemLifecycleState.cs`, `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:97`, `src/MediaEngine.Storage/LibraryItemRepository.cs:1550-1560`
- **Depends on:** none · **Size:** Mechanical
- **Done when:** no reference to the constants remains; build and Domain/Storage tests pass.

### WP3 — Remove Engine actions with no screen
- **What it does:** for each Library Item action no screen uses — list, counts, state-counts, type-counts, batch approve, recover, provisional, apply-match, create-manual, reject — first check native clients (`clients/`) and app permissions; remove the unused ones with their Dashboard client wrappers (`UIOrchestratorService`), and fix the `missingUniverseOnly` filter mismatch for any that stay.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs`, `src/MediaEngine.Web/Services/Integration/EngineApiClient.Operations.cs:612`, `UIOrchestratorService.cs:1090,1120`, `SharedMediaEditorShell.razor.cs:7383`, `src/MediaEngine.Contracts/Items/LibraryItemContracts.cs`, `docs/reference/api-endpoints.md`
- **Depends on:** WP2; Q2 · **Size:** Complex (Engine actions + wire contracts)
- **Done when:** every remaining action has a caller; route-authorization and Contracts guardrails pass; `api-endpoints.md` matches.

### WP4 — Remove unused components
- **What it does:** delete `SettingsReviewQueueTab.razor` (no longer shown; Needs Review replaced it), `SharedMediaBatchConfirmDialog.razor`, `LibraryItems/ReportProblemDialog.razor` (not placed anywhere), and tests that render them directly.
- **Files likely touched:** those components under `src/MediaEngine.Web/Components/`, `tests/MediaEngine.Web.Tests/SettingsNavTests.cs` and related tests
- **Depends on:** Q3 (report-a-problem) · **Size:** Standard
- **Done when:** build and Web tests pass; no route or menu references them.

### WP5 — Make Needs Review honest about failures
- **What it does:** show an error (with retry) when the review list fails to load instead of "No items need review"; show a toast when Dismiss or Keep provider match fails; make the "Added" status filter work (or remove it).
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/RecentlyAddedPageContent.razor:30-36,53-56,250-251,315-320,359-363`, `src/MediaEngine.Web/Services/Integration/EngineApiClient.cs:318-335`
- **Depends on:** none · **Size:** Standard
- **Done when:** component tests cover load failure, action failure and each status filter.

### WP6 — One set of review categories; no impossible ones
- **What it does:** make Live Ingestion and Needs Review use the same reason categories; remove the "Possible duplicates" bucket and unused triggers (UserReport, ArtworkUnconfirmed, ArbiterNeedsReview) unless Q3 keeps one.
- **Files likely touched:** `src/MediaEngine.Api/Services/IngestionOperationsStatusService.cs:3461-3480`, `src/MediaEngine.Web/Services/Editing/ReviewIssueClassifier.cs:28-56`, `src/MediaEngine.Domain/Constants/ReviewTrigger.cs`
- **Depends on:** Q3 · **Size:** Standard
- **Done when:** a test asserts both surfaces map every trigger to the same category.

### WP7 — Guardrail and docs
- **What it does:** add "Registry" as a retired product label to the retired-vocabulary guardrail (allowing unrelated technical names such as `PermissionRegistry`, `CapabilityRegistry`); update docs to say Needs Review: `docs/guides/resolving-reviews.md` (wrong menu path, promises "Retry a failed step"), `docs/architecture/architecture-summary.md:176,186`.
- **Files likely touched:** `tests/MediaEngine.Web.Tests/UiCompositionGuardrailTests.cs`, the docs above, `docs/reference/glossary.md`
- **Depends on:** WP1, WP4 · **Size:** Standard
- **Done when:** guardrail test fails on a planted "Registry" label and passes on the cleaned tree; docs build passes.

## 7. Risks and open questions

- **Risks:** WP1 and WP3 change Engine↔Dashboard contracts — native apps or third-party apps with review permissions might call a removed action. WP3 must check `clients/` and app permission presets first.
- **Q1.** The Registry tab's diagnostics (IDs, artwork source, identity status): **drop them**, or **move them** into the editor's Match & Identity section for administrators? Recommendation: move the identity status and artwork source (useful when fixing a match); drop the rest.
- **Q2.** Should any unused Engine action survive for future bulk editing (for example batch approve)? Recommendation: remove all; a future bulk-editing spec can add what it needs.
- **Q3.** "Report a problem": remove it, or finish it so reports land in Needs Review? Recommendation: remove now; propose as its own spec if wanted.
- **Q4.** "Retry a failed step" is promised by the user guide but only "Retry write-back" exists. Add a retry action to Needs Review, or fix the guide? Recommendation: fix the guide now; retry is a separate spec.

## 8. Gaps and drift

No reference copy of the March 2026 Registry spec was found, so this lists what looks half-built, inconsistent or unclear in today's code.

- **G1.** The Registry tab is unprotected: shown by address flag, not role; uses developer field names.
- **G2.** A failed review-list load shows "No items need review".
- **G3.** Dismiss and Keep provider match fail silently.
- **G4.** The "Added" status filter does nothing.
- **G5.** Engine actions with no screen (list, counts, batch approve, recover, provisional, apply-match, create-manual, reject); one has no Dashboard client at all.
- **G6.** The Dashboard sends a `missingUniverseOnly` filter the Engine doesn't accept.
- **G7.** Stale descriptions: state-count summary names, a 14-day purge comment (default is 30).
- **G8.** "Possible duplicates" can never match; Live Ingestion hard-codes duplicates to 0.
- **G9.** Three review reasons are defined but never raised; user reports only reach the activity log.
- **G10.** Live Ingestion and Needs Review group reasons differently.
- **G11.** Unused components: the old review-queue tab, the batch confirm dialog, the report-problem dialog.
- **G12.** Stale docs: the resolving-reviews guide, the architecture summary, and (until 2026-10-17) CLAUDE.md all named the old Review Queue page.
- **G13.** Whether household administrators (not server administrators) can resolve reviews is unclear.
