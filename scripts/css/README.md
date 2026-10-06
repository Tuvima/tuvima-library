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
