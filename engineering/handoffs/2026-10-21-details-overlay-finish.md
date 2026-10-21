# Handoff: finish the Details overlay and page-based Editing Mode spec

Run this handoff end to end per `.claude/rules/end-to-end-runs.md` and `.claude/rules/cloud-first.md`.

- **Spec:** [details-overlay-and-edit-mode.md](../specs/details-overlay-and-edit-mode.md)
- **Status:** In progress. Waves 1–5 are merged (Waves 4–5 in #362). WP8 (per-type pass and docs) is left, plus the follow-ups below.
- **Written:** 2026-10-21, by the Desktop session that ran Waves 1–5.

## 1. What is merged

| Wave | Work packets | Proposed change |
|---|---|---|
| 1 | WP1 overlay host and navigation, WP2 "..." menu | #305, with main CI fixes #312 and #313 |
| 2–3 | WP3 banner and section slot, WP4 Artwork and Match and Identity sections, WP7 Delete placement and dialogs | #314 |
| 4–5 | WP5 Match step and moves (Engine data and rules, Managed-folder file re-file, Dashboard Match step and mapping table), WP6 batch editing (Engine operations, Dashboard selection bar and section) | #362 |
| — | Format fix for a duplicate from a parallel cloud thread | #344 |

A claude.ai project thread built WP5, WP6 and a "WP8" in parallel (#340, #345 and 1f50499e0). Shy chose to keep this session's implementation (2026-10-20). #362 removes that thread's ItemMove, batch and mapping-table code. Nothing from it was kept: its file step only queued tag writes, and its best guess contradicted D12.

## 2. Work still to do (thread A: WP8 per-type pass and docs)

**Depends on:** #362 merged.
**Can run beside:** Videos waves (they plug into this spec's menu and banner).
**Needs from Shy:** nothing.

Brief for the coordinator to pass on:

> Implement WP8 of engineering/specs/details-overlay-and-edit-mode.md (per-type pass and docs). Read the spec, CLAUDE.md, src/MediaEngine.Web/CLAUDE.md and this handoff.
> 1. **Per-type tests.** For each type, a test covers the edit path (Edit in "...", banner, a pencil save), the move path (Match, Add anyway, or no-list), and the delete path (page "..." and banner "..."): movie, show, season, episode, book, series, album, audiobook (and its parts), comic issue.
>    - Books, comics and movies follow D20: no manual Series move; Series placement comes from re-match; Add anyway into a Series only.
>    - Add Videos once the Videos spec has built its type, through the move-list registry ("no list", D14).
> 2. **Guardrails.**
>    - No drawer anywhere (exists).
>    - No browser pop-ups (exists).
>    - Moves only through `/library/items/move*`, no second move service (add).
>    - Batch edits only through the op-based contract (add).
> 3. **Docs.**
>    - `docs/product/presentation-rules.md`: selection bar, "Edit together", the Match step and mapping table, "Not matched" and Unnumbered, Series per D20.
>    - `docs/guides/editing-items.md` ("Correct an item"): the full current flow.
>    - `src/MediaEngine.Web/CLAUDE.md`: check that the Details component row lists the batch and move components. The Editing services row was updated in #362.
>    - `docs/architecture/inline-media-editing.md`: check it matches.
>    - The archived Editing Mode spec: mark D1 and D8 superseded by this spec (check whether main already did this).
> 4. **Finish.** Run the full CI-strength build and tests. Set the spec Status to "Built, awaiting your Local checks" and update engineering/README.md. Append Local checks to engineering/validation/batch.md. Finish with the merge, then report Complete or Waiting on you.

## 3. Follow-ups found along the way (each its own small thread)

**Code**
1. **Removed-Work ids.** The move result should return the ids of Works it removed or merged. Today the Dashboard decides whether to follow an emptied audiobook by matching its name (`LibraryMoveRules.RemovedEditedWork`).
2. **Unreachable code.** `HierarchyAlignmentService.ApplyAsync` and `GetSuggestionsAsync` became unreachable when the old membership move was retired. Remove them, or fold any shared helper into the retail-match code.
3. **`LibraryFoldersPanel`** still reads `[SupplyParameterFromQuery]`. Under the overlay, a Settings page underneath would see the detail address's query. Convert it to `RoutedPageQuery`, like every routed page.
4. **Scoped styles that never apply.** These pre-date this spec. A scoped class passed to an `App*` child component is never styled:
   - `.sme-footer-btn` (`SharedMediaEditorShell.razor.css` ~90/136/142)
   - `.delete-library__more` (`DeleteFromLibraryDialog.razor.css` ~47/57)
   - `.sme-candidate-track--selectable` (`EditorMatchSection.razor.css` ~289-299)

   Fix with `::deep` within the style-ownership budget, or with elements the component owns.
5. **Refresh metadata** in Match and Identity is disabled only while the item's refresh schedule is queued. The editor has no per-item "still processing" flag; pass the detail page's processing reason in.
6. **Batch inherited-value lock** covers the show/season/album chain only. Books, audiobooks and comics that inherit from a Series aren't detected.
7. **Renumbering edge.** A numbered title whose title equals a free list entry is treated as covering that entry, so it can't be renumbered to that particular entry.
8. **Audiobook preview root.** The move preview assumes an audiobook's root parent is its parent Work. It ignores the `source-folder:` case in `GetLineageByAssetAsync`.
9. **Overlay nits.**
   - Pages with their own layout (reader, setup) aren't kept out of the underlay list.
   - A `history.go(-n)` that silently does nothing is caught only by a 2-second timeout.

**Tests that fail now and then** (passed on rerun; look into each):
- `BaselineRunTests.Inventory_IsStableWhileNothingChangesAndMovesWhenAFileDoes`
- `FirstRunToolsTests.ToolsPage_StopsRefreshingWhenThePageIsClosed`
- `ManagedAccessHouseholdTests.ServerAdministrator_StillSetsATemporaryPassword_FromTheUsersList`
- `IdentityPipelineSignalTests.RetryPolicy_UsesConfiguredBackoffDelay`
- `DocumentOcrTests.SlowFile…`
- `FolderJobServiceTests` (timing)

## 4. Local checks (Desktop, small test library)
These rows are in `engineering/validation/batch.md`; the cloud-thread rows #340, #345 and "WP8" are replaced to match what merged.

## 5. Decisions made while building (already in the code)
- **Q1–Q4 (from the plan):**
  - Tablets get the centred window.
  - A title opened from inside the overlay swaps the content, and Back steps back through titles.
  - "One unit" means one Apply, with each item saving on its own and failures going to Needs Review.
  - Audiobook parts are files (Editions), so their moves are buildable.
- **D9 (unsaved input):**
  - Esc inside a field cancels just that field.
  - Esc elsewhere, a backdrop click, Back or Done with unsaved input asks "Save or discard?". Cancel stays.
- **Page and selection-bar menus:**
  - Delete is in the page "..." for desktop administrators even when not editing (D18).
  - The selection bar puts Move… and Delete in its own "..." (reconciles D8/D11 with section 4's batch delete and mapping table).
- **Matching rules:**
  - Match needs both match and write permission. The preview says so per row (`can_match`).
  - Matching onto an existing entry carries over hearts, plays, bookmarks, queue and collections.
  - An emptied audiobook's hand-picked collection memberships move to the audiobook that received its files.
  - Add anyway stays unnumbered for good: number values aren't written for a flagged item, and slotting skips it.
  - A Not matched item can be matched, or a numbered one renumbered, inside its own container.
- **Batch editing:**
  - Emptying genres is refused with a reason (an empty genre list reverts to the provider's).
  - Batch failures get their own Needs Review trigger.
  - In edit mode, episode and series cards become tick cards, not links.
- **File names:** unnumbered files are named by title alone. The templates use `{TrackNumber?}` and `{EpisodeCode?}`.
