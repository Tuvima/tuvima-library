# P5 â€” Dependency removal and Release CSS

The central and Web framework package references, imports, registrations,
providers and asset links are removed. Shared native services and first-party
theme/control styles supply their behavior. Current README, project guidance and
architecture/design-system documentation describe the native ownership model;
historical reports retain their original context.

NUglify 1.23.3 is a build-only dependency, with BSD-2-Clause license and the
upstream notice retained in `licenses/NUglify.txt` and `THIRD-PARTY-NOTICES.md`.
`scripts/build/dashboard-css.targets` uses a Roslyn build task, not a runtime
minifier. `MinifyFirstPartyDashboardCss` runs after
`ResolveProjectStaticWebAssets`, depends on `ResolveStaticWebAssetsConfiguration`,
and replaces source asset registrations with intermediate generated CSS.
`MinifyDashboardScopedCss` runs after `BundleScopedCssFiles` for the project and
application scoped outputs. Generated content is supplied before endpoint
fingerprinting/compression; original source files remain editable. Debug does not
minify. Unsupported CSS fails the Release task rather than silently passing.

`scripts/build/verify-dashboard-css.mjs` verifies published minified content,
gzip/brotli equality, fingerprinted endpoints and integrity metadata, with optional
actual HTTP delivery. It rejects runtime framework/minifier dependencies and
retired framework assets. Asset-cost measurements separate CSS from JavaScript
and apply identical compression settings; raw totals alone are not the download
gate. Final publish V16 succeeded. The final HTTP verifier reports `verified=true`
and actual endpoint delivery. Delivered CSS decreases from 2,441,797 to 1,519,651
raw bytes, 322,538 to 221,015 gzip bytes and 237,525 to 173,862 brotli bytes.
The scoped bundle decreases from its fresh 1,526,854-byte baseline to 1,278,063.
See the consolidated report for the distinct combined CSS/JavaScript inventory.

The strict dependency guard scans first-party source, providers, registrations,
package references and assets. The pinned icon guard checks exact regeneration
and every reference. The [source inventory](source-inventory.json) retains exact
original/current token and vendor hashes, including raw line-ending differences.
No vendor content or original palette declaration value was changed.

CSS ownership, 2,000-line limits and priority budgets remain enforced. Final source
counts are 1,780 `!important` and 2,888 `::deep`, with 858 priority markers in
app.css. The final source audit has zero errors; nine dominated candidates,
including two priority candidates, remain unpruned. Final fixture and source guards
are part of the integrated verification. This report does not
claim a wholesale proven-dead-rule deletion or complete visual acceptance merely
because legacy selectors were retargeted.

Final Node verification passed 137 tests with zero failures in
`.tmp/mud/node-tests-final-complete.log`; shared checkbox/control verification
passed 33 tests and targeted overlay lifecycle verification passed two tests.
The final serial full solution run (`dotnet test -m:1`) passed 5,100 tests with 34
skips and zero failures across 13 projects, including all 1,766 Dashboard tests.
The preceding concurrent run had one unchanged ingestion timing-test failure and
is preserved separately; the serial rerun passed without a backend or test change.
Three final CSS guardrail tests passed. The fixture is finalized with a tight
1,278,063-byte scoped-bundle ceiling, which the published verifier enforces.
Restore is up to date, and
full Debug/Release builds both have zero warnings and errors. Published asset checks
pass; complete visual parity is not claimed.
The strict documentation build including this report package passed; its log is
`.tmp/mud/docs-strict-report-final.log`.

## Plain-English completion summary

The Dashboard no longer needs the runtime UI framework, and Release builds now
prepare compact first-party CSS. Tests protect that removal, and measured CSS
download costs are lower. Representative browser review supports the implementation;
complete visual-matrix acceptance remains a documented limitation.
