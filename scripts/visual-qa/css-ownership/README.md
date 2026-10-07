# Dashboard CSS ownership evidence

Use the marked disposable [Home fixture](../home-media-cards/README.md) and the
existing [player fixture](../player-update/README.md). Stop both fixture hosts
before running `enrich-fixture.py --root <marked fixture-root>`. It adds synthetic
people, ordered owned/missing books, comic/poster fallback, collection, playlist,
and History cases. The marker makes successful enrichment idempotent. Media copies
are allowed only from the fixture's media directory. No configured catalogue,
credentials, or original user media are read or changed.

Run `provider-fixture.py` for the synthetic Apple search/lookup responses. It binds
only `127.0.0.1:61499`. Enrichment points only the disposable provider manifest at
that origin; all other providers retain their existing isolated settings. Stop the
stub after verification. It has no external API key and suppresses request logs.

`states.json` describes entity-applicable states and ordinary UI preparation. Resolve
`routeKey` through the fixture's public manifest/ownership marker; never infer
private grants from evidence. Capture through existing documented CUA browser/tab
objects. There is no standalone browser-launching CLI and no CDP/Playwright server.

```javascript
var captures = await import('file:///C:/path/to/repo/scripts/visual-qa/css-ownership/capture.mjs');
await captures.captureOwnershipState({ browser, tab, state, width: 1920, height: 1080,
  outputRoot: 'C:/path/to/repo/.tmp/css-ownership/before' });
```

Set the viewport and use normal locators to reach the specified state. Inspect a
fresh DOM snapshot, ensure the selected tab/target/hero and paused player agree,
then capture. Helpers use read-only DOM/style evaluation. Visible artwork and fonts
must settle; changed geometry/state across screenshots is refused. Missing mandatory
selectors fail rather than producing empty passing evidence. Scopes are recorded as
ownership evidence but their generated hashes are excluded from appearance comparison.

```powershell
node --test scripts/visual-qa/css-ownership/test_capture.mjs scripts/visual-qa/css-ownership/test_compare.mjs scripts/visual-qa/css-ownership/test_keyboard.mjs
node scripts/visual-qa/css-ownership/compare.mjs --before .tmp/css-ownership/before --after .tmp/css-ownership/after --matrix scripts/visual-qa/css-ownership/states.json
```

Compare identical state coverage. Tolerances must exist unchanged in the reviewed
baseline and name an exact property path, reason, and numeric bound. Changed or
missing files, targets, geometry, styles, pseudo-elements, or control state fail.
Review paired JPEGs as well as JSON; computed-style equality is not visual proof.
Keep private runtime state and full evidence under ignored `.tmp/`/fixture outputs.
Never commit screenshots or capture images; commit aggregate JSON evidence only when it
is useful for review. Record actual
viewport/DPR and unsupported hover, high-DPR, device, or popout coverage honestly.

The declared matrix includes lower desktop heights and selected 320/420-pixel
widths. `prepare.mjs` closes prior overlays, discards unsaved fixture drafts,
pauses Home, and restores inherited Show missing preferences through normal UI.
Wait for responsive device-context remounts and healthy loaded data. A completed
request or an existing heading alone does not establish a settled viewport.
Never compare a rename form to a closed playlist or stale network error to healthy data.
The matrix check rejects equally incomplete before/after sets.

`captureOwnershipState` is an exported helper, not a shell capture command. Obtain
`browser` and `tab` through the documented CUA session first. Use
`prepareOwnershipState` from `prepare.mjs` with the resolved fixture route, then
inspect the resulting state. The helpers receive those existing handles and never
launch a second browser or connect to a debugging port.

For controls whose implementation changes its markup, review a semantic target
contract **before** collecting the baseline. `state.targets` opts into schema 2:

```javascript
var semanticState = {
  id: 'editor-field',
  targets: [
    { id: 'editor', selector: '.sme-shell', min: 1, max: 1 },
    { id: 'field-control', selectors: {
      before: '.app-field input[role="combobox"]',
      after: '.app-field button[role="combobox"]'
    }, min: 1, max: 1 }
  ]
};
await captures.captureOwnershipState({ browser, tab, state: semanticState,
  phase: 'before', width: 1920, height: 1080, outputRoot: beforeRoot });
// Collect the same contract after the implementation change with phase: 'after'.
```

The example selectors must be grounded in the actual fixture DOM and scoped to
one reviewed field. An explicit `selectorMap: { 'field-control': '...' }` may
also supply the current selector. Target IDs, required counts, pseudo-elements,
geometry, styles and control state still compare strictly. Only the locator
spelling may differ. Omitting a semantic target or changing its reviewed contract
fails. This does not allow replacing comprehensive coverage with a single outer
wrapper after implementation.

Legacy `state.selectors` captures remain schema 1 with the existing strict
selector/count comparison. Existing schema 1 baselines remain valid. Schema 1 and
schema 2 cannot be mixed to silently discard descendant coverage; obtain both
semantic captures from the corresponding implementations, or retain and report
the actual legacy differences for review.

Schema 2 captures explicitly record the paired screenshot filename and required
human review method. The comparator checks both image files exist and reports
pending reviews separately from style differences. It does **not** perform an
automated pixel comparison. Record the actual review in a separate manifest:

```json
{
  "schemaVersion": 1,
  "pairs": [
    {
      "capture": "editor-field-1920x1080.styles.json",
      "decision": "pass",
      "reviewer": "Actual reviewer name",
      "reviewedAt": "Actual ISO timestamp",
      "notes": "Actual observations from both screenshots"
    }
  ]
}
```

Those strings are placeholders, not evidence. Require complete paired review for
the release gate with:

```powershell
node scripts/visual-qa/css-ownership/compare.mjs --before .tmp/css-ownership/before --after .tmp/css-ownership/after --matrix scripts/visual-qa/css-ownership/states.json --visual-review .tmp/css-ownership/screenshot-review.json --require-visual-review
```

Missing, duplicate, unknown, incomplete or failed screenshot reviews cannot pass
that gate. A passing style-only comparison reports pending screenshots and must
not be reported as a passing visual review.

`keyboard-states.json` declares popup, nested-dialog, dialog, select, tab and
expansion coverage. Prepare each fixture state through normal UI first, then run
the exported helper with an existing CUA tab:

```javascript
var keyboard = await import('file:///C:/path/to/repo/scripts/visual-qa/css-ownership/keyboard.mjs');
var readFiles = await import('node:fs/promises');
var matrix = JSON.parse(await readFiles.readFile('C:/path/to/repo/scripts/visual-qa/css-ownership/keyboard-states.json', 'utf8'));
keyboard.validateKeyboardMatrix(matrix);
var state = matrix.states.find(item => item.id === 'tabs-keyboard');
// Navigate/prepare state.route or the resolved state.routeKey using ordinary UI.
await keyboard.runKeyboardState({ tab, state, outputRoot: 'C:/path/to/repo/.tmp/css-ownership/keyboard-before' });
```

Every step uses a declared locator `press` or `click`, waits only on declared UI
conditions, then asserts the captured focus and control state. Ambiguous or missing
action targets and failed assertions refuse evidence. Focus records include role
and name; expanded/selected state and `aria-controls` resolution use semantic target
IDs rather than generated DOM IDs. Transient popup targets may have count zero
only where the step explicitly asserts that closed state. There are no guessed
wait durations or unrecorded successful baselines.

The artwork-library picker uses `CloseOnEscapeKey=true` in both implementations.
The first Escape must close only the child picker; the second Escape closes the
parent editor. The native matrix additionally requires focus to return inside the
parent after the first Escape. The observed original implementation closed the
picker and retained the editor, but left focus on the document body. Record that
as a focus-restoration improvement rather than claiming identical keyboard traces.

The committed matrix is a set of native acceptance assertions, not a measured
legacy baseline. Original tab headers are wrapped in `.mud-tooltip-root`, so their
first/second semantic target selectors use wrapper `:nth-child(1)`/`:nth-child(2)`;
native headers use the first/second direct children of `.tl-tabs-toolbar`. The
matrix unions those reviewed mappings while retaining required single-target
counts and focus/selection assertions. An observed legacy ArrowRight trace may
retain the Providers selection even while Artwork receives focus; record actual
selection and focus rather than changing the native assertion to conceal it.

The original Intrinsic series set label belongs to a hidden input. Its keyboard
owner is `.mud-select:has(input[aria-label='Intrinsic series set'])
.mud-input-slot[tabindex='0']`, whose observed role is null. The native owner is
`[role='combobox'][aria-label='Intrinsic series set']`. Prepare legacy actions with
the actual focusable selector and record its observed role/name and popup state
in a separate original trace. The native matrix requires proper combobox semantics;
that accessibility improvement is an intentional difference, not a passing parity
comparison. The strict trace comparator reports different expected or actual
semantics rather than rewriting the baseline. State definitions may require
fixture-specific selector preparation; assertion failures remain findings.

```powershell
node scripts/visual-qa/css-ownership/keyboard.mjs --before .tmp/css-ownership/keyboard-before --after .tmp/css-ownership/keyboard-after --matrix scripts/visual-qa/css-ownership/keyboard-states.json
```

This CLI compares existing trace files; it never launches or drives a browser.
It rejects an equally incomplete pair of directories. The helper's name resolver
uses ARIA labels, native labels and text fallback, and is not a full browser
accessibility-tree implementation. Review a fresh CUA DOM snapshot alongside
traces. High-DPR rendering, real touch devices, fullscreen, physical keyboard
behavior, hover and reduced motion still require supported separate checks.
