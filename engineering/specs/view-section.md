# Spec: View section cleanup, whose-switch, Recently added and On this day

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-06 · **Related docs:** [Photos and documents editor](photos-documents-editor.md) · [Look pass v2](look-pass-v2.md) · mockup `engineering/design/mockups/view-section/view-section.html`

> **Version note (2026-11-12):** [universal-editor-v6](archive/universal-editor-v6-match-and-aligned-rows.md) (section 9) replaces the "editing is desktop-only" rule wherever this spec states it or hides an edit action on phones: item editing works on desktop, tablet and phone (`EditorDeviceRule.CanEditItems`), the Engine's permission check still decides who may edit, and TVs do not edit. Settings and Vault tools stay desktop-only. The Locked body below is unchanged.
> **Version note (2026-11-12, Shy):** Move to Trash is offered on phones as on a desktop; "phones also get no Trash" (section 4 and WP1) no longer applies.

## 1. Goal

Make View (Photos, Galleries, Documents, People, Places) follow the same rules as the rest of the app, give it one clear way to say whose library you are in, and add two ways to find photos: a **Recently added** pill and an **On this day** memories strip. Cleanup ships first so the new features land on a clean base.

## 2. Scope

**In scope**
- Wave 1 cleanup: Trash confirmation, no toasts, no browser hover text, one shared action list, viewer gallery menu, Hide offered, plain-English smart-gallery rules.
- Wave 2: a "Viewing" switch at the top of the left rail replacing the Photos scope dropdown; menus follow ownership; People and Gallery pages reach parity.
- Wave 3: Recently added pill; On this day strip; Density moved into More filters.

**Out of scope**
- A combined "everything" scope (Q1, defaults to no).
- PDF and comic readers, the Photos-in-View model rework, per-profile editable copies of documents.
- Looking at other people's photos in On this day.

## 3. Decisions made

Shy, 2026-11-06 unless noted.
- D1. Recently added is a filter pill that sorts the whole page by date added, newest first.
- D2. On this day sits above the timeline on the All pill of Mine only. It reads Mine and the Shared Library, never other people's scopes, never Vault, so no "look" is logged for anyone else.
- D3. Density moves into More filters; the Photos top has only the title and pills.
- D4. The whose-switch ("Viewing: Mine") sits at the top of the left rail and applies to every View page. It replaces the Photos scope dropdown. The rail group heading "Personal" is renamed.
- D5. One action list on the selection bar, the tile menu and the viewer. Each action shows only if allowed on that item (own, Shared Library, another person's). Another person's items are look-only.
- D6. Cleanup is built before the new features.
- D8. No combined "everything" scope; the Viewing switch stays (107 A). The On this day strip has no setting; it hides itself when empty (109a A). Photos "Add media" is limited to photos and videos (109b A).
- D9. The whole Photos page is the drop target, not one marked area; a dashed outline surrounds the page while dragging. Dropping files onto Photos shows upload progress (per file and overall) and a result. A file that is not a photo or video is rejected with "This isn't a valid photo or video." and the reason; valid files in the same drop still upload.
- D10. Dropping a picture onto Documents opens a small dialog: "This is a picture. Library can keep it in Documents, but it may fit better in Photos." with Keep in Documents, Move to Photos, and a "Don't ask again" tick, saved per profile.
- D11. Everything here follows the locked look pass v2 (`look-pass-v2.md`): near-black surfaces (page #101115, nav #14151A, cards #1A1B21, hover #272830, menus #2F3039); purple only as an accent, never a surface fill; controls 44 px tall; one filter chip style and one tab style (the pills, More filters and the Documents rail use them); Material icons, Filled only for the selected state; menus and pop-ups open as bottom sheets on phones; one tooltip style and no title= hints. On phones there is no Edit and no Trash/Delete anywhere in View (this removes Trash from the phone selection bar and viewer; Archive stays).
- D7 (existing app rules). Delete is instant after one "Are you sure?"; no toasts; no browser title= hints; every menu and pop-up renders in the top layer; US spelling.

## 4. UI behaviour

**Action list** (each shown only when allowed)

| Item you are looking at | Favorite, Add to Gallery, Download | Edit, Archive, Trash, Hide | Share |
|---|---|---|---|
| Yours (Mine) | yes | yes | Share to Shared Library |
| Shared Library, added by you | yes | yes | Remove from Shared |
| Shared Library, added by someone else | Favorite, Add to my own Gallery, Download | none | none |
| Another person (read-only) | Download only if they allow it | none | none |

Edit stays desktop-only as today, and phones also get no Trash (D11). Trash and Archive ask "Are you sure?" once, then act at once.

| Situation | What the person sees |
|---|---|
| Normal | Rail with "Viewing: Mine", then Photos, Galleries, Documents, People, Places, Artwork Library, gallery shortcuts. Photos shows title, pills (All, Recently added, Favorites, Videos, Archive, Trash, More filters), On this day (All pill only), timeline. |
| Empty | Recently added with nothing: "Nothing added yet". On this day with no matches: strip is hidden. |
| Loading | Existing loading state; On this day loads after the timeline and never blocks it. |
| Error | Timeline error state as today; On this day failing hides the strip quietly and is logged. |
| Not allowed | Profiles without photos see the existing "not available" page; the switch lists only scopes the profile may open. |
| Partly ready | Recently added scrubber: the timeline scrubber is built on date taken, so it is hidden on Recently added. |

Phone: the switch becomes the first row of the menu sheet; On this day cards scroll sideways. Tablet and desktop: as the mockup. TV: not affected.
Switching scope returns to Photos on the same pill when possible. Gallery shortcuts list the viewed person's galleries; dragging into a gallery works only for your own.

## 5. Data / model changes

- A sort option (date added) on the View photo list connection point and its Contracts type, plus fixture update. The Engine already has the ordering.
- A new Engine query for photos whose date taken falls on a month and day, run per allowed scope with the same authorization as the timeline, and a new Contracts type for the response. No new stored data.
- Pre-release: no migrations needed.

## 6. Work packets

### Wave 1: cleanup
**WP1 — Trash and Archive confirmation.** Add the single "Are you sure?" to every Trash and Archive entry (bar, tile menu, viewer, gallery viewer). Trash is not offered on phones (D11). Files: `ViewPage.razor`, `ViewGalleryDetailPage.razor`, shared confirm component. Depends on: none. Size: Standard. Done when: a test proves Trash from each surface asks first and acts after one confirm.

**WP2 — Remove toasts from View.** Swap remaining toast calls (`ViewSectionShell`, `ViewTrashHeader`, `ViewArtworkPage`, `ViewContributionsPage`, View settings panels) to the ribbon voice, and widen the guardrail test to every View file. Depends on: none. Size: Mechanical. Done when: guardrail covers all View files and passes.

**WP3 — Remove browser hover text.** Replace title= in `MediaViewerShell` (Download), `ViewPlaceStoryPanel`, `MediaSectionNavigationItem` rail links with the app tooltip; guardrail against title= in View. Depends on: none. Size: Mechanical. Done when: guardrail passes and no title= remains in View files.

**WP4 — One action list.** One shared action definition used by the selection bar, tile menu and viewer, with the ownership rules from section 4; add Hide/Unhide so the Hidden filter can fill; Download offered everywhere allowed; consistent device gating. Depends on: WP1. Size: Complex. Done when: tests per scope and ownership show the exact action set; all three surfaces list the same actions.

**WP5 — Viewer gallery menu and smart rules text.** Replace the viewer's native select with the app menu; show smart-gallery rules as a plain sentence instead of JSON. Depends on: WP4. Size: Standard. Done when: viewer opens a menu in the top layer; a smart gallery shows readable rules.

**WP6 — Style and file tidy.** Align every View surface to look pass v2 (D11): its surface colors, 44 px controls, the one chip and tab style, Material icons with Filled only when selected, bottom sheets for phone menus; fix off-token sizes and colors, square pills, breakpoint scale; delete dead CSS and its allowlist entry; reformat very long lines in `ViewPage.razor` and `ViewPeoplePage.razor`; move literal text to localized strings. Depends on: none. Size: Mechanical. Done when: UI guardrail tests pass with the allowlist entry removed.

### Wave 2: whose-switch and parity
**WP7 — Viewing switch.** Add the switch at the top of the rail (phone: first menu row), apply it to every View page, remove the Photos scope select, rename the group heading, route gallery shortcuts per viewed person. Depends on: WP4. Size: Complex. Done when: changing the switch changes Photos, Galleries, People, Places and Documents together; Shared Library and another person render the right menus.

**WP8 — People and Gallery parity.** People: un-favorite, and the viewer gets favorite, archive and trash where allowed. Galleries: selection works on smart galleries (copy-only actions), same load behavior as Photos. Depends on: WP4. Size: Standard. Done when: tests show these pages use the same action list.

**WP9 — Accessibility pass.** Announce selection count, arrow-key movement in the toolbar, focus moves into the viewer on open. Depends on: WP7. Size: Standard. Done when: bUnit/keyboard tests pass.

### Wave 3: new features
**WP10 — Recently added pill.** Expose date-added ordering through the Engine connection point and Contracts, update fixtures, add the pill, group the timeline by date added, hide the scrubber on this pill, and make filter state hold both kind and sort. Depends on: WP7. Size: Standard. Done when: a photo taken years ago and added today shows first; Contracts tests and fixtures pass.

**WP11 — On this day.** New Engine query per allowed scope (Mine and Shared Library only), new strip component with one card per past year, hidden when empty or off the All pill, never logs another-person looks. Depends on: WP7, WP10. Size: Complex. Done when: tests prove other people's scopes are never read and cards link to that day's photos.

**WP12 — Density into More filters.** Depends on: WP7. Size: Mechanical. Done when: the Photos top has no Density select and the setting still persists.

**WP14 — Drag and drop feedback.** Photos drop: the slim ribbon gains a progress bar and count (the ribbon today only shows a text message; no toast) with per-file and overall progress and a clear result; non-photo files rejected with the message in D9 while valid files continue; limit "Add media" to photos and videos. The drop target is the whole page on Photos, Documents and each Gallery (on a Gallery, dropped photos are added to the Library and to that Gallery). Documents drop: picture dialog from D10, with a per-profile "Don't ask again" saved in profile preferences (no new storage beyond one preference). Depends on: WP2. Size: Standard. Done when: tests show a document dropped on Photos is rejected with the message, a mixed drop uploads only the valid files with progress, a picture dropped on Documents asks once, and the tick stops the question for that profile only.

**WP13 — Docs.** Update public docs and `presentation-rules.md` for the switch, action list and the two features. Depends on: WP11. Size: Mechanical. Done when: docs build passes.

## 7. Local checks

- **L1.** On a small test library with two profiles and shared photos, switch Mine, Shared and another person; confirm each page follows and the menus match the table. Fail: any edit/trash action on another person's photo.
- **L2.** Import a photo taken years ago; confirm it leads Recently added and sits correctly by date on All.
- **L3.** On this day on Mine with photos dated today in past years, including one in the Shared Library; confirm cards and that nothing from another person appears.
- **L4.** Look pass at desktop, tablet and phone: rail switch, menus in the top layer, no clipping, no browser hover text.
- **L5.** Real media on `Z:\Development\testing\media\real_media`: large timeline stays smooth with Recently added.

- **L6.** Drag a photo, a PDF and a mix onto Photos: progress shows, the PDF is rejected with the message, the photo uploads. Drag a picture onto Documents: the dialog appears; tick "Don't ask again", repeat, and no dialog; check another profile still gets it.

## 8. Risks and open questions

- **Risks:** the switch touches every View page, so wave 2 is the biggest regression risk (Reliability). On this day adds a query per scope (Performance); load it after the timeline and cap the cards. Privacy: only Mine and Shared Library are read.
- **Open questions:** none (107, 109a and 109b answered, see D8). Local check L6 added for drag and drop.

## 9. Gaps and drift

None recorded; the Fable review items map to WP1 to WP9. Not verified in the review: whether native selects pass the ratchet, and whether the gallery editor launcher wraps the v4 frame (check in WP5/WP8).

**Plain English summary:** First we fix where View breaks the app's own rules. Then one switch in the left rail picks whose library you see, and menus offer only what you may do. Last we add the Recently added pill and the On this day strip.
