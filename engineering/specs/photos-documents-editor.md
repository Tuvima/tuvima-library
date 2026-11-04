# Spec: Photos, videos and documents editor (one editor for View)

> **Status:** Locked (Shy, 2026-11-04) · **Version:** 1 · **Last updated:** 2026-11-04 · **Related docs:** [Product overview](../product/overview.md) · [Universal editor v4 (Locked)](universal-editor-v4.md) · [Universal editor v3](universal-editor-v3.md) · [Documents in View](archive/documents-in-view.md) · [Photo and video filing](photo-video-filing.md) · [Safer deleting in View](safer-deleting-in-view.md) · [Batched write-back](batched-writeback.md) · Mockups: [`engineering/design/mockups/photos-documents-editor/photos-documents-editor-mockups.html`](../design/mockups/photos-documents-editor/photos-documents-editor-mockups.html) (built on the v4 mockup frame) · Answers Q11 of the editor overhaul (Shy, 2026-11-04 03:18Z)

> Written from a review of the code on `main`, then **revised 2026-11-04 04:50Z** after Shy asked for one look and feel across the app: this editor now uses the Locked Universal editor v4 frame and controls exactly (dropdown rail, horizontal tabs, Quick Facts, locks and revert arrows, Discard and Save changes, instant delete after "Are you sure?"). Where v4 says something, v4 rules.

## 0. What exists today (verified in code)

There is no single "photos and documents editor". Editing is spread over four places, and none of them behaves like the pop-over editor Shy asked for.

| Thing | Where you edit it today | How it behaves |
|---|---|---|
| One photo or video | The side panel of the full-screen viewer. Description, date taken, location, people and tags each have a pencil and their own Save. | Each field saves **immediately**. No Review, no Cancel for the whole, a toast on success. Date and location offer "Use original" when you overrode the camera's value. |
| Several photos or videos | Nowhere. The bar that appears when you tick photos offers Add to gallery, Share, Download, Favorite, Archive, Trash, Remove from gallery. | You cannot give 40 holiday photos one tag, one place or a corrected date. |
| One document | A "Document details" dialog: title, tags, correspondent, type, date, archive number, notes, and your own fields. | One Save. The closest thing to the target. No Review, no way to see or revert what was there before. |
| Several documents | A separate small dialog **per action** ("Add tags", "Set correspondent", "Set type", "Set date"). | One field at a time, applied at once, with a per-document result report. |
| A gallery (and a saved Documents view, which is a gallery under the hood) | A gallery editor with its own look: Details, Artwork, Membership, Soundtrack, History. | The Artwork tab only says "automatic cover": **you cannot choose a cover**. Membership says "use Add to gallery from the page". History shows one line, "Gallery created". Delete gallery sits at the foot of the rail and acts at once. Toasts on save. |
| Trash for a photo or document | The selection bar, or the viewer. | Immediate, with the usual confirm. Items in a Read-only folder cannot be trashed. |

Three different looks, three different save rules, no multi-select for photos. Editing is already desktop-only for photos; documents and galleries check the same rule.

## 1. Goal

Press Edit on a photo, video, document or gallery (or tick a few) and the same pop-over editor as everywhere else opens at once. You change what you want, see exactly what will change in Review, and nothing is saved until Save changes (delete asks "are you sure" and then happens at once). Ticking many photos or documents lets you tag, date, place or file them all in one go, offering only what makes sense for the whole group.

## 1a. Plain-English walkthrough

This editor is the main editor (Universal editor v4, Locked) with photos, videos, documents and galleries plugged in. It is not a second design: same pop-over (about 1280 × 860 at 1440 wide), same header, same horizontal tabs, same Quick Facts panel, same footer, same Review, same delete confirm. Where this spec says nothing, v4 rules.

1. **Edit opens the pop-over at once** from the viewer's new Edit button, a photo or document card's "..." menu, a gallery's "..." menu, or ticked items. The viewer's per-field pencils and Save buttons go away; the viewer keeps a read-only info panel and Edit (desktop only).
2. **Header.** The picture (the photo itself, or the document's first page), a kicker line ("Photo · Lisbon 2026"), the title (file name for a photo until you give it a description; the document's title), one line of facts, and a "..." menu (Delete, and Open in viewer). No provider links: nothing here has a provider.
3. **Rail of dropdowns, only when there is a family.** Opened from inside a gallery, the rail reads *Lisbon 2026 ▾ › IMG_4521 ▾*; the second dropdown lists the gallery's photos with thumbnails, so you can step through them without closing (this replaces the arrows I had proposed). Opened from the Timeline, Photos, Documents or a search, there is no rail. A saved Documents view works the same way with its documents.
4. **Tabs across the top.** Photo and video: **Details, Files, History**. Document: **Details, Files, History**. Gallery: **Details, Contents, Artwork, History**. (No Match & Identity: nothing here matches a provider. No Artwork on a photo or document: they have no cover to choose.)
5. **Details** is the same form style as everywhere: every field has a **lock** (keep my value) and, once changed, a **revert** arrow. For date taken and place, the revert arrow is the old "Use original": it puts back what the camera recorded. **Quick Facts** on the right: for a photo, type, size, dimensions, camera; for a document, pages, size, type, "In your own space" or "Shared"; for a gallery, photo count, date range, who can see it; plus "Read-only folder" or "Edits stay in Library" when true.
6. **Footer** exactly like v4: where changes go ("Changes save to Library · Your original files are not changed"), how many are waiting, **Discard** and **Save changes**. Save shows **Review changes** first. After Save, the slim ribbon on the page says what was saved; errors appear in place, never as toasts.
7. **Several items.** Tick photos (or documents) and press Edit: the header says "Editing 40 photos" with small pictures, no rail. Only fields every chosen item can take appear (rules in v4 section 3c): photos and videos get Tags, People (add or remove), Date taken (set one date, or shift all by an amount), Place; documents get Tags, Correspondent, Type, Date and your own fields. Identifying fields (file name, title, archive number, notes, and Description) are never offered for a group. Differing values show "Mixed, leave unchanged". From a gallery's Contents, ticked photos open the same group editor.
8. **Contents (gallery).** The members as a grid or list with a tick, a picture and a size; tick several and the editor becomes "Editing 5 photos"; Remove from gallery, Add photos, and the rule builder for a Smart gallery with a live count. Removing keeps the photo in your library.
9. **Artwork (gallery).** Same as v4 3b: a Cover card, a large preview, file facts, **click a version to make it Preferred** (no button). Versions are the gallery's own photos (picked from Contents or here), an upload or a link; **Automatic** stays the default. A saved Documents view and a Vault image gallery have no cover choice where today's rules say so.
10. **Files** lists the item's files (a Live Photo or RAW+JPEG is one item with its parts here): name, folder, size, type, and whether the folder is read-only. Each file has Delete.
11. **Delete is instant after one confirmation** (v4 D13). From the editor's "..." (the item or the whole gallery), from ticked rows in Contents, from a file in Files. The same "Are you sure?" box names what goes ("Move 3 photos to Trash. You can restore them from Trash."), shows the size, says a deleted gallery keeps its photos, and then happens at once. Read-only folder files show the reason instead.
12. **History** shows what the file originally said and what you changed, with revert arrows; for a gallery, when it was made and changed.
13. **Galleries are part of editing.** Details has a **Galleries** field: for one photo it shows the galleries it is in (chips, remove with a cross); for a group it shows "Add to gallery" with your manual galleries and **New gallery…**. Typing a new name there stages "Create gallery *Lisbon 2026* with 40 photos"; Review lists it and Save creates it. A Smart gallery is never offered (it fills itself by its rule). Documents get the same field for saved views only where views are manual; otherwise it is not shown.
14. **Quick actions outside the editor stay quick.** Favorite, Hide, Archive, **Add to gallery** and **New gallery** in the selection bar, the viewer and the card menu act at once and say so in the slim ribbon (they lose nothing and are easy to undo). **New gallery** from the bar opens the gallery in the same v4 frame in create mode: name, the ticked photos already in Contents, cover automatic; Save creates it (Q9).
15. **One action list everywhere.** Favorite, Add to gallery (with New gallery), Download, Share to Shared Library, Archive, Edit, Delete are defined once and shown in the same order in three places: the selection bar, the card "..." menu and the viewer. A new action appears in all three or none.

### The viewer (flipping through photos)

Today the full-screen viewer has its own toolbar and a right-hand info panel with a pencil and a Save on each field, which repeats most of the editor in a different style, and it cannot create a gallery. Proposed:

16. **The viewer is for looking.** The photo fills the screen. Top bar: Close, position ("12 of 214"), then the shared action list: Favorite, Add to gallery (existing galleries and New gallery…), Download, Info, Zoom, Slideshow, and a **"..."** with Edit, Share, Archive, Delete. Left and right arrows (and the keyboard) flip.
17. **The right panel is read-only and looks like Quick Facts.** Same card style as the editor's Quick Facts: date and time, place (small map), people, tags, galleries it is in, description, then file facts (type, size, dimensions, camera). At the top one **Edit** button (desktop only) opens the pop-over on that photo; no pencils, no Save, no inline fields. The panel open or closed is remembered as today.
18. **Edit from the viewer keeps your place.** The editor opens over the viewer; Save or Discard returns to the same photo and the panel shows the new values. If you came from a gallery, the editor's rail is that gallery (point 3).
19. **Phones and tablets** get the same viewer and panel without Edit.

**What stays the same:** desktop-only editing; nothing except Delete is written before Save; Review before every save; the Engine decides permission; your original files are never changed; the Vault keeps its own rules.

**Scope boundaries:** the photos-in-View model is not planned yet (only offered to Shy) and is left alone.

**Acceptance criteria**
- B1. Edit on photo A, close, Edit on photo B shows B (the v4 bug cannot return here). (WP2)
- B2. A photo, a document and a gallery open in the same frame as a film does in v4: header, tabs, Quick Facts, footer, Discard and Save changes; a guardrail test fails if a View editor draws its own frame. (WP2, WP6)
- B3. Nothing is sent before Save; Discard throws everything away. (WP2)
- B4. Ticking 40 photos offers only the shared fields; Description is absent; shifting 12 dates by 3 hours changes each by exactly 3 hours across a daylight-saving change. (WP3)
- B5. A gallery's cover is chosen by clicking a version and can go back to Automatic. (WP4)
- B6. Delete of a photo, a group, a file and a gallery asks once and happens at once; the editor moves on (to the gallery, or closes). (WP5)
- B7. The viewer, document cards and galleries no longer save a field outside the editor. (WP6)
- B8. Ticking 40 photos and choosing New gallery… with a name in Galleries, then Save, creates the gallery with all 40; Review listed it first. (WP3, WP4)
- B9. The selection bar, card menu and viewer show the same actions in the same order; a test fails if one list gains an action the others lack. (WP6)
- B10. In the viewer the side panel has no editable control other than Edit; Edit then Save returns to the same photo showing the new values. (WP6)

## 2. Scope

**In scope**
- The v4 editor frame for: a photo, a video (View videos), a document, a gallery (manual, smart, Vault image gallery), a saved Documents view.
- Group editing for photos and videos together, and for documents.
- Delete from the editor, instant after "Are you sure?".
- Choosing a gallery's cover; adding, removing and creating galleries from the editor; the viewer's toolbar and side panel made to match.
- Removing the viewer's per-field pencils and Save buttons, the Document details dialog, the per-action document bulk dialogs and the old gallery editor shell.
- Public docs for the Photos and Documents guides, explaining why things behave as they do.

**Out of scope**
- How photos are modelled in View (not planned yet).
- Writing edits into the files themselves (stays off; Batched write-back is unchanged).
- Picture editing: crop, rotate, filters.
- Phones and tablets: viewing only.
- Document OCR text editing; document rules and the Inbox screens.
- Editing the Vault's non-image content.

## 3. Decisions made

Those marked "from v4" are Shy's, already Locked in Universal editor v4 and applied here. Shy agreed to every recommendation (Q1 to Q10, 2026-11-04 05:02Z), so the proposed ones below are now decided; the spec waits only for "lock it".

- **D1 (from v4).** One editor look for the whole app: same frame, header, tabs, Quick Facts, lock and revert arrows, footer, Review and delete confirm. Reason: ease of use and Maintenance; Shy: "we can't keep having different UIs when we use the same basic controls".
- **D2 (from v4).** Rail of dropdowns only when there is a family; tabs across the top; Contents on containers (a gallery).
- **D3 (from v4).** Nothing is saved until Save changes, except Delete (instant after one "Are you sure?").
- **D4 (from v4).** Click a version to make it Preferred; no button.
- **D5 (from v4).** A group offers only fields every chosen item can take; differing values show "Mixed, leave unchanged"; identifying fields are never offered.
- **D6 (from v4).** Desktop only. Errors show in place; no toasts.
- **D7.** Edits are stored in Library, never in the file. Reason: Privacy and Reliability; originals stay protected. (True today: View writes only the adding profile's name into files, on a schedule.)
- **D8.** One Save writes all data-store changes in a single step: all or none. Reason: a failed Save never leaves 20 photos half-tagged.
- **D9.** Photo and video groups: Tags and People (add/remove), Date taken (set, or shift by an amount), Place. Description is single-item only. Reason: a shared caption on different photos is almost always a mistake.
- **D10.** Document groups: Tags, Correspondent, Type, Date, your own fields. Title, Archive number, Notes single-item only.
- **D11.** The viewer keeps a read-only info panel and gets one Edit button (desktop only). No pencils.
- **D12.** A gallery's cover is chosen from its own photos, with Automatic as the default. Reason: the editor today promises a cover and offers none.
- **D13.** A Vault image gallery uses the same editor with the Vault's limits; Kids profiles never reach the Vault.
- **D14.** Permissions are decided by the Engine as today; the editor only hides what the Engine would refuse.
- **D15.** Public docs explain the reasoning: why edits stay out of your files, why Description is not offered for groups, why the revert arrow is called Use original in History.
- **D20 (from language-aware wording, Built).** Every word in this editor (Favorite, Gallery, Trash and the rest) comes from the shared wording list and the household's chosen spelling, never typed into a screen; the existing wording guardrail test covers it. Shy, 2026-11-04: "We are US, so it's Favorite".
- **D16.** Favorite, Hide, Archive, Add to gallery and New gallery stay one-click outside the editor and confirm in the ribbon (Shy, Q4 and Q9).
- **D17.** A **Galleries** field in Details adds to, removes from and creates manual galleries; it is staged like every other field and Review lists it (Shy, Q9).
- **D18.** One shared action list in the selection bar, card menu and viewer, in one order (Shy).
- **D19.** The viewer's side panel becomes read-only, styled like Quick Facts, with one Edit button; the per-field pencils and Save buttons are removed (Shy, Q10).

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal, one photo | Pop-over over the dimmed page; header, tabs, Details form with locks, Quick Facts, footer. |
| Normal, several | Header "Editing 40 photos" with small pictures; only shared fields, "Mixed, leave unchanged" where they differ. |
| Galleries field | One photo: chips of its galleries. A group: Add to gallery and New gallery…; a new name shows as "Create gallery" in Review. |
| Viewer | Photo, shared action list on top, read-only info card on the right with Edit. |
| Opened from a gallery | A rail *Lisbon 2026 ▾ › IMG_4521 ▾*; changes on other photos are kept while switching. |
| Documents | First page as the picture; fields as listed; your own fields appear only if you have defined some. |
| Gallery | Header shows the cover; Contents grid with ticks; Artwork with Preferred; Smart galleries show the rule and a live count. |
| Empty | A gallery with no photos says so and offers Add photos. |
| Opening | Frame and header appear at once with skeleton fields; always the clicked item. |
| Delete | "Are you sure?" naming what goes and the space freed; on yes the item leaves the page at once; a group reports "37 moved, 3 stayed: Read-only folder". |
| Engine unreachable | A plain error line and Try again; staged changes kept. |
| File still being scanned | "Details are still being read"; camera and size fill in when ready. |
| Missing file | "File missing"; only Details, Files and Delete are offered. |
| Read-only folder | Everything except Delete-from-disk can be edited (stored in Library); Delete shows the reason. |
| Narrow window (below 1280) | Rail shortens and Quick Facts move under the form (as v4). |
| Leaving with changes | "Unsaved changes" (as v4). |
| Phone, tablet | No Edit control; viewing only. |
| Kids profile | Edits only what a Kids profile may edit today; nothing of the Vault. |

## 5. Data / model changes

None to the data structure. Everything it edits already exists: description, captured date with its "use original" flag, location with its flag, people, tags, and the document fields. New in the Engine: one **batched save** action per kind so Save is a single step (today each field and each bulk operation is its own call), and a way to read the original (embedded) values for History. A gallery's cover already has a field (`CoverItemId`) that the editor does not set today. Pre-release: no migrations.

## 6. Work packets

Built in the cloud after v4's frame (WP3 there) exists; these packets reuse its components rather than building another.

### WP1 — Batched save for View items
- **What it does:** one Engine action that applies a set of edits to one or many photos, videos or documents in one step (all or none), returning per-item results for the Trash step.
- **Files likely touched:** `src/MediaEngine.Api/` View endpoints, `src/MediaEngine.Contracts/LocalAssets/`, View repositories in `src/MediaEngine.Storage/`, contract fixtures.
- **Depends on:** none
- **Size:** Complex
- **Done when:** tests show a save of tags, people, date, place on 3 items applies all or none; a failing item rolls the set back; the contract snapshot is refreshed.

### WP2 — View items in the v4 editor (photo, video, document)
- **What it does:** the v4 frame (reusing its components, not a copy) with Details, Files and History for one item; header, Quick Facts, locks and revert arrows, optional rail from a gallery, Review rows, unsaved-changes guard.
- **Files likely touched:** `src/MediaEngine.Web/Services/Editing/`, `src/MediaEngine.Web/Components/MediaEditor/`, new View editor components.
- **Depends on:** WP1, v4 WP3 (the new frame)
- **Size:** Complex
- **Done when:** component tests show nothing is sent before Save, Discard throws changes away, Review lists before and after, the revert arrow restores the embedded date and place, and a guardrail fails if a View editor draws its own frame.

### WP3 — Groups
- **What it does:** Edit on a ticked group; shared-field rules; shift-by-amount for dates; "Mixed"; grouped Review lines.
- **Files likely touched:** `src/MediaEngine.Web/Services/Editing/BulkEdit*.cs` or successors, the selection toolbar.
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** tests show a mixed photo and video group offers only shared fields; Description is absent for a group; shifting 12 dates by 3 hours changes each by exactly 3 hours across a daylight-saving change.

### WP4 — Gallery editor on the same frame
- **What it does:** Details, Contents (members, rule builder), Artwork (cover with Preferred), History; Vault variant; saved Documents view variant; gallery delete with confirm.
- **Files likely touched:** `src/MediaEngine.Web/Components/Collections/GalleryEditorShell.razor` (retired), `GalleryEditorLauncherService`, galleries pages.
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** tests show a cover can be chosen and "Automatic" restored; removing a member and saving leaves the photo in the library; a Vault gallery shows no soundtrack; clicking a version makes it Preferred.

### WP5 — Delete from the editor
- **What it does:** Delete button with an "are you sure" for items, groups and galleries; instant on yes; group report; Read-only folder reason; the same action from the "..." menu.
- **Files likely touched:** editor components, `TrashViewItem` paths in `src/MediaEngine.Web/Services/Integration/`.
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** declining the confirm changes nothing; confirming moves the item to Trash with no Save; a group uses one confirm; a Read-only item shows the reason instead of the button.

### WP6 — Retire the old surfaces
- **What it does:** one shared action list for the selection bar, card menu and viewer; Add to gallery and New gallery in the viewer; the read-only Quick-Facts-style info panel with Edit; remove the viewer pencils and per-field saves, the Document details dialog, the per-action bulk dialogs, the old gallery editor shell and their strings and styles; add the Edit button to the viewer, cards and selection bar.
- **Files likely touched:** `ViewImmersiveViewer.razor`, `ViewDocumentDetailsDialog.razor`, `ViewDocumentBulkDialog.razor`, `ViewSelectionToolbar.razor`, `GalleryEditorShell.razor`, guardrail tests.
- **Depends on:** WP2 to WP5
- **Size:** Standard
- **Done when:** a guardrail test fails if any View screen saves a field outside the editor; no toast is used for editor results.

### WP7 — Docs
- **What it does:** Photos and Documents guides in `tuvima-docs`, explaining the reasoning (D12), plus the presentation rules page.
- **Files likely touched:** `tuvima-docs` `content/products/library/`, `docs/product/presentation-rules.md`.
- **Depends on:** WP2 to WP6
- **Size:** Mechanical
- **Done when:** the docs build passes.

### Review
v4 has Fable review its implementation; the same applies here for WP1 (all-or-nothing save and permissions).

## 7. Local checks

- **L1.** Tick 40 real photos, add a tag and shift the date by 3 hours, Review, Save; spot-check three. Fail if one photo is half-changed.
- **L2.** Edit a photo in a Read-only (linked) folder: Details work, Trash is refused with the reason.
- **L3.** Discard after changing five fields: nothing changed. Delete a photo from the editor: a confirm appears, and it is in Trash straight away. Close the browser tab mid-edit: nothing changed.
- **L4.** Choose a gallery cover; refresh; the cover persists. Remove a photo from the gallery; it is still in Photos.
- **L5.** Edit three documents together (tags, correspondent, type); Review lines read plainly.
- **L6.** The same on a tablet and a phone: no Edit control appears.

## 8. Risks and open questions

- **Risks:** (1) A group Save touching thousands of photos is slow; the editor limits a group to what the selection bar already allows and runs the save as one job with progress. (2) Shifting dates can move photos into other days of the Timeline; Review says so ("moves 4 photos to the previous day"). (3) Photos-in-View modelling is unplanned; this spec touches only editing and must be revisited if that model changes how a photo, album or gallery is defined.
- **Open questions (nothing here is locked):** see below. Each has the context, the options and a recommendation.

**Q1. Photos and videos in one editor.** A video in View has the same fields as a photo (description, date, place, people, tags). A: one editor for both, edited together. B: separate. **Recommend A.**

**Answered by Shy (2026-11-04 05:02Z): agreed, option A (the recommendation).**

**Q2. Where Place lives.** A: inside Details (a map beside the fields). B: its own tab. **Recommend A** (v4 has no tab for a single field).

**Answered by Shy (2026-11-04 05:02Z): agreed, option A (the recommendation).**

**Q3. Description for a group.** A: not offered (it would paste one caption on 40 photos). B: offered, as "add the same sentence to all". **Recommend A.**

**Answered by Shy (2026-11-04 05:02Z): agreed, option A (the recommendation).**

**Q4. Favorite, Hide, Archive.** A: stay one-click outside the editor (they lose nothing and flip back). B: staged like everything else. **Recommend A.**

**Answered by Shy (2026-11-04 05:02Z): agreed, option A (the recommendation).**

**Q5. Trash in the editor.** Decided by Shy (2026-11-04): instant after an "are you sure", not staged. Applied above.

**Q6. Gallery cover.** A: choose any member, or "Automatic". B: automatic only (today). **Recommend A.**

**Answered by Shy (2026-11-04 05:02Z): agreed, option A (the recommendation).**

**Q7. Date shift.** A: offer "shift by an amount" as well as "set date". B: only "set date". **Recommend A**, because a camera with the wrong clock is the common case.

**Answered by Shy (2026-11-04 05:02Z): agreed, option A (the recommendation).**

**Q8. Stepping between photos.** Using v4's dropdown rail: when you open a photo from inside a gallery, the rail shows *Gallery ▾ › Photo ▾* and the second dropdown lists the gallery's photos with thumbnails. From the Timeline or search there is no rail. A: do that (recommended). B: no rail for photos at all; close and pick another.

**Answered by Shy (2026-11-04 05:02Z): agreed, option A (the recommendation).**

**Q9. Add to gallery and New gallery from the bar and the viewer.** A: instant with a ribbon (like Favorite; easy to undo), plus the staged Galleries field inside the editor (recommended). B: only inside the editor, staged, so the bar's Add to gallery goes away. **Recommend A**: adding 40 photos to a gallery is the most common thing people do and should be one click.

**Answered by Shy (2026-11-04 05:02Z): agreed, option A (the recommendation).**

**Q10. The viewer panel.** A: read-only card like Quick Facts with one Edit button (recommended). B: keep inline editing in the viewer. **Recommend A**: one place to edit, uniform with every other screen.



**Answered by Shy (2026-11-04 05:02Z): agreed, option A (the recommendation).**

## 9. Gaps and drift

- Built and matches the spec: Trash rules for Read-only folders, "Use original" for date and place, document custom fields.
- Built differently: edits save field by field (viewer), one action at a time (documents bulk), with toasts, and each surface has its own look; v4 replaces all of it.
- In the spec but not built: group editing for photos, choosing a gallery cover, Review, delete from the editor.

## Plain English summary

Today, editing a photo, a document or a gallery means three different kinds of window, each saving in its own way, and you cannot edit many photos at once. This plan gives them all the same editor as the rest of Library: open it, change things, see a clear list of what will change, and only then save. Ticking 40 photos lets you tag, date or place them together, and offers only what makes sense for all of them. Your original files are never touched, and deleting asks "are you sure" and then happens at once. A few choices, like whether to offer a shared caption or arrows between photos, are yours to make before this is locked.
