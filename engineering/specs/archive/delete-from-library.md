# Spec: Delete from library

> **Status:** Built · **Version:** 5 · **Last updated:** 2026-10-20 · **Related docs:** [Product overview](../../product/overview.md) · [Item Detail page](item-detail.md) (D17, D26) · [Registry retirement](registry-retirement.md) (WP3) · [Library model and intake (public)](../../../docs/architecture/library-model-and-intake.md) · [Security model (public)](../../../docs/architecture/security.md)

> Written 2026-10-17 after Shy decided the old, unused delete actions go (Registry retirement WP3) and deletion is rebuilt properly. Versions 2–5 (same day) record Shy's answers as D12–D24. Version 4 drops the recycle bin for library media: films, books, audiobooks, music, comics and TV can be downloaded again, so they are deleted straight away after the confirmation. A recycle bin applies only to personal files in View (D23), which get their own View spec (D24). No questions are left; Shy Locked the spec on 2026-10-17. Built 2026-10-19 to 2026-10-20 (Shy's WP5 answer recorded as D25; as-built notes at the end of section 8). Section 8 records what the old delete did, because it shapes the risks.

## 1. Goal

An administrator at a desktop can delete a title they no longer want: it leaves the library, and its files leave the disk wherever Tuvima is allowed to touch them, freeing the space straight away. Before anything happens they see exactly what will go, what will stay on disk and why, and, as Shy asked, a warning naming every hand-picked collection the title is in.

## 2. Scope

**In scope**
- **Delete from library** for one title (one Work, with all its Editions and files), started from the title's detail page in Editing Mode.
- **Delete** for one song or several selected songs from the Listen page's song table (D13), using the same dialog. Songs have no detail page of their own, so this is the only way to delete music.
- A preview the Engine works out first, shown in a confirmation dialog: each file and what will happen to it, the hand-picked collections the title will leave, and any hand-picked collection that will be deleted because it would be empty.
- What happens to files in **managed** folders (deleted straight away after the confirmation, D16, D22), **as-is** folders, and **protected originals** (files a merge left in place).
- What happens to watch/read/listen progress, bookmarks, history, Needs Review entries, background jobs and artwork for the deleted title.
- Stopping files left on disk from being added back by the next scan, and an **Excluded files** list to add them back (D10, D15).
- Two Engine actions (preview, delete), their permission checks, a live update so open screens drop the title, and the tests.
- Making sure the old batch delete action and the Listen page's old Delete are gone (D12; Registry retirement removes them first).
- Recording the rule for personal files (D23), so the View work follows it.

**Out of scope**
- A recycle bin for library media (D22).
- Deleting personal files (photos, home videos, documents in View) from disk. They are not Works; the rule they follow is D23, and they get their own View spec (D24).
- Multi-select delete anywhere other than the Listen song table (browse rows, collections, search results) (D14).
- Deleting a whole group in one go: a TV show or season, a book or comic series, an album or a Universe (D14). Groups disappear on their own when their last title goes, through the existing empty-group clean-up. Deleting a film, an episode, a book, an audiobook, a comic issue or a song is in scope because each is one Work.
- Deleting just one Edition or one file while keeping the title (for example, dropping the 1080p copy and keeping the 4K one) (D14).
- Phones, tablets and TV: editing is desktop-only (a desktop with a fine, hover-capable pointer), and native and third-party apps get no delete permission.
- People, characters and Series records: deleting a title never deletes a person; Series and Universes are tidied only by the existing empty-group clean-up.

## 3. Decisions made

- **D1. Deletion is rebuilt, not revived** (Shy, 2026-10-17). Registry retirement WP3 removes the single-item delete action, and now the batch one with the Listen page's old Delete too (D12). This spec replaces them with a preview-and-confirm pair that obeys the source-file safety rules. It may reuse the old clean-up code only where that code is correct (section 8 lists what was not).
- **D2. Delete is for administrators on a desktop.** It shows only on a desktop with a fine, hover-capable pointer (the desktop-only editing rule), and only to someone the Engine treats as an effective administrator (current enabled account, active admin grant, and the surface PIN unlock where one is set) (D18). On a detail page it appears only in Editing Mode. The Engine checks on every call; hiding the button is never the protection.
- **D3. Hand-picked collections are always named in the warning** (Shy, 2026-10-17): "when deleting any item from the library (and disk) we should have warnings if it's attached to a manually curated collection. Automatic collections are not important to note since they work off the canonical metadata." The warning names each hand-picked (manual) collection. Automatic (rule-based) collections, and Series and Universes that come from metadata, are not listed.
- **D4. A hand-picked collection left with no items is deleted with the title** (Item Detail D26), and the dialog says so before you confirm, naming it. The editor's rule that you can't save or empty a collection to zero items (Item Detail D17) is unchanged; deleting its last title is the only way a hand-picked collection disappears without being deleted directly. Automatic collections are never deleted by this.
- **D5. Original source media stay protected** (CLAUDE.md Never #7). Every file change goes through the shared source-file safety gate, the same one the organising and merge jobs use. Tuvima never deletes, moves or renames a file in an **as-is** folder, a file a merge left in place, or any file the gate refuses. Those titles still leave the library; their files stay where they are, and the dialog says so file by file.
- **D6. The Engine decides, the Dashboard shows.** The preview comes from the Engine. Confirming sends back the preview's fingerprint; if anything changed since the preview (a new file, a new collection membership, a folder switched to as-is), the Engine refuses and the dialog shows the new preview. This follows the existing rule for any file-changing plan: work it out, show it, confirm that exact plan, check again just before acting.
- **D7. Personal state goes with the title** (D20). Every profile's progress, bookmarks, highlights, ratings, queue entries and play counts for the title are removed, so nothing points at a title that no longer exists. The dialog says how many profiles have progress ("3 people have progress on this") without naming them.
- **D8. Needs Review entries and queued work for the title are closed.** Its Needs Review items disappear; pending identification and enrichment jobs are cancelled; artwork that only this title used is removed.
- **D9. One server activity record is kept.** The title's own History (identity, metadata, artwork and ingestion events) goes with it, but the server keeps one activity entry: who deleted what, when, how many files were deleted, how much space was freed, and how many files were left on disk.
- **D10. A deleted title does not come back on the next scan.** Files that stay on disk (as-is, protected, or a file that couldn't be removed) are remembered as excluded, so scans skip them; **Add back** in Settings undoes it (D15).
- **D11. Nothing is left behind in the data store.** Deleting a title removes every record tied to it, including the metadata claims, chosen values, search entries, person links and job rows the old delete left behind (section 8).

Shy's answers (2026-10-17). To Q1–Q9: "go with your recommendations except Q5 - deleting means deleting, and in a library reclaming space is important so nobody wants to wait 30 days". On media versus personal files: "I think for media like movies, etc that can be re-downloaded etc... the delete immediately with a confirmation is probably fine. We don't need a recycle bin. So the behavior for recycle really only applies to personal media."

- **D12. (Q1) The old delete goes now.** The Listen page's song Delete and the batch delete action are removed by Registry retirement WP3, so there is no unsafe delete while this spec is built. Music has no Delete until WP5 lands.
- **D13. (Q2) The Listen song table gets Delete back** with the new dialog, for one song and for selected songs. The Engine actions take a list from the start.
- **D14. (Q3) Titles and songs only.** Deleting a whole show, season, album or series, multi-select elsewhere, and deleting one Edition or file are each a follow-up spec.
- **D15. (Q4) Files left on disk are excluded from scans**, with an **Excluded files** list and **Add back** in Settings → Libraries.
- **D16. (Q5, amended) Deleting means deleting.** After the confirmation, files in a managed folder are removed for good straight away and the space is freed. Nothing is held for a waiting period.
- **D17. (Q6) On the detail page, Delete is the last entry, in red, of the editing bar's More menu.**
- **D18. (Q7) Effective administrators only**; no app or native-client permission.
- **D19. (Q8) Sidecars go with the title.** Files that share the title's file name or that Tuvima wrote (subtitles, .nfo, posters) are deleted with it; any other file is left; folders the delete leaves empty are removed.
- **D20. (Q9) Progress is removed with the title** (D7).
- **D21. (Q10) Replaced by D22.** Version 3 offered "Delete immediately" or "Move to recycle bin" in the dialog for library media. Shy then decided library media needs no recycle bin.
- **D22. No recycle bin for library media.** Films, TV, books, audiobooks, comics and music can be downloaded again, so the confirmation dialog is the safety step and deletion is immediate. The dialog says plainly that files removed from disk can't be recovered.
- **D23. Personal files get a recycle bin.** Photos, home videos and documents in View can't be downloaded again. Deleting one from disk always moves it to a recycle bin first (building on View's existing Trash), and the file leaves the disk only when the recycle bin is emptied. "Delete immediately" is never offered for personal files. This spec only records the rule (D24).
- **D24. (Q11) Personal files get their own View spec** (Shy, 2026-10-17: "build the personal-files recycle bin in its own View spec; this spec covers library media only"). That spec builds deleting personal files from disk on View's Trash, with **Empty Trash** as the only step that removes a file, and proves ownership and gets a fresh confirmation as the View architecture requires. This spec covers library media only.

- **D25. (WP5) Selecting songs** (Shy, 2026-10-19). Songs could not be selected in the Listen song table (the old selection toolbar was removed in April 2026), so desktop administrators get a checkbox on each song row and, when any are checked, a slim bar: "N selected · Delete… · Clear". Nothing else changes for anyone: no other bulk actions, no selection for non-administrators, and no checkbox or Delete on phones, tablets or touch-only devices. The same table also shows album and artist song lists, which get the same Delete.

## 4. UI behaviour

### Where Delete is

- **Detail page** (administrator, desktop, Editing Mode on): the editing bar shows **Done**, **Manage Artwork**, **Match and Identity** and **More**. **Delete from library…** is the last entry in **More**, in red, below a divider (D17). Never on a deep link that opens straight into editing (CLAUDE.md Never #1).
- **Listen song table** (administrator, desktop; D13): a Delete icon on each song row, and Delete for selected songs, open the same dialog. For anyone else, or on a phone, tablet or touch-only device, the Delete icon is not shown.
- **Settings → Libraries**: the **Excluded files** list (D15).
- No other surface has Delete.

### The confirmation dialog

Opening it asks the Engine for a preview ("Checking what will be deleted…"). Then, for one title:

> **Delete "Dune: Part One" from your library?**
>
> **Files**
> - Movies › Dune Part One (2021) — 2 files, 61.4 GB — *will be deleted from disk*
> - Old Rips › Dune.2021.mkv — *stays on disk: Tuvima only reads this folder*
> - Imports › Dune.mkv — *stays on disk: kept as the original of a merge*
>
> **Hand-picked collections** — This is in 2 collections you made: **Comfort Films**, **Road Trip**. It will be taken out of them. **Road Trip** has nothing else in it, so it will be deleted too.
>
> **Progress** — 3 people have progress on this. It will be removed.
>
> Deleting frees 61.4 GB. Files deleted from disk can't be recovered.
>
> [Cancel] [Delete from library and disk]

- **Cancel** has the focus when the dialog opens; Escape cancels.
- The red button reads **Delete from library and disk** when at least one file will be deleted, and **Delete from library** when every file stays where it is (the "can't be recovered" line is then left out).
- Sections with nothing to say are left out (no "0 collections").
- Collections are sorted A–Z; after five, "and 3 more" expands the list.
- For several songs the heading reads **Delete 12 songs from your library?**, the files list is grouped by folder with totals, and each hand-picked collection shows how many of the songs it holds ("Road Trip (4 of these)").

| Situation | What the person sees |
|---|---|
| Normal | The dialog above. After confirming, "Deleting…", then the detail page closes back to where they came from (the song table refreshes) and a toast says "Deleted "Dune: Part One". 61.4 GB freed." |
| Every file is in an as-is folder or protected | Same dialog; every file row says it stays on disk; the button reads **Delete from library**; a line explains the files will be skipped by future scans. |
| In no hand-picked collection | The collections section is left out. Automatic collections are never mentioned. |
| Something changed since the preview | "Something changed since you opened this. Here's the updated list." The dialog refreshes; nothing has been deleted. |
| A file can't be removed (in use, no permission) | The title is still deleted from the library; that file stays on disk and is excluded from scans; the toast says "1 file couldn't be removed and was left on disk." |
| Engine unreachable | "Couldn't reach the Engine. Nothing was deleted." with Try again. |
| Not allowed (admin rights removed, PIN lock expired) | "You need administrator access to delete items." Nothing was deleted. |
| Already gone (deleted in another window) | "This item is no longer in your library." The detail page closes. |
| Still being processed | Delete is allowed; the dialog adds "Processing for this title will be stopped." |
| Someone is playing it | Delete is allowed; their player stops with "This item is no longer in your library." |

### Settings → Libraries

**Excluded files** (per library, shown only when there are any): file, folder, when and by whom, with **Add back** on each row. Adding back scans the file again.

### Everywhere else

Open screens on every device and profile (browse rows, Home, Continue lists, collection pages, Needs Review, native apps) drop the title through the live update, without a reload.

## 5. Data / model changes

- **New:** a list of **excluded files** that scans skip: the folder, the file's place in it, its fingerprint, when and by whom (D10, D15).
- **Changed:** removing a hand-picked collection that becomes empty (D4) replaces today's rule, which removed any collection no title pointed at, whether hand-picked or automatic (section 8).
- **Existing tables keep their shape.** Deleting a title removes its Work, Editions and Media Assets and everything tied to them (D7, D8, D11).
- **Existing libraries:** no reset needed.

## 6. Work packets

Security and data-loss risk: WP1 and WP2 run at high effort with a reviewer pass focused on file safety and authorization. Fable is only used with Shy's approval (`.claude/rules/model-routing.md`).

### WP1 — Engine: deletion preview
- **What it does:** a `LibraryItemDeletionService` (Engine) builds a preview for a list of Work IDs (1 to 200): every media file and sidecar (D19) with its folder label, size and outcome (`delete` / `keep_as_is` / `keep_protected` / `keep_missing`), decided by asking `SourceMutationPolicyGate` (never by inspecting the path or `management_mode` directly); the hand-picked collections (`collections.membership_mode = 'Manual'`, membership through `collection_items`) each title is in, with `will_be_deleted` when every remaining member is in the request; the number of profiles with personal state; whether processing is running; the bytes that will be freed; and a fingerprint over all of it. New Engine action `POST /library/items/deletion-preview`, `RequireEffectiveAdministrator()`, no application permission. Only Works are accepted; View personal items are refused (D23). Contracts: `LibraryItemDeletionPreviewRequest { work_ids }`, `LibraryItemDeletionPreview { items[], files[], curated_collections[], profiles_with_progress, processing_running, bytes_freed, fingerprint }` in `src/MediaEngine.Contracts/Items/`, with fixtures.
- **Files likely touched:** `src/MediaEngine.Api/Services/Libraries/` (new service), `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs`, `src/MediaEngine.Domain/Contracts/ILibraryItemCurationRepository.cs`, `src/MediaEngine.Storage/LibraryItemCurationRepository.cs` (replace `GetRemovalTargetsAsync`, whose inner join skips titles without files), `src/MediaEngine.Ingestion/SourceMutationPolicyGate.cs` (read-only use), `src/MediaEngine.Contracts/Items/`, `tests/MediaEngine.Contracts.Tests` fixtures (`tools/Update-ContractFixtures.ps1`), `src/MediaEngine.Api/DependencyInjection/`.
- **Depends on:** Registry retirement WP3 merged (including D12).
- **Size:** Complex.
- **Done when:** tests pass for: as-is file → `keep_as_is`; merge-ledger original → `keep_protected`; managed file → `delete`; title in a Manual collection is listed and a Smart collection is not; last member flagged `will_be_deleted`; a View item ID is refused; non-administrator, PIN-locked administrator and application token get 403; route-authorization guardrail passes.

### WP2 — Engine: delete action and full clean-up
- **What it does:** `POST /library/items/delete { work_ids, fingerprint }`, `RequireEffectiveAdministrator()`. Recomputes the preview; a different fingerprint returns 409 with the new preview. Otherwise, per title: deletes each `delete` file through the gate (D16, D22); records kept files as excluded (D10); then in one transaction deletes the Work and everything tied to it, including `canonical_values`, `metadata_claims`, `identity_jobs` (cancelled first), `entity_artwork_links`, search entries, person links, Needs Review rows and every per-profile table (`user_states`, `consumption_history`, `profile_work_preferences`, `player_queue_items`, `audiobook_listen_history`, `music_play_stats`, reader and audiobook bookmarks and highlights; to be confirmed against `schema.sql`), and Manual collections left empty. Then runs `WorkHierarchyMaintenanceService.CleanupEmptyParentsAsync`, writes one `SystemActionType.MediaRemoved` activity row per request with counts and bytes freed, and publishes a live "library items removed" event. A file that fails mid-way leaves the title deleted and that file excluded, and the response reports it. Confirms the old `POST /library/items/batch/delete` action, its wrappers and the Listen page's old Delete are gone (D12).
- **Files likely touched:** as WP1, plus `src/MediaEngine.Storage/Schema/schema.sql` (excluded-files table), a new `ExcludedSourceFileRepository`, `src/MediaEngine.Storage/Services/WorkHierarchyMaintenanceService.cs`, `src/MediaEngine.Web/Services/Integration/EngineApiClient.Operations.cs`, `IEngineApiClient.Operations.cs`, `UIOrchestratorService.cs`, `docs/reference/api-endpoints.md`, `docs/reference/database-schema.md`.
- **Depends on:** WP1.
- **Size:** Complex.
- **Done when:** tests pass for: stale fingerprint → 409 and nothing changed; as-is and merge-original files untouched on disk after delete; managed files gone from disk; no row for the deleted Work, Editions or Assets remains in any table (a test walks every table with a work, edition, asset or entity column); per-profile state removed; empty Manual collection deleted, Smart collection kept; activity row written with bytes freed; live event published; a locked file leaves the title deleted and the file excluded; non-administrator → 403; old batch delete route absent.

### WP3 — Scans skip excluded files
- **What it does:** the scanner and folder watcher skip excluded files. An excluded file whose fingerprint changes (a new file at the same place) is scanned again.
- **Files likely touched:** `src/MediaEngine.Ingestion/` (scan and watcher filters), tests.
- **Depends on:** WP2.
- **Size:** Standard.
- **Done when:** a rescan after deleting an as-is title does not add it back; a different file put at the same place is ingested.

### WP4 — Dashboard: the dialog and the detail page entry
- **What it does:** a `DeleteFromLibraryDialog` (shared `App*` dialog primitives) that loads the preview, shows the sections in section 4 including the space freed and the "can't be recovered" line, handles 409 by showing the new preview, and every state in the table. A red **Delete from library…** entry at the end of the editing bar's More list, shown only when Editing Mode is on, `EditorDeviceRule.CanEdit` is true and the Engine says the viewer is an administrator; the handler checks `CanEdit` again. On success the detail page closes back to its origin and a toast shows. Browse rows, Home and collection pages react to the live event.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/DetailHero.razor` (`EditingMoreActions`), `EditingModeBar.razor`, a new dialog under `src/MediaEngine.Web/Components/LibraryItems/` or `Components/Shared/`, a state service for the live event, `tests/MediaEngine.Web.Tests`.
- **Depends on:** WP1, WP2.
- **Size:** Standard.
- **Done when:** component tests show: the entry absent when not editing, not an administrator, or `CanEdit` is false; collections named A–Z and the "will be deleted too" line; the button label and the "can't be recovered" line follow whether any file will be deleted; Cancel focused on open; 409 refreshes the list. Visual check per `src/MediaEngine.Web/CLAUDE.md` at desktop width in light and dark.

### WP5 — Listen song table gets Delete back with the new dialog (D13)
- **What it does:** a song row Delete icon and Delete for selected songs (the old ones are removed by Registry retirement WP3, D12) open `DeleteFromLibraryDialog` with the song Work IDs; the icon shows only for administrators where `CanEdit` is true, and the handlers check `CanEdit` too.
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/ListenPage.razor.cs`, `src/MediaEngine.Web/Components/Listen/ListenSongTable.razor`, `tests/MediaEngine.Web.Tests`.
- **Depends on:** WP4.
- **Size:** Standard.
- **Done when:** component tests show no Delete icon for a non-administrator or a touch device; selecting 3 songs opens one dialog for all 3; no browser `confirm` is used.

### WP6 — Excluded files in Settings (D15)
- **What it does:** Settings → Libraries gets, per library, an **Excluded files** list (file, folder, when, by whom) with **Add back**, which removes the exclusion and queues a scan of that file. Engine actions to list and remove exclusions, administrator only.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/` (settings or library endpoints), `src/MediaEngine.Contracts/Settings/`, `src/MediaEngine.Web/Components/Settings/` (libraries tab), tests.
- **Depends on:** WP3.
- **Size:** Standard.
- **Done when:** adding back an excluded as-is file brings the title back after its scan; non-administrator → 403.

### WP7 — Docs
- **What it does:** `docs/reference/api-endpoints.md` (new and removed actions), `docs/reference/database-schema.md` (new table), `docs/architecture/library-model-and-intake.md` (deleting and excluded files under Managed and As-is; the D23 rule for personal files beside the existing Archive and Trash text), `docs/guides/library-settings.md` (excluded files), `docs/product/presentation-rules.md` (where Delete appears), `docs/reference/glossary.md` (excluded file).
- **Depends on:** WP2–WP6.
- **Size:** Mechanical.
- **Done when:** the docs build passes and each page matches the built behaviour.

## 7. Risks and open questions

**Risks**
- **The delete that exists today can remove files Tuvima should never touch** (section 8). D12 removes it in Registry retirement WP3; until that merges it is still live.
- **Deleting library media from disk can't be undone** (D16, D22). The confirmation dialog is the safety step: it lists every file, says how much space is freed and that files can't be recovered, and Cancel has the focus. The preview-then-recheck rule (D6), the safety gate (D5) and the WP1–WP2 tests guard against deleting the wrong thing.
- A title deleted while being played or read: the player stops with a clear message; native apps drop it on the live update or next refresh.
- WP2 adds a data-store table and wire contracts; it needs the Contracts fixtures refreshed and conflicts with any other branch refreshing them.

**Open questions**

None. Q1–Q11 were answered on 2026-10-17 (D12–D24).

## 8. Gaps and drift

What the old delete actions did (found 2026-10-17; the single one is removed on the Registry retirement branch, and the batch one with the Listen page Delete is being removed there too, D12):

- **Built, and worth keeping:** administrator-only access; the empty Series and Universe clean-up after the last title goes; one activity record per deleted title; removing the title's Needs Review rows and Tuvima's own artwork files.
- **Built unsafely:** files were deleted directly, without the shared safety gate, so a file in an as-is folder or a merge original could be deleted (D5 fixes this). No preview, no fingerprint check, a plain browser confirm. No live update, so other screens kept showing the title. The Listen page hides its Delete on narrow screens, but neither the button nor its handler checks the desktop-only editing rule.
- **Left behind:** metadata claims, chosen values, identity jobs and artwork links for the deleted title stayed in the data store (D11).
- **Wrong rule for collections:** a collection was removed when no title pointed at it as its Series, whatever its kind; hand-picked memberships were ignored and automatic collections could be removed too (D4 replaces this).
- **Not covered by tests:** neither delete action had an Engine test.

As built (2026-10-20):

- **In-person administrators only.** Both Engine actions use a stricter guard than "effective administrator": the person must be signed in at the Dashboard, so a paired app or native client acting for an administrator is refused as well (D18).
- **A delete the Engine didn't confirm.** If the Engine times out or fails after the delete was sent, the dialog says "The Engine didn't answer in time. Check your library before trying again." rather than "Nothing was deleted", which may not be true.
- **Waiting for running work.** Delete marks the title's identification and enrichment jobs as stopped, then waits up to 30 seconds for any job already running to let go before removing records.
- **Shared pictures stay.** A Tuvima-owned picture file that another title or person still uses is never deleted with the title.
- **Personal activity history stays** (Settings revamp D25): it keeps its own copy of the title and is not removed.
- **Where the dialog lives.** `DeleteFromLibraryDialog` sits in `Components/Shared/`, because a guardrail keeps `Components/LibraryItems/` free of the retired workspace.
