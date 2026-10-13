---
title: "Supported devices and widths"
description: "Reference the Dashboard's four device classes, the supported screen widths, the breakpoint scale and the touch rules every page follows."
audience: "developer"
category: "reference"
product_area: "dashboard"
tags:
  - "responsive"
  - "devices"
  - "breakpoints"
status: current
---

# Supported devices and widths

The Dashboard tells four form factors apart. The browser reports them as `data-form-factor` on the `<html>` element and as `DeviceContextService.FormFactor` in Razor.

| Form factor | Width | Notes |
|---|---|---|
| `phone` | under 600 px | Bottom dock navigation. Phones never open an editor. |
| `tablet` | 600 to 1023 px | Desktop layout with touch sizing. |
| `desktop` | 1024 px and wider | Top navigation, hover and keyboard. |
| `tv` | opt-in | Opened with `?device=tv` (remembered for the browser session) or a TV user agent (Android TV, BRAVIA, SMART-TV, Tizen, Web0S). Remote-driven; 56 px targets. |

`data-touch="true"` is set on `<html>` when the primary input is touch (`(hover: none), (pointer: coarse)`), whatever the form factor. `DeviceContextService.IsTouch` mirrors it.

The Engine's UI-settings key is unchanged: phone uses `mobile`, tablet and desktop use `web`, tv uses `television`.

## Supported widths

| Width | Meaning |
|---|---|
| 320 px | Must not break (no overflow, nothing unreachable). |
| 360 to 430 px | Phones. |
| 768 to 1024 px | Tablets. |
| 1280 to 1920 px | Desktop and TV. |
| 2560 px | Content stops growing (max-width). |

## Breakpoint scale

New CSS uses only `600px`, `840px` and `1180px`, plus the existing tokens `640px` and `960px`. CSS media queries repeat the literals because custom properties are not valid in media conditions; `tuvima.tokens.css` holds the matching `--tl-breakpoint-*` values that JavaScript reads. `ResponsiveBreakpointGuardrailTests` fails on any other width and on any new `100vh`. Older off-scale values are listed in `tests/MediaEngine.Web.Tests/Fixtures/responsive-breakpoint-allowlist.txt`; remove lines as styles move to the scale, and never add new ones.

## Touch rules

- Tap targets are at least 44 px on touch and on phone and tablet; 48 px on the bottom dock; 56 px on TV.
- Nothing is hover-only on touch: it is visible by default or behind a "⋯" button.
- Nothing is drag-only: every drag action has a menu equivalent.
- Phone dialogs are bottom sheets; tablet and desktop use dialogs.
- Phone tables are stacked cards with no page-level horizontal scroll at 360 px.
- Use `100dvh`, never `100vh`, and apply safe-area insets on full-bleed pages.
