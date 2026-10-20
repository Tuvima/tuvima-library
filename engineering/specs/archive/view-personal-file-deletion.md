# Spec: Deleting personal files in View

> **Status:** Built · **Version:** 3 · **Last updated:** 2026-10-20 · **Related docs:** [Product overview](../../product/overview.md) · [Delete from library](delete-from-library.md) (D22–D24, proposed change #244) · [View personal media (public)](../../../docs/architecture/view-personal-media.md) · [Library model and intake (public)](../../../docs/architecture/library-model-and-intake.md) ("Personal items") · [Security model (public)](../../../docs/architecture/security.md)

> Written 2026-10-17 from Shy's decisions in the Delete from library spec: library media is deleted straight away after a confirmation (D22), but personal files in View can't be downloaded again, so they always go to a recycle bin first (D23), in a spec of their own (D24). View already has a **Trash** that only hides items; nothing in it ever leaves the disk. This spec adds the one step that does: **Empty Trash**. Version 2 (same day) records Shy's answers to Q1–Q9 as D13–D21, and version 3 the answer to Q10 as D22. No questions are left. Shy Locked it on 2026-10-18. Built 2026-10-20 (Shy's answer on failed Shared Library copies recorded as D23; as-built notes at the end of section 8).

## 1. Goal

Someone using View can finally free disk space by getting rid of photos, home videos and documents they no longer want. Deleting stays a two-step act: an item first goes to Trash, where it can still be restored, and it leaves the disk only when its owner empties the Trash on a computer and confirms a summary of exactly what will go. Items from folders Tuvima only reads can't be trashed or deleted at all: they are there to look at.

## 2. Scope

**In scope**
- **Empty Trash** for a person's own Personal Space, on a desktop: the only action that removes personal files from disk (D1, D2, D17).
- A summary worked out by the Engine before anything is removed: how many photos, videos and documents, the space freed, anything skipped and why, and which Galleries lose items (D7).
- Which files may be removed: only files in **managed** View folders (browser uploads, the Timeline, managed folder imports) beneath the owner's own Personal Space folder, each one checked by the shared source-file safety gate (D4, D5).
- Taking **Move to Trash** away from items in **linked** read-only folders (D18).
- **Compound items** (Live Photo, RAW + JPEG + XMP, original + edited) leave as one: every file that belongs to the item goes together, or none does (D6).
- **Generated thumbnails and previews** for a removed item are deleted with it (D6).
- Who may empty a Trash: the owner, Kids profiles included; not Shared View viewers, Gallery-share viewers, administrators or apps (D3, D13).
- What happens to Galleries, Gallery covers and Collections built on Galleries, both while an item is in Trash and when it is removed (D8, D20).
- How the Trash shows its size, so space held by Trash is never invisible (D9).
- Engine actions for the summary and for Empty Trash, their permission checks, and the tests.

**Out of scope**
- Library media (films, TV, books, audiobooks, music, comics): see [Delete from library](delete-from-library.md).
- **"Delete immediately"**, deleting chosen items from inside Trash, or any other way to remove a personal file from disk except emptying the whole Trash (D1, D16).
- A Trash that empties itself after a period (D15).
- **Shared Library** items: the household's shared photos and documents (the Shared Timeline and named Shared folders, which people contribute to and a household administrator accepts). Nobody can trash or delete them; that is a later spec (D14).
- Deleting another person's personal files, including by an administrator (D3).
- Empty Trash on a phone or tablet, and in native and third-party apps (D3, D17).
- Noticing files that someone deletes outside Tuvima (View's folder watcher ignores deletions today; section 8).
- Reworking how the Shared Library contribution clean-up removes files (section 8; follow-up).
- Undo after Empty Trash. There is no second recycle bin.

## 3. Decisions made

D1–D12 follow from Shy's decisions in the Delete from library spec and from View's existing architecture rules. D13–D22 are Shy's answers to Q1–Q10 (2026-10-17).

- **D1. Trash first, always** (Delete from library D23). Moving an item to Trash stays reversible and never touches the file. A file leaves the disk only through Empty Trash. "Delete immediately" is never offered.
- **D2. Empty Trash is the "separate, explicit operation" the architecture asked for.** The architecture says any permanent file operation must separately prove ownership, source containment, managed/writable policy and a fresh explicit confirmation. Empty Trash does all four: the Engine checks the person owns each item, that each file sits inside that person's own managed folder, that the safety gate allows removing it, and that the person confirmed the exact summary they were shown (D7).
- **D3. Only the owner empties their Trash.** Empty Trash works on the person's own Personal Space only ("Mine"). Following View's rules:
  - **Shared View** and **Gallery-share** viewers can see items but never trash or delete them; the Engine answers as if the item does not exist.
  - **Administrators** get no power to delete someone else's personal files. View's rule is that administrator rights don't become rights over another person's content; an administrator looking into another household is read-only.
  - **Native and third-party apps** can't empty Trash, even with photo access, because their photo permissions are for viewing. Empty Trash needs a signed-in person at the Dashboard.
- **D4. Only files Tuvima manages can be removed.** Tuvima never deletes, moves or renames a file in a linked folder (D18).
- **D5. Every file goes through the safety gate.** The same shared source-file safety gate the library's organising and merge jobs use decides every removal. View doesn't use it today; this spec teaches it about View's managed and linked folders. A file outside the person's own managed folder, or one the gate refuses for any reason, stays on disk.
- **D6. An item leaves as one.** All of an item's files (photo and Live Photo video, RAW, JPEG and XMP, original and edited version, audio companion) go together. If any one of them can't be removed, none is, and the item stays in Trash with the reason. Thumbnails and previews are throwaway copies and are deleted after the item's files. A file on disk that another item still points to is never removed; matching content (the same photo imported twice) doesn't count as sharing, only the same file on disk does.
- **D7. The Engine works out the summary; the person confirms that exact summary.** Opening Empty Trash asks the Engine for a summary and a fingerprint of it. Confirming sends the fingerprint back. If anything changed in between (an item restored in another window, a new item trashed), the Engine refuses and the dialog shows the new summary. The Engine checks every item and file again just before removing it. This is the same rule as Delete from library D6.
- **D8. Galleries lose the items, never the Gallery.** Emptying removes the items from every Manual Gallery they were in. A Gallery left with nothing in it stays (deleting a Gallery is always separate). A Gallery whose chosen cover is removed falls back to its newest photo, as it already does. Smart Galleries and Collections, which work from rules or reference whole Galleries, simply stop showing the items.
- **D9. The Trash shows its size.** The Trash view shows "N items · 4.2 GB" so space held in Trash is visible, on every device. Nothing else changes in Settings.
- **D10. One activity entry per Empty Trash.** The server keeps one activity entry: which profile emptied their Trash, when, how many items, how much space was freed, how many were skipped. No file names or thumbnails, because personal media is private.
- **D11. Nothing is left behind in the data store.** Removing an item clears every record tied to it (details, tags, document fields, Gallery memberships, people and place notes, search entries, offline-download entries on paired phones), and the file records once no item uses them.
- **D12. Pending Shared Library contributions are protected.** An item that is waiting to be added to the Shared Library, or is being copied there, is skipped by Empty Trash with "Waiting to be added to the Shared Library; cancel that first." An item already added keeps its contribution history; the history no longer links to the removed item.

Shy's answers (2026-10-17):

- **D13. (Q1) Kids profiles empty their own Trash like anyone else** (Shy: "Yes, but the only thing they can trash/restore are personal items of their own - not the shared library"). A Kids profile can move its own personal items to Trash, restore them and empty its Trash, with the same rules and dialog as everyone. It can never trash, restore or delete Shared Library items.
- **D14. (Q2) Shared Library items stay out of this spec.** Nobody can trash or delete them for now; a later spec can add that for household administrators.
- **D15. (Q3) Trash never empties itself.** Only Empty Trash removes files, so nothing leaves the disk without the owner confirming it at that moment.
- **D16. (Q4) Empty Trash empties the whole Trash.** There is no deleting chosen items from inside Trash. To keep something, restore it first.
- **D17. (Q5) No deleting on a phone** (Shy: "no deleting on a phone either makes this easier"). **Empty Trash** appears only on a desktop with a fine, hover-capable pointer, the same desktop-only rule as editing; the Engine refuses it from anything but a Dashboard session. On a phone or tablet the Trash shows its size and "Empty Trash on a computer." Phones keep Move to Trash and Restore (D22).
- **D18. (Q6) Read-only folders are for looking at, not deleting** (Shy: "if it's read only, then you can't delete anything. It's just for consumption."). Items whose files are in a linked read-only folder can't be moved to Trash: the button isn't shown for them and the Engine refuses. A selection that mixes them with other items trashes the others and says "3 items are in a read-only folder and weren't moved to Trash." Favorite, Archive, Hide and Galleries still work for them, because those never touch the file. A compound item counts as read-only if any of its files is in a linked folder. Linked items already in Trash before this is built can be restored; Empty Trash skips them ("In a read-only folder").
- **D19. (Q7) One Trash for photos and documents.** Each Personal Space has one Trash; Photos and Documents each show their part of it. Empty Trash from either place empties the whole Trash, and the dialog lists photos, videos and documents separately so nothing is a surprise.
- **D20. (Q8) Trashed items leave Galleries while in Trash.** A trashed item disappears from Manual Galleries and can't be a Gallery's cover; Restore brings it back where it was.
- **D21. (Q9) The paired-app gap is fixed separately, first.** The possible gap that lets a paired phone app move photos to Trash (section 8) is checked and fixed in its own small change before this spec is built.
- **D22. (Q10) Phones keep Move to Trash and Restore.** Only Empty Trash needs a computer. Nothing leaves the disk from a phone, and Trash can always be undone.
- **D23. Only in-flight Shared Library work protects an item** (Shy, 2026-10-20). D12 applies to items whose contribution is still pending, or whose transfer is waiting, planned, being copied or being cleaned up. A copy that failed, needs a household administrator's attention, was cancelled, declined or completed no longer holds the item back: Empty Trash deletes the person's own copy, and the history stays without a link to it.

## 4. UI behaviour

### Where Trash and Empty Trash are

| Place | Today | After this spec |
|---|---|---|
| Photos selection bar and photo viewer | **Move to Trash** / **Restore** (phone: in the row's More sheet) | Unchanged for managed items, phones included (D22). Not shown for items in a read-only folder (D18). |
| Documents bulk bar | **Move to Trash** / **Restore** | Same as above. |
| Photos → **Trash** filter | Lists trashed items, Restore | Adds a header: "Items in Trash stay on your disk until you empty Trash." · "128 items · 4.2 GB" · **Empty Trash…** (red, desktop only). |
| Documents → **Trash** | Lists trashed documents, Restore | Same header and **Empty Trash…**, which empties the whole Trash (D19). |
| Trash on a phone or tablet | Lists trashed items, Restore | The header with its size and "Empty Trash on a computer." No Empty Trash button (D17). |
| Folders, Galleries, People, Places | No trash actions | Unchanged. No Empty Trash. |
| Settings | Nothing about Trash | Unchanged. |
| Native apps | No trash actions | Unchanged. |

**Empty Trash…** shows only when the Trash has something in it, the person is looking at their own Personal Space (Mine), and the desktop-only rule allows it. The Engine checks again on every call.

### The confirmation dialog

Opening it asks the Engine for the summary ("Checking what's in Trash…"). Then:

> **Empty Trash?**
>
> **128 items will be deleted from your disk for good:** 112 photos, 9 videos, 7 documents · 4.2 GB
>
> **Galleries** — 14 of these were in 3 of your Galleries: **Beach 2025**, **Grandma's 80th**, **Receipts**. They'll be taken out for good.
>
> **Skipped** — 2 items are waiting to be added to the Shared Library. They stay in Trash.
>
> Photos and documents deleted from your disk can't be recovered. Tuvima has no other copy.
>
> [Cancel] [Delete 128 items from disk]

- **Cancel** has the focus when the dialog opens; Escape cancels.
- Live Photos and RAW + JPEG sets count as one item each; the dialog says "Live Photos and RAW sets count as one item and all their files go together" when any are present.
- Sections with nothing to say are left out. Galleries are sorted A–Z; after five, "and 3 more".
- If every item in Trash would be skipped, the dialog says why and offers only **Close**.

| Situation | What the person sees |
|---|---|
| Normal | The dialog above. After confirming, "Deleting… 40 of 128", then the Trash is empty and a toast says "Deleted 128 items. 4.2 GB freed." |
| Trash is empty | No Empty Trash button; the Trash view says "Trash is empty." |
| Something changed since the summary | "Something changed since you opened this. Here's the updated list." Nothing was deleted. |
| An item can't be removed (file in use, no permission, a file the safety gate refuses) | The other items go; that item stays in Trash with a note on its tile ("Couldn't delete: the file is in use"). The toast says "126 deleted, 2 couldn't be deleted and are still in Trash." |
| Engine unreachable | "Couldn't reach the Engine. Nothing was deleted." with Try again. |
| Not allowed (someone else's space, a viewer of a shared Gallery, an app, a phone) | No Empty Trash button. A direct call gets "not found" or "not allowed"; nothing is deleted. |
| Trying to trash an item in a read-only folder | No Move to Trash for it. In a mixed selection, the others go to Trash and a note says how many were left because they're in a read-only folder (D18). |
| A phone has an offline copy of a deleted photo | The phone drops it the next time it checks its download list. |

## 5. Data / model changes

- **No new information about items.** Trash keeps using the existing "trashed at" time on each item.
- **Changed:** Empty Trash removes items, their file records and their thumbnails for good; until now nothing in View was ever removed.
- **Changed:** the links from Shared Library contribution history to the original item no longer block removing that item (D12). Pre-release, so a fresh data store is acceptable.
- **Existing data:** no reset needed for anything else.

## 6. Work packets

Security and data-loss risk: WP1 and WP2 run at high effort with a reviewer pass focused on file safety and authorization. Fable only with Shy's approval (`.claude/rules/model-routing.md`). The paired-app fix (D21) lands first in its own change.

### WP1 — Engine: View in the safety gate, and the Empty Trash summary
- **What it does:** Teaches `SourceMutationPolicyGate` (`src/MediaEngine.Ingestion/SourceMutationPolicyGate.cs`) about View: a delete request for a file under the managed View root is allowed only when the file's `view_sources.storage_mode` is `managed`, the path is contained (after resolving links) beneath that owner's `Profiles/{storage_label}` folder (`ViewStorageService.GetProfileRoot`), and the file is writable; `linked` sources are always refused. Adds `ViewTrashDeletionPlanner` (`src/MediaEngine.Api/Services/View/`) that, for the caller's Mine scope, lists trashed items (`local_items.trashed_at IS NOT NULL`) with each item's files from `local_item_files` / `local_file_sources`, decides per item `delete` / `skip_read_only` / `skip_contribution_pending` / `skip_refused` (from the gate, never from path inspection alone), finds paths another live item still uses (D6), Manual Galleries affected (`view_gallery_items`), counts by media kind, bytes freed and a fingerprint. New Engine actions `GET /view/trash/summary` (D9; any device) and `POST /view/trash/empty-preview`. Authorization: `IViewResourceAuthorizationService` with Mine scope and `Manage` on every item; the preview and empty actions are refused for `PrincipalKind.DelegatedUserClient` and `ServiceApplication` (D3, D17). Restricted (Kids) profiles are allowed (D13); Shared Library items never appear (D14). Contracts in `src/MediaEngine.Contracts/LocalAssets/ViewTrashContracts.cs`: `ViewTrashSummaryDto { item_count, photo_count, video_count, document_count, bytes }`, `ViewTrashEmptyPreviewDto { items[], galleries[], delete_count, skipped[], bytes_freed, fingerprint }`, with fixtures (`tools/Update-ContractFixtures.ps1`).
- **Files likely touched:** `src/MediaEngine.Ingestion/SourceMutationPolicyGate.cs`, `src/MediaEngine.Ingestion/Contracts/ISourceMutationPolicyGate.cs`, `src/MediaEngine.Api/Services/View/` (new planner), `src/MediaEngine.Api/Services/LocalAssets/ViewStorageService.cs` (expose the containment check), `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs` (or a new `ViewEndpoints.Trash.cs`), `src/MediaEngine.Storage/LocalAssetRepository.cs`, `src/MediaEngine.Contracts/LocalAssets/`, `tests/MediaEngine.Ingestion.Tests`, `tests/MediaEngine.Api.Tests`, `tests/MediaEngine.Contracts.Tests`, `src/MediaEngine.Api/DependencyInjection/`.
- **Depends on:** the paired-app fix (D21) merged.
- **Size:** Complex.
- **Done when:** tests pass for: managed upload → `delete`; a linked item already in Trash → `skip_read_only`; a path outside the owner's profile folder (including through a symbolic link) → refused; a Live Photo is one item with both files; a path shared with another item is not listed for deletion; pending contribution → `skip_contribution_pending`; another profile's trashed items never appear; a Kids profile gets its own preview; a Shared View viewer, a Gallery-share viewer, an administrator of another household, a paired app token and a service application get not-found or 403; the route guardrails (`ViewEndpointRouteTests`) pass.

### WP2 — Engine: Empty Trash
- **What it does:** `POST /view/trash/empty { fingerprint }`, same authorization as WP1. Recomputes the plan; a different fingerprint answers 409 with the new preview. Per `delete` item: re-checks each file with the gate, moves all its files into a hidden staging folder inside the same managed root (excluded from scans and the watcher) so a failure part-way can move them back (D6), deletes them, deletes the thumbnail folder (`AssetPathService.GetCentralDerivedPath("local-item", itemId, …)`), then in one transaction deletes the `local_items` row (cascades cover metadata, file links, tags, document rows, Gallery items, annotations, search keys, device photo downloads), the `local_file_sources` rows for removed paths, and `local_files` rows left with no link. Changes `view_shared_contribution_items` and `view_shared_transfers` references to the item from `RESTRICT` to `SET NULL` (D12). Writes one activity row (D10). Returns per-item results; an item that failed stays in Trash with its reason.
- **Files likely touched:** as WP1, plus `src/MediaEngine.Storage/Schema/schema.sql`, a new `ViewTrashDeletionService`, the activity writer, `docs/reference/database-schema.md`.
- **Depends on:** WP1.
- **Size:** Complex.
- **Done when:** tests pass for: stale fingerprint → 409 and nothing changed; managed files and thumbnails gone from disk; a Live Photo whose video is locked keeps both files and stays in Trash; a file shared with another item stays; no row for the removed item remains in any `local_*`, `doc_*`, `view_gallery_items` or search table (a test walks every table with an item column); a Gallery left empty still exists; activity row written with count and bytes and no file names; the scanner does not re-add a removed managed item.

### WP3 — Engine: Trash rules for read-only items and Galleries (D18, D20)
- **What it does:** Trash refuses items with any file in a `linked` source: the single-item `POST /view/items/{id}/trash` answers a clear refusal, and the documents bulk `move_to_trash` reports them as `skipped` with reason `read_only_source`. Restore keeps working for them. Item responses carry a `can_trash` flag so the Dashboard can hide the button. Manual Gallery item lists, counts and cover selection skip trashed items (`ViewGalleryRepository.GetItemsAsync`, cover checks), as the timeline, Smart Galleries and discovery already do; memberships are kept so Restore puts them back.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs` (`MapLifecycle`), `src/MediaEngine.Api/Services/View/ViewDocumentBulkService.cs`, `src/MediaEngine.Contracts/LocalAssets/` (`can_trash`, fixtures), `src/MediaEngine.Storage/ViewGalleryRepository.cs` (and `src/MediaEngine.Domain/Contracts/IViewGalleryRepository.cs` if a signature changes), `tests/MediaEngine.Api.Tests`, `tests/MediaEngine.Storage.Tests`.
- **Depends on:** none (can run beside WP1).
- **Size:** Standard.
- **Done when:** tests show a linked item can't be trashed (single and bulk) but a trashed linked item can be restored; a Live Photo with one linked file is refused; a trashed item vanishes from its Manual Gallery and cover and comes back on Restore.

### WP4 — Dashboard: Trash header, read-only items and the Empty Trash dialog
- **What it does:** A Trash header in Photos' Trash filter and Documents' Trash mode showing the explanation line and size (`GET /view/trash/summary`). On a desktop where `EditorDeviceRule.CanEdit` is true, in Mine scope with a non-empty Trash, a red **Empty Trash…**; elsewhere "Empty Trash on a computer." The handler checks `CanEdit` again. An `EmptyTrashDialog` built on the shared `App*` dialog primitives showing every section and state in section 4, progress while deleting, 409 refresh, per-item failure notes on tiles. Move to Trash is hidden for items with `can_trash` false, and a mixed selection shows the read-only note. No browser `confirm`.
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/ViewPage.razor`, `src/MediaEngine.Web/Components/Pages/ViewDocumentsPage.razor`, `src/MediaEngine.Web/Components/Pages/ViewSelectionToolbar.razor`, `src/MediaEngine.Web/Components/Shared/MediaViewerShell.razor`, a new `src/MediaEngine.Web/Components/View/EmptyTrashDialog.razor`, `src/MediaEngine.Web/Services/Integration/EngineApiClient.View.cs`, `IEngineApiClient.View.cs`, `tests/MediaEngine.Web.Tests`.
- **Depends on:** WP1, WP2, WP3.
- **Size:** Standard.
- **Done when:** component tests show the button absent in Shared scope, on an empty Trash and when `CanEdit` is false; Cancel focused on open; 409 refreshes; no Move to Trash on a read-only item. Visual check per `src/MediaEngine.Web/CLAUDE.md` at desktop width in light and dark, and at phone width showing "Empty Trash on a computer."

### WP5 — Docs
- **What it does:** `docs/architecture/view-personal-media.md` (Asset states, Galleries, Filesystem and lifecycle safety), `docs/architecture/library-model-and-intake.md` (Personal items: Empty Trash is the permanent-file operation; read-only items can't be trashed), `docs/reference/api-endpoints.md`, `docs/reference/database-schema.md`, `docs/guides/view-personal-space.md`, `docs/product/presentation-rules.md` (where Empty Trash appears), `docs/reference/glossary.md` (Empty Trash).
- **Depends on:** WP2–WP4.
- **Size:** Mechanical.
- **Done when:** the docs build passes and each page matches what was built.

## 7. Risks and open questions

**Risks**
- **Removing a personal file can't be undone, and there may be no other copy.** The guards are: Trash first (D1), the summary and fresh confirmation (D7), the safety gate and folder containment (D2, D5), all-or-nothing per item (D6), desktop only (D17), and the WP1–WP2 tests.
- **Shared files on disk.** Two items can point at the same file record; D6 keeps any file another item uses. WP1's test covers it.
- **Kids profiles can delete their own photos for good** (D13). The confirmation dialog and desktop-only rule are the only guards; there is no grown-up approval.
- WP2 and WP3 change the data store and wire contracts: fixtures need refreshing and conflict with any other branch refreshing them.

**Open questions**

None. Q1–Q10 were answered on 2026-10-17 (D13–D22).

## 8. Gaps and drift

Found while writing this spec (2026-10-17):

- **Trash never removes anything.** Trash and Restore only set or clear the item's trashed time; the documents bulk action says so in its own description. There is no purge, Empty Trash or timed clean-up, so space used by trashed items is never freed.
- **Paired apps and Trash (being checked, D21).** The trash, archive and restore actions ask for "manage" rights on the item, but for a paired app the Engine maps that to the view-only photo permission and doesn't exclude item changes for apps the way it does for folders and preferences. A paired app with photo access may therefore be able to trash or archive photos. No native app does this today.
- **Read-only items can be trashed today.** Nothing stops a linked-folder item going to Trash (D18 changes that).
- **View doesn't use the safety gate.** The Shared Library contribution clean-up removes managed originals after checking both copies match, but without the shared safety gate or a folder containment check. Worth a follow-up so every file removal goes through one gate.
- **Manual Galleries still show trashed items** and can keep one as cover (D20, WP3).
- **Deleted files aren't noticed.** View's folder watcher listens for new, changed and renamed files only, and scans only add. A file deleted outside Tuvima leaves its item in View, showing as missing when opened. Out of scope; worth its own spec.
- **No live update for View changes.** Other open windows see trash and delete changes on their next refresh.

As built (2026-10-20):

- **Order of removal.** Empty Trash moves an item's files into a hidden staging folder inside the owner's own managed folder, removes the records in one transaction, and only then deletes the staged files and thumbnails. If a move or the transaction fails, the files go back. A recovery sweep at startup and after each Empty Trash finishes or undoes interrupted work. This keeps the records and the disk consistent at every step (D6).
- **Last checks.** Just before removal the Engine checks again that each item is still in Trash, that no Shared Library work has started on it, and that no other item, in this or another library, now uses one of its files; any change puts the files back and keeps the item in Trash.
- **Links are refused.** A file reached through any link (junction or symbolic link), even one that stays inside the folder, is never removed.
- **Read-only from the item's own library.** An item counts as read-only only when one of its files is in a linked folder of its own library; someone else's linked copy of the same photo doesn't make it read-only.
- **The Trash size** (`GET /view/trash/summary`) is for the person at the Dashboard only, like the other Trash actions; native apps have no Trash screens.
- **Wording.** The dialog's last line reads "Library has no other copy." (the app is called Library in everything people see). The dialog shows "Deleting…" without a running count, because the Engine answers once at the end.
- **Galleries in the dialog** use a count of distinct items in any of your Galleries (`gallery_item_count`).
- **Places and Gallery pages** already offered Move to Trash from the shared photo viewer before this spec (section 4 lists them as having no trash actions); that viewer now hides it for read-only items like everywhere else.
- **Visual check done 2026-10-20** (proposed change #331): desktop light and dark and phone width; small footer-spacing, Cancel-focus and tile-note fixes landed there.
