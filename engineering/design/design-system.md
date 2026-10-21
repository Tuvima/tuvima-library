# Tuvima design system

> **Status:** Current · **Last updated:** 2026-10-17 · **Related docs:** [Product overview](../product/overview.md) · [UI consistency standard (public)](../../docs/design-system/ui-consistency-standard.md) · [Supported devices (public)](../../docs/reference/ui-supported-devices.md) · [Dashboard rules](../../src/MediaEngine.Web/CLAUDE.md) · [Mockups](mockups/)

The look, voice and component rules for the Tuvima Dashboard and reader. Values come from the shipped stylesheet `src/MediaEngine.Web/wwwroot/tuvima.tokens.css` (tokens named `--tl-*`) as of 2026-10-17. If this page and that file disagree, the file wins — update this page.

## Character

**Cinematic, minimalist, dark-only.** Deep navy surfaces, soft glows and blur, one purple accent for the app's own controls, and the media's own artwork as the star. No patterns, textures or illustrations — just fades, blur and light. Covers are shown as-is (never filtered, never cropped beyond their poster ratio).

## Voice

- Warm, literary, a little reverent. Tuvima **presents** a library the person already owns; it never "creates" one.
- Confident and direct; no hype, **no exclamation marks**, **no emoji**.
- Talk to **you** ("Drop your files into a folder"), never "users". Talk about the product in the third person ("Tuvima reads each one").
- **Sentence case** for body text, menus and messages; **Title Case** for page titles, tabs, section headings and buttons.
- Proper nouns: Library, Universe, Series, Work, Edition, Engine, Dashboard, Read, Watch, Listen, Collections, **Needs Review**.
- Middle dot `·` separates metadata ("Neil Gaiman · Audiobook · 2019"). Use an en dash for pauses in copy.
- Examples: empty state "Your library is empty. Drop files into your watched folder to begin."; error toast "Could not reach the Engine."; button "Continue Reading".

## Colour

The Dashboard is **dark only** (`color-scheme: dark`). The reader adds light, sepia and dark page themes (and can follow the device).

| Role | Token | Value |
|---|---|---|
| Primary accent (active nav, main buttons, progress) | `--tl-accent-primary` | `#8852FC` (hover `#A46FFF`, pressed `#7652D6`) |
| Secondary accent | `--tl-accent-secondary` | `#38BDF8` |
| Collection highlight | `--tl-accent-collection` | `#D6A84F` |
| Page background | root / shell | `#070A12` / `#0B1020` |
| Surfaces (low → high) | surface, raised, hover, elevated | `#111827`, `#151E2F`, `#1A2438`, `#1D2940` |
| Overlay | overlay | `rgba(0,0,0,.72)` |
| Text | primary, secondary, muted | `#F5F7FB`, `#B6C2D6`, `#94A3B8` |
| Success · Info · Warning · Danger | semantic | `#22C55E` · `#38BDF8` · `#F59E0B` · `#EF4444` (each with soft and border variants) |
| Unmatched · Idle | semantic | `#FB7185` · `#7F8DA5` |
| Player | `--player-bg`, `--player-surface`, text | `#0F131E`, `#121623`, `#F7F7FA` |

**Media-type colours** (chips and badges only — never page chrome): books `#60A5FA`, audiobooks `#34D399`, movies `#FB7185`, TV `#A78BFA`, music `#FBBF24`, comics `#22D3EE`, games `#22C55E`, documents `#B6C2D6`, unknown `#7F8DA5`. Provider badges use the provider's own brand colour (for example TMDB, Wikidata, MusicBrainz).

## Typography

| Use | Font | Notes |
|---|---|---|
| Interface | Segoe UI Variable (system UI fallbacks) | Not shipped; uses the device's font |
| Brand and deliberate media identity | Montserrat | Self-hosted |
| Reading (EPUB) | Merriweather | Reader only |
| Paths, IDs, code | JetBrains Mono | |

Sizes: 12 · 13 (secondary) · 14 · 16 · 18 · 24 px. Weights: 400 body, 500 labels, 600 card titles and controls, 700 headings. Line heights 1.25 / 1.5 / 1.625. Small-caps labels use 0.08em letter spacing. Nothing smaller than 12px — make hierarchy with weight and spacing, not tiny text.

## Spacing, shape, depth and motion

- **Spacing:** 4 · 8 · 12 · 16 · 20 · 24 · 32 · 40 px. Control heights 36 / 42 / 48 px.
- **Corners:** 4 · 6 · 8 · 12 px, pill 999px; controls 10px.
- **Shadows:** small / medium / large tiers plus hover tiers; shadows appear on floating things (hero, menus, dialogs), not resting cards.
- **Focus:** 3px ring in `#A78BFA` with 2px offset. Every interactive element needs a visible focus state.
- **Motion:** 140 / 220 / 360 ms with ease curves; no bounces, springs or parallax. Respect "reduce motion" (auto-advancing carousels pause, transitions shorten).

## Layout and devices

| Device class | Width | Notes |
|---|---|---|
| Phone | under 600px | Bottom-sheet menus, table rows become cards, no editing |
| Tablet | 600–1023px | Touch rules apply up to 1023px |
| Desktop | 1024px and up | |
| TV | opt-in | 56px targets |

New CSS may only break at **600 / 840 / 1180 px** (plus the touch query at 1023px); `ResponsiveBreakpointGuardrailTests` enforces this. Touch targets are at least **44 × 44 px** (48px on the bottom dock). Use `100dvh`, not bare `100vh`.

## Components and conventions

- Use the first-party **`App*` components** in `src/MediaEngine.Web/Components/Shared/` — AppButton, AppIconButton, AppTextField, AppSelect, AppSwitchRow, AppChoiceRow, AppCheckbox, AppDialog, AppTable, AppTabs, AppCard, AppAlert, AppStatusBadge, AppTooltip, AppProgressBar, AppSpinner, AppMediaArtwork, and the page-state trio AppPageState / AppEmptyState / AppErrorState. Don't build one-off controls.
- **Every screen designs four states:** normal, empty, loading (skeletons), error (with a way forward).
- **Phones:** dialogs can become bottom sheets, tables can become cards, row actions use a 44px "more" button — never hover-only or drag-only actions.
- **Icons:** semantic keys map to FontAwesome Solid SVGs (`AppIcon`); buttons use a pinned Material icon set (`AppMaterialIcon`). Icons in the same row share family, size, stroke and target size. Icon-only buttons need a tooltip and an accessible name.
- **Styles** belong to the component that renders the markup (component-scoped CSS, max 2,000 lines each); global layers are tokens, native utilities/structure/fields and `app.css`. No new `!important`.
- **Logos:** `assets/images/tuvima-logo.svg` (and `-dark`), `tuvima-icon.svg` for favicon and empty states. Never type "TUVIMA" as a stand-in for the logo.
- Visual checks happen in the rendered app at phone, tablet and desktop widths; see `docs/design-system/visual-qa-checklist.md`.

## Mockups

[`mockups/`](mockups/) holds the spring-2026 design previews (`preview/*.html`), a Dashboard UI kit (`ui_kits/dashboard/`), and their own token file `colors_and_type.css`. Those tokens are **older** than the shipped ones: accent `#8B5CF6` (now `#8852FC`), backgrounds `#0B1220`/`#111827` (now `#070A12`/`#0B1020`), Montserrat as the default font (now brand-only), different status and media colours, larger radii and different motion timings. Use them for layout ideas, not values. New mockups referenced by specs go in `mockups/` with the spec's name.

## Known gaps (2026-10-17)

- Two colour sources disagree: CSS tokens and `config/ui/palette.json` (used for some chips and universe graphs).
- `--tl-media-view` is used on the For Me page but never defined.
- Global styles remove the focus outline on links and buttons in places (`native-utilities.css:7`); a replacement ring isn't confirmed everywhere.
