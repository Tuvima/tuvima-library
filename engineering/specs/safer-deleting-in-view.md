# Spec: Safer deleting in View

> **Status:** Locked · **Version:** 2 · **Last updated:** 2026-10-21 · **Related docs:** [Product overview](../product/overview.md) · [Deleting personal files in View](archive/view-personal-file-deletion.md) (Built v3; sections 2 and 8 name the three gaps this spec closes) · [Delete from library](archive/delete-from-library.md) · [Videos](videos.md) (D14, D49 recycle bin) · [View personal media (public)](../../docs/architecture/view-personal-media.md) · [Security model (public)](../../docs/architecture/security.md)

> Written 2026-10-21 after Shy agreed (open-items review) to plan safer deleting in View. This is a **new spec, not a new Version of the Built one**: Deleting personal files in View is Built and archived, and its meaning does not change. It builds on it. Version 2 records Shy's answers on how Missing items behave (hidden, 90-day purge, findable in search). Locked by Shy on 2026-10-21.

## 1. Goal

Nothing in View should be able to remove a person's photos, home videos or documents except through the careful, confirmed route, and View should never silently lie about what is on disk. Two things are still wrong today: one clean-up job deletes personal originals without going through the shared safety check, and if someone deletes a file outside Library (in File Explorer, from another app) View keeps showing it as if it were there.

## 2. What is already done (so we don't redo it)

The review that prompted this spec listed "Trash never frees disk space" as open. **That is out of date.** Empty Trash was built and merged on 2026-10-20 (Deleting personal files in View, v3): Trash shows its size, **Empty Trash…** works on a desktop with a summary and a fresh confirmation, every file goes through the shared safety gate, an item leaves as one, and a hidden staging folder lets a failure be undone. Library media (films, books, music and so on) has its own recycle bin in the Engine's data folder, with its own Settings section, restore and empty (Videos D49). Shy also decided that Trash never empties itself (D15 of the View spec). This spec keeps all of that.

## 3. Scope

**In scope**
- **The shared safety gate for the Shared Library clean-up** (WP1): when a personal original is removed after it has been copied to the Shared Library, the removal goes through `SourceMutationPolicyGate`, the same check Empty Trash uses.
- **Noticing files deleted outside Library** (WP2 to WP4): View finds out when a file is gone, hides the item, keeps its data for a grace period (90 days by default, set in Settings), and still lets people find it in search.
- A short check that **Empty Trash and the recycle bin stay consistent** with the new "missing" state (WP5).
- Docs and tests.

**Out of scope**
- A Trash or recycle bin that empties itself after some days (decided: never, D15).
- Deleting Shared Library items (still a later spec, D14 of the View spec).
- Changing Empty Trash, its dialog or who may use it.
- Deleting a whole show, season, album or Series (separate; Shy has asked for it by default, planned on its own).
- Anything for library media (films, TV, books, music, comics): the library scanner and Delete from library already handle those.
- Community sharing (deferred).

## 4. Decisions made

- **D1. One gate for every personal-file removal.** After this spec there is no code path that removes a personal original from disk without the shared safety gate. Reason: Reliability and Privacy (one place to prove a delete is allowed).
- **D2. The Shared Library clean-up keeps its own extra check** (the copy and the original must still match, checked just before removal) and adds the gate on top. The gate can only make it stricter: if the gate refuses, the original stays and the transfer shows "cleanup pending" with the reason, exactly as it does today for a locked file.
- **D3. Missing is a state first, a deletion only after the grace period.** When a file has disappeared, View keeps the item's record, notes, tags, bookmarks, Gallery memberships and people and place notes and marks the item **Missing**. Only after the grace period (D8) does Library remove them.
- **D4. An offline folder is not a missing file.** If the folder itself can't be reached (unplugged drive, network share asleep), nothing is marked Missing and the grace-period clock does not run for that folder. The folder shows "Folder offline".
- **D5. A returning file comes back with everything.** If the file returns (drive reconnected, restored from a backup), the item reappears with all its data, as long as it hasn't been purged.
- **D6. Compound items are missing only when their main file is.** A Live Photo whose video was deleted is still a photo; the item stays normal with a small "Part of this item is missing" note. RAW + JPEG + XMP: missing when no viewable file is left.
- **D7. Missing items are hidden** (Shy, 2026-10-21). They do not appear in the Timeline, Photos, Documents, Galleries, covers, People, Places or any shelf. No Missing filter on those pages.
- **D8. Purge after 90 days** (Shy, 2026-10-21). A Missing item's record and its data (notes, bookmarks, tags, Gallery spots and so on) are removed 90 days after the file was first seen missing. This is a setting (default 90 days, with "Never purge" as an option), shown in Settings with a plain explanation, and described in the docs. Nothing else ever removes records for missing files. Library, not a person, does the purge, and it never touches a file.
- **D9. Search can find Missing items** (Shy, 2026-10-21). Search has a **Missing** filter, off by default. With it on, searching for a file name, a note or a tag finds Missing items and each result says "File missing. If you want it back, tell an administrator." Results follow the same permissions as everywhere: a person only ever finds Missing items they could have seen when the file was present (own Personal Space, their Shared Library access, content limits, Kids rules and documents-off profiles).
- **D10. No manual "Remove from Library" for Missing items.** Hiding plus the 90-day purge replaces it, so there is nothing for a Kids profile or anyone else to clear (this retires the earlier Q3).

## 5. UI behaviour

| Situation | What the person sees |
|---|---|
| A file was deleted outside Library | Within about a minute (or at the next start) the item disappears from Timeline, Photos, Documents, Galleries and the rest. Nothing is shown to say it vanished. |
| A file was moved or renamed outside Library | If the new place is inside a watched folder, Library finds it again by its contents and the item simply updates. Otherwise it is Missing like any other. |
| Searching with the **Missing** filter on | Missing items matching the search (file name, note, tag) are listed, dimmed, with "File missing. If you want it back, tell an administrator." Opening one shows its notes and tags but no file. Only items the person was allowed to see appear (D9). |
| Searching with the filter off (default) | Missing items never appear. |
| Whole folder unreachable | The folder row in Settings shows "Folder offline"; its items look normal; nothing is marked Missing and no clock runs (D4). |
| File comes back | The item reappears everywhere with all its data (D5). |
| 90 days pass | The record and its data are removed. A later search finds nothing. If the file returns after that, it is scanned in as a new item. |
| Settings | A "Missing files" row in View settings: "Keep information about missing files for [90] days, then remove it" with a "Never remove" choice, and one line explaining that files are never touched. Only administrators change it (location to be confirmed in WP4). |
| Trashed item whose file vanished | Empty Trash treats it as already gone: it removes the records and counts it as "already missing" in the summary rather than failing. |
| Shared Library clean-up refused by the gate | The Shared copy is kept, the original stays, and the transfer says "The original couldn't be removed: <reason>." |
| Phone, tablet, TV, native apps | Same hiding and search behaviour; there are no actions to hide. |

## 6. Data / model changes

- A **missing since** time on each file record (empty when the file is present). The item reads as Missing when its main file is missing (D6). The purge compares this time with the setting.
- A **missing-file retention** setting (days, default 90, or never) in configuration, not hard-coded.
- Search indexes keep Missing items but tag them so default searches skip them and the Missing filter finds them.
- A **folder reachable** flag per View source, set by the scan and watcher (D4).
- No change to the item graph, wire names for existing fields, or Empty Trash. New fields are added to Contracts with fixtures. Pre-release data store, so a reset is acceptable.

## 7. Work packets

Data-loss risk: WP1 and WP3 get a reviewer pass focused on file safety. Built in the cloud, validated locally.

### WP1 — Shared Library clean-up uses the gate
- **What it does:** Routes `ViewSharedTransferService.FinishCleanupAsync` (the `File.Delete(file.Source)` at about line 292) through `ISourceMutationPolicyGate` with a "delete personal original" request, keeping the content re-check. A refusal leaves the file and reports the reason. Any other raw delete of a personal file found while doing this goes through the gate too.
- **Files likely touched:** `src/MediaEngine.Api/Services/View/ViewSharedTransferService.cs`, `src/MediaEngine.Ingestion/SourceMutationPolicyGate.cs` (a request kind, if one is missing), `tests/MediaEngine.Api.Tests`, `tests/MediaEngine.Ingestion.Tests`, a guardrail test.
- **Depends on:** none.
- **Size:** Standard.
- **Done when:** tests show an original in a linked folder or outside the owner's profile folder is never removed by the clean-up; a refusal leaves a `cleanup_pending` transfer with the reason; a guardrail test fails if code under `Services/View` calls `File.Delete` or `Directory.Delete` outside the gate-approved services.

### WP2 — Engine: notice deletions (watcher and startup sweep)
- **What it does:** The folder watcher (`FileSystemViewSourceWatcher`) also listens for deleted files and raises a `Deleted` event. The indexer marks the file's record **missing since now**, after checking the folder is reachable (D4). A sweep at Engine start and after a full scan marks files that no longer exist, and clears the mark for files that came back (D5). A rename or move is matched by content hash to a file seen elsewhere in the same source before anything is marked (section 5). Files in the Empty Trash staging folder are ignored as today.
- **Files likely touched:** `src/MediaEngine.Api/Services/LocalAssets/ViewSourceIndexingHostedService.cs`, `src/MediaEngine.Storage/LocalAssetRepository.cs`, `src/MediaEngine.Storage/Schema/schema.sql`, `tests/MediaEngine.Api.Tests`, `tests/MediaEngine.Storage.Tests`.
- **Depends on:** none (can run beside WP1).
- **Size:** Complex.
- **Done when:** tests show a deleted file is marked missing and nothing else is removed; a folder that can't be reached marks nothing; a returning file clears the mark; a moved file within the source is re-linked, not marked; a Live Photo missing only its video stays normal with the part-missing flag; no clean-up deletes a record for a missing file.

### WP3 — Engine: hide, search filter, purge, Empty Trash with missing files
- **What it does:** (a) Every View list, count, cover and discovery query skips Missing items. (b) Search gets a `missing` filter (off by default) that returns Missing items only within the caller's existing permissions, each flagged `file_missing`. (c) A purge job (hosted background service) removes Missing items older than the retention setting, with all their data and thumbnails, in one transaction per item, skipping any whose folder is offline (D4); it writes one activity entry per run (count only, no names). (d) Empty Trash's planner counts an already-missing file as "already gone" and still removes the item.
- **Files likely touched:** `src/MediaEngine.Storage/LocalAssetRepository.cs` and the View list/search repositories, `src/MediaEngine.Api/Services/View/` (search, new purge service), `ViewTrashDeletionPlanner.cs`, `ViewTrashDeletionService.cs`, `src/MediaEngine.Api/DependencyInjection/`, `config/` (retention setting), `src/MediaEngine.Contracts/` (`file_missing`, search filter, fixtures), `tests/MediaEngine.Api.Tests`, `tests/MediaEngine.Storage.Tests`, `tests/MediaEngine.Contracts.Tests`.
- **Depends on:** WP2.
- **Size:** Complex.
- **Done when:** tests show Missing items vanish from every list and count; the search filter finds a Missing item by name, note and tag and never one the caller couldn't see (another profile's item, a documents-off profile, over the content limit, Kids rules); purge removes only items past the setting, only in reachable folders, and leaves files untouched; "Never" disables it; Empty Trash on a vanished file succeeds with "already missing"; a returning file before purge restores everything.

### WP4 — Dashboard: search filter, Missing result, and the setting
- **What it does:** The **Missing** filter in search, the dimmed Missing result with "File missing. If you want it back, tell an administrator." and its read-only detail view, the "Folder offline" row in View folders, and the retention setting row (administrators only) with its explanation. No badges or filters on Photos or Documents (D7).
- **Files likely touched:** the search page and components under `src/MediaEngine.Web/Components/`, View folder settings components, `EngineApiClient.View.cs`, `tests/MediaEngine.Web.Tests`.
- **Depends on:** WP2, WP3.
- **Size:** Standard.
- **Done when:** component tests show the filter, the result wording, no actions on a Missing result, the setting editable only by administrators; visual check per `src/MediaEngine.Web/CLAUDE.md` in light and dark at desktop and phone width.

### WP5 — Docs and the batch list
- **What it does:** Updates `docs/architecture/view-personal-media.md`, `docs/guides/view-personal-space.md`, `docs/reference/api-endpoints.md`, `docs/reference/database-schema.md`, `docs/product/presentation-rules.md`, plus the Settings reference page for the retention setting; appends the Local checks to `engineering/validation/batch.md`; updates `engineering/README.md`.
- **Depends on:** WP1 to WP4.
- **Size:** Mechanical.
- **Done when:** the docs build passes and pages match what was built.

## 8. Local checks (for SHY-DESKTOP, appended to the batch list)

1. On a small test View folder (never the real library), delete a photo in File Explorer: within about a minute it disappears from Photos and the Timeline; it is not shown anywhere.
2. Search for its name, then a note or tag on it, with the Missing filter on: it is found, dimmed, with the "tell an administrator" line. With the filter off: not found.
3. As a profile that never had access to that photo, search with the filter on: it is not found.
4. Put the photo back: it reappears with its notes and Gallery spots.
5. Rename a photo in File Explorer: it updates, nothing goes missing.
6. Make the folder unreachable: nothing disappears and the folder says "Folder offline".
7. Set the retention to 1 day on a test library, wait for the purge: the record and its notes are gone, the file (if present elsewhere) untouched.
8. Move a photo to Trash, delete its file outside, Empty Trash: it completes and says "already missing".
9. Contribute a photo to the Shared Library and let the original clean up: the original goes only when expected; made read-only, it stays.

## 9. Risks and open questions

**Risks**
- A watcher can miss events (it has a buffer; the sweep at start and after scans is the backstop).
- Marking Missing after a short network-share hiccup would be alarming; D4 and a short delay before marking (about a minute, rechecked) guard that.
- Hash matching a moved file costs reading the file; only done for files the scan has not seen before in that source.

**Open questions**

None. Shy answered the earlier questions on 2026-10-21 (D7 to D10). One small detail to confirm when building: where in Settings the retention row lives (View settings is the proposal).

## Plain English summary

Empty Trash already works, so the worry that "Trash never frees disk space" is out of date. Two real gaps remain. First, one background job that tidies up after you share a photo with the household removes your original without the usual safety check; it will now use the same check as everything else. Second, if you delete a photo in File Explorer, Library still shows it. From now on that photo quietly disappears from every page. Library remembers it, with its notes, bookmarks and tags, for 90 days (you can change that in Settings, or turn the clean-up off). You can still find it by searching with a "Missing" filter, where it says the file is missing and to ask an administrator if you want it back, and you only ever see what you were allowed to see anyway. If the file comes back, everything comes back with it. After 90 days the leftover record is removed. An unplugged drive never counts as missing. Nothing changes about how Empty Trash works, and Library never deletes your files because they went missing.
