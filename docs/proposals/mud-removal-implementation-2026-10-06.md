# Dashboard component migration

Baseline: `c765bc91`. Implements the reviewed MudBlazor-removal handoff across P0–P5.

## Product walkthrough

1. Browsing retains the current routes, media artwork, colors, typography, filters and focus treatment. The library, its metadata and ownership rules do not change. P0 records coverage; P1 replaces icons, theme variables and layout atoms; P3 and P4 replace controls and structural components. Acceptance: desktop, phone and lower-height screens keep usable geometry and keyboard navigation.
2. Editing remains a modal over the current detail page and unchanged URL. Confirmation dialogs, validation, draft protection and actionable Undo notifications remain available. P2 owns native dialogs, focus, popovers and toasts. Acceptance: opening an editor, changing a field, attempting to close, keeping the draft and then discarding all work through ordinary controls.
3. Playback retains its persistent owner and dock. Speed, Sleep, bookmarks and nested menus stay usable in desktop, phone sheets, fullscreen and popout windows without resizing the page. P2 and P3 own popup hosting and selectors; P4 preserves the shell. Acceptance: closing a nested menu leaves its parent sheet open and returns focus to its trigger.
4. Release delivers less CSS with no MudBlazor runtime package. P5 removes obsolete vendor wiring and CSS, verifies build-time minification and reports asset sizes. CSS savings are measured separately from JavaScript savings; vendor assets and existing token values remain unchanged.

## Technical work packages

- P0: repository/asset baseline, dependency inventory and capture/keyboard harness with stable semantic targets. Preserve existing meaningful tests.
- P1: deterministic first-party Material path catalog with attribution, equivalent token definitions and native icon/typography/layout primitives; include unprefixed vendor utilities.
- P2: scoped first-party dialog/toast APIs, inline and hosted dialogs, action callbacks, draft-close guards, modal/fullscreen-aware positioning and focus lifecycle.
- P3: native controls retaining controlled binding, debounce, validation, cancellation, sizing and playback coordinator contracts.
- P4: native tabs, expansion panels, tables, drawer/shell and the used donut chart.
- P5: remove package/providers/imports; retarget selector ownership, prune only proven dead CSS, minify Release outputs without altering source and measure publish compression.

## Execution and review

The Product Owner authorized implementation across every phase after the initial review. Work is integrated on `codex/mud-removal-all-phases`, with disjoint component ownership during parallel implementation, rather than merging unreviewed phase branches into `main`. No push or merge is authorized. Tests move alongside the components they exercise instead of waiting until P5. Baseline and browser artifacts remain disposable; evidence limitations must be reported rather than represented as passing checks.

Use repository restore/build/test gates, CSS ownership audits, JavaScript checks, reviewed browser captures and keyboard verification. Preserve backend, playback ownership, vendor files, reader highlight colors and existing token values. Final reports distinguish implemented work from checks actually completed.
