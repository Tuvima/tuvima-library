# Look pass v2: who owns what

> **Status:** Locked (companion to [Look pass v2](look-pass-v2.md)) · **Version:** 1 · **Last updated:** 2026-11-06 · **Rule (Shy 2026-11-06):** nothing in the shared look is fixed twice by separate threads.

**How it works.** The Look pass v2 thread builds every shared rule once, in the token file and the shared parts. Feature threads keep their layouts and behavior and only *consume* the shared parts: they use the tokens and parts, never their own colors, sizes, icons, menus or tooltips. If a feature thread needs a shared rule changed, it asks the Look pass v2 thread and does not fix it locally. Any decision made with Shy about the shared look is added to [look-pass-v2.md](look-pass-v2.md) and to this map.

## Shared rules and owners

| Shared rule | Fixed once by (packet) | Consumed by |
|---|---|---|
| Surfaces and colors: near-black set, purple accent only, hand-typed colors out of View, palette retired | Look v2 WP0 | every thread |
| Control height 44 / 36, control and menu text sizes (menu font size and spacing, local-check bug 26) | Look v2 A1 | all |
| Page title and line-height tokens; shelf and section heading style ("Pick up where you left off") | Look v2 A2, A14 | Home, Watch, Read, Listen, View, Settings |
| Weights, uppercase labels (Taste labels, item 137, sizes and weights) | Look v2 A3 | fix build applies its Taste wording only; type comes from A3 |
| Icon sizes, one family, Filled only for selected; icon tones | Look v2 A4 | hero rows, tiles, player, plugins, View |
| Filter chip and tab styles | Look v2 A5 | View review, Browse, Settings, Libraries, Activity |
| Menus as bottom sheets on phones; menu rows, width, danger red | Look v2 A6, A10 | Tiles, View, Editor, player, Plugins |
| Hero buttons and Like/Dislike states | Look v2 A7 | Editor v4.1 (home hero, details pages) and Tiles consume it |
| Dialog anatomy sizes | Look v2 A8 (with Look pass WP2) | all dialogs |
| Touch targets 44 px | Look v2 A9 | all |
| Contrast and focus rings | Look v2 A10 | all |
| Tooltip design, replacing every `title=` hint | Look v2 A11 | all |
| Player look: text, pop-out panels and backgrounds, round to square buttons, icon family, 56 px hit areas, blue tooltip surface, 16 px radius | Look v2 A13 (rules); the player validation thread applies layout and behavior | player validation thread |
| Visual proof at 360 / 768 / 1440 | Look v2 A12 | all |

## Overlaps, by thread

- **Player validation:** keeps layout, controls and behavior against its mockups. The old blue panel surface (rgba 18,22,35) with 16 px radius, mixed icon sets, 56 px hit areas and the blue tooltip surface are fixed by A13 plus A4, A9 and A11, not locally. Mockup differences that conflict with A13 go to Shy through this thread.
- **Plugins (v6):** keeps its layout. The mockup's navy palette, 36 px buttons and 30 px icons follow WP0, A1 and A4 when built; the Plugins build must not copy those values.
- **Editor v4.1 (home hero, details pages):** keeps its layout and behavior. Hero buttons, Like/Dislike and detail-page surfaces come from A7 and WP0; WP13b Home hero pictures and item 138 (other videos shown as books) stay with the Editor thread.
- **Photos and View review (D11 and the cleanup batch):** keeps menus' contents, filters' contents and the cleanup batch. Chip and tab styling come from A5, hand-typed View colors from WP0, `title=` hints from A11; View does not fix them separately.
- **Tiles (One tile design):** keeps tile size, "..." menu, hover and the no-art tile (D7, WP3). Heading style and the 13 px text floor with two-line wrap are A14; the tile hover outline uses the accent token only.
- **Fix build (local-check bugs):** bug 26 menu size and spacing is A1; bug 28 tooltip and pop-up top layer is A11 plus the top-layer rule; the Taste labels (137) wording is the fix build's, type and weight are A3.
- **Libraries and Activity builds (running):** keep their work. They follow these standards for anything new and do not add their own colors, sizes or tooltips; leftovers are cleaned by the packets above.

## Order of building

WP0, A1, A4, A10 first; then A2, A5, A6, A11; then A3, A7, A8, A9, A13, A14; A12 last. Feature threads that land first use the tokens as they will exist; if a token is missing they ask this thread instead of inventing one.
