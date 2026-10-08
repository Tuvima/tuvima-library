import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
const dir = path.dirname(fileURLToPath(import.meta.url));
const read = p => JSON.parse(fs.readFileSync(path.join(dir, p), 'utf8'));
const digest = p => crypto.createHash('sha256').update(fs.readFileSync(path.join(dir, p))).digest('hex');
const manifest = read('file-manifest.json'), routes = read('route-manifest.json'), authorization = read('authorization.json');
assert.equal(new Set(manifest.files.map(r => r.path)).size, manifest.files.length, 'Duplicate inventory path');
assert.equal(new Set(routes.routes.map(r => r.oldUrl)).size, routes.routes.length, 'Duplicate old route');
assert.equal(digest('file-manifest.json'), authorization.appliesToManifestSha256, 'Authorization manifest drift');
assert.equal(digest('route-manifest.json'), authorization.appliesToRouteManifestSha256, 'Authorization route drift');
for (const [action, count] of Object.entries(manifest.counts)) {
  const rows = manifest.files.filter(r => r.proposedAction === action);
  assert.equal(rows.length, count.files); assert.equal(rows.reduce((n,r) => n+r.bytes, 0), count.bytes);
}
assert.equal(manifest.trackedScreenshotCount, 15);
assert.deepEqual(manifest.screenshotCopies, ['docs/design-system/assets/screenshots/epub-reader.png']);
for (const row of manifest.files) {
  assert.match(row.sha256, /^[a-f0-9]{64}$/); assert(row.bytes >= 0); assert(row.reason && row.currentAuthority);
  if (row.proposedAction === 'DELETE') assert(['screenshot','qa-log'].includes(row.purpose), `Unexpected deletion: ${row.path}`);
  if (row.proposedAction === 'KEEP-MOVE') assert.equal(row.destination, row.path.replace(/^docs\//, 'engineering/'));
  assert(!row.path.startsWith('tools/reports/'), 'Out-of-scope cleanup');
}
for (const protectedPath of ['docs/reference/approved-plugins.json', 'docs/reference/wikidata-property-map.md']) {
  assert.equal(manifest.files.find(r => r.path === protectedPath)?.proposedAction, 'PUBLISH');
}
assert.equal(manifest.files.find(r => r.path === 'src/MediaEngine.Web/CLAUDE.md')?.proposedAction, 'KEEP-IN-PLACE');
assert.equal(routes.routes.find(r => r.oldPath === 'docs/404.md')?.oldUrl, '/tuvima_library/404.html');
assert(routes.routes.some(r => r.baselinePublication === 'not_in_nav'));
assert.equal(read('claim-ledger.json').claims.length, 13);
console.log(`Inventory verified: ${manifest.files.length} unique paths, ${routes.routes.length} unique old routes; action totals and authorization hashes agree.`);
