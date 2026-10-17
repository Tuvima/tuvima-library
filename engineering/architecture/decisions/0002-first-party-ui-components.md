# 0002 — First-party `App*` UI components; MudBlazor removed

**Date:** 2026-10-06 · **Status:** Accepted

## What
The Dashboard uses its own component family in `src/MediaEngine.Web/Components/Shared/` (`AppButton`, `AppDialog`, `AppSelect`, `AppTable`, `AppTooltip`, …) with scoped services for dialogs, toasts and popovers. The MudBlazor library was removed in phases P0–P5 (`engineering/reports/mud-removal/`).

## Why
- **Maintenance:** one set of components that match Tuvima's design tokens, instead of overriding a third-party theme.
- **Performance:** smaller CSS/JS bundles; Release CSS is minified and size-checked in CI.
- **Reliability:** guardrail tests (`AppComponentSystemGuardrailTests`, `NativeUiDependencyGuardrailTests`) stop a UI library creeping back.

## Trade-offs accepted
Tuvima owns the accessibility and behaviour of every control. Icons derived from MudBlazor's set keep their attribution in `THIRD-PARTY-NOTICES.md`.

## Follow-up
A few stale MudBlazor mentions remain in docs and comments (for example `docs/architecture/playback.md:164`); cleanup is a planned follow-up session.
