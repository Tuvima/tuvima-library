# 0006 — All-in-one management workspace retired; shared editor + Needs Review

**Date:** 2026-04-14 to 2026-04-25 (`9c044688`, `ab64dcf2`, `2b8bb3aa`); extended 2026-10-17 · **Status:** Accepted

## What
Earlier versions had one big administration screen for seeing and fixing every item (first under an older name, then called "Registry"). It is gone. Fixing a single item opens the shared editor as a window over the item's own page; items the Engine isn't sure about go to **Needs Review** (Settings → Recently Added). On 2026-10-17 Shy decided to retire the remaining "Registry" remnants too — see [the retirement spec](../../specs/registry-retirement.md).

## Why
- **Maintenance:** one editor used everywhere instead of a second, drifting correction tool.
- **Presentation:** people fix things where they find them, without leaving the page.

## Guardrails
`UiCompositionGuardrailTests` blocks the retired vocabulary in code and public docs; CLAUDE.md forbids rebuilding the workflow. Bulk editing is switched off until a spec brings it back.
