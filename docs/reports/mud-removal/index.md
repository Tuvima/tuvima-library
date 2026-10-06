# Dashboard component migration: consolidated review

Baseline: `c765bc91`. Implementation branch: `codex/mud-removal-all-phases`.
This package records P0â€“P5 implementation and the evidence available on October 6,
2026. Implementation, clean Debug/Release builds, the full serial solution test
run and the delivered-CSS reduction gate are complete. Representative
original/native browser review is recorded; complete
visual-matrix acceptance remains unproven. No independent external phase approval,
merge or deployment is claimed.

## Product-owner walkthrough

1. **Browse the same library.** Home, media lanes, search and detail routes retain
   their existing purpose, media identities and artwork. P1 replaces icons, theme
   values and layout atoms; P4 replaces the shell and structural controls. The
   observable acceptance is usable, familiar navigation and artwork geometry on
   desktop, phone and lower-height screens. P0 records the comparison evidence.
2. **Edit without losing the current page or draft.** The editor remains a modal
   above the unchanged detail URL. P2 supplies dialogs, focus, draft-close guards,
   confirmation and notifications; P3 supplies fields and selectors. Opening a
   nested artwork picker, closing only that picker, keeping an unsaved draft and
   deliberately discarding it remain representative acceptance journeys.
3. **Use controls with a keyboard.** P2â€“P4 own popup dismissal, focus restoration,
   labelled inputs, tab selection and expansion state. The native select exposes
   combobox semantics; a nested dialog restores focus inside its retained parent.
   These are documented improvements over observed legacy behavior, not claims
   that every legacy keyboard trace was identical.
4. **Keep playback ownership intact.** Speed, Sleep, bookmarks, queue tools and
   nested menus keep the existing player owner and coordinator contracts. P2 and
   P3 supply their shared control infrastructure; P4 preserves shell offsets. The
   acceptance journey closes a nested menu while its parent sheet stays usable,
   without changing page width or creating a competing playback owner.
5. **Deliver a smaller, maintainable Dashboard.** P5 removes the runtime UI
   framework and builds first-party CSS with a build-only minifier. Existing
   palette values and vendor files remain preserved. The release acceptance is
   measured CSS savings, correct fingerprints and compression, plus successful
   representative browser review. Backend storage, library ownership, routes and
   enrichment behavior are outside this change.

## Phase package

| Phase | Implementation and review evidence |
|---|---|
| P0 | [Safety net and measured baseline](p0-safety-net.md) |
| P1 | [Theme, icons and layout atoms](p1-theme-icons-layout.md) |
| P2 | [Dialog, popup, toast and focus infrastructure](p2-overlays.md) |
| P3 | [Native form controls](p3-form-controls.md) |
| P4 | [Structural components and shell](p4-structural-components.md) |
| P5 | [Dependency removal and Release CSS](p5-release-css.md) |

The Product Owner authorized all-phase implementation after reviewing the plan.
The changes are integrated in one review branch, rather than claiming six merged
phase branches. No push, merge or deployment is represented here. Tests were
migrated alongside their owners instead of waiting until the last phase.

## Measured dependency inventory

[The machine-readable inventory](source-inventory.json) compares Git source at
the exact baseline commit with the working native checkout, including new files.
Reproduce it with `scripts/visual-qa/css-ownership/source-inventory.py --baseline
c765bc91 --output docs/reports/mud-removal/source-inventory.json` using Python.
It reads source and Git archive data; it does not build or change application files.

| Exact measured source pattern | Baseline | Native |
|---|---:|---:|
| Opening framework component tags | 1,085 | 0 |
| Distinct component names | 46 | 0 |
| `Icons.Material` member references | 1,957 | 0 |
| Distinct referenced Material icons | 387 | 0 |
| `IDialogService` / `ISnackbar` identifiers | 21 / 49 | 0 / 0 |
| Framework namespace / service registration | 115 / 1 | 0 / 0 |
| `var(--mud-â€¦)` reads | 148 | 0 |
| `.mud-*` class tokens | 1,168 | 0 |
| Central/Web framework package references | 1 / 1 | 0 / 0 |

These are reproducible lexical counts, not copied estimates from the handoff.
Opening tags exclude closing tags; the inventory records the regex and per-file
counts. For example, the handoff's icon total differs from this measured total.
Three retained first-party `DialogOptions` parameter identifiers and 104 generic
enum-shaped expressions are also recorded: those include instance properties and
domain types and are not proof of a surviving framework type. The strict
`NativeUiDependencyGuardrailTests` checks dependency-bearing APIs and source.
Generated icon attribution is allowed, while its content is pinned separately.

The original 234 declarations in `tuvima.tokens.css` have identical values. Its
LF-normalized SHA-256 is
`477f1f553416adb12f33682a4aa6f054e18769064185b67f47b664428fe5eebf`.
All eight vendor files have identical LF-normalized hashes. The inventory preserves
both raw and normalized hashes: raw differences reflect the existing Git checkout
line-ending conversion, not a vendor change. The pinned Material snapshot contains
394 members and has LF-normalized SHA-256
`cbb779b8054b28cae712ce43a9ff872f90c0a812fcb49fa83902d350cce0c50a`.

## Verification status

| Check | Last verified result | Final gate |
|---|---|---|
| Full solution tests | Serial `dotnet test -m:1`: 5,100 passed, 34 skipped, 0 failed across 13 projects; all 1,766 Dashboard tests passed | Passed; `.tmp/mud/solution-tests-debug-final.log` |
| Node tests, excluding Support | 137 passed, 0 failed in `.tmp/mud/node-tests-final-complete.log` | Passed |
| Python CSS audit tests | 14 passed, 0 failed | Passed |
| Changed/new JS syntax | 27 changed/new implementation and test files checked without syntax errors; `.tmp/mud/js-syntax-final-complete.log` | Passed |
| Strict documentation build | Passed including finalized package; `.tmp/mud/docs-strict-report-final.log` | Passed |
| Exact icon regeneration and source guards | Deterministic snapshot/reference tests and measured zero-dependency inventory | Passed in verified integrated checks |
| Published CSS fingerprints/compression/HTTP delivery | Publish V16 succeeded; `.tmp/mud/p5/published-css-final.json` reports verified true and actual endpoint delivery | Passed |
| CSS ownership/line/priority guardrails | Final source markers: 1,780 `!important`, 2,888 `::deep`; app.css contains 858 priority markers; ownership/2,000-line checks retained | Three final tests passed in `.tmp/mud/style-guardrails-final.log`; audit has zero errors; nine dominated candidates including two priority candidates remain unpruned |
| Targeted overlay lifecycle | Two targeted lifecycle tests passed; menus wait for actual rendered-close state before opening a child | Passed |
| Shared checkbox/control tests | 33 passed in `.tmp/mud/shared-ui-checkbox-final.log` | Passed |
| Native keyboard matrix | Select, tabs, expansion and nested-dialog assertions passed | Representative coverage passed; legacy differences retained |
| Live player utility rows | Three visible utilities retain 44px targets and 22px glyphs at 1920Ã—1080, 390Ã—844 and 390Ã—667 | Passed measured browser geometry; physical-device behavior not inferred |
| Paired screenshots and complete matrix | Original/native Home, browse, detail, gallery and editor captures form a reviewed subset | **Complete matrix not proven** |

Only `.tmp/mud/p0/verified-before` and `.tmp/mud/p5/verified-after` are valid
sources for final paired review. Older trial captures and stale publish directories
are not final evidence. Actual filenames available when this package was prepared
are listed in [the evidence index](evidence-index.json). Presence alone is not a
passing comparison. Screenshots, source geometry and keyboard traces cover different
claims; a passing style comparison cannot substitute for screenshot review.

Selected committed evidence includes the [original editor](assets/before-media-editor-1920x1080.jpg)
and [native editor](assets/after-media-editor-1920x1080.jpg),
[original gallery](assets/before-component-gallery-1920x1080.jpg) and
[native gallery](assets/after-component-gallery-1920x1080.jpg),
[original inner-editor measurements](assets/before-editor-inner.json) and
[native inner-editor measurements](assets/after-editor-inner.json),
[asset costs](assets/asset-cost-final.json) and
[published CSS verification](assets/published-css-final.json).
Native [select](assets/select-keyboard.keyboard.json),
[tab](assets/tabs-keyboard.keyboard.json),
[expansion](assets/expansion-keyboard.keyboard.json) and
[nested-dialog](assets/nested-dialog-keyboard.keyboard.json) traces accompany
the [player utility measurements](assets/player-icon-rows.json).
Paired screenshot observations are agent manual review, not independent external
human approval or proof of every matrix state.

Observed legacy keyboard findings include an Intrinsic series selector whose
focusable owner has no combobox role, a tab interaction with focus and selection
differing, and nested picker dismissal retaining the editor while leaving focus
on the document body. Native assertions require stronger semantics and focus
restoration. The harness documents these differences and does not manufacture a
passing baseline or relax array, geometry, style or target coverage checks.

## Delivered asset measurements

The final measurements in `.tmp/mud/p5/asset-cost-final.json` use identical
before/after compression settings and include the preserved vendor assets.
Unchanged .NET framework files are excluded equally. CSS savings are distinct
from savings across the combined CSS/JavaScript inventory.

| Asset inventory | Before raw | After raw | Before gzip | After gzip | Before brotli | After brotli |
|---|---:|---:|---:|---:|---:|---:|
| All delivered CSS | 2,441,797 | 1,519,651 | 322,538 | 221,015 | 237,525 | 173,862 |
| Combined CSS/JavaScript | 4,928,927 | 3,959,034 | 1,022,693 | 911,109 | 820,304 | 747,578 |

The delivered CSS gate passes: raw CSS is 37.77% smaller, gzip CSS 31.48% smaller
and brotli CSS 26.80% smaller. The scoped bundle is 1,278,063 raw bytes, below its
fresh 1,526,854-byte baseline. Published fingerprint/integrity metadata and actual
HTTP content match the minified bytes; gzip/brotli representations decompress to
those same bytes. The six minified first-party assets are a subset of the complete
delivered inventory, so their verifier totals must not be substituted for it.
The fixture is finalized with a tight 1,278,063-byte scoped-bundle ceiling, and
the final published verifier enforces that ceiling.

## Acceptance limits and final full-suite record

The implementation and build/asset gates are complete. Representative
browser review found and corrected app-bar stacking, scrollbar defaults, tab-bar
geometry, checkbox sizing/glyph treatment, control typography and textarea height.
The checkbox restores the observed 42px target while rendering the pinned glyph
at 1.5rem, measured at 21px; controls retain observed 14px/600 typography and
105.1875px textarea height. Generic button minimum height and typography precedence
are restored; both original and native edit buttons measure 42px. All visible
metadata rows have matching positions and heights in the recorded editor-inner
measurements. Two original hidden Additional rows are unmounted natively, a benign
DOM difference outside visible row geometry. Shared icon sizes use 1.25/1.5/2.25rem,
strong text retains weight 700, and outlined/text/neutral-filled/disabled colors
use the corresponding original palette values.
Final V16 gallery review confirms checkbox/switch thumbs at RGB(182,194,214) and
the original primary 135-degree gradient from RGB(136,82,252) to RGB(118,82,214).
The unchanged dirty-editor inline Escape policy retains the draft. Close followed
by Keep editing or Discard was confirmed through ordinary controls.

Native select, tabs, expansion and nested-dialog matrix checks passed. Original
traces remain separately recorded where semantic/focus behavior differs. Full
visual parity is not established: the valid paired captures cover a representative
subset. Screenshot export can toggle a scrollbar and change geometry; the helper
refuses such evidence rather than weakening its stability check. Physical touch
and reduced-motion emulation were unavailable in the documented browser surface.
These limits are acceptance gaps, not passing visual results.

- Final restore and Debug/Release build log paths and exact warning/error counts:
  all projects up to date; both builds succeeded with **0 warnings and 0 errors**.
  Logs: `.tmp/mud/solution-restore-final.log`,
  `.tmp/mud/solution-build-debug-final.log`,
  `.tmp/mud/solution-build-release-final.log`.
- Final full-solution passed/skipped/failed totals and log paths:
  **5,100 passed, 34 skipped, 0 failed across 13 projects**, using serial
  `dotnet test -m:1` with runtime hosts stopped. Log:
  `.tmp/mud/solution-tests-debug-final.log`. All 1,766 Dashboard tests passed.
  The preceding concurrent run is preserved separately in
  `.tmp/mud/solution-tests-debug-concurrent-final.log`: it had one failure in the unchanged
  `IngestionEngine_ZeroByteFile_DoesNotCrashPipeline`: after its 5-second wait it
  expected `no_result` and observed `running`. The final serial rerun passed;
  backend behavior and the test remain unchanged. No backend fix is claimed.

## Plain-English completion summary

The Dashboard's UI components and infrastructure now have first-party owners,
with tests and inventories protecting that transition. The implementation retains
the existing product journeys, adds clearer keyboard semantics and delivers less
CSS. Full serial solution tests, JavaScript and asset checks pass. Representative browser review supports
the change, while the complete requested visual matrix remains an explicit limitation.
