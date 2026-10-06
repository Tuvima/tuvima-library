# Conservative Dashboard CSS audit

Development tooling only; the application has no Python dependency. Install the
BSD-licensed parser with `python -m pip install -r scripts/css/requirements.txt`
in a development virtual environment, then run:

```powershell
python -m unittest discover -s scripts/css -p test_audit.py
python scripts/css/audit.py --output .tmp/css-audit.json
```

The audit reads first-party Dashboard CSS, excluding `bin`, `obj`, and `vendor`.
It scans source under `src` for static tokens and dynamic class prefixes.
`--prune` removes only repeated declarations with identical selector, property,
value, importance, and conditional/layer context. Every selector in a grouped
rule must be superseded. Different-value browser fallbacks, keyframes, and final
declarations without semicolons are preserved. Files with unsupported syntax
are reported and left untouched.

Retired selectors require an explicit reviewed manifest:

```powershell
python scripts/css/audit.py --retired-class-list scripts/css/retired-player-editor-classes.json --output .tmp/css-audit.json
```

Add `--prune` only after reviewing the dry-run output. The manifest records the
October 5 source-proven retired classes; a class with a static reference or a
matching dynamic prefix fails validation. Absence from one screenshot is never
proof of retirement. Classes nested inside selector functions are not inferred
dead. Check other generated markup or external integrations when reviewing new
manifest entries. No priority markers are removed from surviving declarations.

Use same-configuration builds and desktop/phone comparisons to review the result.
See the [cleanup report](../../docs/reports/css-cleanup-2026-10-05.md).

## Ownership and bounded changes

```powershell
python scripts/css/audit.py --ownership --output .tmp/css-ownership/audit.json
python scripts/css/audit.py --max-lines 2000 --output .tmp/css-ownership/line-check.json
python scripts/css/audit.py --file src/MediaEngine.Web/Components/Details/DetailPage.razor.css --output .tmp/css-ownership/detail-dry-run.json
```

Ownership reports classify literal HTML references separately from component
parameters, C# renderers/fragments, and dynamic prefixes. A static owner is a
candidate: inspect emitted DOM scopes, compiled selectors, and consumers before
moving rules. `mud-*` indicates third-party targets; unresolved targets remain
visible and never authorize deletion. Selector functions are scanned without
interpreting quoted attribute values as classes. Unsupported parsing remains
reported; source lines and raw non-comment marker counts still cover those files.

`--max-lines` fails for oversized isolated `.razor.css` inputs; globals are
reported but exempt. The Dashboard guardrail fixture has an empty line-exception list.
Ownership transfers explain per-file marker increases while aggregate budgets
cannot grow. Its finalized Release scoped-bundle ceiling is 1,278,063 bytes, below the inherited
1,526,854-byte ceiling. Line and override ratchets remain active. See
`docs/reports/mud-removal/p5-release-css.md` for the measured gate and visual coverage limits.

Release minification targets live in `src/MediaEngine.Web/Build/DashboardCss.targets`.
Docker copies that directory before restore, and an import guard verifies this
ordering. CI publishes the Dashboard in Release and runs
`node scripts/build/verify-dashboard-css.mjs .tmp/ci/dashboard-publish "" .tmp/ci/published-css.json`
to check minification, compressed bytes, fingerprints and the scoped bundle ceiling.

Mutation now requires one or more exact `--file` arguments as well as `--prune`.
There is no implicit whole-tree pruning. Out-of-tree, vendor, and generated files
are refused. Inspect the selected dry run before applying it. Raw byte totals and
UTF-8/LF-normalized byte totals are separate to make checkout line endings visible.
