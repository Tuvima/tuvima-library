# Spec: Delete a show, season, album or Series

> **Status:** Built, awaiting your Local checks (Waves 1 and 2 merged; D21 keeps progress, bookmarks and listening history by file fingerprint; the photo grid D16 was dropped by Shy because only home videos use the bin; ratings and queue entries are not kept) · **Version:** 7 · **Last updated:** 2026-10-21 · **Related docs:** [Product overview](../../product/overview.md) · [Delete from library (archived, D14, D16 to D22)](delete-from-library.md) · [Details overlay and edit mode (D5, D17, D18)](../details-overlay-and-edit-mode.md) · [Videos (D49 recycle bin setting)](../videos.md) · [Universes](../universe.md) · [Presentation rules](../../../docs/product/presentation-rules.md)

> Written 2026-10-21 after Shy said a delete for seasons and series should exist by default because "it's pretty normal and expected behavior". The Delete from library spec (D14) deliberately covered titles and songs only and left this as a follow-up. This spec is that follow-up. Version 2 records Shy's answer to Q1 (D11) and the large-group summary rule (D3). Version 3 adds type-to-confirm for the biggest deletes (D4, Q2). Version 4 adds the visible Recycle bin (D12 to D20, WP4); version 7 adds the "leaving soon" reminder (D23); version 6 records Q3 and Q4 as D21 and D18 (progress is kept while an item is in the bin, and the bin empties itself after 90 days) at Shy's request, which widens the scope beyond group delete.

## 1. Goal

An administrator at a desktop can remove a whole group in one go: a TV show, one season of a show, an album, or a Series of books or comics. They see exactly what will go before anything happens, the same way they do for one title, so clearing out a show they are finished with no longer means deleting forty episodes one at a time.

## 2. Scope

**In scope**
- **Delete show**, **Delete season**, **Delete album** and **Delete Series** (books, comics, audiobooks), each started from that group's own page.
- Reusing the Delete from library dialog, safety rules and clean-up, extended to a list of titles with a group summary.
- Respecting the per-media-type **Recycle bin** checkbox (Videos D49).
- Live update on every open screen and device.

**Out of scope**
- Deleting a Universe. A Universe is a grouping across formats; it has no files of its own and is never deleted from here.
- Deleting a hand-picked collection. That already exists in the collection editor and only removes the grouping, never the titles in it.
- Deleting one Edition or one file, multi-select on browse shelves, and personal items in View (separate specs).
- Phones, tablets and touch-only devices, and non-administrators: no Delete anywhere (unchanged rule).
- Undo. Only the recycle bin, where a type has it turned on.

## 3. Decisions made

- **D1. Same rules as one title.** Everything in Delete from library (D2 administrators on a desktop, D5 original source media stay protected, D6 the Engine previews and re-checks, D7 to D11 personal state, Needs Review, activity record, excluded files and full clean-up) applies to every title in the group. Reason: Reliability and Uniformity; no second, looser delete.
- **D2. Where Delete lives.** The last red entry of the page's "..." menu (and the editing banner's "..." in Editing Mode), exactly as for a single title (Details overlay D5, D18). Show and album: on their own page. **A season has no page of its own** (it opens inside its show), so **Delete season** sits in the "..." menu of the show page while a season is selected in the season control, and the entry names the season ("Delete Season 2…"). Series: on the Series page.
- **D3. The dialog shows the group, not just a count.** Heading names the group and what is inside it ("Delete "The Expanse", Season 2 from your library?"). Up to 12 titles are listed by name in a short scrollable list (episode names, tracks, books). Above that the list is replaced by **totals broken out by kind** (Shy, 2026-10-21), so the person knows not just how many but what: "2 seasons · 38 episodes", "1 show · 5 seasons · 212 episodes", "14 books in 3 sub-series", "1 album · 18 tracks across 2 discs". The same applies to files ("286 files in 4 folders, 142 GB") and to people with progress. Partial totals say what is missing ("Season 3: 6 of 10 episodes owned"). The files grouped by folder with the existing "will be deleted / stays on disk" marking, total space freed, hand-picked collections affected (D3 and D4 of the Delete spec, now counted per collection: "Road Trip (4 of these)"), and how many people have progress. Sections with nothing to say are left out.
- **D4. The button states the size, and the biggest deletes also ask for the name** (Shy, 2026-10-21: some deletes should need a typed name). The red button always counts, for example **Delete 10 episodes and 10 files**, or **Delete 14 books** when every file stays on disk. Cancel has the focus; Escape cancels. **Type-to-confirm applies when files will be deleted from disk and the delete is any of:** a whole show; a whole Series; any group of more than 25 titles; or more than 50 GB freed. Under that line (one season, one album, a short Series of a few titles) the counted button is enough. The dialog then shows "Type **The Expanse** to confirm" with a text box; the red button stays disabled until it matches (ignoring capital letters and extra spaces). The name is the group's own title as shown in the heading (a season asks for "Season 2"). With the Recycle bin on (D7), or when every file stays on disk, nothing is destroyed for good, so no typing is asked. The numbers (25 titles, 50 GB) are settings in the Engine's config, not fixed in code. Reason: Reliability; typing makes the largest, unrecoverable deletes deliberate without slowing everyday ones.
- **D5. Partial ownership.** Only what the library owns is deleted. A season with 4 of 10 episodes owned deletes those 4; the dialog says "4 episodes in your library". Missing (not owned) episodes, tracks or books are never counted.
- **D6. Deleting everything inside deletes the group.** Deleting a show removes its seasons and episodes. Deleting a season leaves the show and its other seasons; if it was the last season, the show goes too (the dialog says so before you confirm). Deleting an album removes its tracks. An artist with no tracks left has no page, as today.
- **D7. Recycle bin follows the type setting.** Where a library type has **Recycle bin** on (Videos by default, D49), group deletes move files to the bin and the dialog says so; elsewhere deletion is immediate and the dialog says "can't be recovered".
- **D8. One action, one result.** The Engine deletes the whole group as one planned action with one preview fingerprint (Delete D6). If anything changed since the preview, nothing is deleted and the dialog refreshes. After confirming, the person sees one progress state ("Deleting 10 episodes…") and one toast ("Deleted "The Expanse", Season 2. 18.4 GB freed.").
- **D9. A file that can't be removed does not stop the rest.** Titles whose files can be removed are deleted; a file that is in use or refused stays on disk, is excluded from scans (Delete D10) and the toast says how many were left, same as single-title delete.
- **D10. One activity entry for the group.** The server activity record names the group, the number of titles and files, space freed and files left on disk, in one line, so the log stays readable.
- **D12. The Recycle bin is visible on every library page** (Shy, 2026-10-21). A small **Recycle bin** entry sits at the bottom left of each library page, showing the total size and item count for that library (for example "Recycle bin · 38 items · 24 GB"). Shown only for library types that have the Recycle bin checkbox on (D7); hidden when empty and off. Everyone can see it and open it.
- **D13. Only administrators can empty it or restore from it.** Empty bin and Restore are administrator-only, desktop only, checked by the Engine. Emptying asks for a confirmation with the total ("Permanently delete 38 items and free 24 GB? This can't be undone.") and is the only step that removes files for good.
- **D14. What a person sees inside depends on what they may see.** Everyone sees the total size. The contents are listed only for items the profile could already see in the library (its age limit and library access apply). Everything else is rolled into one line: "Other media: 12 items, 9.1 GB", with no titles, covers or kinds that would reveal what it is. A profile with documents off never learns a document exists, so documents it cannot see go into "Other media" too, never a separate "Documents" line. Reason: Privacy; this keeps content limits intact (Kids default PG).
- **D15. Anyone can ask to keep an item.** On a listed item, **Ask to keep** sends a request to the administrators (it appears in their notifications and in the bin as "Requested by Mia"). An administrator can **Restore** (the item returns exactly as it was, with every profile's progress, bookmarks and ratings intact, D21) or dismiss the request. The person is told the answer. A request is only possible while the item is still in the bin.
- **D16. Photos, documents and other detailed files get an easier view.** Large or visually detailed items (View photos, home videos, documents) show as a thumbnail grid with a preview on open, grouped by day deleted, with filters (type, who deleted it, size) and select-many for Restore or Ask to keep, instead of the plain list used for titles. Titles, shows and albums stay as grouped rows (a deleted show appears once with its totals, as in D3, and expands to episodes).
- **D17. Each entry shows who deleted it and when**, and the space it holds; nothing in the bin counts toward library totals or search, and nothing in it plays or is scanned again.
- **D18. The bin empties itself after 90 days** (Shy, 2026-10-21), counted from the day each item was deleted. The number of days is a setting (Settings > Libraries, beside the Recycle bin checkbox; 90 by default, with a "Never" choice). The bin panel shows each item's "Leaves in 34 days", and a warning chip appears on items with under 7 days left. The automatic purge is the same permanent step as Empty and is written to the activity history (one line per run: items, files, space freed). This closes the gap that View's Trash never freed space. Administrators can still Empty early (D13) or Restore.
- **D23. A "leaving soon" reminder so the bin is never forgotten** (Shy, 2026-10-21). When items will be permanently deleted within 7 days, administrators see a notice with the count and size: "12 items (8.4 GB) will be deleted from the Recycle bin in 7 days", with **Review** (opens the bin sorted soonest first, on everything) and **Keep them** (Restore all). It appears in two places: a card at the top of the administrator's Settings > Overview, and a line with a badge in the top bar's Settings menu under the Recycle bin entry (D20). It shows from 7 days out, repeats daily with the right number ("in 3 days", then "tomorrow", then "today"), and disappears when nothing is leaving soon. Dismissing the card hides it for the day only. The lead time (7 days) is a setting beside the days setting. Other profiles see no notice, but their bin view shows "Leaves in N days" on items they can see and they can Ask to keep (D15). Reason: Reliability; a permanent delete should never be a surprise.
- **D21. Progress is kept while an item is in the bin** (Shy, 2026-10-21). For library types with the Recycle bin on, deleting an item keeps every profile's progress, bookmarks, highlights, ratings and queue entries for it, hidden from Continue lists, shelves and search while it sits in the bin. **Restore** brings them all back so an accident costs nobody their place. When the item leaves the bin for good (Empty, Delete now or the 90-day purge) all that personal data and the item's remaining records are removed (Delete D7, D8, D11). Amends Delete from library D7 for types with the bin on; with the bin off, personal data goes at delete time as before. The confirmation dialog's progress line changes to "2 people have progress on this. It's kept for 90 days in the Recycle bin."
- **D19. Opening the bin filters to where you came from, with a one-tap switch to everything** (Shy, 2026-10-21). From a library page, the bin opens already filtered to that page (Movies page shows deleted movies; a Books page, deleted books). A clear chip at the top, "Showing: Movies · **Show all**", switches to every item in the bin in place, and back again, without leaving or reloading the panel. Opened from the global entry (D20) it starts on everything. The filter choice is kept in the address, so reload and Back keep your place.
- **D20. The bin is reachable from anywhere** (Shy, 2026-10-21). The top bar's Settings menu has a **Recycle bin** entry showing the total ("Recycle bin · 24 GB"), for everyone, on every page. It opens the same bin on everything. It follows the same visibility rules (D12, D14) and shows even when the bin is empty ("Recycle bin is empty").
- **D11. Deleting a Series deletes the books, comics or audiobooks in it** (Shy, 2026-10-21: "agree on deleting everything - with proper warnings"). The Series goes with them. The dialog warns in plain words, with the totals from D3 ("This deletes the Series and all 14 books in it"), plus the standard file, collection and progress warnings.

## 4. UI behaviour

### Where Delete is

| Page | Entry | What it deletes |
|---|---|---|
| TV show | "..." › **Delete show…** | all owned seasons and episodes |
| TV show, a season selected | "..." › **Delete Season N…** | that season's owned episodes |
| Album | "..." › **Delete album…** | all owned tracks |
| Series (books, comics, audiobooks) | "..." › **Delete Series…** | per Q1 |

Administrators on a desktop with a fine pointer only; the Engine checks every call.

### The Recycle bin entry and view

- **Entry:** bottom left of every library page whose type has the Recycle bin on, "Recycle bin · 38 items · 24 GB" (nothing shown when empty). Opens a panel (desktop), filtered to that page (D19), or full page (phone and tablet, view and Ask to keep only).
- **Administrator:** sees everything, with **Restore**, **Empty recycle bin** and per-item **Delete now**.
- **Everyone else:** sees allowed items, a single "Other media" line with count and size for the rest (D14), and **Ask to keep** on listed items.
- **Filter chip:** the bin opens filtered to the page you came from, with "Show all" one tap away (D19).
- **Global entry:** a Recycle bin item in the top bar's Settings menu opens it on everything (D20).
- **Photos, documents, home videos:** thumbnail grid with preview, day grouping, filters and select-many (D16).

| Situation | What the person sees |
|---|---|
| Items leaving within 7 days (administrator) | Overview card and Settings-menu badge: "12 items (8.4 GB) will be deleted in 7 days · Review · Keep them" |
| Bin empty | Library-page entry hidden; Settings menu entry says "Recycle bin is empty" |
| Opened from a library page | Filtered to that page's type; chip "Showing: Movies · Show all" |
| Show all tapped | Every visible item, chip reads "Showing: All media · Only Movies" |
| Child profile, 20 items, 6 above its age limit | 14 items listed, "Other media: 6 items, 3.2 GB", total size 24 GB |
| Ask to keep sent | Item shows "Requested"; administrators are notified |
| Request answered | Toast/notification: "Restored" or "Not restored" |
| Another admin empties the bin | Open panels update live to "Recycle bin emptied" |
| Engine unreachable | "Couldn't reach the Engine." with Try again |

### The dialog (show example)

> **Delete "The Expanse" from your library?**
>
> **10 episodes in 2 seasons**, S1 E1 to S2 E6 *(list expands)*
> **Files**
> - TV › The Expanse › Season 1 — 10 files, 24.1 GB — *will be deleted from disk*
> - Old Rips › Expanse.S02.mkv — *stays on disk: Tuvima only reads this folder*
>
> **Hand-picked collections** — Comfort Shows (the whole show).
> **Progress** — 2 people have progress on this. It's kept for 90 days in the Recycle bin (otherwise removed).
>
> Deleting frees 24.1 GB. Files deleted from disk can't be recovered.
>
> [Cancel] [Delete 10 episodes and 10 files]

| Situation | What the person sees |
|---|---|
| Normal | Dialog above; after confirming "Deleting 10 episodes…", then the page closes back to the shelf and a toast reports the result |
| Preview is slow (large group) | "Checking what will be deleted…" with a spinner; Delete stays disabled until it finishes |
| Nothing owned (all missing) | Entry not shown |
| Last season of a show | Dialog adds "This is the last season, so the show will be removed too." |
| Some files protected or in as-is folders | Those rows say they stay on disk; button drops "and disk" if none are deleted |
| Something changed since the preview | "Something changed since you opened this. Here's the updated list." Nothing deleted |
| Some files can't be removed | Rest is deleted; toast says "3 files couldn't be removed and were left on disk." |
| Engine unreachable | "Couldn't reach the Engine. Nothing was deleted." with Try again |
| Not allowed | "You need administrator access to delete items." |
| Someone is playing something in it | Allowed; their player stops with "This item is no longer in your library." |
| Still processing | Allowed; dialog notes processing will stop |

Everywhere else, open screens drop the group live, without a reload.

## 5. Data / model changes

- **None to the shape of the data store.** Reuses the excluded-files list, activity record and clean-up from Delete from library.
- **Contracts:** the delete preview and delete action take a list of titles and a group label (the song delete already takes a list).
- Existing libraries: no reset needed.

## 6. Work packets

### WP1 — Engine: group preview and delete
- **What it does:** Resolves a show, season, album or Series (per Q1) to its owned titles, builds one combined preview (titles, files, collections, progress, space) with one fingerprint, and deletes the whole set as one action with one activity entry; applies the D6 last-season rule and the per-type recycle bin.
- **Files likely touched:** `src/MediaEngine.Api/Services/Libraries/` (the existing deletion service), `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs`, `src/MediaEngine.Contracts/`, `tests/MediaEngine.Api.Tests` (to be confirmed)
- **Depends on:** none
- **Size:** Complex
- **Done when:** tests show a 10-episode season previews 10 titles and deletes them under one fingerprint; a changed library between preview and confirm refuses; as-is files stay; the last season removes the show; one activity entry is written.

### WP2 — Dashboard: entries and group dialog
- **What it does:** Adds the four menu entries (D2) and extends the shared dialog with the group summary, counted button and expandable title list (D3, D4).
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/` (menu and banner), `src/MediaEngine.Web/Components/Shared/LibraryItemDeletionFlow.cs`, `tests/MediaEngine.Web.Tests`
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** component tests show the entries only for desktop administrators, the season entry names the selected season, and the button text counts titles and files.

### WP3 — Live update, docs and guardrail
- **What it does:** Open screens drop the group live; docs updated (presentation rules, API reference); a guardrail test that every group delete goes through the shared preview.
- **Files likely touched:** `src/MediaEngine.Web/`, `docs/product/presentation-rules.md`, `docs/reference/api-endpoints.md`, `tests/`
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** a second open window drops the deleted show without reload; docs no longer say "A whole show, season, album or series cannot be deleted yet".

### WP4 — Recycle bin entry, views and requests
- **What it does:** Bottom-left entry on library pages, the panel with the administrator and limited views (D12 to D17), the filter chip and Settings-menu entry (D19, D20), the leaving-soon card and badge (D23), the thumbnail grid for photos and documents, Ask to keep requests and notifications, the 90-day automatic purge as a durable background job with a days setting (D18), keeping and restoring progress (D21), Restore and Empty (administrator only), with the Engine filtering by profile access so aggregated items never leak titles or kinds.
- **Files likely touched:** `src/MediaEngine.Api/` (recycle bin read and request actions), `src/MediaEngine.Contracts/`, `src/MediaEngine.Web/Components/` (library page shell, new panel and grid), View's existing Trash code, `tests/` (to be confirmed)
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** tests show a Kids profile gets only PG-or-lower items plus an aggregate with no titles; a profile with documents off gets no document hint; Ask to keep reaches administrators; only administrators can Restore or Empty; the thumbnail grid renders photos at 100+ items.

## 7. Local checks

- **L1.** On a small test library with a show of two seasons: delete Season 2 from the show page. The dialog lists its episodes and files; Season 1 stays; the toast reports space freed.
- **L2.** Delete the last remaining season: the dialog warns the show goes too.
- **L3.** Delete an album and a Series on real files in `C:\Temp\real_import` copies (never originals): files leave the disk, as-is folders keep theirs.
- **L4.** Turn **Recycle bin** on for TV and repeat L1: files go to the bin, and the dialog says so.
- **L5.** Delete a whole show: the dialog asks you to type its name, and the red button stays off until it matches; delete one small season: no typing.
- **L6.** Move a few items to the Recycle bin, then open it as a Kids profile: only allowed items are named, the rest read "Other media" with a total; Ask to keep reaches your admin profile.
- **L7.** Open the bin from the Movies page: only movies, with a "Show all" chip that reveals the rest. Open it from the top bar Settings menu on any page: everything.
- **L8.** Delete a film you have part-watched with the bin on, then Restore it: it resumes at the same spot for each profile. Set the days to 1 and confirm the item leaves on its own and its progress is gone.
- **L9.** Set the days to 8 and delete a film: after a day the Overview card and the Settings-menu badge say it leaves tomorrow; Review opens the bin sorted soonest first; Keep them restores it.
- **L10.** Phone and non-administrator profile: no Delete entry anywhere.

## 8. Risks and open questions

- **Risks:** a bigger blast radius per click (a whole show or Series), so the preview must be clear and complete; large groups make the preview slow (show a spinner, do not guess); the Details overlay build (WP7/WP8) is reshaping the same menus, so whoever builds second adapts to the other.
- **Open questions:**
  - **Q1. Answered (Shy, 2026-10-21):** delete everything in the Series, with proper warnings (D11).
  - **Q3. Answered (Shy, 2026-10-21):** progress is kept while an item is in the bin and removed when it leaves for good (D21).
  - **Q4. Answered (Shy, 2026-10-21):** the bin empties itself after 90 days by default, configurable (D18).
  - **Q2. Answered (Shy, 2026-10-21):** type-to-confirm as in D4 (whole show, whole Series, over 25 titles or over 50 GB, only when files are destroyed). Locked by Shy on 2026-10-21; no open questions.

## Plain English summary

This adds Delete to show, season, album and Series pages for administrators on a desktop, using the same preview and safety rules as deleting one title: you see every title, file and space freed, and original files in protected folders are never touched. A season has no page of its own, so Delete Season sits in the show page's menu once you pick the season. Deleting a Series deletes the books in it, with a clear warning. When a group is too big to list (say 300 episodes), the dialog shows totals broken out by kind, such as "2 seasons, 38 episodes" or "14 books", so you know what is going, not just how many.
