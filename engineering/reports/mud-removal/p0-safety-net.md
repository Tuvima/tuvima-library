# P0 â€” Safety net and baseline

P0 supplies review tools and a measured original baseline; it does not certify
every requested browser state as covered. See the [consolidated review](./index.md).

The disposable fixture and declared state matrix cover representative Home,
detail, browse, editor, picker, control, popup and playback surfaces. Existing CUA
browser handles drive capture; there is no fictitious standalone capture CLI.
The helper records actual viewport/DPR and JPEG dimensions, geometry, computed
styles, pseudo-elements and control state. It refuses unstable captures and
missing targets. Semantic target IDs permit reviewed old/native selector mappings
without silently discarding schema-1 descendant coverage.

Object-key order is ignored only when transporting otherwise identical objects;
all values and array order remain strict. The screenshot uses an explicit viewport
rectangle, retaining scrollbar gutters. Instability errors identify changed groups
and labelled control coordinates. The comparison gate requires declared coverage
and actual paired image files; human screenshot review has its own metadata gate.

The keyboard helper executes locator press/click actions and declared condition
waits. It records focus role/name, expanded/selected state and resolved control
relationships without comparing random generated IDs. The committed assertions
are native acceptance expectations, not invented legacy results. Original traces
are retained separately where observed behavior differs. See the harness README
for actual legacy selector mappings and accessibility limitations.

Rather than leaving a temporary permissive dependency ratchet in the final
implementation, a strict zero-dependency source/package/provider/asset guard is
paired with [a per-file measured before/after inventory](./source-inventory.json).
This records the requested regression boundary in the final architecture.

Evidence: only `.tmp/mud/p0/verified-before` and `.tmp/mud/p5/verified-after`;
the [evidence index](./evidence-index.json) records actual files. Relevant capture,
comparison and keyboard regression tests passed within the final 137 passing Node
tests. Native select, tabs, expansion and nested-dialog matrix assertions passed.
Valid paired captures cover a representative subset; complete matrix coverage and
full visual parity are not established. Export-induced scrollbar geometry changes
still refuse capture, and physical touch/reduced-motion emulation was unavailable.

## Plain-English completion summary

Reviewers can inspect what was actually captured and distinguish passing checks
from untested states. The safety tools now reject incomplete or unstable evidence;
they do not establish full visual acceptance on their own.
