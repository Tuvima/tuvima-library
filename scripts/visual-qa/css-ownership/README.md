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
node --test scripts/visual-qa/css-ownership/test_compare.mjs
node scripts/visual-qa/css-ownership/compare.mjs --before .tmp/css-ownership/before --after .tmp/css-ownership/after --matrix scripts/visual-qa/css-ownership/states.json
```

Compare identical state coverage. Tolerances must exist unchanged in the reviewed
baseline and name an exact property path, reason, and numeric bound. Changed or
missing files, targets, geometry, styles, pseudo-elements, or control state fail.
Review paired JPEGs as well as JSON; computed-style equality is not visual proof.
Keep private runtime state and full evidence under ignored `.tmp/`/fixture outputs.
Commit selected sanitized screenshots and aggregate evidence only. Record actual
viewport/DPR and unsupported hover, high-DPR, device, or popout coverage honestly.

The declared matrix includes lower desktop heights and selected 320/420-pixel
widths. `prepare.mjs` closes prior overlays, discards unsaved fixture drafts,
pauses Home, and restores inherited Show missing preferences through normal UI.
Wait for responsive device-context remounts and healthy loaded data. A completed
request or an existing heading alone does not establish a settled viewport.
Never compare a rename form to a closed playlist or stale network error to healthy data.
The matrix check rejects equally incomplete before/after sets.
