# Spec: Editing Mode on the item page (as built)

> **Status:** Built · **Version:** 1 · **Last updated:** 2026-10-18 · **Related docs:** [Product overview](../../product/overview.md) · [Item Detail page](item-detail.md) · [Registry retirement](registry-retirement.md) · [Presentation rules (public)](../../../docs/product/presentation-rules.md) · [Inline media editing (public)](../../../docs/architecture/inline-media-editing.md)

> This spec writes down what Editing Mode does today, after PR 223 (merged). It was written by reading the built code on `main` and comparing it with the original plan (the "unified details" plan and its U1 brief). Facts I could not check from the planning side are marked "to be confirmed by Claude Code". Shy's decisions on the gaps in section 8 were recorded on 2026-10-18 (see D14).

## 1. Goal

When something on a title is wrong or missing, a person on a computer fixes it right on the title's own page: press **Edit**, tap a small pencil next to the field, type, save. Bigger jobs (artwork, matching to the right title) open in a side panel beside the page, so the page never disappears behind a pop-up window. Phones, tablets, TVs and apps stay for watching, reading and listening only.

## 2. Scope

**In scope**
- Editing Mode on the page of a single item: a movie, TV show, season, episode, book, audiobook, comic issue, music album and any other single Work.
- The **Edit / Done** switch, pencils, locks, the artwork buttons over the hero, the top buttons, the side panel (the "drawer"), the unsaved-changes question, and the rule for which devices may edit.

**Out of scope**
- Editing people, series shelves, structural collections and curated collections: they keep their own editor window (see D11).
- Editing from Needs Review, browse pages, Recently Added, Listen pages, Universe pages: they keep the editor window.
- Photos and documents (View): they follow the same device rule but have their own editors (see D12).
- Editing several items at once, adding, reordering and moving episodes or tracks, delete: separate plans (batch and structural editing are not built).
- The overlay that opens titles over the current page, and the Details tab contents: covered by the Item Detail spec.
- Who is allowed to edit: the Engine's permission check decides, unchanged.

## 3. Decisions made

Decisions Shy made while the plan was written (2026-10-15 to 2026-10-16), still true:

- **D1.** Editing happens in place, on the item's own page, with a right-hand drawer. It replaces the floating editor window for single items. The drawer is the same editor, not a new management area; Needs Review stays the home for exceptions and Settings stays configuration only.
- **D2.** Editing is desktop-only. Every other device (phone, tablet, TV, car, any native app) is consumption-only: no pencils, no Edit button, no drawer, no edit routes.
- **D3.** A touch-first device at desktop width (an iPad asking for the desktop site) does not edit; a laptop with a touch screen and a mouse does. This replaced the earlier idea of treating an undetectable tablet as a desktop.
- **D4.** No undo and no change journal. A single-field edit saves straight away and can simply be edited again. History is read-only and is not in the drawer.
- **D5.** Provider facts stay read-only, shown with a lock and a short reason. Structural parent moves stay in Match and Identity. Details stays lean (metadata, local fields, sorting).
- **D6.** Editing Mode never starts from a deep link or a reload; leaving the page or pressing Done ends it.

Decisions that describe what was built (recorded here so the next change starts from the truth):

- **D7. One rule, one place.** `EditorDeviceRule.CanEdit` allows editing only when the device class is the web Dashboard, the form factor is desktop, the browser reports a fine pointer, and the pointer can hover. Anything else, including a browser that reports nothing, means no. Every edit handler asks the rule again, so hiding a button is never the only protection. A window dragged to tablet width ends Editing Mode on its own.
- **D8. Permission plus device.** The Edit button shows only when the Engine offers "edit" for this item to this person and the device may edit. While the Engine says editing is unavailable (for example, the item is still being processed), the button shows disabled with the reason.
- **D9. One field open at a time.** Opening a pencil is refused while another field or the drawer has unsaved input. Opening the drawer is refused while a field is open. Save is Enter or the check button; Esc cancels the field first and only the next Esc closes the drawer.
- **D10. Unsaved input is never dropped silently.** Done, closing the drawer and leaving the page ask first, in a notice on the page ("Keep editing / Discard / Save and finish", or "Save and leave" when navigating away).
- **D11. Where Editing Mode does not apply.** People, structural collections and curated collections show no Edit / Done; their More menu "Edit" still opens their own editor window. Phones keep one disabled row "Edit on a computer" in the More sheet when the menu has other items; tablets and TVs show nothing about editing.
- **D12. View (photos, documents) editors follow the same device rule.** Their launchers refuse off-desktop with the toast "Edit on a computer".
- **D13. Fields saved from a profile's own layer stay in that layer.** If a field's current value comes from "This profile", a pencil save writes to that profile's preference layer; every other field saves through the item's display overrides.
- **D14. Gap decisions (2026-10-18).** Shy asked to lock in the spec after reviewing the gaps below. Each gap in section 8 is recorded with its recommended outcome as the decision. Shy can change any of them on request.

## 4. UI behaviour

**What a person sees on a desktop with a mouse**

1. **Edit / Done.** A button on the hero reads **Edit** when off and **Done** when on. It appears only for people who may edit this item (D8).
2. **Pencils.** With Editing Mode on, one small pencil sits beside each editable single-item field: the title, the tagline or hero line, the description, the credits (director, author, narrator and similar), release date or year, runtime, rating, content rating, and genres. The Overview also lists the other editable fields (tags, language, original title and the like) under "Fields". The same field shown twice has one pencil in one place.
3. **Locks.** A provider-owned fact shows a lock instead of a pencil, with a one-line reason: "Set by TMDB. Change it in Match and Identity." A value inherited from a parent says "Inherited from its parent. Change it there."
4. **Editing one field.** The pencil opens the editor's own inline field in place (genres and tags use the tag editor). Enter or the check saves that one field; Esc or the close button cancels. Success shows "Saved" beside the field; a failure keeps what was typed and shows the error beside it.
5. **Artwork buttons.** **Edit Background**, **Edit Logo** and **Edit Cover** float over the hero, only for the slots this item can hold. Each opens the drawer on Artwork for that slot.
6. **Top buttons.** Beside Done: **Manage Artwork** (drawer on Artwork), **Match and Identity** (drawer on the matching and rematch flow, with its existing preview and review gating) and **More** (the usual list of page actions, without Edit).
7. **The drawer.** About 380 pixels wide, slides in on the right and leaves the hero identity visible. The header says "Editing <title>" and names the section. Tabs: **Details**, **Artwork**, **Match and Identity**. History is not here. It closes with X, Esc or Done and gives focus back to what opened it. The same drawer is used for every media type.
8. **Unsaved input.** See D10.

| Situation | What the person sees |
|---|---|
| Normal | The behaviour above. |
| Empty field | The field shows "Not set" in the Overview list with a pencil. |
| Loading | The first press of Edit loads the item's editable fields; the pencils appear when they are ready. |
| Error: fields cannot be loaded | A notice "Editing is not ready" with the reason; Manage Artwork and Match and Identity still work. |
| Error: a save fails | The field stays open with the typed value and the error beside it. |
| Error: not allowed | No Edit button. If a handler is reached anyway, a toast "Edit on a computer" or the Engine's refusal. |
| Edit unavailable right now | Edit shows disabled with the Engine's reason on hover. |
| Partly ready (item still being enriched) | Same as unavailable when the Engine says so; otherwise editing works and later provider refreshes do not overwrite edited fields. |
| Phone | No Edit, no pencils, no drawer. The More sheet shows a disabled "Edit on a computer" row. |
| Tablet | Tablet layout with no edit controls and no mention of editing. |
| TV, car, native app | No edit controls. |
| Kids and other profiles without edit permission | No Edit button, even on a desktop. |

## 5. Data / model changes

None new. Field saves use the existing Engine action `PUT /library/items/{id}/display-overrides` (or the profile preference layer, D13). The drawer re-houses the existing `SharedMediaEditorShell` sections. No new wire types. Nothing is stored about the device: the browser reports its signals and the Dashboard decides. Existing libraries are unaffected.

## 6. Work packets

All built in PR 223 (one proposed change). Listed so a later change can see how the work was cut.

### WP1 — The device rule
- **What it does:** One rule for "may this device edit" used by every launcher and handler; browser reports form factor and pointer; mode ends when the device stops qualifying.
- **Files likely touched:** `src/MediaEngine.Web/Services/Editing/EditorDeviceRule.cs`, `Services/Theming/DeviceContextService.cs`, the launcher services in `Services/Editing/`.
- **Depends on:** none · **Size:** Standard
- **Done when:** `EditorDeviceRuleTests` pass for desktop with mouse (yes) and tablet, phone, TV, unknown, no pointer signal, touch-only (no).

### WP2 — Editing Mode state and toggle
- **What it does:** `EditingModeContext` holds on/off, drawer state and unsaved-input state; `EditingModeToggle` and `EditingModeBar` show Edit / Done and the top buttons.
- **Files likely touched:** `Services/Editing/EditingModeContext.cs`, `Components/Details/EditingModeToggle.razor`, `EditingModeBar.razor`, `DetailHero.razor`, `DetailPage.razor`, `OverflowActionMenu.razor`.
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** `EditingModeTests` show Edit for an editor on a desktop with a mouse and nothing for Kids, phones or tablets.

### WP3 — Pencils, locks and per-field save
- **What it does:** `ItemFieldEditSession` loads fields and saves one at a time; `EditableFieldPencil` and `EditingLibraryFields` place pencils and locks on the hero and Overview.
- **Files likely touched:** `Services/Editing/ItemFieldEditSession.cs`, `Components/Details/EditableFieldPencil.razor`, `EditingLibraryFields.razor`, `HeroMetadataPills.razor`, `OverviewTab.razor`.
- **Depends on:** WP2 · **Size:** Complex
- **Done when:** a saved field shows "Saved"; a failed save keeps the typed value; Esc cancels; provider facts show a lock.

### WP4 — Drawer and artwork buttons
- **What it does:** `EditingDrawer` re-houses the editor on Details, Artwork and Match and Identity (no History); `EditingHeroArtworkButtons` opens it on the right slot.
- **Files likely touched:** `Components/Details/EditingDrawer.razor`, `EditingHeroArtworkButtons.razor`, `Components/MediaEditor/SharedMediaEditorShell.razor(.cs)`.
- **Depends on:** WP2 · **Size:** Complex
- **Done when:** `EditingDrawerTests` show each button opening its own tab, Esc closing a field before the drawer, and focus returning to the opener.

### WP5 — Unsaved-input guard, docs and tests
- **What it does:** Done, drawer close and page navigation ask before discarding; docs and guardrail tests describe Editing Mode and the desktop-only rule.
- **Files likely touched:** `EditingModeBar.razor`, `docs/product/presentation-rules.md`, `docs/architecture/inline-media-editing.md`, `src/MediaEngine.Web/CLAUDE.md`, root `CLAUDE.md`, the editing tests.
- **Depends on:** WP2–WP4 · **Size:** Standard
- **Done when:** the guard tests pass and the docs no longer say "tablets and desktops use the editor".

## 7. Risks and open questions

- **Risks**
  - Three devices were never confirmed from the planning side: the iPad "desktop site" case, a touch laptop with a mouse, and a window resized from desktop to tablet width. Wrong detection would either hide editing from someone who should have it or show it where it should not be. Device checks to be confirmed by Claude Code.
  - The screenshot set the plan asked for (movie, TV show, album, audiobook, book at desktop width, in Editing Mode and with the drawer) and the clean full test run were requested in the brief; whether they were produced is to be confirmed by Claude Code.
  - The drawer re-houses a very large editor (about 7,800 lines of code behind it). Changes there can break both the old window and the drawer; the tests cover both paths.
  - The Dashboard's scoped CSS size limit was raised by about 4 KB for Editing Mode; the next styling addition may hit the limit again.
  - Hiding controls is not the only protection, but the Engine itself does not know the device, so a hand-made call from a phone's browser would still pass the Engine's permission check (the Engine decides who, not where).
- **Open questions:** none. Every gap has a decision (section 8, D14).

## 8. Gaps and drift

- **Built and matches the plan:** the Edit / Done toggle, one pencil per editable field with locks and reasons, per-field save through the existing route (Enter / check saves, Esc cancels, inline success and failure), the three artwork buttons, Manage Artwork, Match and Identity and More, a drawer of about 380 px with Details, Artwork and Match and Identity tabs and no History, the unsaved-input guard, no undo, the phone "Edit on a computer" row, nothing about editing on tablets, and the existing editor window kept for Needs Review, browse pages and Recently Added.
- **Built differently (what changed):**
  - **G1. Fourth signal in the device rule.** The plan named form factor plus pointer; the build also requires the device class to be the web Dashboard, so native app shells can never edit even if they report desktop and a mouse. *Recommendation: keep.* It follows D2 more strictly. **Decision: keep (D7).**
  - **G2. A device that stops qualifying leaves Editing Mode by itself.** Not in the plan. *Recommendation: keep.* It makes the rule hold after a window resize or pointer change. **Decision: keep (D7).**
  - **G3. Unsaved-input question is a notice on the page, not a dialog.** The plan said reuse the navigation lock and guard; the build does, but the question appears as an inline notice with "Keep editing / Discard / Save and finish". *Recommendation: keep.* It keeps the page visible and works with the drawer. **Decision: keep (D10).**
  - **G4. Profile-layer saves.** A field whose value comes from "This profile" saves to the profile's preference layer, not to display overrides. The plan and docs say display overrides only. *Recommendation: keep the behaviour (a save to the wrong layer would silently not show for that profile) and add one sentence to the public docs.* **Decision: keep, docs sentence (D13).**
  - **G5. Edit button disabled with a reason.** The plan only said hidden when not permitted. The build shows it disabled with the Engine's reason when editing is temporarily unavailable. *Recommendation: keep.* **Decision: keep (D8).**
  - **G6. Episode rows still open the editor window.** On a show page, the edit button on an owned episode row opens the editor window (as an episode) instead of using Editing Mode, which the plan said only non-detail callers would do. *Recommendation: keep for now and move it into Editing Mode in the per-type edit pages plan (the plan's stage U2).* **Decision: keep as interim; owner is the per-type edit pages plan.**
  - **G7. People, structural collections and curated collections keep their own editor window from the detail page.** Matches Shy's 2026-10-16 interim decision. *Recommendation: keep until the per-type plan decides.* **Decision: keep (D11).**
  - **G8. Public docs wording.** The brief required the rule to be described as a Dashboard rule, not an Engine boundary; the docs now say so and the Engine's permission check is unchanged. **Decision: keep (D7, risks).**
- **In the plan but not built:**
  - **G9. Per-type edit pages** (series with seasons and episodes, episode breadcrumbs, album tracks, audiobook chapters, book series), **structural actions** (add, reorder, move, delete) and **batch editing** with its preview and confirmations: separate later plans, not part of PR 223. *Recommendation: leave as separate plans.* **Decision: leave (out of scope).**
  - **G10. History inside the Details tab** and removal of the Universe and Related tabs: handled by the Item Detail work, not by Editing Mode. **Decision: leave (see Item Detail spec).**
  - **G11. The overlay host** (titles opening over the page, with Editing Mode inside): separate plan; Editing Mode works on the normal full page today. **Decision: leave.**

## Plain English summary

Editing Mode is finished and works the way Shy wanted: on a computer with a mouse, an Edit button on a title's page turns on little pencils next to each field you may change, plus buttons for artwork and for matching the title to the right record, all in a side panel so the page never disappears. Phones, tablets, TVs and apps can never edit, and the rule is checked again every time something is saved, not just by hiding buttons. Nothing is lost silently: leaving with unsaved typing asks first. A few small differences from the original plan are kept because they make it safer, and the one real loose end is that clicking edit on an episode still opens the old editor window until the per-type edit pages are built.
