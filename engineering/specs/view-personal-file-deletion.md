# Spec: Deleting personal files in View

> **Status:** Draft · **Version:** 1 · **Last updated:** 2026-10-17 · **Related docs:** [Product overview](../product/overview.md) · [Delete from library](delete-from-library.md) (D22–D24, proposed change #244) · [View personal media (public)](../../docs/architecture/view-personal-media.md) · [Library model and intake (public)](../../docs/architecture/library-model-and-intake.md) ("Personal items") · [Security model (public)](../../docs/architecture/security.md)

> Written 2026-10-17 from Shy's decisions in the Delete from library spec: library media is deleted straight away after a confirmation (D22), but personal files in View can't be downloaded again, so they always go to a recycle bin first (D23), in a spec of their own (D24). View already has a **Trash** that only hides items; nothing in it ever leaves the disk. This spec adds the one step that does: **Empty Trash**. Nothing here is built. Questions Q1–Q9 in section 7 wait for Shy.

## 1. Goal

Someone using View can finally free disk space by getting rid of photos, home videos and documents they no longer want. Deleting stays a two-step act: an item first goes to Trash, where it can still be restored, and it leaves the disk only when its owner empties the Trash and confirms a summary of exactly what will go. Files Tuvima only reads (linked folders) are never touched.

## 2. Scope

**In scope**
- **Empty Trash** for a person's own Personal Space: the only action that removes personal files from disk (D1, D2).
- A summary worked out by the Engine before anything is removed: how many photos, videos and documents, the space freed, which items stay on disk and why, and which Galleries lose items (D7).
- Which files may be removed: only files in **managed** View folders (browser uploads, the Timeline, managed folder imports) beneath the owner's own Personal Space folder, each one checked by the shared source-file safety gate. Files in **linked** read-only folders are never removed (D4, D5).
- **Compound items** (Live Photo, RAW + JPEG + XMP, original + edited) leave as one: every file that belongs to the item goes together, or none does (D6).
- **Generated thumbnails and previews** for a removed item are deleted with it (D6).
- Who may empty a Trash, and what Shared View viewers, Gallery-share viewers, administrators and Kids profiles can and can't do (D3; Q1, Q2).
- Where **Empty Trash** appears in View on desktop and phone (Q5).
- What happens to Galleries, Gallery covers and Collections built on Galleries (D8; Q8).
- How the Trash shows its size, so space held by Trash is never invisible (D9).
- Engine actions for the summary and for Empty Trash, their permission checks, and the tests.

**Out of scope**
- Library media (films, TV, books, audiobooks, music, comics): see [Delete from library](delete-from-library.md).
- **"Delete immediately"** or any way to remove a personal file from disk without it first sitting in Trash (D1).
- Deleting another person's personal files, including by an administrator (D3).
- Native and third-party apps: they get no Empty Trash (D3).
- Noticing files that someone deletes outside Tuvima (View's folder watcher ignores deletions today; section 8).
- Reworking how the Shared Library contribution clean-up removes files (section 8; follow-up).
- Undo after Empty Trash. There is no second recycle bin.

## 3. Decisions made

These follow from Shy's decisions in the Delete from library spec and from View's existing architecture rules. Open choices are in section 7.

- **D1. Trash first, always** (Delete from library D23). Moving an item to Trash stays exactly as it is today: reversible, files untouched, available wherever Trash is offered now. A file leaves the disk only through Empty Trash (or Q4's alternative, if Shy picks it). "Delete immediately" is never offered.
- **D2. Empty Trash is the "separate, explicit operation" the architecture asked for.** The architecture says any permanent file operation must separately prove ownership, source containment, managed/writable policy and a fresh explicit confirmation. Empty Trash does all four: the Engine checks the person owns each item, that each file sits inside that person's own managed folder, that the safety gate allows removing it, and that the person confirmed the exact summary they were shown (D7).
- **D3. Only the owner empties their Trash.** Empty Trash works on the person's own Personal Space only ("Mine"). Following View's rules:
  - **Shared View** and **Gallery-share** viewers can see items but never trash or delete them; the Engine answers as if the item does not exist.
  - **Administrators** get no power to delete someone else's personal files. View's rule is that administrator rights don't become rights over another person's content; an administrator looking into another household is read-only.
  - **Native and third-party apps** can't empty Trash, even with photo access, because their photo permissions are for viewing. Empty Trash needs a signed-in person at the Dashboard.
  - **Shared Library** items (the household's shared originals) and **Kids profiles** are open questions (Q2, Q1).
- **D4. Linked read-only folders are never changed.** Tuvima never deletes, moves or renames a file in a linked folder. What Empty Trash does with such an item is Q6.
- **D5. Every file goes through the safety gate.** The same shared source-file safety gate the library's organising and merge jobs use decides every removal. View doesn't use it today; this spec teaches it about View's managed and linked folders. A file outside the person's own managed folder, or one the gate refuses for any reason, stays on disk.
- **D6. An item leaves as one.** All of an item's files (photo and Live Photo video, RAW, JPEG and XMP, original and edited version, audio companion) go together. If any one of them can't be removed, none is, and the item stays in Trash with the reason. Thumbnails and previews are throwaway copies and are deleted after the item's files. A file on disk that another item still points to (another profile's import of the same folder file, for example) is never removed; matching content (the same photo imported twice) doesn't count as sharing, only the same file on disk does.
- **D7. The Engine works out the summary; the person confirms that exact summary.** Opening Empty Trash asks the Engine for a summary and a fingerprint of it. Confirming sends the fingerprint back. If anything changed in between (an item restored in another window, a new item trashed, a folder switched), the Engine refuses and the dialog shows the new summary. The Engine checks every item and file again just before removing it. This is the same rule as Delete from library D6.
- **D8. Galleries lose the items, never the Gallery.** Emptying removes the items from every Manual Gallery they were in. A Gallery left with nothing in it stays (deleting a Gallery is always separate). A Gallery whose chosen cover is removed falls back to its newest photo, as it already does. Smart Galleries and Collections, which work from rules or reference whole Galleries, simply stop showing the items.
- **D9. The Trash shows its size.** The Trash view shows "N items · 4.2 GB" so space held in Trash is visible. Nothing else changes in Settings.
- **D10. One activity entry per Empty Trash.** The server keeps one activity entry: which profile emptied their Trash, when, how many items, how much space was freed, how many items were left on disk. No file names or thumbnails, because personal media is private.
- **D11. Nothing is left behind in the data store.** Removing an item clears every record tied to it (details, tags, document fields, Gallery memberships, people and place notes, search entries, offline-download entries on paired phones), and the file records once no item uses them.
- **D12. Pending Shared Library contributions are protected.** An item that is waiting to be added to the Shared Library, or is being copied there, is skipped by Empty Trash with "Waiting to be added to the Shared Library; cancel that first." An item already added keeps its contribution history; the history no longer links to the removed item.

## 4. UI behaviour

### Where Trash and Empty Trash are

| Place | Today | After this spec |
|---|---|---|
| Photos selection bar and photo viewer | **Move to Trash** / **Restore** (phone: in the row's More sheet) | Unchanged (D1). |
| Documents bulk bar | **Move to Trash** / **Restore** | Unchanged. |
| Photos → **Trash** filter | Lists trashed items, Restore | Adds a header: "Items in Trash stay on your disk until you empty Trash." · "128 items · 4.2 GB" · **Empty Trash…** (red). |
| Documents → **Trash** | Lists trashed documents, Restore | Same header and **Empty Trash…** (Q7 decides whether it empties documents only or the whole Trash). |
| Folders, Galleries, People, Places | No trash actions | Unchanged. No Empty Trash. |
| Settings | Nothing about Trash | Unchanged, except Q6's "Removed from View" list if Shy picks it. |
| Native apps | No trash actions | Unchanged. |

**Empty Trash…** shows only when the Trash has something in it, the person is looking at their own Personal Space (Mine), and the device rule allows it (Q5 recommends desktop only, using the same desktop-only rule as editing). The Engine checks again on every call.

### The confirmation dialog

Opening it asks the Engine for the summary ("Checking what's in Trash…"). Then:

> **Empty Trash?**
>
> **128 items will be deleted from your disk for good:** 112 photos, 9 videos, 7 documents · 4.2 GB
>
> **Galleries** — 14 of these are in 3 of your Galleries: **Beach 2025**, **Grandma's 80th**, **Receipts**. They'll be taken out.
>
> **Stays on disk** — 6 items are in a folder Tuvima only reads (**Old Phone Backup**). They'll be removed from View, but their files stay where they are.
>
> **Skipped** — 2 items are waiting to be added to the Shared Library.
>
> Photos and documents deleted from your disk can't be recovered. Tuvima has no other copy.
>
> [Cancel] [Delete 128 items from disk]

- **Cancel** has the focus when the dialog opens; Escape cancels.
- Live Photos and RAW + JPEG sets count as one item each; the dialog says "Live Photos and RAW sets count as one item and all their files go together" when any are present.
- Sections with nothing to say are left out. Galleries are sorted A–Z; after five, "and 3 more".
- When every item stays on disk, the button reads **Remove 6 items from View** and the "can't be recovered" line is left out.

| Situation | What the person sees |
|---|---|
| Normal | The dialog above. After confirming, "Deleting… 40 of 128", then the Trash is empty and a toast says "Deleted 128 items. 4.2 GB freed." |
| Trash is empty | No Empty Trash button; the Trash view says "Trash is empty." |
| Something changed since the summary | "Something changed since you opened this. Here's the updated list." Nothing was deleted. |
| An item can't be removed (file in use, no permission, a file the safety gate refuses) | The other items go; that item stays in Trash with a note on its tile ("Couldn't delete: the file is in use"). The toast says "126 deleted, 2 couldn't be deleted and are still in Trash." |
| Engine unreachable | "Couldn't reach the Engine. Nothing was deleted." with Try again. |
| Not allowed (someone else's space, a viewer of a shared Gallery, a Kids profile if Q1 says so, a phone if Q5 says so) | No Empty Trash button. A direct call gets "not found" or "not allowed"; nothing is deleted. |
| A phone has an offline copy of a deleted photo | The phone drops it the next time it checks its download list. |

## 5. Data / model changes

- **No new information about items.** Trash keeps using the existing "trashed at" time on each item.
- **Changed:** Empty Trash removes items, their file records and their thumbnails for good; until now nothing in View was ever removed.
- **Changed:** the links from Shared Library contribution history to the original item no longer block removing that item (D12).
- **New, only if Shy picks Q6 (a):** a list of linked-folder files removed from View, so scans don't add them back, with **Add back** in Settings.
- **New, only if Shy picks Q3 (b) or (c):** a "delete Trash after N days" setting.
- **Existing data:** no reset needed.

## 6. Work packets

Security and data-loss risk: WP1 and WP2 run at high effort with a reviewer pass focused on file safety and authorization. Fable only with Shy's approval (`.claude/rules/model-routing.md`). Packets assume the recommended answers in section 7; Shy's answers may change them.

### WP1 — Engine: View in the safety gate, and the Empty Trash summary
- **What it does:** Teaches `SourceMutationPolicyGate` (`src/MediaEngine.Ingestion/SourceMutationPolicyGate.cs`) about View: a delete request for a file under the managed View root is allowed only when the file's `view_sources.storage_mode` is `managed`, the path is contained (after resolving links) beneath that owner's `Profiles/{storage_label}` folder (`ViewStorageService.GetProfileRoot`), and the file is writable; `linked` sources are always refused. Adds `ViewTrashDeletionPlanner` (`src/MediaEngine.Api/Services/View/`) that, for the caller's Mine scope, lists trashed items (`local_items.trashed_at IS NOT NULL`) with each item's files from `local_item_files` / `local_file_sources`, decides per item `delete` / `keep_linked` / `skip_contribution_pending` / `skip_refused` (from the gate, never from path inspection alone), finds paths another live item still uses (D6), Manual Galleries affected (`view_gallery_items`), counts by media kind, bytes freed and a fingerprint. New Engine action `POST /view/trash/empty-preview`. Authorization: `IViewResourceAuthorizationService` with Mine scope, `Manage` on every item; refused for `PrincipalKind.DelegatedUserClient` and `ServiceApplication` (D3) and, per Q1, for restricted profiles. Contracts in `src/MediaEngine.Contracts/LocalAssets/ViewTrashContracts.cs`: `ViewTrashSummaryDto { item_count, photo_count, video_count, document_count, bytes }`, `ViewTrashEmptyPreviewDto { items[], galleries[], delete_count, keep_on_disk_count, skipped[], bytes_freed, fingerprint }`, with fixtures (`tools/Update-ContractFixtures.ps1`). Also `GET /view/trash/summary` for D9.
- **Files likely touched:** `src/MediaEngine.Ingestion/SourceMutationPolicyGate.cs`, `src/MediaEngine.Ingestion/Contracts/ISourceMutationPolicyGate.cs`, `src/MediaEngine.Api/Services/View/` (new planner), `src/MediaEngine.Api/Services/LocalAssets/ViewStorageService.cs` (expose containment check), `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs` (or a new `ViewEndpoints.Trash.cs`), `src/MediaEngine.Storage/LocalAssetRepository.cs`, `src/MediaEngine.Contracts/LocalAssets/`, `tests/MediaEngine.Ingestion.Tests`, `tests/MediaEngine.Api.Tests`, `tests/MediaEngine.Contracts.Tests`, `src/MediaEngine.Api/DependencyInjection/`.
- **Depends on:** none.
- **Size:** Complex.
- **Done when:** tests pass for: managed upload → `delete`; linked-folder file → `keep_linked`; a path outside the owner's profile folder (including through a symbolic link) → refused; a Live Photo is one item with both files; a path shared with another profile's item is not listed for deletion; pending contribution → `skip_contribution_pending`; another profile's trashed items never appear; a Shared View viewer, a Gallery-share viewer, an administrator of another household, a paired app token and a service application get not-found or 403; the route guardrails (`ViewEndpointRouteTests`) pass.

### WP2 — Engine: Empty Trash
- **What it does:** `POST /view/trash/empty { fingerprint }`, same authorization as WP1. Recomputes the plan; a different fingerprint answers 409 with the new preview. Per `delete` item: re-checks each file with the gate, moves all its files into a hidden staging folder inside the same managed root (excluded from scans and the watcher) so a failure part-way can move them back (D6), deletes them, deletes the thumbnail folder (`AssetPathService.GetCentralDerivedPath("local-item", itemId, …)`), then in one transaction deletes the `local_items` row (cascades cover metadata, files links, tags, document rows, Gallery items, annotations, search keys, device photo downloads), the `local_file_sources` rows for removed paths, and `local_files` rows left with no link. `keep_linked` items are removed from View without touching the file and recorded per Q6. Changes `view_shared_contribution_items` and `view_shared_transfers` references to the item from `RESTRICT` to `SET NULL` (D12; pre-release, a fresh data store is acceptable). Writes one activity row (D10). Returns per-item results; an item that failed stays in Trash with its reason.
- **Files likely touched:** as WP1, plus `src/MediaEngine.Storage/Schema/schema.sql`, a new `ViewTrashDeletionService`, the activity writer, `docs/reference/database-schema.md`.
- **Depends on:** WP1.
- **Size:** Complex.
- **Done when:** tests pass for: stale fingerprint → 409 and nothing changed; managed files and thumbnails gone from disk; linked files untouched; a Live Photo whose video is locked keeps both files and stays in Trash; a file shared with another item stays; no row for the removed item remains in any `local_*`, `doc_*`, `view_gallery_items` or search table (a test walks every table with an item column); a Gallery left empty still exists; a Gallery cover pointing at a removed item falls back; activity row written with count and bytes and no file names; the scanner does not re-add a removed managed item, and does not re-add a removed linked item (Q6 a).

### WP3 — Engine: trashed items leave Galleries while in Trash (Q8)
- **What it does:** Manual Gallery item lists, counts and cover selection skip trashed items (`ViewGalleryRepository.GetItemsAsync`, cover checks), as the timeline, smart Galleries and discovery already do. Memberships are kept so Restore puts them back.
- **Files likely touched:** `src/MediaEngine.Storage/ViewGalleryRepository.cs` (and `src/MediaEngine.Domain/Contracts/IViewGalleryRepository.cs` if a signature changes), `tests/MediaEngine.Storage.Tests`.
- **Depends on:** none.
- **Size:** Standard.
- **Done when:** a trashed item vanishes from its Manual Gallery and its cover, and comes back on Restore.

### WP4 — Dashboard: Trash header and the Empty Trash dialog
- **What it does:** A Trash header in Photos' Trash filter and Documents' Trash mode showing the explanation line and size (WP1 summary) and a red **Empty Trash…** shown only for Mine scope, a non-empty Trash and `EditorDeviceRule.CanEdit` (Q5); the handler checks `CanEdit` again. An `EmptyTrashDialog` built on the shared `App*` dialog primitives showing every section and state in section 4, progress while deleting, 409 refresh, per-item failure notes on tiles. No browser `confirm`.
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/ViewPage.razor`, `src/MediaEngine.Web/Components/Pages/ViewDocumentsPage.razor`, a new `src/MediaEngine.Web/Components/View/EmptyTrashDialog.razor`, `src/MediaEngine.Web/Services/Integration/EngineApiClient.View.cs`, `IEngineApiClient.View.cs`, `tests/MediaEngine.Web.Tests`.
- **Depends on:** WP1, WP2.
- **Size:** Standard.
- **Done when:** component tests show the button absent in Shared scope, on an empty Trash and when `CanEdit` is false; Cancel focused on open; the button label and "can't be recovered" line follow whether any file will be deleted; 409 refreshes. Visual check per `src/MediaEngine.Web/CLAUDE.md` at desktop width in light and dark, and phone width showing no Empty Trash.

### WP5 — Settings: linked files removed from View (Q6 a)
- **What it does:** Each linked source card in Settings → View shows "N files removed from View" with **Add back**, which clears the record and rescans that file. Engine actions to list and clear, owner only.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/ViewLibrarySettings.razor`, `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs`, `src/MediaEngine.Contracts/LocalAssets/`, tests.
- **Depends on:** WP2.
- **Size:** Standard.
- **Done when:** adding back a removed linked file brings it back to the timeline after its scan; another profile gets not-found.

### WP6 — Docs
- **What it does:** `docs/architecture/view-personal-media.md` (Asset states, Galleries, Filesystem and lifecycle safety), `docs/architecture/library-model-and-intake.md` (Personal items: Empty Trash is the permanent-file operation), `docs/reference/api-endpoints.md`, `docs/reference/database-schema.md`, `docs/guides/view-personal-space.md`, `docs/product/presentation-rules.md` (where Empty Trash appears), `docs/reference/glossary.md` (Empty Trash).
- **Depends on:** WP2–WP5.
- **Size:** Mechanical.
- **Done when:** the docs build passes and each page matches what was built.

## 7. Risks and open questions

**Risks**
- **Removing a personal file can't be undone, and there may be no other copy.** The guards are: Trash first (D1), the summary and fresh confirmation (D7), the safety gate and folder containment (D2, D5), all-or-nothing per item (D6), and the WP1–WP2 tests.
- **Shared files on disk.** Two items can point at the same file record; D6 keeps any file another item uses. WP1's test covers it.
- **A paired phone app may be able to move photos to Trash today** (section 8). It can't delete anything from disk, but it should be closed (Q9).
- WP2 changes the data store and adds wire contracts: fixtures need refreshing and conflict with any other branch refreshing them.

**Open questions**

- **Q1. Kids profiles.** Can a Kids profile empty its own Trash?
  - (a) **Recommended:** no. A Kids profile can move to Trash and restore as today, but Empty Trash is hidden and refused. Its Trash is emptied only by Q3's timed clean-up if Shy picks one; otherwise it grows until a grown-up signs in to that profile and empties it.
  - (b) Yes, like anyone else.
  - (c) No, and an administrator can empty a Kids profile's Trash from Settings, seeing only the count and size, never the items. This bends View's rule that administrator rights don't reach another person's content.
- **Q2. Shared Library items** (the household's shared originals under Shared). Today nobody can trash them.
  - (a) **Recommended:** out of this spec. Shared Library items keep no Trash and no delete for now; a follow-up spec adds trash and Empty Trash for household administrators (with the administrator PIN), the same people who accept contributions.
  - (b) In this spec: household administrators can trash and empty Shared Library items.
- **Q3. Does Trash empty itself?**
  - (a) **Recommended:** no. Only Empty Trash removes files. Personal files have no other copy, so nothing leaves the disk without a person confirming it at that moment, which is also what the architecture asks for. D9 keeps the space visible.
  - (b) Yes, after 30 days (as phones do), plus Empty Trash at any time. The Trash header would say "Items are deleted for good 30 days after you trash them." This means files leave the disk without a fresh confirmation.
  - (c) A per-profile setting, off by default, with 30, 60 or 90 days.
- **Q4. Only the whole Trash, or chosen items too?**
  - (a) **Recommended:** only the whole Trash. To keep something, restore it first. One button, one summary, simplest to get right.
  - (b) Also **Delete from disk** for selected items inside Trash, with the same dialog for just those items.
- **Q5. Phones.** Moving to Trash and restoring already work on a phone.
  - (a) **Recommended:** Empty Trash only on a desktop (the same desktop-only rule as editing). On a phone the Trash shows its size and "Empty Trash on a computer." Fewer accidental taps on a step that can't be undone.
  - (b) Empty Trash on phones too, with the same dialog.
- **Q6. Items from linked read-only folders.** Their files can never be removed. What does Empty Trash do with them?
  - (a) **Recommended:** remove them from View and leave the file; Tuvima remembers it so scans don't add it back, and **Add back** on the folder in Settings undoes it (WP5). Matches the library's Excluded files (Delete from library D15).
  - (b) Leave them in Trash forever with "Lives in a read-only folder."
  - (c) Don't let them be trashed at all; offer Hide or Archive instead.
- **Q7. Photos and documents share one Trash.** Each Personal Space has one Trash; Photos and Documents each show their part of it.
  - (a) **Recommended:** Empty Trash empties the whole Trash from either place, and the dialog lists photos, videos and documents separately so nothing is a surprise.
  - (b) Each place empties only what it shows (Photos empties photos and videos, Documents empties documents).
- **Q8. While an item is in Trash, does it still show in Galleries?** Today the timeline hides it but Manual Galleries and their covers still show it.
  - (a) **Recommended:** hide it from Galleries and covers while in Trash, and bring it back on Restore (WP3).
  - (b) Leave Galleries as they are.
- **Q9. The paired-app gap** (section 8). The Engine appears to let a paired phone app with view-only photo access move a person's photos to Trash.
  - (a) **Recommended:** fix it now in its own small change, before this spec, with a test, because it's live today.
  - (b) Fix it inside WP1.

## 8. Gaps and drift

Found while writing this spec (2026-10-17):

- **Trash never removes anything.** Trash and Restore only set or clear the item's trashed time; the documents bulk action says so in its own description. There is no purge, Empty Trash or timed clean-up, so space used by trashed items is never freed.
- **Paired apps and Trash (to verify).** The trash, archive and restore actions ask for "manage" rights on the item, but for a paired app the Engine maps that to the view-only photo permission and doesn't exclude item changes for apps the way it does for folders and preferences. A paired app with photo access may therefore be able to trash or archive photos. No native app does this today. Q9.
- **View doesn't use the safety gate.** The Shared Library contribution clean-up removes managed originals after checking both copies match, but without the shared safety gate or a folder containment check. Worth a follow-up so every file removal goes through one gate.
- **Manual Galleries still show trashed items** and can keep one as cover (Q8, WP3).
- **Deleted files aren't noticed.** View's folder watcher listens for new, changed and renamed files only, and scans only add. A file deleted outside Tuvima leaves its item in View, showing as missing when opened. Out of scope; worth its own spec.
- **No live update for View changes.** Other open windows see trash and delete changes on their next refresh.
