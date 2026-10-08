---
title: "Tuvima Design System"
summary: "Visual, voice, and interaction guidance for the Tuvima Library Dashboard and reader surfaces."
audience: "designer"
category: "reference"
product_area: "design"
tags:
  - "design-system"
  - "dashboard"
  - "reader"
---

# Tuvima Design System

Design system for **Tuvima Library** - a unified media intelligence platform that organizes books, audiobooks, movies, TV, music, and comics by story rather than by file type. The visual language is **cinematic, minimalist, dark-only**, built around deep-navy surfaces, glassmorphic panels, and a single purple product chrome accent.

## Sources

- **Source repo:** `Tuvima/tuvima_library` (https://github.com/Tuvima/tuvima_library) - .NET 10 / Blazor + MudBlazor 9 codebase. The Dashboard ships as `src/MediaEngine.Web`.
- **Core stylesheet referenced:** `src/MediaEngine.Web/wwwroot/app.css`
- **Icon system:** FontAwesome solid SVGs copied from `src/MediaEngine.Web/wwwroot/icons/fontawesome/solid/`
- **Fonts:** Segoe UI Variable/system UI for interface text, with self-hosted Montserrat for brand/media identity, Merriweather for reading, and JetBrains Mono for technical values.
- **Logos:** `assets/images/library.svg` for the Dashboard navbar; `tuvima-logo.svg`, `tuvima-logo-dark.svg`, and `tuvima-icon.svg` for broader brand surfaces.
- **Screenshots:** `Screenshot removed during documentation cleanup; no capture is retained in this repository.`

## Products

Tuvima Library is a single-product system with two distinct UI surfaces:

1. **Dashboard** - the cinematic library browser (home, swimlanes, collection detail, settings, Review Queue). Dark, glassy, full-bleed hero banners.
2. **EPUB Reader** - the in-app reader surface (`/read/{assetId}`). Serif, page-like, softer tones.

This system covers both.

---

## Content Fundamentals

The voice is **warm, literary, and a little reverent** - it treats a media collection as a personal archive worth presenting, not just a folder tree to sort. Marketing lines ("Make your media collection discoverable", "You already own the stories. Tuvima makes them discoverable.") set the frame: Tuvima doesn't *create* a library, it **presents** one. Every feature copy choice should preserve that philosophy.

**Tone**
- Confident, direct, never marketing-hype. No exclamation marks in UI copy.
- Slightly literary - favors evocative nouns (library, story, Review Queue, universe, chronicle) over generic SaaS words (workspace, project, dashboard panel).
- Privacy is framed as a promise, not a feature: "no accounts", "no telemetry", "no cloud AI".

**Casing**
- **Sentence case** for body copy, descriptions, toast messages, and menu items ("Resolving reviews", "Adding media").
- **Title Case** for page titles, tab labels, section headings, and CTAs ("Continue Your Journey", "Recently Added", "Review Queue").
- Proper nouns are capitalized consistently: **Library**, **Universe**, **Series**, **Work**, **Edition**, **Media Asset**, **Review Queue**, **Engine**, **Dashboard**, **Read**, **Watch**, **Listen**, and **Collections**.

**Person**
- Default to **you** ("Drop your files into a folder", "Your Dune ebook lives here"). Never "users".
- Speak about the product in the third person ("Tuvima reads each one", "The Engine benchmarks your hardware").

**Emoji & symbols**
- **No emoji** in UI. Icons are FontAwesome solid SVGs.
- Middle-dot ` | ` used as meta separator ("Neil Gaiman  |  Audiobook  |  2019").
- En dash `--` used for copy beats, never `--`.

**Concrete examples**
- Empty state: *"Your library is empty. Drop files into your watched folder to begin."*
- CTA: *"Continue Reading"*, *"Shuffle"*, *"Seed Library"* - use a direct, truthful action label.
- Hero subtitle: *"You've read 38% of Dune - pick up where you left off."* (resolved from phrase templates)
- Toast: *"Library seeded."* / *"Could not reach the Engine."*
- Section labels (small caps): *"IN PROGRESS"*, *"RECENTLY ADDED"*, *"YOU MIGHT LIKE"*.

---

## Visual Foundations

**Palette** - dark-only, cinematic. Deep navy base (`#0B1220 -> #111827`) with soft radial glows of blue (`rgba(59,130,246,.12)`) top-left and cyan (`rgba(14,165,233,.10)`) top-right. Every card is a **glassmorphic** layer over this base: `rgba(255,255,255,0.02)` fill, `rgba(255,255,255,0.06)` border. The single product chrome accent is purple (`#8852FC`) - used for active nav, primary CTAs, in-progress indicators, and nothing else. Media-type accents (books indigo, audiobooks blue, movies pink, TV emerald, music amber, comics orange) are reserved for provider badges and media-type chips, never page chrome. EPUB reader highlight colors remain reader-specific.

**Typography** - **Segoe UI Variable** is the interface face, with system UI fallbacks. Weights used: 400 body, 500 labels, 600 card titles, and 700 section headings. **Montserrat** is scoped to brand and deliberate media-identity treatments. **Merriweather** is scoped to the EPUB reading surface. **JetBrains Mono** is used for paths, URLs, identifiers, and code. Compact metadata never falls below 12px; use letter spacing and weight, not undersized text, to create small-caps hierarchy.

**Spacing** - roughly 8pt: `4 / 8 / 12 / 16 / 20 / 24 / 32 / 40 / 48 / 64`. Page gutters are generous (`padding: 24px` min). Swimlanes use tight horizontal gaps (`gap: 12px`) so covers feel like a continuous shelf.

**Backgrounds** - the base page uses the fixed navy gradient. Detail pages overlay a **fixed, full-bleed blurred cover image** (`filter: blur(40px) saturate(0.6)`) with a vertical fade into the page background (`transparent -> rgba(6,10,22,1)` by 80%). A **page ambient glow** radial (`ellipse 110% 55% at 50% 0%`) tinted by the hero collection's dominant color washes the viewport at 14% opacity. No patterns, no textures, no illustrations. Just fades, blur, and light.

**Animation** - restrained. Hover/focus transitions `150-200ms` ease. Hero slide enter: `opacity 0 -> 1` + `translateY(6px -> 0)` over `550ms`. Carousel auto-advance every `8s` only when reduced motion is not requested; it must pause on hover and keyboard focus and expose pause/resume controls. Dot indicator active: `background: #8852FC; transform: scale(1.35)` with `250ms ease`. Ambient glow crossfade `600ms ease`. No bounces, no springs, no parallax.

**Hover states** - surfaces gain `background: rgba(255,255,255,0.05)` (aka `--tv-hover-overlay`). Ghost buttons add a subtle border-color shift. Primary purple CTAs brighten the gradient (`#8852FC`, brightening to the `#A46FFF` hover tone) and strengthen shadow (`0 4px 18px -> 0 6px 24px rgba(136,82,252,0.42)`). Icons lift from 35% to ~88% opacity on hover.

**Press states** - no visible shrink. Amber active overlay (`rgba(234,179,8,0.22)`) applies to active nav, selected toggle items.

**Borders** - almost always `1px solid rgba(255,255,255,0.06)` (inner-card) or `rgba(255,255,255,0.08)` (outer shell). Focus rings on text inputs flip to `rgba(255,255,255,0.8)`. No colored borders except the purple CTA's subtle `1.5px solid rgba(0,0,0,0.15)` inner edge.

**Shadow system**
- `--tv-shadow-sm: 0 2px 8px rgba(0,0,0,0.30)` - card rest
- `--tv-shadow: 0 8px 32px rgba(0,0,0,0.50)` - glass panels
- `--tv-shadow-md: 0 16px 36px rgba(15,23,42,0.40)` - modals
- `--tv-shadow-lg: 0 22px 48px rgba(15,23,42,0.60)` - hero
- `--tv-inset-glow: 0 0 0 1px rgba(255,255,255,0.04) inset` - subtle rim light on glass
- `--tv-amber-glow: 0 4px 18px rgba(139,92,246,0.28)` - purple CTA rest
- `--tv-amber-glow-strong: 0 6px 24px rgba(139,92,246,0.42)` - purple CTA hover

**Protection gradients** - used on hero banners to keep text legible against cover art: vertical linear-gradient from `rgba(6,10,22,0.10) 0%` through `0.55 @ 35%`, `0.90 @ 60%`, `1.00 @ 80%`. No capsule/pill backgrounds around hero text - the gradient does the work.

**Layout rules**
- **Top bar** is fixed at `72px`, z-index above MudBlazor app bar (`calc(appbar + 150)`).
- **Left dock** is floating glass - does not offset content (`--tv-dock-width: 0px` in flow).
- **Swimlanes** scroll horizontally, covers are fixed-width (`--cardWidth`, typically 160-200px), gap 12px.
- **Detail pages** scroll a transparent content layer on top of the fixed hero.
- Mobile (`<=768px`) hides TopBar actions (bell/avatar); nav drawer slides from left with 100dvh height.

**Transparency & blur** - used intentionally, not decoratively:
- Glass panels: `rgba(12,16,36,0.80)` + `backdrop-filter: blur(8px)`.
- Carousel arrows: `rgba(0,0,0,0.55)` + `blur(8px)`, opacity `0 -> 1` on carousel hover.
- Hero cover blur fallback: `filter: blur(40px) saturate(0.6)` on cover art when no dedicated hero asset exists.

**Imagery tone** - covers are shown as-is, never cropped beyond a 2:3 poster ratio, never filtered except the deliberate 40px-blur hero fallback. No b&w, no duotone. Trust the source art.

**Corner radii** - `xs 6px  |  sm 8px  |  md 10px  |  lg 12px  |  xl 16px  |  2xl 20px  |  pill 999px`. Cards use `12px` (`--tv-radius-lg`). CTAs use `8px`. Toggles use `14px` outer / `12px` inner. Pills for counts use `999px`.

**Cards** - glassmorphic surfaces. Default: `background: rgba(255,255,255,0.02); border: 1px solid rgba(255,255,255,0.06); border-radius: 12px; padding: 20px 24px;`. Accent (featured) variant uses `1px solid rgba(139,92,246,0.24)` purple border and `color: #8B5CF6` for the title. No drop shadow at rest - shadow shows only on hovered/floating surfaces (hero, modals).

---

## Iconography

The codebase uses **FontAwesome Solid** SVGs served statically from `/icons/fontawesome/solid/{name}.svg`. An `AppIconCatalog` (in `src/MediaEngine.Web/Components/Shared/AppIconCatalog.cs`) aliases semantic keys (`search`, `home`, `read`, `watch`, `listen`, `collections`, `settings`, `intelligence`, `server`, etc.) to specific solid icons (`magnifying-glass`, `house`, `book-open-reader`, `film`, `headphones`, `boxes-stacked`, `gear`, `wand-magic-sparkles`, `server`...). MudBlazor Material Icons are used only for provider-accent icons (TMDB, Wikidata, MusicBrainz) and MudBlazor's own internal widgets - the design system itself is FontAwesome-first.

**Rules**
- No emoji anywhere in the UI.
- No unicode dingbats as icons. The middle-dot ` | ` is used as a meta separator only.
- No hand-drawn/custom SVG illustrations. The **Tuvima mark** (the golden spiral) is the only bespoke glyph - used as logo, favicon, and empty-state hero.
- Default icon size: `16px` in nav / inline, `20px` in buttons, `24-32px` in cards, `80px` in empty states.
- Default icon color: `rgba(248,248,248,0.55)` muted, `rgba(248,248,248,0.88)` hover. Accent icons on active nav flip to purple.
- Provider icons (TMDB, Wikidata, etc.) use their brand colors (`#01B4E4` TMDB, `#339966` Wikidata, `#BA478F` MusicBrainz) - never the product chrome accent.

**Copied icons** (in `assets/icons/`): `book-open`, `book-open-reader`, `boxes-stacked`, `cart-shopping`, `chevron-down/left/right`, `circle-info`, `clipboard-check`, `clock`, `film`, `folder-open`, `folder-tree`, `gear`, `headphones`, `house`, `layer-group`, `list-check`, `magnifying-glass`, `microchip`, `music`, `play`, `server`, `share-nodes`, `shield-halved`, `sliders`, `table-list`, `timeline`, `toggle-on`, `triangle-exclamation`, `tv`, `user`, `users`, `wand-magic-sparkles`, `wrench`, `xmark`.

**Logo usage**
- Library lockup (`assets/images/library.svg`) - Dashboard navbar.
- Full horizontal logo (`assets/images/tuvima-logo.svg`) - login screens and marketing.
- Icon mark (`assets/images/tuvima-icon.svg`) - favicon, empty-state centerpiece, app icon.
- Never replace logo placements with hand-typed "TUVIMA" text.

---


### Icon rows and player typography (October 5 2026)

Validate icons together whenever they share a row or area. Peer actions must use the same icon family, view box, stroke treatment, rendered glyph size, target size, shape, border, and alignment. Do not mix filled Material and outline playback icons within one peer row. Selected states may change color; intentionally dominant transport Play/Pause may have a larger target and must be documented as a separate role. Icon-only controls require a tooltip and accessible name, plus keyboard focus and selected/expanded semantics.

Favorite, Rate, and More on the dock, phone full player, desktop full player and popout use `PlaybackSongActions`: 44px circular targets, 1px borders and 22px `PlaybackUtilityGlyph` icons in a 24-unit view box with a 1.5-unit stroke. Detail hero actions keep their own size contract. Regression checks cover the shared hosts, glyph types, callbacks and accessible labels. Run the read-only `tools/validate-player-icon-rows.js` in the rendered browser at desktop, phone, short-phone and popout sizes; require equal dimensions and centered glyphs within 1px. Also inspect resting, focus, rated/favorited and open-menu states. Do not infer size parity from source alone.

Lyrics use the shared UI sans-serif family, bold container-sized text and a larger bright active line. Adjacent lines are dimmed without readability-damaging blur. Lyrics has no visible heading or Synced badge; timed highlighting conveys synchronization. Queue/history has no Continue Playing or source header: only Up Next and History tabs. Preserve accessible panel names, meaningful error/empty states, LRCLIB attribution, timing, scrolling and seek behavior. Lyrics and Queue mode triggers remain icon-only with tooltips.

## Index

Root files:
- `README.md` - this file
- `SKILL.md` - agent skill manifest
- `colors_and_type.css` - design tokens (CSS custom properties) + typography roles
- `assets/` - logos, icons, favicons, screenshots
- `fonts/` - Montserrat, Merriweather, JetBrains Mono TTFs
- `preview/` - design-system cards rendered for the Design System tab
- `ui_kits/dashboard/` - the Cinematic Dashboard UI kit (JSX components + index.html)

UI kits:
- **Dashboard** (`ui_kits/dashboard/`) - home, hero carousel, swimlanes, top bar, left dock, cards, search, collection detail, settings panel.

## Current Dashboard/Product UI Model

Home, Read, Watch, Listen, Collections, and Search are the user-facing discovery and media surfaces. Detail pages and media rows/cards launch inline editing through the shared media editor. Review Queue is only for blocked or uncertain items that need human confirmation. Settings/Admin is for configuration and operational/system concerns. Do not add all-in-one management routes, components, docs, or workbench flows.

Design changes must preserve the current quality gates: no removed all-in-one management workflow, no obsolete management navigation label, and media correction controls should launch the shared editor instead of creating a separate management surface.
