# Spec: Registry retirement — Needs Review is the home for exceptions

> **Status:** Built · **Version:** 5 · **Last updated:** 2026-10-17 · **Related docs:** [Product overview](../../product/overview.md) · [Item Detail](item-detail.md) · [ADR 0006 — management workspace retired](../../architecture/decisions/0006-management-workspace-retired.md) · [Resolving reviews guide (public)](../../../docs/guides/resolving-reviews.md)

> Replaces the March 2026 "Registry spec v3". Version 5 records Shy's answers to all four open questions and all thirteen gaps (2026-10-17). Per Shy, "Registry" is **retired entirely** so it no longer exists in the app, the docs or Claude's context. Sections 4 and 5 describe what exists today; section 6 is the removal plan.

> **Build progress (2026-10-17):** WP2–WP7 and the WP8 docs are built in the Registry retirement proposed change (#245). WP1 and the WP8 retired-vocabulary guardrail ship with the Item Detail proposed change (Item Detail WP10); this spec moves to Built once both are on main.

## 1. Goal

There is one, clearly named place to deal with library items the Engine isn't sure about — **Needs Review** — and no leftover "Registry" screen, tab, wording or dead Engine actions that confuse people or future work. Needs Review also tells the truth when something fails.

## 2. Scope

**In scope:** the remnants of the spring-2026 Registry (a single all-in-one screen to see and fix every item): the diagnostic "Registry" tab on detail pages, leftover item-state names and Engine actions nothing uses, unused components, stale docs and labels; making Needs Review report failures honestly; one shared set of review categories; restricting Needs Review to server administrators.

**Out of scope:** redesigning Needs Review, bringing back bulk editing, a general "retry a failed step" action, a "Report a problem" feature, the shared editor's own layout. Each would be a separate spec.

## 3. Decisions made

- **D1.** (2026-04-14 → 04-25) The all-in-one Registry screen was retired. Fixing one item → the shared editor; exceptions → Needs Review; item history → the editor's History section. Recorded in [ADR 0006](../../architecture/decisions/0006-management-workspace-retired.md).
- **D2.** (2026-10-17, Shy) Retire the name and every remnant. New docs call the exceptions place **Needs Review**.
- **D3.** Bulk editing stays switched off until a spec brings it back; the editor launcher refuses multi-item launches and a guardrail test enforces it.
- **D4 (Q1).** (2026-10-17, Shy) The Registry tab's diagnostics move: **identity status and artwork source** go into the editor's Match & Identity section, shown to server administrators only. Everything else on the tab is dropped.
- **D5 (Q2).** (2026-10-17, Shy) Remove **all** Library Item Engine actions that no screen uses. A future bulk-editing spec adds what it needs.
- **D6 (Q3).** (2026-10-17, Shy) Remove "Report a problem" now. If wanted later, it is its own spec where reports land in Needs Review.
- **D7 (Q4).** (2026-10-17, Shy) Fix the user guide to match what exists ("Retry write-back" only). A general retry action is a separate spec.
- **D8 (G1, G5, G6, G7, G11, G12).** Change code / docs as in the work packets. G6 (the unaccepted `missingUniverseOnly` filter) disappears with the actions it belongs to; if any action survives, the mismatch is fixed.
- **D9 (G2, G3).** Change code: a failed review-list load shows an error with Retry; a failed Dismiss or Keep provider match shows a short message.
- **D10 (G4).** Change code: the "Added" status filter is made to work (not removed).
- **D11 (G8, G9, G10).** Change code: remove the impossible "Possible duplicates" bucket and the three never-raised review reasons (UserReport, ArtworkUnconfirmed, ArbiterNeedsReview); Live Ingestion and Needs Review use one shared set of categories. User reports stay in the activity log only.
- **D12 (G13).** (2026-10-17, Shy) **Only server administrators can see and resolve reviews.** Household administrators manage their own household's users and what those users can see; they cannot resolve reviews, and do not see the Needs Review count or page.
- **D13.** (2026-10-17, Shy) **Approved apps keep Needs Review access.** An app a server administrator has granted review permission may still list and resolve reviews; D12 limits people, not those apps.
- **D14.** (2026-10-17, Shy) **WP1 is built inside Item Detail WP10**, on the Item Detail work, right after its WP2. This spec's WP8 waits for it.
- **D15.** (2026-10-17, Shy) **An app acting for a signed-in person needs that person to be a server administrator** to list or resolve reviews (Option A); server-integration and automation apps keep access under D13. This is an Engine-side check only: native apps stay consumption-only and get no review or admin screens.
- **D16.** (2026-10-17, Shy) **Remove song delete now.** WP3 also removes the batch delete Engine action and the Listen page's Delete song button, because that delete removed files even from folders Tuvima may only read, breaking the rule that original source media stay protected. Music has no Delete until the Delete from library spec is built.

## 4. UI behaviour (as built today, before this spec's changes)

### Where exceptions live: Settings → Recently Added → Needs Review

- **Who:** server administrators only (made explicit by D12). Household administrators and Kids profiles never see it.
- **How people get there:** the account menu shows **Needs Review** (with a count) to a server administrator; it opens Recently Added with the Needs Review section expanded. Old addresses (`/settings/review`, `/operations/needs-review`, other `/operations/*` pages) redirect here.
- **What it shows:** a pinned **Needs Review** list (every pending item, oldest first, no paging), then **Library activity** history (25 per page). Filters: search, media type, status (All / Added / Needs Review), date range, sort.
- **Row actions:** **Review** (opens the shared editor in review mode), **Find in recent history**, **Keep provider match**, **Dismiss**. Films that look like TV episodes get a "Move to TV" suggestion card. History rows offer Edit (not on phones), Open details, View ingestion run.

### What sends an item to Needs Review

- A background step for that item ends **blocked**, **missing**, or **failed for good**.
- Ingestion is unsure: identity confidence below 0.40 (unidentifiable) or below the auto-link threshold (low confidence); a placeholder title; the item sits in an "Other" folder; the media type is uncertain.
- Organising files hits a problem: name collision, a merge, or a failed write-back.
- An item is only queued once; a second problem doesn't add a duplicate.
- 20 reason codes exist today (17 after this spec); people see plain labels such as "Low confidence match".

### What each action does

| Action | Result |
|---|---|
| Resolve (save in the review editor) | Saves the person's locked values and chosen identity, re-runs matching without re-queuing, marks the item resolved, logs it |
| Dismiss | Marks it dismissed; metadata unchanged |
| Keep provider match | Marks the item as "not part of a universe" and dismisses |
| Move to TV | Files a film as Season 0 Episode 1 of the matching show (the file on disk doesn't move) |
| Keep as film | Same as Dismiss |

### The leftover "Registry" tab (removed by this spec)

A read-only list of technical facts on detail pages, visible only when the address includes `?context=admin`; nothing links there and there is no role check. After this spec it does not exist; identity status and artwork source appear in the editor's Match & Identity section for server administrators.

### States (after this spec)

| Situation | What the person sees |
|---|---|
| Normal | Needs Review list, then activity history |
| Empty | "No items need review"; "No review items match the search" when filtered |
| Loading | A progress bar |
| Review list fails to load | An error message with a Retry button (never "No items need review") |
| History fails to load | A warning (as today) |
| Dismiss or Keep provider match fails | A short message saying it didn't work; the item stays in the list |
| Status filter | All / Added / Needs Review each change the history list |
| Household administrator or Kids profile | No Needs Review menu entry or page |

Phone, tablet, desktop and TV: unchanged. Editing remains desktop-only; the editor's Match & Identity diagnostics follow the same rule.

### Settings that change what's flagged

Auto-link 0.85, conflict 0.6 (`scoring`); auto-review confidence 0.60, retail auto-accept 0.90, retail ambiguous 0.65, disambiguation 0.7, post-hydration organise 0.70 (`hydration`); media-type auto-assign 0.70, review 0.40 (`disambiguation`); rejected items kept 30 days (default). The 0.40 "unidentifiable" cut-off is fixed in code.

## 5. Data / model changes

The review list is stored in the review queue (status Pending / Resolved / Dismissed, with reason, confidence and candidates). Each work also carries a lifecycle state left over from the Registry (Identified / In review / Provisional / Rejected) that is calculated on the fly; the named constants are unused.

This spec removes unused names, actions and review reasons only. No stored data is migrated; if a column or reason value is dropped, pre-release libraries may need a reset (acceptable).

## 6. Work packets

### WP1 — Remove the Registry tab; move two diagnostics to the editor
- **What it does:** delete the Details "Registry" tab and the `?context=admin` presentation path; drop its identity badge; show identity status and artwork source in the editor's Match & Identity section for server administrators only (D4).
- **Files likely touched (to be confirmed by Claude Code):** `src/MediaEngine.Web/Components/Details/RegistryTab.razor`, `DetailPage.razor:149-151`, `DetailPage.razor.css`, `HeroMetadataPills.razor:83-86`, `src/MediaEngine.Api/Services/Details/Internals/DetailCompositionOrchestrator.ViewModelBuilder.cs:879-882`, `…Core.cs:99`, `…DetailPresentationPolicy.cs:19-22`, `src/MediaEngine.Contracts/Details/DetailDtos.cs` (`IsAdminView`, `DetailTab.IsAdminOnly` if unused), the editor's Match & Identity section, `tests/MediaEngine.Web.Tests/UnifiedDetailComponentTests.cs:298`, Contracts wire snapshots
- **Depends on:** none · **Size:** Complex (wire contract change)
- **Done when:** no detail page can render a Registry tab; a server administrator sees identity status and artwork source in the editor's Match & Identity section and a household administrator does not; Contracts snapshot updated deliberately; Web and Contracts tests pass.

### WP2 — Remove unused item-lifecycle names
- **What it does:** delete the unused `LibraryItemLifecycleState` constants and fix stale comments (state-count summary wording, 14-vs-30-day purge comment).
- **Files likely touched (to be confirmed by Claude Code):** `src/MediaEngine.Domain/Constants/LibraryItemLifecycleState.cs`, `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:97`, `src/MediaEngine.Storage/LibraryItemRepository.cs:1550-1560`
- **Depends on:** none · **Size:** Mechanical
- **Done when:** no reference to the constants remains; build and Domain/Storage tests pass.

### WP3 — Remove Engine actions with no screen
- **What it does:** remove every Library Item action no screen uses (list, counts, state-counts, type-counts, batch approve, recover, provisional, apply-match, create-manual, reject) with their Dashboard client wrappers. First check native clients (`clients/`) and app permission presets for callers and report any found before removing (D5).
- **Files likely touched (to be confirmed by Claude Code):** `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs`, `src/MediaEngine.Web/Services/Integration/EngineApiClient.Operations.cs:612`, `UIOrchestratorService.cs:1090,1120`, `SharedMediaEditorShell.razor.cs:7383`, `src/MediaEngine.Contracts/Items/LibraryItemContracts.cs`, `docs/reference/api-endpoints.md`
- **Depends on:** WP2 · **Size:** Complex (Engine actions + wire contracts)
- **Done when:** every remaining action has a caller; route-authorization and Contracts guardrails pass; `api-endpoints.md` matches; the `missingUniverseOnly` mismatch no longer exists.

### WP4 — Remove unused components
- **What it does:** delete `SettingsReviewQueueTab.razor`, `SharedMediaBatchConfirmDialog.razor`, `LibraryItems/ReportProblemDialog.razor` and the tests that render them directly (D6).
- **Files likely touched (to be confirmed by Claude Code):** those components under `src/MediaEngine.Web/Components/`, `tests/MediaEngine.Web.Tests/SettingsNavTests.cs` and related tests
- **Depends on:** none · **Size:** Standard
- **Done when:** build and Web tests pass; no route or menu references them.

### WP5 — Make Needs Review honest about failures
- **What it does:** show an error with Retry when the review list fails to load; show a message when Dismiss or Keep provider match fails; make the "Added" status filter work (D9, D10).
- **Files likely touched (to be confirmed by Claude Code):** `src/MediaEngine.Web/Components/Pages/RecentlyAddedPageContent.razor:30-36,53-56,250-251,315-320,359-363`, `src/MediaEngine.Web/Services/Integration/EngineApiClient.cs:318-335`
- **Depends on:** none · **Size:** Standard
- **Done when:** component tests cover load failure, action failure and each status filter.

### WP6 — One set of review categories; no impossible ones
- **What it does:** make Live Ingestion and Needs Review use the same reason categories; remove the "Possible duplicates" bucket and the unused triggers UserReport, ArtworkUnconfirmed, ArbiterNeedsReview (D11).
- **Files likely touched (to be confirmed by Claude Code):** `src/MediaEngine.Api/Services/IngestionOperationsStatusService.cs:3461-3480`, `src/MediaEngine.Web/Services/Editing/ReviewIssueClassifier.cs:28-56`, `src/MediaEngine.Domain/Constants/ReviewTrigger.cs`
- **Depends on:** none · **Size:** Standard
- **Done when:** a test asserts both surfaces map every remaining trigger to the same category.

### WP7 — Server administrators only
- **What it does:** make sure the Needs Review menu entry and count, the Recently Added review section, and every Engine action that lists, resolves, dismisses or keeps a review require a server administrator; household administrators and Kids profiles get nothing and are refused by the Engine, not just hidden by the Dashboard (D12). Report anything found that is looser today.
- **Files likely touched (to be confirmed by Claude Code):** the account menu, `RecentlyAddedPageContent.razor`, the review Engine actions and their permission checks, route-authorization tests
- **Depends on:** none · **Size:** Standard
- **Done when:** tests show a household administrator and a Kids profile are refused by the Engine and see no Needs Review entry; a server administrator is unaffected.

### WP8 — Guardrail and docs
- **What it does:** add "Registry" as a retired product label to the retired-vocabulary guardrail (allowing unrelated technical names such as `PermissionRegistry`, `CapabilityRegistry`); update docs to say Needs Review: `docs/guides/resolving-reviews.md` (correct the menu path; replace the "Retry a failed step" promise with what exists, "Retry write-back"; say only server administrators can resolve reviews), `docs/architecture/architecture-summary.md:176,186`, `docs/reference/glossary.md`.
- **Files likely touched (to be confirmed by Claude Code):** `tests/MediaEngine.Web.Tests/UiCompositionGuardrailTests.cs`, the docs above
- **Depends on:** WP1, WP4, WP7 · **Size:** Standard
- **Done when:** the guardrail test fails on a planted "Registry" label and passes on the cleaned tree; the docs build passes.

## 7. Risks and open questions

- **Risks:** WP1 and WP3 change Engine↔Dashboard contracts. Native apps or third-party apps with review permissions might call a removed action; WP3 must check `clients/` and app permission presets first and stop and report if it finds a caller. Removing review reasons (WP6) may need a reset of pre-release libraries (acceptable).
- **Open questions:** none.

## 8. Gaps and drift

Every item below has Shy's decision (2026-10-17).

| Gap | Decision | Where |
|---|---|---|
| G1 Registry tab unprotected | Change code: removed | WP1 |
| G2 Failed list load shows "No items need review" | Change code | WP5 |
| G3 Dismiss / Keep provider match fail silently | Change code | WP5 |
| G4 "Added" filter does nothing | Change code: make it work | WP5 |
| G5 Engine actions with no screen | Change code: remove all | WP3 |
| G6 `missingUniverseOnly` mismatch | Drop: disappears with G5 | WP3 |
| G7 Stale descriptions | Change code | WP2 |
| G8 "Possible duplicates" can never match | Change code: remove bucket | WP6 |
| G9 Three review reasons never raised | Change code: remove | WP6 |
| G10 Two screens group reasons differently | Change code: share one set | WP6 |
| G11 Unused components | Change code: delete | WP4 |
| G12 Stale docs | Change code | WP8 |
| G13 Who can resolve reviews | Decided: server administrators only (D12) | WP7 |
