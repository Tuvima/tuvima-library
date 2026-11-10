# Spec: One look, version 2 (near-black surfaces, one type and icon ladder, one tooltip)

> **Status:** Locked · **Version:** 1.2 · **Last updated:** 2026-11-06 · **Related docs:** [Look pass](look-pass.md) · [Uniformity review](../design/look-pass/uniformity-review-2026-11-06.md) · [Mockup](../design/mockups/look-pass-v2/unified-look-mockup.html) · [Before and after](../design/mockups/look-pass-v2/settings-before-after.html) · [One tile design](one-tile-design.md) · [Design system](../design/design-system.md)

> **Version note (2026-11-10):** [universal-editor-v5](archive/universal-editor-v5-header-navigation-and-supporting-files.md) (section 9) changes nothing here: the editor keeps these tokens, and the lock and Quick facts use `--tl-accent-primary`.

## 1. Goal

The whole app looks and feels like one product: a calm neutral near-black background (the one the editor and details pages already use), purple only as an accent, one set of text sizes and weights, one icon style, buttons and chips that match everywhere, readable menus and targets on phones, and one good-looking tooltip in place of the browser's.

## 2. Scope

**In scope**
- Neutral near-black surface tokens for the whole Dashboard, and hand-typed colors in View moved onto tokens.
- Amendments to the Look pass (section 6, packets A1 to A12 and WP0).
- Hero buttons (Play, Read, Listen), Like and Dislike, filter chips, tabs, phone menus, touch targets, contrast and focus, and the app's own tooltip.

**Out of scope**
- Anything the Libraries, Activity, One tile and View review specs own. They follow the standards here; this spec does not take their work.
- New features, new screens, and editing on phones (editing stays desktop-only).

## 3. Decisions made

- **D1 (Shy 2026-11-06).** Surfaces are neutral near-black app-wide, not violet: page #101115, nav #14151A, cards #1A1B21, hover #272830, menus and raised #2F3039; the purple glow leaves the page gradient. **This amends Look pass D2 (violet-navy surfaces) as a deliberate override.** Purple (#8852FC, text and icon use the lighter #A46FFF) is an accent only: selected nav row, tab, chip, switch, primary button and focus.
- **D2.** The old `--tl-palette-*`, `--app-*` colors and `palette.json` color conflicts retire in the same change.
- **D3.** View's hand-typed colors (Photos timeline, Places map and timeline, immersive viewer) move onto tokens here.
- **D4.** All 12 packets are built, in waves.
- **D5.** On phones every menu is a bottom sheet by default (52 px rows).
- **D6.** Tooltip: menu-gray bubble, 13 px / 500, 6 px corners, no arrow, max 280 px, Top or Bottom only, 150 ms hover, 0 ms on keyboard focus, 300 ms warm window, long-press 500 ms on touch with a 1.5 s linger. No browser `title=` hints remain, guarded by a test.
- **D7.** Editing stays desktop-only; Edit and Delete never appear in phone sheets (Never rule 1).
- **D9 (Shy 2026-11-06, 119).** Players follow the same rules: packet A13.
- **D10 (Shy, local walk 2026-11-06).** Home's "Pick up where you left off" heading uses a different font size from every other shelf heading (it is 24 px, `ContinueAcrossMediaSection.razor.css:2`, while other shelves use their own). Shelf and section headings become one style: packet A14. Readable tile text for books and other videos is covered by One tile design D7 and WP3 (two-part no-art tile); A14 adds only the text-size rule.
- **D8.** Standards from this spec are handed to the Tiles, View review, Editor and local-check fix threads; the Libraries and Activity builds follow them without taking their work.

## 4. UI behaviour

See the mockup (numbered markers 1 to 8). Standards:

| Area | Standard |
|---|---|
| Page title | 24 / 600 (28 at 1180 and wider); Home hero keeps the hero size |
| Tabs, buttons, menus | 16 / 500-600; helper text 13; body 14; line heights 1.2 / 1.3 / 1.5 |
| Weights | 400 / 500 / 600 / 700; no 800; 700 for titles only |
| Controls | 44 px tall (36 for dense desktop controls); square corners |
| Filter chip | one design: 36 desktop, 44 touch, 14 / 500, one selected look |
| Icons | sizes 16 / 20 / 24 / 32 / 40; Outlined by default, Filled only for a selected or on state; one family |
| Hero row | Play is a plain purple fill, no white border; companions 48 px with Material icons; a rated title shows a filled thumb with purple edge at rest everywhere |
| Menus | 272 px, 44 px rows, 16 / 500, soft red Delete that passes 4.5:1 |
| Phones | all interactive targets at least 44 px; menus as sheets; no popover on a phone-width page |
| Tooltip | per D6; touch shows it only for icon-only controls |

Empty, loading and error states are unchanged by this spec.

## 5. Data / model changes

None. Colors, sizes and icons live in the token file and shared parts.

## 6. Work packets

- **WP0 Near-black tokens (Standard).** Set the D1 values in `wwwroot/tuvima.tokens.css`, remove the purple glow from `--tl-bg-app-gradient` and shell gradient, point splash and `DetailPage.razor.css` / `DetailsOverlayHost.razor.css` at the tokens, retire palette colors (D2), move View hand-typed colors to tokens (D3). *Depends on:* none. *Done when:* a token test asserts the values; no `#0B0920`/`#100B2A` remain; View styles contain no hex colors.
- **A1 Control height and type (Mechanical).** `--tl-control-height-md` 44 (`tokens.css:328`); delete `--tl-control-font-size-lg`; 36 px controls 14, 44/48 px 16; menu text 500. *Depends on:* none. *Done when:* a token test asserts 36/44/48 and the two font steps.
- **A2 Title and line-height tokens (Standard).** `--tl-font-size-title`; line-height ladder 1.2/1.3/1.5; replace the 145 large `clamp()` sizes; ratchet line heights. *Depends on:* A1. *Done when:* no page title outside the tokens.
- **A3 Weights and labels (Mechanical).** Ban 800; one overline style or sentence case; retire `--tl-text-faint`. *Depends on:* A2. *Done when:* the weight ratchet has zero 800 and uppercase labels fall below 20.
- **A4 Icon ladder and one family (Standard).** Size tokens; Filled only with a selected/on condition; retire FontAwesome `AppIcon`; stroke glyphs only in the player; `AppIconTone` wired into `AppMaterialIcon`. *Depends on:* none. *Done when:* the icon guardrail passes and the hero row uses one family.
- **A5 One filter chip and one tab (Standard).** `AppChip` kind filter; replace browse toggles, quick filters, View chips and Places filter buttons; View kind tabs and Browse switcher become the segmented control. *Depends on:* A1. *Done when:* the tab guardrail allowlist is empty except the segmented control.
- **A6 Phone sheets by default (Standard).** The one menu part is a sheet on phones with 52 px rows, including bare `AppOverflowMenu`, the Places filters and the reaction flyout. *Depends on:* Look pass WP3. *Done when:* no popover opens at phone width in the preview page's menu list.
- **A7 Hero and reaction states (Standard).** Rated state filled with accent edge on every surface; Material icons on the hero row; drop the primary's white border. *Depends on:* A4, A6. *Done when:* a bUnit test shows the filled thumb at rest in hero, tile menu and dock.
- **A8 Dialog anatomy fix (Mechanical).** Icon tile 44, close X 44 with 24 px icon. *Depends on:* Look pass WP2. *Done when:* DialogAnatomyGuardrail asserts the sizes.
- **A9 Touch floor (Standard).** Coarse-pointer rule: 44 px minimum for chips, avatar button, tile buttons, Places toolbar; one touch token. *Depends on:* A5. *Done when:* `tools/validate-touch-targets.js` reports zero at 360 and 768 on Home, Watch, View, Settings, a detail page and the editor.
- **A10 Contrast and focus (Mechanical).** Danger text token at 4.5:1 on #2F3039; accent text uses `#A46FFF`; remove the global outline reset (`native-utilities.css:7`). *Depends on:* WP0. *Done when:* a contrast test over the token pairs passes and the focus ring shows on every control in the preview page.
- **A11 Tooltip (Standard).** Rename to `app-tooltip` with `--tl-tooltip-*` tokens and D6 behavior; replace the 133 `title=` hints; retire `AppNativeButton.Title`; add `TooltipGuardrailTests` (allowlist: img, iframe, abbr, time, SVG title). *Depends on:* A1. *Done when:* the guardrail passes and a tooltip shows above a dialog.
- **A13 Players (Standard).** Player text uses the type ladder (remove stray 19 and 22 px); pop-outs and panels (speed, audio, subtitles, queue, context panels) and player menus use the menu gray `#2F3039`, the menu radius and 44 px rows, with a dark scrim over video allowed but no blue tint (retire `--playback-popover-surface` `rgba(18,22,35,.96)` and the `--playback-*` surface family in favor of the shared tokens); square action buttons instead of round (`PlaybackSongActions.razor.css:3`); one icon family; A11 tooltip; pop-outs become bottom sheets on phones. Coordinates with the thread validating players against their mockups. *Depends on:* WP0, A1, A4, A11. *Done when:* no `--playback-*` surface token differs from the shared tokens, and a player pop-out measures 44 px rows on the preview page.
- **A14 Shelf headings and tile text (Mechanical).** One shelf and section heading style (20 / 600, line height 1.3) used by Home, Continue, Watch, Read, Listen and View shelves, including `ContinueAcrossMediaSection`; tile text inside a tile (no-art title and detail line) is never smaller than 13 px, wraps to two lines with an ellipsis and is never clipped by the tile edge. *Depends on:* A2, One tile design WP3. *Done when:* a test asserts every shelf heading uses the shelf heading token, and a no-art book and video tile render their full title on two lines in the preview page.
- **A12 Visual proof (Standard).** Before/after of one Home shelf, one Settings page, the View places toolbar, the detail hero and a tooltip at 360, 768 and 1440 (added to Look pass WP15). *Depends on:* WP0, A1 to A11.

Order: WP0, A1, A4, A10 first; then A2, A5, A6, A11; then A3, A7, A8, A9; A12 last (A13 with the third wave). A1 and A8 touch files Look pass WP2 to WP4 own, so coordinate with that build.

## 7. Local checks

- L1. On SHY-DESKTOP, look at Home, Settings, a detail page, View (Photos, Places) and the editor: all near-black, purple only on selected things.
- L2. Hover a few icon buttons: the tooltip appears fast, above the item, never clipped, also inside dialogs.
- L3. On a phone (or 360 px window): menus open as bottom sheets, nothing is smaller than a thumb, no Edit or Delete in sheets.
- L4. Like a title: the thumb stays filled on the detail page after reload.

## 8. Risks and open questions

- Touches nearly every page; built in waves with the ratchets so it can be reverted by token.
- Cloud builds cannot take screenshots, so look checks are Local checks.
- Assumptions about Netflix and HBO Max specifics are labeled in the review.
- Overlaps Look pass WP2 to WP4 and the One tile spec; sequence with those builds.
