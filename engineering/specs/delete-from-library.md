# Spec: Delete from library

> **Status:** Draft · **Version:** 1 · **Last updated:** 2026-10-17 · **Related docs:** [Product overview](../product/overview.md) · [Item Detail page](item-detail.md) (D17, D26) · [Registry retirement](registry-retirement.md) (WP3) · [Library model and intake (public)](../../docs/architecture/library-model-and-intake.md) · [Security model (public)](../../docs/architecture/security.md)

> Written 2026-10-17 after Shy decided the old, unused delete actions go (Registry retirement WP3) and deletion is rebuilt properly. Nothing here is built. Where the text depends on an open question it says "(pending Qn)" and follows the recommendation; section 7 holds the questions, and the spec can't be Locked until they are answered. Section 8 records what the old delete did, because it shapes the risks.

## 1. Goal

An administrator at a desktop can delete a title they no longer want: it leaves the library, and its files leave the disk wherever Tuvima is allowed to touch them. Before anything happens they see exactly what will go, what will stay on disk and why, and, as Shy asked, a warning naming every hand-picked collection the title is in.

## 2. Scope

**In scope**
- **Delete from library** for one title (one Work, with all its Editions and files), started from the title's detail page in Editing Mode.
- **Delete** for one song or several selected songs from the Listen page's song table (pending Q2), using the same dialog. Songs have no detail page of their own, so this is the only way to delete music.
- A preview the Engine works out first, shown in a confirmation dialog: each file and what will happen to it, the hand-picked collections the title will leave, and any hand-picked collection that will be deleted because it would be empty.
- What happens to files in **managed** folders, **as-is** folders, and **protected originals** (files a merge left in place).
- What happens to watch/read/listen progress, bookmarks, history, Needs Review entries, background jobs and artwork for the deleted title.
- Stopping deleted files from being added back by the next scan (D10, Q4).
- Two Engine actions (preview, delete), their permission checks, a live update so open screens drop the title, and the tests.
- Retiring the old batch delete action and its Listen page wiring (pending Q1).

**Out of scope**
- Multi-select delete anywhere other than the Listen song table (browse rows, collections, search results) (Q3).
- Deleting a whole group in one go: a TV show or season, a book or comic series, an album or a Universe (Q3). Groups disappear on their own when their last title goes, through the existing empty-group clean-up. Deleting a film, an episode, a book, an audiobook, a comic issue or a song is in scope because each is one Work.
- Deleting just one Edition or one file while keeping the title (for example, dropping the 1080p copy and keeping the 4K one) (Q3).
- Photos and personal media (View). They are not Works and already have their own Archive and Trash.
- An in-app "restore deleted items" screen. If files are held for 30 days (Q5), recovery is by hand for now.
- Phones, tablets and TV: editing is desktop-only (a desktop with a fine, hover-capable pointer), and native and third-party apps get no delete permission.
- People, characters and Series records: deleting a title never deletes a person; Series and Universes are tidied only by the existing empty-group clean-up.

## 3. Decisions made

- **D1. Deletion is rebuilt, not revived** (Shy, 2026-10-17). Registry retirement WP3 removes the single-item delete action. The batch delete action was kept on that branch because the Listen song table still uses it (pending Q1). This spec replaces both with a preview-and-confirm pair that obeys the source-file safety rules. It may reuse the old clean-up code only where that code is correct (section 8 lists what was not).
- **D2. Delete is for administrators on a desktop.** It shows only on a desktop with a fine, hover-capable pointer (the desktop-only editing rule), and only to someone the Engine treats as an effective administrator (current enabled account, active admin grant, and the surface PIN unlock where one is set) (pending Q7). On a detail page it appears only in Editing Mode. The Engine checks on every call; hiding the button is never the protection.
- **D3. Hand-picked collections are always named in the warning** (Shy, 2026-10-17): "when deleting any item from the library (and disk) we should have warnings if it's attached to a manually curated collection. Automatic collections are not important to note since they work off the canonical metadata." The warning names each hand-picked (manual) collection. Automatic (rule-based) collections, and Series and Universes that come from metadata, are not listed.
- **D4. A hand-picked collection left with no items is deleted with the title** (Item Detail D26), and the dialog says so before you confirm, naming it. The editor's rule that you can't save or empty a collection to zero items (Item Detail D17) is unchanged; deleting its last title is the only way a hand-picked collection disappears without being deleted directly. Automatic collections are never deleted by this.
- **D5. Original source media stay protected** (CLAUDE.md Never #7). Every file change goes through the shared source-file safety gate, the same one the organising and merge jobs use. Tuvima never deletes, moves or renames a file in an **as-is** folder, a file a merge left in place, or any file the gate refuses. Those titles still leave the library; their files stay where they are, and the dialog says so file by file.
- **D6. The Engine decides, the Dashboard shows.** The preview comes from the Engine. Confirming sends back the preview's fingerprint; if anything changed since the preview (a new file, a new collection membership, a folder switched to as-is), the Engine refuses and the dialog shows the new preview. This follows the existing rule for any file-changing plan: work it out, show it, confirm that exact plan, check again just before acting.
- **D7. Personal state goes with the title** (pending Q9). Every profile's progress, bookmarks, highlights, ratings, queue entries and play counts for the title are removed, so nothing points at a title that no longer exists. The dialog says how many profiles have progress ("3 people have progress on this") without naming them.
- **D8. Needs Review entries and queued work for the title are closed.** Its Needs Review items disappear; pending identification and enrichment jobs are cancelled; artwork that only this title used is removed.
- **D9. One server activity record is kept.** The title's own History (identity, metadata, artwork and ingestion events) goes with it, but the server keeps one activity entry: who deleted what, when, how many files were removed, held or left on disk.
- **D10. A deleted title does not come back on the next scan.** Files that stay on disk (as-is, protected, or a file that couldn't be removed) are remembered as excluded, so scans skip them (how this is undone: Q4).
- **D11. Nothing is left behind in the data store.** Deleting a title removes every record tied to it, including the metadata claims, chosen values, search entries, person links and job rows the old delete left behind (section 8).

## 4. UI behaviour

### Where Delete is

- **Detail page** (administrator, desktop, Editing Mode on): the editing bar shows **Done**, **Manage Artwork**, **Match and Identity** and **More**. **Delete from library…** is the last entry in **More**, in red, below a divider (Q6). Never on a deep link that opens straight into editing (CLAUDE.md Never #1).
- **Listen song table** (administrator, desktop; pending Q2): the existing Delete icon on a song row, and Delete for selected songs, open the same dialog. For anyone else, or on a phone, tablet or touch-only device, the Delete icon is not shown.
- No other surface has Delete.

### The confirmation dialog

Opening it asks the Engine for a preview ("Checking what will be deleted…"). Then, for one title:

> **Delete "Dune: Part One" from your library?**
>
> **Files**
> - Movies › Dune Part One (2021) — 2 files, 61.4 GB — *moved to deleted items, removed for good after 30 days* (pending Q5)
> - Old Rips › Dune.2021.mkv — *stays on disk: Tuvima only reads this folder*
> - Imports › Dune.mkv — *stays on disk: kept as the original of a merge*
>
> **Hand-picked collections** — This is in 2 collections you made: **Comfort Films**, **Road Trip**. It will be taken out of them. **Road Trip** has nothing else in it, so it will be deleted too.
>
> **Progress** — 3 people have progress on this. It will be removed.
>
> [Cancel] [Delete from library and disk]

- **Cancel** has the focus when the dialog opens; Escape cancels.
- The red button reads **Delete from library and disk** when at least one file will leave the disk, and **Delete from library** when none will.
- Sections with nothing to say are left out (no "0 collections").
- Collections are sorted A–Z; after five, "and 3 more" expands the list.
- For several songs the heading reads **Delete 12 songs from your library?**, the files list is grouped by folder with totals, and each hand-picked collection shows how many of the songs it holds ("Road Trip (4 of these)").

| Situation | What the person sees |
|---|---|
| Normal | The dialog above. After confirming, "Deleting…", then the detail page closes back to where they came from (the song table refreshes) and a toast says "Deleted "Dune: Part One"." with the number of files removed. |
| Every file is in an as-is folder or protected | Same dialog; every file row says it stays on disk; the button reads **Delete from library**; a line explains the files will be skipped by future scans. |
| In no hand-picked collection | The collections section is left out. Automatic collections are never mentioned. |
| Something changed since the preview | "Something changed since you opened this. Here's the updated list." The dialog refreshes; nothing has been deleted. |
| A file can't be removed (in use, no permission) | The title is still deleted from the library; that file stays on disk and is excluded from scans; the toast says "1 file couldn't be removed and was left on disk." |
| Engine unreachable | "Couldn't reach the Engine. Nothing was deleted." with Try again. |
| Not allowed (admin rights removed, PIN lock expired) | "You need administrator access to delete items." Nothing was deleted. |
| Already gone (deleted in another window) | "This item is no longer in your library." The detail page closes. |
| Still being processed | Delete is allowed; the dialog adds "Processing for this title will be stopped." |
| Someone is playing it | Delete is allowed; their player stops with "This item is no longer in your library." |

### Everywhere else

Open screens on every device and profile (browse rows, Home, Continue lists, collection pages, Needs Review, native apps) drop the title through the live update, without a reload.

## 5. Data / model changes

- **New:** a list of **excluded files** that scans skip: the folder, the file's place in it, its fingerprint, when and by whom (D10, Q4).
- **New (if Q5 is "hold"):** a hidden deleted-items folder inside each managed folder, a record of each held file and when it was held, and a daily clean-up that removes held files after the holding period. Scans skip the hidden folder.
- **Changed:** removing a hand-picked collection that becomes empty (D4) replaces today's rule, which removed any collection no title pointed at, whether hand-picked or automatic (section 8).
- **Existing tables keep their shape.** Deleting a title removes its Work, Editions and Media Assets and everything tied to them (D7, D8, D11).
- **Existing libraries:** no reset needed.

## 6. Work packets

Security and data-loss risk: WP1 and WP2 run at high effort with a reviewer pass focused on file safety and authorization. Fable is only used with Shy's approval (`.claude/rules/model-routing.md`).

### WP1 — Engine: deletion preview
- **What it does:** a `LibraryItemDeletionService` (Engine) builds a preview for a list of Work IDs (1 to 200): every media file and Tuvima-written sidecar (Q8) with its folder label and outcome (`delete` / `hold` / `keep_as_is` / `keep_protected` / `keep_missing`), decided by asking `SourceMutationPolicyGate` (never by inspecting the path or `management_mode` directly); the hand-picked collections (`collections.membership_mode = 'Manual'`, membership through `collection_items`) each title is in, with `will_be_deleted` when every remaining member is in the request; the number of profiles with personal state; whether processing is running; and a fingerprint over all of it. New Engine action `POST /library/items/deletion-preview`, `RequireEffectiveAdministrator()`, no application permission. Contracts: `LibraryItemDeletionPreviewRequest { work_ids }`, `LibraryItemDeletionPreview { items[], files[], curated_collections[], profiles_with_progress, processing_running, fingerprint }` in `src/MediaEngine.Contracts/Items/`, with fixtures.
- **Files likely touched:** `src/MediaEngine.Api/Services/Libraries/` (new service), `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs`, `src/MediaEngine.Domain/Contracts/ILibraryItemCurationRepository.cs`, `src/MediaEngine.Storage/LibraryItemCurationRepository.cs` (replace `GetRemovalTargetsAsync`, whose inner join skips titles without files), `src/MediaEngine.Ingestion/SourceMutationPolicyGate.cs` (read-only use), `src/MediaEngine.Contracts/Items/`, `tests/MediaEngine.Contracts.Tests` fixtures (`tools/Update-ContractFixtures.ps1`), `src/MediaEngine.Api/DependencyInjection/`.
- **Depends on:** Registry retirement WP3 merged.
- **Size:** Complex.
- **Done when:** tests pass for: as-is file → `keep_as_is`; merge-ledger original → `keep_protected`; managed file → `hold` (or `delete`, per Q5); title in a Manual collection is listed and a Smart collection is not; last member flagged `will_be_deleted`; non-administrator, PIN-locked administrator and application token get 403; route-authorization guardrail passes.

### WP2 — Engine: delete action and full clean-up
- **What it does:** `POST /library/items/delete { work_ids, fingerprint }`, `RequireEffectiveAdministrator()`. Recomputes the preview; a different fingerprint returns 409 with the new preview. Otherwise, per title: moves (or deletes, Q5) each allowed file through the gate, records kept files as excluded (D10), then in one transaction deletes the Work and everything tied to it, including `canonical_values`, `metadata_claims`, `identity_jobs` (cancelled first), `entity_artwork_links`, search entries, person links, Needs Review rows and every per-profile table (`user_states`, `consumption_history`, `profile_work_preferences`, `player_queue_items`, `audiobook_listen_history`, `music_play_stats`, reader and audiobook bookmarks and highlights; to be confirmed against `schema.sql`), and Manual collections left empty. Then runs `WorkHierarchyMaintenanceService.CleanupEmptyParentsAsync`, writes one `SystemActionType.MediaRemoved` activity row per request with counts, and publishes a live "library items removed" event. A file that fails mid-way leaves the title deleted and that file excluded, and the response reports it. Removes the old `POST /library/items/batch/delete` action, `BatchDeleteLibraryCatalogItemsAsync` / `DeleteLibraryCatalogItemAsync` wrappers and `DeleteLibraryItemResponse` if Registry retirement has not already (Q1).
- **Files likely touched:** as WP1, plus `src/MediaEngine.Storage/Schema/schema.sql` (excluded-files table; held-files table if Q5 is hold), a new `ExcludedSourceFileRepository`, `src/MediaEngine.Storage/Services/WorkHierarchyMaintenanceService.cs`, `src/MediaEngine.Web/Services/Integration/EngineApiClient.Operations.cs`, `IEngineApiClient.Operations.cs`, `UIOrchestratorService.cs`, `docs/reference/api-endpoints.md`, `docs/reference/database-schema.md`.
- **Depends on:** WP1.
- **Size:** Complex.
- **Done when:** tests pass for: stale fingerprint → 409 and nothing changed; as-is and merge-original files untouched on disk after delete; managed files gone from their folder (held or removed); no row for the deleted Work, Editions or Assets remains in any table (a test walks every table with a work, edition, asset or entity column); per-profile state removed; empty Manual collection deleted, Smart collection kept; activity row written; live event published; a locked file leaves the title deleted and the file excluded; non-administrator → 403; old batch delete route absent.

### WP3 — Scans skip excluded and held files; held-file clean-up
- **What it does:** the scanner and folder watcher skip excluded files and the hidden deleted-items folder; a daily clean-up removes held files after `deleted_retention_days` (default 30, alongside `rejected_retention_days` in maintenance settings), through the gate, following `RejectedFileCleanupService`. An excluded file whose fingerprint changes (a new file at the same place) is scanned again.
- **Files likely touched:** `src/MediaEngine.Ingestion/` (scan and watcher filters), `src/MediaEngine.Api/Services/` (new clean-up hosted service), `src/MediaEngine.Domain/Configuration/MaintenanceSettings.cs`, `config/core.json` or the maintenance config file it reads (to be confirmed), `docs/reference/configuration.md`.
- **Depends on:** WP2.
- **Size:** Standard.
- **Done when:** a rescan after deleting an as-is title does not add it back; a held file older than the retention period is removed and a newer one is kept; the hidden folder never appears as media.

### WP4 — Dashboard: the dialog and the detail page entry
- **What it does:** a `DeleteFromLibraryDialog` (shared `App*` dialog primitives) that loads the preview, shows the sections in section 4, handles 409 by showing the new preview, and every state in the table. A red **Delete from library…** entry at the end of the editing bar's More list, shown only when Editing Mode is on, `EditorDeviceRule.CanEdit` is true and the Engine says the viewer is an administrator; the handler checks `CanEdit` again. On success the detail page closes back to its origin and a toast shows. Browse rows, Home and collection pages react to the live event.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/DetailHero.razor` (`EditingMoreActions`), `EditingModeBar.razor`, a new dialog under `src/MediaEngine.Web/Components/LibraryItems/` or `Components/Shared/`, a state service for the live event, `tests/MediaEngine.Web.Tests`.
- **Depends on:** WP1, WP2.
- **Size:** Standard.
- **Done when:** component tests show: the entry absent when not editing, not an administrator, or `CanEdit` is false; collections named A–Z and the "will be deleted too" line; the button label switches between "…and disk" and library-only; Cancel focused on open; 409 refreshes the list. Visual check per `src/MediaEngine.Web/CLAUDE.md` at desktop width in light and dark.

### WP5 — Listen song table uses the new dialog (pending Q2)
- **What it does:** the song row Delete icon and Delete for selected songs open `DeleteFromLibraryDialog` with the song Work IDs; the browser `confirm` goes; the icon shows only for administrators where `CanEdit` is true, and the handlers check `CanEdit` too.
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/ListenPage.razor.cs` (`DeleteTrackAsync` and the selected-songs handler), `src/MediaEngine.Web/Components/Listen/ListenSongTable.razor`, `tests/MediaEngine.Web.Tests`.
- **Depends on:** WP4.
- **Size:** Standard.
- **Done when:** component tests show no Delete icon for a non-administrator or a touch device; selecting 3 songs opens one dialog for all 3; no `confirm` call remains.

### WP6 — Excluded files list in Settings (pending Q4)
- **What it does:** Settings → Libraries gets an **Excluded files** list per library (file, folder, when, by whom) with **Add back**, which removes the exclusion and queues a scan of that file. Engine actions to list and remove exclusions, administrator only.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/` (settings or library endpoints), `src/MediaEngine.Contracts/Settings/`, `src/MediaEngine.Web/Components/Settings/` (libraries tab), tests.
- **Depends on:** WP3.
- **Size:** Standard.
- **Done when:** adding back an excluded as-is file brings the title back after its scan; non-administrator → 403.

### WP7 — Docs
- **What it does:** `docs/reference/api-endpoints.md` (new and removed actions), `docs/reference/database-schema.md` (new tables), `docs/architecture/library-model-and-intake.md` (delete, held and excluded files under Managed and As-is), `docs/guides/library-settings.md` (excluded files, retention), `docs/product/presentation-rules.md` (where Delete appears), `docs/reference/configuration.md` (retention setting), `docs/reference/glossary.md` (excluded file, deleted items).
- **Depends on:** WP2–WP6.
- **Size:** Mechanical.
- **Done when:** the docs build passes and each page matches the built behaviour.

## 7. Risks and open questions

**Risks**
- **The delete that exists today can remove files Tuvima should never touch** (section 8). Until Q1 is answered, the Listen page's Delete removes a song's files even in an as-is folder. Q1 is the fix.
- File deletion is the one thing in Tuvima that can't be undone without the holding folder (Q5). The preview-then-recheck rule (D6), the safety gate (D5) and the WP1–WP2 tests are the protection.
- A title deleted while being played or read: the player stops with a clear message; native apps drop it on the live update or next refresh.
- WP2 adds data-store tables and wire contracts; it needs the Contracts fixtures refreshed and conflicts with any other branch refreshing them.

**Open questions** (each with a recommendation)

- **Q1. The old batch delete is still live.** Your 2026-10-17 decision removed both old delete actions, but the Registry retirement branch kept the batch one because the Listen page's song Delete (one song, or selected songs) uses it. It deletes files without the safety gate, so it can remove files from an as-is folder, and it uses a plain browser "OK / Cancel" box. Options: (a) remove the Listen song Delete and the batch action now, in Registry retirement WP3 as you decided, so there is no unsafe delete until this spec is built; (b) keep them until this spec replaces them (WP2, WP5); (c) patch them now to respect the safety gate, then replace them later. **Recommendation: (a)**: it matches your decision, removes the risk today, and nothing half-safe is kept around. Music loses Delete until WP5 lands.
- **Q2. Should the Listen song table get Delete back with the new dialog?** (a) Yes, for one song and for selected songs (WP5); the Engine actions take a list from the start. (b) No; deletion only from detail pages, so songs can't be deleted until songs get one. **Recommendation: (a)**: songs have no detail page, and deleting a few tracks at once is the common music case.
- **Q3. Groups, other multi-select and single Editions?** Deleting a whole show, season, album or book series; multi-select on browse rows; deleting one Edition or file but keeping the title. (a) None of these now: titles and songs only, each a follow-up spec. (b) Add shows, seasons and albums now with a full episode/track list in the dialog. **Recommendation: (a)**: a show can be hundreds of files across folders and needs its own preview design; the Engine actions already take a list, so follow-ups reuse them.
- **Q4. When files stay on disk, how do they stop coming back, and how is that undone?** (a) Remember them as excluded, with an **Excluded files** list and **Add back** in Settings → Libraries (WP6). (b) Remember them with no screen; removing the folder from the library and adding it again clears its exclusions. (c) Don't exclude: the title comes back on the next scan, and the dialog says so. **Recommendation: (a)**: without exclusion a delete from an as-is folder silently undoes itself, and without a screen nobody can find out why a file is ignored.
- **Q5. What does "removed from disk" mean in a managed folder?** (a) Move the files to a hidden deleted-items folder inside the same managed folder, removed for good after 30 days (a setting). (b) Move them to the Engine's existing rejected-files area, which already has a 30-day clean-up; that area may be on another drive, so a 60 GB film would be copied, slowly. (c) Remove them for good straight away. **Recommendation: (a)**: a quick move on the same drive, works the same on Windows, Docker and Unraid, and gives 30 days to recover by hand. (c) is simplest, but one wrong click loses a file for good.
- **Q6. Where on the detail page?** (a) The last entry, in red, of the editing bar's **More** menu. (b) Its own red **Delete…** button in the editing bar beside **Done**. (c) A new File tab in the Editing drawer (today it has Details, Artwork and Match and Identity only). **Recommendation: (a)**: reachable only while editing, out of the way of everyday edits, no new tab.
- **Q7. Who may delete?** (a) Effective administrators only (current enabled account, active admin grant, surface PIN unlock where set); no app or native-client permission. (b) Anyone the Engine lets edit the item. **Recommendation: (a)**: removing files is a server-level act, not a metadata correction. This is also what the old actions required.
- **Q8. Sidecar files beside a managed title (subtitles, .nfo, posters Tuvima wrote)?** (a) Delete them with the title when they share its file name or Tuvima wrote them; leave any other file; remove folders the delete leaves empty. (b) Delete the media files only. **Recommendation: (a)**: it matches what the organising job already does with sidecars and emptied folders; otherwise managed folders fill up with orphaned subtitles.
- **Q9. Keep progress in case the title is added back?** (a) Remove it with the title (D7). (b) Keep it for 30 days, matched by file fingerprint. **Recommendation: (a)**: it keeps Continue lists honest and avoids a hidden store of removed titles.

## 8. Gaps and drift

What the old delete actions did (found 2026-10-17; the single one is removed on the Registry retirement branch, the batch one is still in use):

- **Built, and worth keeping:** administrator-only access; the empty Series and Universe clean-up after the last title goes; one activity record per deleted title; removing the title's Needs Review rows and Tuvima's own artwork files.
- **Built unsafely:** files were deleted directly, without the shared safety gate, so a file in an as-is folder or a merge original could be deleted (D5 fixes this). No preview, no fingerprint check, a plain browser confirm. No live update, so other screens kept showing the title. The Listen page hides its Delete on narrow screens, but neither the button nor its handler checks the desktop-only editing rule.
- **Left behind:** metadata claims, chosen values, identity jobs and artwork links for the deleted title stayed in the data store (D11).
- **Wrong rule for collections:** a collection was removed when no title pointed at it as its Series, whatever its kind; hand-picked memberships were ignored and automatic collections could be removed too (D4 replaces this).
- **Not covered by tests:** neither delete action had an Engine test.
