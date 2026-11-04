# Spec: One look for the whole app (look pass)

> **Status:** Locked (Shy, 2026-11-04) · **Version:** 1 · **Last updated:** 2026-11-04 · **Related docs:** [Design system](../design/design-system.md) · [One shared look (Built)](one-shared-look.md) · [Spec audit 2026-11-03](../validation/spec-audit-2026-11-03.md) · [Local check 3 Nov](../validation/local-check-2026-11-03.md) · [Local check 2 Nov](../validation/local-check-2026-11-02.md) · [Tile menu matrix](../design/look-pass/tile-menu-matrix.md) · [Findings inventory](../design/look-pass/findings.md) · [Mockups](../design/mockups/look-pass/look-pass-mockups.html)

## 0. What exists today (verified in code, 4 Nov, Fable review)
See the findings inventory for the full list. In short: the shared parts mostly exist, but pages bypass them. 6 dialog builds, 3 menu row definitions, 293 font sizes, 197 corner values, 958 hand-typed colors, 1,504 forced overrides, 62 rounded pills left, 6 unused components, about 1,000 lines of unused style rules, four competing color systems, and no rule that says which icon gets which color.

## 1. Goal
Every screen looks and behaves like one product: the same dialog, the same buttons, the same menu, the same pills, the same icon colors, the same text sizes, all controlled from a few central places, and the build refuses a page that goes its own way. Old and unused code goes away. (Uniformity, Maintenance, Extensibility, Performance: fewer, smaller style rules.)

## 2. Scope
**In scope**
- App-wide rules (section 4) and the central controls that enforce them.
- Dialogs, buttons, menus, tiles, pills and badges, icon colors, the color set (including the intro-animation purple), type scale, spacing, corner shapes, hover and focus.
- Folding in every look item from the 2 and 3 Nov local checks and the spec audit (section 7 maps each).
- Merging duplicate shared parts, deleting unused parts and dead style rules, retiring the old UI-library naming and the extra token systems.
- New automatic checks (guardrails) so each rule stays fixed.
- A final Fable review of the finished work (WP16).

**Out of scope** (own threads or Locked specs)
- Libraries redesign and folder picker, Activity page (queued before this).
- Player controls and panels beyond using the shared rules (Unified player, Locked).
- Editor layout (Universal editor v4), Plugins and Integrations screens v6, Universe contents and entry points, Photos and documents editor: layout stays theirs; they adopt these rules (section 6).
- Needs Review and Match page redesign, Users and profile layout, QR pairing.
- Light theme (the app stays dark-only).

## 3. Decisions made
Existing (from Shy, earlier): square pills app-wide; icons with color; (i) tooltips instead of explanatory text; no toasts; centered Settings pages; Tuvima Sans only for top nav and brand; US English; same control means same look everywhere; Delete is instant after an "are you sure"; tiles wrap naturally, no centering; "Mark as watched" removed; Like and Dislike only; Listen uses Favorites; no trailer button (trailer is an Extra); a reference screenshot is representative, not a pixel spec.

Answered by Shy 2026-11-04 06:04Z (all option A, as recommended):
- **D1.** Dialog anatomy as the reference screenshot: tinted icon tile, bold title, close X, body, and two buttons on the right. The safe action is filled purple and focused; the destructive action is outlined red and to its left. Delete uses the same pattern.
- **D2.** Page and panel surfaces and the launch splash move to the intro animation's violet-navy; the accent becomes one purple everywhere.
- **D3.** One color per area: Watch rose, Listen amber, Read blue, View teal; Vault gold. Used for the area's icon tile, label and filter chip everywhere.
- **D4.** Green means healthy or done, nothing else. The idle Activity indicator stops being green.
- **D5.** Icon buttons are rounded squares (the same 10 px corners as every control), not circles. Round stays only for avatars, switches, progress bars, seek bars and the player dock.
- **D7.** Native controls (about 495 places) are all migrated to the shared parts as the last wave (WP10), not just ratcheted.
- **D6.** Existing look-related Locked specs are not rewritten; each gets a short "Look pass addendum" saying it uses these rules, as a new version note.

## 4. The rules (what the pass builds)

### 4.1 Type scale (one ladder, tokens only)
12 caption · 13 secondary · 14 body · 16 control, menu and dialog body · 18 lead and button on hero · 20 dialog title · 24 page title · 32 hero title. Weights 400 / 500 / 600 / 700. Nothing under 12. A page may not write its own font size; it picks a step.

### 4.2 Buttons (three levels, three heights)
- **Primary** (filled purple, one per screen), **Secondary** (clearly outlined), **Quiet** (text only), **Destructive** (outlined red, never filled).
- Heights: **36** (dense desktop rows), **44** (standard, touch-safe), **48** (hero Play, Read, Watch, Listen with 18 px text). Playback transport keeps its documented 56 px targets.
- Hero action row: Play and Resume are the same 48 px size and one line. Resume shows progress as a thin bar along the bottom edge. Companion icon buttons are 48 px rounded squares. The hover highlight is exactly the button's own box (fixes the 34 versus 44 px thumbs mismatch).
- Cancel is always Secondary, never bare text. Back, Skip, Restore a backup are Secondary or Quiet, never unstyled.

### 4.3 Dialogs
One build (`AppDialogShell`), sizes Small 420 / Medium 640 / Large 960; phones show a bottom sheet. Anatomy: 44 px icon tile in the severity color (amber warning, red removes things, purple question, blue information), 20 px bold title, 36 px close X, 16 px body (one to two short lines saying what happens and what stays), footer right-aligned with the safe filled button last and focused. Escape, backdrop click and X all mean the safe choice. Setup dialogs may use the 24 px title. Confirm dialogs are `AppConfirmDialog` only; `AppMessageBox` renders it.

### 4.4 Menus (the "..." menu, account menu, overflow menus)
One menu part. Rows 44 px, 16 px text, 22 px icons in the icon color rules, 13 px right-aligned hint, 272 px wide (one width token), thin dividers between groups, Delete red and last. On phones the same menu is a bottom sheet with larger rows. The tile menu content and order follow the tile menu matrix (Like / Dislike only, Favorites for Listen, "<Type> Details", "Remove from Continue ..." in the menu and the detail "..." menu, no X on Continue tiles). No page may set its own menu row size.

### 4.5 Tiles and shelves
Every tile in a row is the same height (square albums are wider, equally tall); portrait tiles in lanes, landscape only for Continue; hero at least 70 percent of screen height on desktop (value confirmed on the real app, one token); at most one small square badge per tile ("New"), top left, never overlapping; the "..." button is always visible, bottom right of the cover, 36 px; one hover everywhere: bold white border with purple ring around the cover only, no growth, title never covered. Rows wrap naturally and are left-aligned (no centering).

### 4.6 Pills, badges, chips
One chip part with kinds (status, media type, official, plain, toggle). Square 3 px corners. Allowed round things: avatars, switches, progress and seek bars, spinner, player dock. Pills in one row share height and baseline.

### 4.7 Color and icons
- One purple (`#8852FC`, hover `#A46FFF`), surfaces move to the intro's violet-navy (final values picked from the animation, written once in the token file). Splash and intro animation read from the same tokens.
- Icon tone is chosen by meaning, in one place: Neutral, Accent (actions on the item), Info (blue, details and links), Success (green, healthy and done only), Warning (amber), Danger (red, delete and broken only), Area (Watch, Listen, Read, View, Vault), Media type. A page asks for a tone, never a color. The ready-made icon tile (soft 14 percent wash in the tone) is the one tile used for Libraries, setup (Find, Connect, Enjoy, Private by default), settings sections and dialog headers.
- No hex colors in feature styles; the old `--app-*`, `--tl-palette-*` and `palette.json` colors are retired and the media-type colors have one source.

### 4.8 Spacing, shape, layering
Spacing steps 4 / 8 / 12 / 16 / 20 / 24 / 32 / 40. Corners 4 / 6 / 8 / 12 and controls 10. Layers only from the layer tokens (menu, popover, dialog, toast, player). Forced overrides are not allowed in new code and the count ratchets down.

### 4.9 Central controls
All of the above live in `tuvima.tokens.css` plus the shared parts. Changing a value in the token or the shared part changes every page. A new "Look rules" page in the design-system doc lists the tokens and parts, and the in-app design preview page shows every part in every state.

## 5. UI behavior
| Situation | What the person sees |
|---|---|
| Any dialog | Same shape, buttons and keyboard behavior; destructive is outlined red and never the focused button |
| Phone | Dialogs and menus become bottom sheets; buttons stay 44 px or larger; "..." always visible on tiles |
| TV | Menus open on a long press of select, same order; focus ring replaces hover |
| Empty, loading, error | Unchanged; they use the same shared state parts |
| Reduce motion | No hover growth anywhere (there is none), transitions shorten |

## 6. Effect on Locked, unbuilt specs (addendum, not rewrite)
- **Unified player:** uses the shared panel, menu rows, button heights and volume/hover rules; bookmarks, subtitles, speed and gear panels follow 4.3 and 4.4 (local check items 31 to 33, 48 to 52).
- **Universal editor v4 and Photos and documents editor:** Discard, Save, Review and delete dialogs use 4.3 exactly (the reference screenshot is the Discard dialog); the editor's own layout is unchanged.
- **Plugins and Integrations screens v6:** tile icons and status ticks use the icon tone rules; no stray green.
- **Universe entry points and Universe contents:** the Universe icon and dropdowns use the shared icon tone and menu parts.
- **Libraries redesign v2 (being written):** area chips and icons use the Area tones in 4.7.
Each of these gets a short "Look pass addendum" in its next version note.

## 7. Where each reported item goes
Dialogs, button colors, font sizes: WP2, WP4, WP8. Uncolored icons and stray green: WP1, WP5. Purple background: WP1. Menu fonts: WP3. Pills square and aligned: WP6. Play, Resume, thumbs sizes: WP4. Tile height, badges, hover, "...": WP7. Hero height: WP7. Secondary buttons without a border: WP4. Player panels: Unified player. Needs Review look and Users layout: own threads.

## 8. Data / model changes
None. Pre-release: no migrations. The old tokens are deleted outright.

## 9. Work packets
Built in the cloud, at most two at once, in waves. Every packet ends with the build and the affected tests; the full checks run once at the end of each wave.

**Wave 0 (foundation)**
- **WP1 Tokens and colors.** Define the 26 missing tokens or remove their users; delete the 32 unused ones; add area, tone, layer and menu-width tokens; violet-navy surfaces and the single purple; point intro animation, splash and launch screen at the tokens; retire `--tl-palette-*`, `--tl-zindex-*`, `--app-*` and the `palette.json` color conflicts. *Files:* `wwwroot/tuvima.tokens.css`, `app.css`, `AppThemeStyles.cs`, `Setup/IntroAnimation.razor.css`, `config/ui/palette.json`. *Depends on:* none. *Size:* Standard. *Done when:* a token-resolution test fails on any undefined `--tl-*` and passes; intro and app use the same purple.
- **WP2 Dialogs.** One dialog build per 4.3; migrate all 6 paths and the 11 hand-built ones; delete `AppHostedDialog`; fix the 10 unstyled footers; `AppMessageBox` renders `AppConfirmDialog`. *Depends on:* WP1. *Size:* Complex. *Done when:* DialogAnatomyGuardrail passes with no allowlist beyond the viewer overlays, and the Discard dialog matches the mockup.

**Wave 1**
- **WP3 Menus.** One menu part and one item part per 4.4; remove the 34 px / 48 px / account-menu variants and the second item type; retire the 6 hand-drawn menus. *Depends on:* WP1. *Size:* Standard. *Done when:* MenuMarkupGuardrail has no per-page size rules; menu rows measure 44 px, 16 px on every menu.
- **WP4 Buttons.** Heights 36 / 44 / 48; hero action row, Resume, thumbs and companion buttons per 4.2; icon-button shape (D5); Secondary and Quiet everywhere a bare "Back", "Skip", "Cancel" exists. *Depends on:* WP1. *Size:* Standard. *Done when:* hero Play measures 48 px with 18 px text, Resume matches, hover area equals button box (test), no unstyled secondary buttons.
- **WP5 Icons and area colors.** Icon tone enum and one tone map; `AppIconTile` becomes the one tile; apply to Libraries, setup welcome and Private panel, settings sections, Universe and tile menus; remove stray greens (idle Activity indicator, Private panel fallback, misused Success chips); delete `--area-colour-*`. *Depends on:* WP1. *Size:* Standard. *Done when:* IconToneGuardrail passes; Libraries icons render in area colors.

**Wave 2**
- **WP6 Pills and corners.** One chip part; replace the 62 rounded ends and the hand-made pills in waves; ratchet to zero outside the allowlist. *Depends on:* WP1, WP4. *Size:* Complex. *Done when:* RadiusGuardrail passes.
- **WP7 Tiles, shelves, hero.** 4.5 in full, including the tile menu matrix content, one-badge rule, uniform heights, hero height token, one hover. *Depends on:* WP3, WP4, WP6. *Size:* Complex. *Done when:* tile-height test per page type, tile hover guardrail covers every tile file.
- **WP8 Type scale.** Map the recurring ad-hoc font sizes to the ladder, starting with the largest 10 files; ratchet. *Depends on:* WP1. *Size:* Complex (mechanical in most places). *Done when:* TypeScaleGuardrail ratchet drops from 1,308 ad-hoc to the agreed number and the 11 px and 10 px literals are gone.

**Wave 3 (merge and clean)**
- **WP9 Merge duplicates.** Cards, selects, pagers, tabs (8 hand-drawn tab strips onto the shared tabs). *Depends on:* WP6. *Size:* Standard.
- **WP10 Native controls.** Move pages from the bare native button, input and select to the shared ones, in waves by folder (Pages, Listen, Watch, Details, Settings); leave an allowlist for playback and the reader. *Depends on:* WP4. *Size:* Complex. *Done when:* count falls from 495 to the allowlist.
- **WP11 Delete the old.** Unused components, dead style rules (verify the dynamic ones first), old UI-library naming, `tl-u-*` color utilities, forced overrides ratchet. *Depends on:* WP9, WP10. *Size:* Standard. *Done when:* DeadCssGuardrail and a no-unused-component test pass.

**Wave 4 (lock it in)**
- **WP12 Guardrails.** Radius, type scale, color, native control, dialog anatomy, token, layer, tabs, dead CSS, icon tone, as ratchets where full zero is not yet possible. *Depends on:* WP2 to WP11. *Size:* Standard.
- **WP13 Docs.** Update `design-system.md` (retire stale notes), the public UI consistency page with the why behind each rule, and the in-app design preview page. *Depends on:* WP12. *Size:* Standard.
- **WP14 Addenda.** Add the look-pass note to each affected Locked spec (section 6) as a new version note. *Depends on:* none. *Size:* Mechanical.
- **WP15 Visual proof.** Screenshots of dialogs, menus, hero, tiles, Libraries, setup at phone, tablet and desktop width, published as one Artifact page; no screenshots committed. *Depends on:* WP7. *Size:* Standard.
- **WP16 Fable review.** Review of the whole result against sections 4 and 7, before the final merge. *Depends on:* all. *Size:* Standard.

## 10. Local checks
Walk on SHY-DESKTOP, fresh session, small test library: open every dialog type (Discard, Delete, Save, Review) and compare to the mockup; the "..." menu on a movie, a Continue tile, an album and a person; hero Play, Resume and thumbs at 1440 and 1280; a row of mixed tiles (portrait and square) on Home, Watch, Listen and Read; Libraries and setup welcome for icon colors; find any remaining green; Settings pages for aligned square pills; phone width for bottom sheets.

## 11. Risks and open questions
- Changing the surface tint touches every page: visual proof is required; it is one token change so it can be reverted quickly.
- Migrating 495 native controls is large and touches many pages; it runs last and as a ratchet so nothing blocks the visible fixes.
- Dead-style deletion could remove classes built on the fly; each deletion is checked first.
- The hero height value (70 percent) needs a look on the real app.
- The "stray green" is not confirmed on screen; Q4 asks Shy to point it out if the guesses are wrong.

## 12. Gaps and drift
- Design system doc says one tab style and no hand-built tabs; 8 pages still draw their own (WP9).
- Design system names `AppWarningStrip` and `AppOfficialPill` as current; the first is unused and the second only shown on the preview page (WP11/WP13).
- `palette.json` and tokens disagree on media colors (WP1).

## Plain English summary
The app already has the building blocks for a consistent look, but each page picks its own sizes and colors, so it feels uneven. This plan sets one shared rulebook (one dialog, one set of buttons, one menu, square pills, a color for each kind of media, one set of text sizes), puts every value in one central place, tidies up the dozens of duplicate or unused pieces, and adds automatic checks so pages cannot drift again. The dialog in your picture becomes the standard for every dialog. The work is built in the cloud in four waves, with a final review by the strongest model, and you check the result in one pass on your PC.
