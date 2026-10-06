---
title: "UI Consistency Standard"
summary: "Canonical button hierarchy, typography roles, exceptions, and enforcement for the Dashboard."
audience: "designer"
category: "reference"
product_area: "dashboard"
status: "active"
---

# UI Consistency Standard

Date: 2026-09-12

## Button hierarchy

Every Dashboard action uses `AppButton` or `AppIconButton`. Pages express intent with app-owned `ButtonStyle`, `Tone`, and `AppControlSize` values; they do not pass MudBlazor `Variant`, `Color`, or `Size` values through the wrapper.

- Primary commit or start action: `Filled` + `Primary`. Examples: Save, Create, Continue, Scan now.
- Secondary action: `Outlined`, normally `Neutral` or `Primary`. Examples: Test connection, Edit, Set up manually.
- Tertiary or low-emphasis action: `Text`, normally `Neutral` or `Primary`. Examples: Learn more, View all, Cancel in a lightweight context.
- Destructive entry action: `Outlined` + `Error`.
- Confirmed destructive action: `Filled` + `Error`.
- Warning is reserved for a risky but non-destructive action. Success and Info communicate state; they are not alternate brand colors.

Use only one filled primary action in an action region. A page may contain multiple independent cards, dialogs, or forms, each with its own primary action. Loading actions retain their label, show a spinner, expose `aria-busy`, and cannot be clicked again.

The purple filled **Scan now** action on Ingestion is intentional because it starts the page's primary operation. A purple outline elsewhere is correct only when that action is secondary in its local context.

## Typography

- Interface: `--font-ui` (`Segoe UI Variable`, then system UI fallbacks). This is the default for navigation, controls, settings, tables, and body copy.
- Brand and prominent media identity: `--font-brand` (Montserrat), scoped to deliberate identity treatments rather than all interface text.
- Reader: `--font-reader` (Merriweather), scoped to long-form reading and its preview.
- Technical values: `--tl-font-mono` (JetBrains Mono with system monospace fallbacks), used for paths, URLs, identifiers, and code.
- Minimum readable size: `--tl-font-size-xs` (12px at the default root size). Body copy defaults to 14px; secondary copy is 13px; captions and compact metadata are 12px.

Do not use a smaller font to solve layout pressure. Shorten copy, allow wrapping, widen the region, or change the information density instead.

### Control typography

Buttons, segmented controls, tabs, and other shared action controls use the interface font and one consistent semibold weight. Visual priority comes from fill, border, and color—not heavier label text.

- Font family: `--font-ui` (`Segoe UI Variable`, `Segoe UI`, then system UI fallbacks).
- Font weight: `--tl-control-font-weight` (600) for primary, secondary, tertiary, selected, and unselected labels alike.
- Compact size: `--tl-control-font-size-sm` (13px).
- Normal size: `--tl-control-font-size-md` (14px).
- Large size: `--tl-control-font-size-lg` (15px).
- Line height: `--tl-control-line-height` (1.25).
- Letter spacing: `--tl-control-letter-spacing` (0).

Do not use 700 weight, all caps, or extra tracking to make a button primary or a segmented option selected. Purpose-built display identities, headings, status badges, table headers, numeric metrics, and reader content may retain their intentional typography when they are not acting as shared controls.

## Intentional exceptions

The October 5 shared-control family is `AppSelect` (with typed/native/int/media-type adapters), `AppTooltip`, `AppRangeSlider`, `AppProgressBar`, and `AppSpinner`. Use explicit intrinsic selector sizing for bounded series/set selectors; forms retain full-width fields. Tooltip help supports hover, keyboard focus, Escape, and optional touch long-press without replacing accessible labels. Lyrics and Queue player modes use glyphs with tooltips, without visible label spans. See [the player/control verification report](../reports/player-controls-2026-10-05.md) for acceptance evidence and remaining device checks.

- Playback controls use the shared purple playback token family and existing player surfaces.
- Semantic warning, error, success, and information states keep their token colors.
- Provider logos and media artwork keep source/brand color.
- The EPUB reading surface uses Merriweather for content, while its controls remain in the interface font.

## Inventory snapshot

The 2026-09-12 source inventory found 368 `AppButton`, 67 `AppIconButton`, 67 `AppSelect`, 98 `AppTextField`, and 284 `AppNativeButton` usages. The button migration removed all legacy `Variant` and `Color` attributes from shared button call sites. The typography pass replaced 300 sub-12px declarations with the shared caption floor.

`/design-system/components` is the visual reference for hierarchy, tones, loading/disabled states, typography roles, and shared controls. `UiConsistencyGuardrailTests` prevents legacy button APIs, raw page-level Mud buttons, and sub-floor text sizes from returning.

## CSS ownership and removal

Keep presentation rules with the component that owns the rendered elements. Moving scoped CSS requires reviewing the emitted scope and descendant/portal behavior; splitting files alone does not reduce delivered styling. Use `scripts/css/audit.py` for conservative duplicate and retired-selector evidence, then compare affected desktop and phone states. Different-value browser fallbacks, conditional states, and active vendor overrides remain unless their replacement is proven. The [October 5 cleanup report](../reports/css-cleanup-2026-10-05.md) records the current measurements and remaining ownership work.

## CSS ownership and verification

Place native-element rules beside the component that emits that element. Inspect compiled isolation selectors and actual DOM attributes for C# renderers, render fragments and shared controls. Keep documented contextual `::deep` boundaries for shared/MudBlazor internals; keep portal bridges global when popups leave the scoped ancestor. A rule that formerly did not match must not start matching just because ownership changes. Preserve specificity, media conditions and source-order winners.

Isolated CSS is capped at 2,000 lines per owner. Extract cohesive existing markup with explicit presentation inputs when an owner is too large; CSS-only shards, wrappers, formatting compression and shared scope IDs do not satisfy this standard. No new `!important` is permitted. Retained overrides move through balanced budgets, and removals require a proven ordinary winner and desktop/phone comparisons. Tokens, vendor CSS, reader CSS and parser-unsupported files require their own explicitly scoped work.

The [Dashboard ownership evidence](../reports/css-ownership-2026-10-06.md) records measurements and acceptance. Use `scripts/css/audit.py`, `StyleOwnershipGuardrailTests`, and the documented CUA capture/offline comparison workflow. Record unsupported hover, high-density and device coverage; do not silently count them as passing checks.
