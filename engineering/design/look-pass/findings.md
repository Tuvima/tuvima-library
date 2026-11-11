# Look pass: findings inventory (2026-11-04)

Sources: Shy's reports (local checks 2 and 3 Nov, spec audit), the tile menu matrix, and a read-only Fable review of the whole Dashboard code (629 pages and parts, 413 style files, `app.css` at 6,786 lines). Counts are from the code on main. Where the review could not confirm something by eye, it says so.

## A. What Shy reported (look items only)

| # | What Shy sees | Where it comes from in the code |
|---|---|---|
| 1 | Dialog boxes look inconsistent: button colors, text sizes | 6 different dialog builds. Cancel buttons are plain text in 27 places and outlined in 13. Destructive buttons are filled in 8, outlined in 4, text-only in 3. 10 dialogs leave the first button unstyled. Titles run 20 to 32 px. Two different size ladders (420/760/980 and 600/960/1280) |
| 2 | Secondary buttons ("Back", "Skip", "Restore a backup") don't read as buttons | Three levels exist on paper; 377 places use a bare native button that skips them |
| 3 | Libraries and setup icons have no color; Find / Connect / Enjoy and "Private by default" are dull | Icons inherit text color unless told otherwise. Only about 8 icons are given a color on purpose. A ready-made colored icon tile (`AppIconTile`) exists and nobody uses it |
| 4 | Page background should match the intro animation's purple; unexplained green | Intro uses accent #8B5CF6 and backgrounds #0B0920 / #1A1036 / #301B59. App uses #8852FC and #070A12 / #0B1020. The splash screen is #0B0920. Likely greens: the idle Activity indicator (green when idle), the "Private by default" panel (points at a color that does not exist, so a fallback green shows), Library health ticks, and green "Approved/Location" chips on Universe pages. Not confirmed on screen |
| 5 | "..." menu text too small | Menu rows are 34 px with 14 px text in the base style, forced to 48 px by a later rule, and the account menu has its own set (11 to 16 px mixed). Menu width has two competing values |
| 6 | Pills should be square and aligned | 62 places still use fully rounded ends (999 px), 113 use 50%. Only four tile files are guarded. Hand-made pill styles number 147 |
| 7 | Play / Read / Watch button too short | 40 px box holding 24 px text (`HeroActionRow.razor.css:4`) |
| 8 | Resume button crammed | Same 40 px box for two lines of text plus a bar |
| 9 | Thumbs hover circle larger than the button | Button 34 px, hover area 44 px (`MediaRateControl.razor.css`) |
| 10 | Tiles: different heights per row, badges overlap, hover differs, "..." unclear | Six different tile-height rules; separate badge styles for rating, New, section label and Vault; hover effects vary by page |
| 11 | Hero too short | Built to 55 percent of screen height on purpose |
| 12 | Player panels (bookmarks, subtitles, speed, gear), volume, hover behavior | Belongs to the Unified player spec (Locked); this pass only supplies the shared panel, menu and button rules it uses |
| 13 | Metadata/Settings pills misaligned | Hand-made pills; no shared pill component |

## B. What the code review found (beyond Shy's list)

**Components that are never used (safe to remove):** `AppFilterBar`, `AppForm`, `AppFormFieldRow` (carries 20 forced overrides), `AppIconTile` (should be used, see C), `AppMediaTypeSelect`, `AppWarningStrip`. `AppStepIndicator` and `AppOfficialPill` appear only on the design preview page.

**Components that overlap and should merge:**
- Dialogs: `AppDialog`, `AppDialogFrame`, `AppDialogShell`, `AppHostedDialog`, `AppConfirmDialog`, `AppMessageBox`, `AppDrawer`, `AppPopover`. Plus 11 pages that draw their own dialog.
- Menus: `AppOverflowMenu`, `AppRowMenu`, `AppMenuItem`, a second item type, plus 6 pages that draw their own menu.
- Pills: `AppChip`, `AppStatusBadge`, `AppMediaPill`, `AppOfficialPill`, `AppToggleChip`, plus 147 hand-made styles.
- Selects: `AppSelect`, `AppNativeSelect`, `AppIntSelect`, `AppTypedSelect`.
- Cards: `AppCard`, `AppPanel`, `AppListCard`, `AppStateCard`, `AppMetricCard`, plus about 230 hand-made card styles.
- Pagers: `AppPager`, `AppCompactPager`. Tabs: 8 pages draw their own tab strip.
- Buttons: `AppNativeButton` 377 uses (plus 118 for native inputs and selects) bypass the shared button.

**Style sheet health:**
- 1,663 font sizes using 293 different values; only 21 percent use the type tokens.
- 197 different corner values. 15 or so control heights against a scale of 3.
- 958 hard-coded colors outside the token file (the app purple is typed out by hand 99 times).
- 60+ layering (z-index) values, some up to 2,147,483,000.
- 1,504 forced overrides (`!important`).
- 26 tokens used but never defined (for example the one behind the stray green). 32 tokens defined but never used.
- Four color systems at once: the main tokens, an `--app-*` copy, an older `--tl-palette-*` set from the previous UI library, and a `palette.json` file whose colors disagree with the tokens (books green in one, blue in the other).
- 228 classes in `app.css` that nothing references (about 1,000 lines, 15 percent of the file). About 20 may be built on the fly, so check before deleting.
- Old UI-library naming still in use (`AppColor`, `AppVariant`, `AppTypo`).
- Hover: five different glow styles and several zoom amounts on cards.

**What already works:** the shared button, text field, alert, empty/error state, skeleton, switch row and card are used widely. There are no raw `<button>` tags outside the shared folder. Square pill token and guardrail exist for tiles.

## C. Why it feels uneven (plain words)
The pieces to make the app uniform mostly exist, but each page picks its own sizes and colors instead of using them, and nothing forces it. So the fix is two things at once: a small set of rules with one place to change them, and automatic checks that fail the build when a page goes its own way.

## D. Things this pass does not cover (own threads)
Needs Review and Match page redesign; Users and profile layout; Plugins/Integrations pages (Locked v6); Universe pages; the player's own controls (Locked unified player); Libraries and folder picker, Activity page (queued before this).
