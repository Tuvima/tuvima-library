# Tuvima design system

> **Status:** Current · **Last updated:** 2026-11-07 · **Spec:** [Look pass v3](../specs/look-pass-v3.md) · **Proof page:** `/design-system/components` (`DesignSystemPreview.razor`) · **Related docs:** [Product overview](../product/overview.md) · [UI consistency standard (public)](https://docs.tuvima.com/products/library/design-system/ui-consistency-standard/) · [Dashboard rules](../../src/MediaEngine.Web/CLAUDE.md)

The look, voice and component rules for the Tuvima Dashboard. Every value below is copied from `src/MediaEngine.Web/wwwroot/tuvima.tokens.css` (tokens named `--tl-*`, plus `--font-*`). The token file wins: if this page and the file disagree, fix this page. `DesignSystemDocDriftTests` fails when a color, type, icon, row or window value here differs from the file, or names a token that does not exist. The proof page shows every token and every state of every shared part.

## Character

**Cinematic, minimalist, dark-only** (`color-scheme: dark`). Neutral near-black surfaces, one purple accent for the app's own controls, and the media's own artwork as the star. No patterns, textures or illustrations. Covers are shown as-is (never filtered, never cropped beyond their poster ratio). Why one look: when the same control looks the same everywhere, people learn the app once, and a restyle is a small edit in one place instead of a hunt through hundreds of files.

## Voice

- Warm, literary, a little reverent. Tuvima **presents** a library the person already owns; it never "creates" one.
- Confident and direct; no hype, **no exclamation marks**, **no emoji**.
- Talk to **you**, never "users". Talk about the product in the third person ("Tuvima reads each one").
- **Sentence case** for body text, menus, labels and messages; **Title Case** for page titles, tabs, section headings and buttons. The wordmark is the only uppercase text.
- Proper nouns: Library, Universe, Series, Work, Edition, Engine, Dashboard, Read, Watch, Listen, View, Collections, **Needs Review**.
- Middle dot `·` separates metadata ("Neil Gaiman · Audiobook · 2019").

## Color roles

Surface tokens are the only backgrounds in the Dashboard. Purple appears only as: selected (nav row, tab underline, chip, menu row), the primary button, a switch that is on, progress, the focus ring and the Like state. Area colors appear only in chips, icon tiles and area labels. Status colors appear only on status. Nothing else is colored, and a guardrail fails a component style that contains a hex or `rgb()` color outside its allowlist. Icons ask for a tone (neutral, accent, info, success, warning, danger, an area or a media type), never a color.

### Surfaces

| Role | Token | Value | Used for |
|---|---|---|---|
| Page | `--tl-bg-root` | `#101115` | The page behind everything |
| Navigation and rail | `--tl-bg-shell` | `#14151A` | Top navigation, section rails, the shell |
| Card | `--tl-bg-surface` | `#1A1B21` | Cards, panels, resting surfaces |
| Raised | `--tl-bg-surface-raised` | `#1F2027` | Surfaces one step above a card |
| Hover | `--tl-bg-surface-hover` | `#272830` | Hover wash on cards and rows |
| Menu and elevated | `--tl-bg-surface-elevated` | `#2F3039` | Menus, dropdowns, tooltips, pop-outs |
| Overlay | `--tl-bg-overlay` | `rgba(0, 0, 0, 0.72)` | The scrim behind a Window |

### Text

| Role | Token | Value | Used for |
|---|---|---|---|
| Primary text | `--tl-text-primary` | `#F5F7FB` | Titles, labels, values |
| Secondary text | `--tl-text-secondary` | `#B6C2D6` | Helper lines, menu icons, the default icon tone |
| Muted text | `--tl-text-muted` | `#94A3B8` | Captions, metadata |
| Disabled text | `--tl-text-disabled` | `rgba(148, 163, 184, 0.58)` | Disabled controls |
| Text on accent | `--tl-text-on-accent` | `#FFFFFF` | Text on a purple fill |
| Text on warning | `--tl-text-on-warning` | `#1F1303` | Text on an amber fill |
| Text on Vault gold | `--tl-text-on-collection` | `#17120A` | Text on a gold fill |
| Danger text | `--tl-text-danger` | `#FF8A8A` | Error text and destructive labels |

### Borders

| Role | Token | Value | Used for |
|---|---|---|---|
| Subtle border | `--tl-border-subtle` | `rgba(148, 163, 184, 0.10)` | Hairlines between rows, the rail divider |
| Default border | `--tl-border-default` | `rgba(148, 163, 184, 0.16)` | Fields, cards, menus |
| Strong border | `--tl-border-strong` | `rgba(182, 194, 214, 0.46)` | Outlined buttons |
| Divider | `--tl-divider` | `rgba(148, 163, 184, 0.10)` | Dividers inside a surface |

### Accent and Vault gold

| Role | Token | Value | Used for |
|---|---|---|---|
| Accent | `--tl-accent-primary` | `#8852FC` | Selected, primary button, switch on, progress, Like |
| Accent hover | `--tl-accent-primary-hover` | `#A46FFF` | Hover on the accent; accent text and icons |
| Accent pressed | `--tl-accent-primary-active` | `#7652D6` | Pressed accent |
| Accent soft | `--tl-accent-primary-soft` | `rgba(136, 82, 252, 0.16)` | Selected wash (menu row, chip, rail row) |
| Accent border | `--tl-accent-primary-border` | `rgba(136, 82, 252, 0.42)` | Border of an accent chip |
| Accent glow | `--tl-accent-primary-glow` | `rgba(136, 82, 252, 0.28)` | Focus and hover glow |
| Focus ring | `--tl-focus-ring` | `#A78BFA` | Keyboard focus ring |
| Vault gold | `--tl-accent-collection` | `#D6A84F` | Vault and curated collections |
| Vault gold hover | `--tl-accent-collection-hover` | `#E2B75E` | Hover on gold |
| Vault gold soft | `--tl-accent-collection-soft` | `rgba(214, 168, 79, 0.14)` | Gold wash |

### Menus and dropdowns

Neutral: the elevated surface, the default border, a neutral hover wash, a purple wash only on the selected row, neutral icons. No purple border on a menu.

| Role | Token | Value | Used for |
|---|---|---|---|
| Menu surface | `--tl-menu-surface` | `var(--tl-bg-surface-elevated)` | Menu and dropdown background |
| Menu border | `--tl-menu-border` | `var(--tl-border-default)` | Hairline around a menu |
| Menu divider | `--tl-menu-divider` | `var(--tl-border-subtle)` | Line between menu groups |
| Menu hover | `--tl-menu-item-hover` | `rgba(255, 255, 255, 0.06)` | Neutral 6% white wash on a hovered row |
| Menu selected | `--tl-menu-item-selected` | `var(--tl-accent-primary-soft)` | Purple wash on the selected row |
| Menu icon | `--tl-menu-icon` | `var(--tl-text-secondary)` | Menu icons stay neutral |

### Status

| Role | Token | Value | Used for |
|---|---|---|---|
| Success | `--tl-status-success` | `#22C55E` | Healthy, done, matched |
| Info | `--tl-status-info` | `#38BDF8` | In progress, informational |
| Warning | `--tl-status-warning` | `#F59E0B` | Needs attention, amber reasons |
| Danger | `--tl-status-danger` | `#EF4444` | Failed, destructive |
| Unmatched | `--tl-status-unmatched` | `#FB7185` | Not matched |
| Idle | `--tl-status-idle` | `#7F8DA5` | Off, skipped |
| Success soft | `--tl-status-success-soft` | `rgba(34, 197, 94, 0.14)` | Success wash |
| Info soft | `--tl-status-info-soft` | `rgba(56, 189, 248, 0.14)` | Info wash |
| Warning soft | `--tl-status-warning-soft` | `rgba(245, 158, 11, 0.14)` | Warning wash |
| Danger soft | `--tl-status-danger-soft` | `rgba(239, 68, 68, 0.14)` | Danger wash |

### Areas

| Role | Token | Value | Used for |
|---|---|---|---|
| Watch | `--tl-area-watch` | `#FB7185` | Watch chips, icon tiles, labels |
| Listen | `--tl-area-listen` | `#FBBF24` | Listen chips, icon tiles, labels |
| Read | `--tl-area-read` | `#60A5FA` | Read chips, icon tiles, labels |
| View | `--tl-area-view` | `#2DD4BF` | View chips, icon tiles, labels |
| Vault | `--tl-area-vault` | `#D6A84F` | Vault chips, icon tiles, labels |

**Media-type colors** (chips and badges only, never page chrome): books `--tl-media-books`, audiobooks `--tl-media-audiobooks`, movies `--tl-media-movies`, TV `--tl-media-tv`, music `--tl-media-music`, comics `--tl-media-comics`, games `--tl-media-game`, documents `--tl-media-document`, unknown `--tl-media-unknown`. Provider badges use the provider's own brand color.

## Typography

One interface typeface, **Inter** (variable, SIL OFL 1.1, self-hosted woff2), so the Dashboard looks the same on every device. Tuvima Sans is the wordmark and top navigation only.

| Face | Token | Value | Used for |
|---|---|---|---|
| Inter | `--font-ui` | `"Inter", system-ui, -apple-system, sans-serif` | Every piece of interface text |
| Tuvima Sans | `--font-brand` | `"Tuvima Sans", sans-serif` | The wordmark and top navigation only |
| Merriweather | `--font-reader` | `"Merriweather", Georgia, serif` | The book reader |
| JetBrains Mono | `--tl-font-mono` | `'JetBrains Mono', 'Cascadia Code', Consolas, monospace` | Paths, URLs, identifiers |

Sizes are a ladder; a page picks a step and never writes its own size. Hand-typed sizes may not appear in component styles.

| Step | Token | Value | Used for |
|---|---|---|---|
| 12 caption | `--tl-font-size-xs` | `0.75rem` | Captions, metadata, the tile text floor |
| 13 secondary | `--tl-font-size-secondary` | `0.8125rem` | Helper lines, form labels, tooltips |
| 14 body | `--tl-font-size-body` | `var(--tl-font-size-sm)` | Body copy |
| 16 controls | `--tl-font-size-md` | `1rem` | Controls, menus, rows |
| 20 section | `--tl-font-size-dialog-title` | `1.25rem` | Section and shelf titles, window titles |
| 24 page title | `--tl-font-size-title` | `1.5rem` | Page titles (28 at 1180 px and wider) |
| 32 hero | `--tl-font-size-hero` | `2rem` | Hero titles |

| Weight | Token | Value | Used for |
|---|---|---|---|
| Regular | `--tl-font-weight-regular` | `400` | Body copy |
| Medium | `--tl-font-weight-medium` | `500` | Labels, menu rows, chips |
| Semibold | `--tl-font-weight-semibold` | `600` | Group headings, card and row titles |
| Bold | `--tl-font-weight-bold` | `700` | Page and hero titles only |

Line heights 1.2 / 1.3 / 1.5 (`--tl-line-height-title`, `-heading`, `-body`). No uppercase labels; if a page needs an overline it is 12 / 600 with 0.06em spacing.

## Spacing and shape

| Step | Token | Value | Used for |
|---|---|---|---|
| 4 | `--tl-space-1` | `4px` | Gaps and padding |
| 8 | `--tl-space-2` | `8px` | Gaps and padding |
| 12 | `--tl-space-3` | `12px` | Gaps and padding |
| 16 | `--tl-space-4` | `16px` | Gaps and padding |
| 20 | `--tl-space-5` | `20px` | Gaps and padding |
| 24 | `--tl-space-6` | `24px` | Gaps and padding |
| 32 | `--tl-space-8` | `32px` | Gaps and padding |
| 40 | `--tl-space-10` | `40px` | Gaps and padding |

| Name | Token | Value | Used for |
|---|---|---|---|
| Extra small | `--tl-radius-xs` | `4px` | Small insets |
| Small | `--tl-radius-sm` | `6px` | Small controls |
| Medium | `--tl-radius-md` | `8px` | Cards, panels |
| Extra large | `--tl-radius-xl` | `12px` | Windows and large cards |
| Control | `--tl-control-radius` | `10px` | Buttons and fields |
| Label and chip | `--tl-radius-pill` | `3px` | Chips and labels (square, not round) |
| Round | `--tl-radius-round` | `999px` | Avatars, switches, progress and seek bars, the player dock only |

Tiles never grow or change shape on hover: the cover gets one 2 px purple outline. Focus is a 3 px ring (`--tl-focus-ring`, `--tl-focus-ring-width`) with a 2 px offset on every interactive element. Motion is 140 / 220 / 360 ms (`--tl-motion-duration-fast`, `-medium`, `-slow`); no bounces, springs or parallax; reduced motion is respected. Shadows appear on floating things only (menus, windows, the hero), not on resting cards.

## Icons

One size ladder with six names; the icon part takes only these and nothing else sizes an icon.

| Size name | Token | Value | Used for |
|---|---|---|---|
| Inline16 | `--tl-icon-size-xs` | `16px` | Inline and in chips |
| Control20 | `--tl-icon-size-sm` | `20px` | Inside 36 px controls |
| Row24 | `--tl-icon-size-md` | `24px` | Inside 44 and 48 px controls, menus, rail rows, list rows |
| Tile32 | `--tl-icon-size-lg` | `32px` | Dialog and card icon tiles |
| Choice48 | `--tl-icon-size-xl` | `48px` | Choice tiles |
| Hero64 | `--tl-icon-size-2xl` | `64px` | Empty states, setup step heroes |

Two placements. **Above text (a choice tile, `AppChoiceTile`):** a square at least 120 px, the icon (48 px) on a soft tinted square (the area color at `--tl-tone-wash` for area tiles, neutral otherwise), the label under it at 14 / 500, an optional second line at 13 / 400 muted, centered. Empty states use a 64 px icon with no tinted square. **Beside text (a row or button):** the icon leads, 24 px in rows and 44 px buttons, 20 px in 36 px buttons, 12 px gap, centered on the first line of text, in `--tl-text-secondary` unless the row is selected or carries a tone. Icons never sit right of their label except a trailing chevron or a "..." in a row.

## Rows and heights

Every row is one flex line with label, helper and control centered on one axis. Row heights come from a ladder, and the controls inside them from the control heights.

| Row | Token | Value | Used for |
|---|---|---|---|
| Compact | `--tl-row-h-compact` | `44px` | Menu, rail and toolbar rows |
| Default | `--tl-row-h-default` | `56px` | Settings and list rows |
| With a picture | `--tl-row-h-media` | `64px` | Rows that carry a picture |
| Label column | `--tl-row-label-column` | `200px` | Left column of a settings or list row |
| Field label gap | `--tl-field-label-gap` | `6px` | Between a form label and its field |
| Small control | `--tl-control-height-sm` | `36px` | Dense desktop rows |
| Standard control | `--tl-control-height-md` | `44px` | Standard, touch-safe controls |
| Large control | `--tl-control-height-lg` | `48px` | Hero and large controls |

A label may wrap only in a settings or list row, and then the control stays centered on the whole row. In a toolbar or button row a label shortens with an ellipsis and the full text goes in a tooltip.

## Windows

Everything that floats over the page is one of four kinds, each one shared part. Hand-built dialog markup is retired.

| Kind | Token | Value | Used for |
|---|---|---|---|
| Window S | `--tl-window-s` | `480px` | A confirmation or a short form |
| Window M | `--tl-window-m` | `640px` | A form or a picker |
| Window L | `--tl-window-l` | `900px` | A workspace such as the editor |
| Window corners | `--tl-window-radius` | `12px` | Window corners |
| Sheet corners | `--tl-sheet-radius` | `16px` | Top corners of a Sheet |
| Pop-out Menu | `--tl-popout-menu` | `272px` | A menu |
| Pop-out Panel | `--tl-popout-panel` | `400px` | A tool panel |
| Pop-out corners | `--tl-popout-radius` | `8px` | Pop-out corners |
| Pop-out row | `--tl-popout-row` | `44px` | Row height in a pop-out |
| Viewer close | `--tl-viewer-close` | `44px` | Close button target in a Viewer |

- **Window** (`AppDialog`): centered, with a scrim. S for a confirmation or short form, M for a form or picker, L for a workspace. One anatomy: 20 / 600 title, an optional one-line description, a close X at 44 px top right, the body, and a footer.
- **Sheet:** the Window on phones. Every Window becomes a Sheet under 600 px: it slides from the bottom, has 16 px top corners and a handle, and keeps the same anatomy.
- **Pop-out** (`AppPopover`): anchored to its control, no scrim, closes on an outside press or Escape. Menu (44 px rows) or Panel (tools such as the player's speed, audio, subtitles and queue, and the library folders pop-over), on the elevated surface. On phones a Pop-out becomes a Sheet.
- **Viewer** (`AppViewer`): full screen, for the reader, photo and document viewers, search and the details overlay. It has its own close X at 44 px.

**Footer order, left to right:** a quiet (text) button first, then a destructive button (outlined red), then Cancel or Discard, then the one primary button last. Buttons sit in one right-aligned row at one height and never stack on desktop; on phones they stack full width, primary first. A destructive confirmation is marked by the screen that asks (`AppDialogOptions.DestructiveConfirm`), never guessed from the label.

## Buttons

- **Three levels:** primary (filled purple, one per region), secondary (outlined with `--tl-border-strong`), quiet (text only), plus outlined red for a destructive entry and filled red for a confirmed destructive action. Heights 36 / 44 / 48.
- **Icon button (`AppIconButton`), one part, five looks:** Outlined (default, on surfaces), Text (no border, no fill), Filled (primary and the Like state), OverArtwork (a soft dark scrim for use on covers and heroes) and Soft. Sizes 36 / 44 / 48 with icons 20 / 24 / 24. An icon-only button needs a tooltip and an accessible name.
- **Hero button:** one wide solid light (white, dark text) Play, Watch, Resume, Read or Listen button, tall, with a large label and icon, and a thin progress bar under Resume. Companions (My List, Like, Edit, More, and Open for a folder of videos) are the Text icon button at 48 px with a short label under it where there is room. It is light on purpose so it reads over any artwork.

| Part | Token | Value | Used for |
|---|---|---|---|
| Small icon | `--tl-icon-button-icon-sm` | `20px` | Icon inside a 36 px icon button |
| Large icon | `--tl-icon-button-icon-lg` | `24px` | Icon inside a 44 or 48 px icon button |
| Scrim | `--tl-scrim-soft` | `rgba(16, 17, 21, 0.55)` | Soft dark scrim behind an icon over artwork |
| Hero button fill | `--tl-hero-action-bg` | `#F5F7FB` | The hero's one main button |
| Hero button hover | `--tl-hero-action-bg-hover` | `#E3E6EC` | Hover on the hero button |
| Hero button text | `--tl-hero-action-text` | `#101115` | Hero button label and icon |
| Hero button height | `--tl-hero-action-height` | `56px` | Hero button height |
| Hero label size | `--tl-hero-action-label-size` | `20px` | Hero button label |
| Hero icon size | `--tl-hero-action-icon-size` | `24px` | Hero button icon |

## Tooltips

A tooltip exists only when (a) the control is icon-only, (b) the control is disabled and the tooltip says why, or (c) text is cut off and the tooltip shows it in full. Never on a control with a visible label that says the same, and never on list or menu rows. It appears after 150 ms on a mouse, at once on keyboard focus and on a long press on touch; it hides when its control leaves the page, on any press, when a menu or dialog opens, and after 4 seconds on a mouse. The global `title` attribute is retired: a hint is a `Tooltip` parameter or `AppTooltip`.

| Part | Token | Value | Used for |
|---|---|---|---|
| Surface | `--tl-tooltip-surface` | `var(--tl-bg-surface-elevated)` | Tooltip background |
| Border | `--tl-tooltip-border` | `var(--tl-border-default)` | Tooltip hairline |
| Text | `--tl-tooltip-text` | `var(--tl-text-primary)` | Tooltip text |
| Corners | `--tl-tooltip-radius` | `6px` | Tooltip corners |
| Size | `--tl-tooltip-font-size` | `0.8125rem` | Tooltip text size |
| Widest | `--tl-tooltip-max-width` | `280px` | Tooltip maximum width |

## Alignment

1. **Rows.** One flex line, label, helper and control vertically centered on one axis.
2. **Labels and fields.** A form field's label sits above the field (13 / 500, 6 px gap); helper text sits under it at 13 px muted. A settings or list row is the other shape: label and helper left, the control at the right end. The two shapes never mix in one group. A label is never right-aligned and never inside the field.
3. **Buttons.** In a footer or toolbar, one row, right-aligned, in the footer order above, one height and one baseline.
4. **Text.** Left-aligned everywhere except numbers and durations (right, tabular numerals) and choice tiles (centered). No centered body copy, dialogs or settings.
5. **Crowding.** A toolbar or header row holds one group left and one right, both on the centre line. More than three actions: the rest go in "...".
6. **Text never rides on a control.** Helper, status, error or result text that belongs to a control ("Saved", "Key is invalid") goes on its own full-width row directly under the row that holds the control, at 13 px, and that row is reserved so nothing shifts.

`tools/validate-alignment.js` checks the proof page and four real screens against these rules.

## Settings

Settings is one page: a title and one line, a tab row (Advanced stays last and amber), then groups. A group is a 16 / 600 heading, an optional helper line, and rows (`AppSettingsGroup`, `AppSettingRow`: label and helper left, control right, 56 px minimum, hairline between) on the page background with no card border. A card stays only for an object (a backup destination, a connected device, a plugin) and for a danger group (outlined red). The section shell and its rail use `--tl-bg-shell` with a 1 px `--tl-border-subtle` divider, 44 px rail rows, the neutral hover and the purple soft selected wash; no gradient and no glow.

## Layout and devices

| Device class | Width | Notes |
|---|---|---|
| Phone | under 600 px | Sheets, table rows become cards, no editing |
| Tablet | 600-1023 px | Touch rules apply up to 1023 px |
| Desktop | 1024 px and up | |
| TV | opt-in | 56 px targets |

New CSS may only break at **600 / 840 / 1180 px** (plus the touch query at 1023 px); the existing 640 and 960 tokens are tolerated, and `ResponsiveBreakpointGuardrailTests` enforces the list with a shrinking allowlist of older widths. JavaScript reads the same numbers from the tokens.

| Name | Token | Value | Used for |
|---|---|---|---|
| Phone | `--tl-breakpoint-phone` | `600` | Under this width is a phone |
| Desktop | `--tl-breakpoint-desktop` | `1024` | From this width is a desktop |
| Navigation | `--tl-breakpoint-navigation` | `840` | Bottom dock and compact navigation |
| Wide | `--tl-breakpoint-wide` | `1180` | Wide page layouts |
| Compact | `--tl-breakpoint-compact` | `640` | Existing compact layouts |
| Content | `--tl-breakpoint-content` | `960` | Existing content layouts |

Touch targets are at least 44 by 44 px (48 on the bottom dock). Use `100dvh`, not bare `100vh`. Editing is desktop-only.

## Tiles

One size app-wide: poster 214 x 321, square 321, landscape 320 x 180; phones 112 x 168. No size control, no text under the cover; a status label flush top-left, a count label flush bottom-left, the "..." always visible top right (44 px tap area). Tokens: `--tl-tile-poster-w`, `--tl-tile-square-w`, `--tl-tile-landscape-w`, `--tl-tile-menu-button`.

## Pictures

Every picture asks the Engine for the copy that fits its drawn size, never the original (spec: [Right-sized pictures](../specs/archive/right-sized-pictures.md)). Copies, by long side: **tiny 96**, **small 320**, **medium 960**, **large 2160**. Rule: the smallest copy at least the drawn size; fixed-size pictures add a 1x/2x/3x list, screen-wide ones a width list. In code: `ArtworkSize`, `MediaTileArtworkUrl.ForDrawnSize`, `SrcSetForDrawnSize`, `SrcSetByLongSide`; shared primitives (`PersonAvatar`, `AppAvatar`, `AppMediaArtwork`, `AppImage`) size themselves and take `DrawnPx` when drawn larger. Lists and rows load pictures lazily; heroes never do.

| Surface | Drawn (CSS px, long side) | Copy asked for |
|---|---|---|
| People lists, cast lists, family tree, Events and Added rows, table and song-row covers, Continue strip | 32-64 | tiny (96); small on sharp screens |
| Now playing bar, queue and history rows, editor thumbnails | 48-128 | tiny or small by drawn size |
| Credit card faces and character pictures | up to 140 | small |
| Home, Watch, Listen, Read tiles and shelves | 128-342 | small; medium on sharp screens |
| Search results, Listen album and artist art, Series and album rails | 160-400 | small or medium by drawn size |
| Tile hover panel, details cover, person portrait, full player, editor artwork preview | 320-640 | medium |
| Hero carousel, details and phone hero backgrounds | full width | medium on phones, large on desktop and TV |
| Artwork picker "full view" and downloads | full | original |

## Shared parts and conventions

- Use the first-party **`App*` components** in `src/MediaEngine.Web/Components/Shared/`; do not build one-off controls. Native buttons, inputs and selects are replaced by `AppButton`, `AppTextField` and `AppSelect`; what stays native (playback, the reader, tiles, toggle controls) is listed in `LookRatchetGuardrailTests`.
- **Tabs:** one style (`.tl-tab`): flat, a 2 px accent underline on the active tab, muted text, white when active. Tabs are navigation, not a filter. The row scrolls sideways on a phone and never wraps. Pages never restyle tabs.
- **Every screen designs four states:** normal, empty, loading (skeletons), error (with a way forward).
- **List pages:** the page name appears once, in the header; then a summary strip, a search and filter row, tabs with counts, a card grid (3 columns on desktop, 2 on tablet, 1 on phone, equal-height cards) and `AppPager` (nothing drawn until a list is longer than 24 items).
- **Lists and tables:** column gap `--tl-list-column-gap`, row height `--tl-list-row-height`; text left-aligned, numbers right-aligned, icon columns centered; the play button always has its own narrow column; rarely used row actions live in the "..." menu, and destructive actions are never a loose icon in a row.
- **Styles** belong to the component that renders the markup (component-scoped CSS, at most 2,000 lines each); global layers are the tokens, native utilities and `app.css`. No new `!important`.
- **Row buttons (`AppRowButton`, universal editor v5):** the one button for a control whose whole face is the caller's own markup, such as a dropdown's trigger or one row of a picker list (a still, a title, a mark). It draws no box of its own: the page gives the look through `Class`, and the primitive owns the reset, the focus ring and the disabled state. Extra attributes (`id`, `role`, `aria-*`, `data-*`) go to the button. Use `AppButton` or `AppIconButton` when the control is a labelled action; use `AppRowButton` only when the face is custom. Never write a raw `<button>` in a feature component.
- **Color wash (`AppWash`, universal editor v5):** a decorative gradient behind a block, made from two colors (`#rrggbb`) and how much of each to keep (0 to 100), mixed into the surface so text on top stays readable. The caller passes the colors and strengths, never CSS; a color that is not plain hex draws nothing, and a wash with no usable color is not drawn at all, so the block is the plain surface. The wash is hidden from assistive technology and never takes a click; the page places and sizes it through `Class`. How strong it is comes from configuration (`config/ui/palette.json`, `editor_header`), not from code.
- **Logos:** `assets/images/tuvima-logo.svg` (and `-dark`), `tuvima-icon.svg`. Never type "TUVIMA" as a stand-in.
- Visual checks happen in the rendered app at phone, tablet and desktop widths; see `docs/design-system/visual-qa-checklist.md`.

## Mockups

[`mockups/`](mockups/) holds the spring-2026 design previews and their own older token file `colors_and_type.css`. Those values are older than the shipped ones. Use them for layout ideas, not values.

## Known gaps

- `config/ui/palette.json` still feeds some chips and Universe graphs; media-type colors the Engine serves come from it. Page colors come from the tokens.
- The token file still defines values the ladders above do not name: `--tl-font-size-lg` (1.125rem), `--tl-control-font-weight` (600, while the type ladder says buttons are 500), `--tl-letter-spacing-label` (0.08em, while the overline rule says 0.06em), `--tl-line-height-relaxed`, `--tl-bg-app-gradient` and `--tl-bg-shell-gradient` (the shell is meant to carry no gradient), and `--tl-radius-lg` (equal to `--tl-radius-md`). The cascade cleanup packet decides which go.
