// Finalize captured baseline metadata without re-reading changed source files.
import fs from 'node:fs';
import crypto from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const dir = path.dirname(fileURLToPath(import.meta.url));
const read = p => JSON.parse(fs.readFileSync(path.join(dir, p), 'utf8'));
const write = (p, data) => fs.writeFileSync(path.join(dir, p), JSON.stringify(data, null, 2) + '\n');
const manifest = read('file-manifest.json');
const guide = manifest.files.find(r => r.path === 'src/MediaEngine.Web/CLAUDE.md');
guide.proposedAction = 'KEEP-IN-PLACE'; guide.purpose = 'repository-authority';
guide.reason = 'New user-authored Dashboard guidance remains at its requested source location.';
guide.currentAuthority = 'Explicit user-authored current authority; preserve.';
manifest.counts = Object.fromEntries(['PUBLISH', 'KEEP-IN-PLACE', 'KEEP-MOVE', 'DELETE'].map(action => [action, {
  files: manifest.files.filter(r => r.proposedAction === action).length,
  bytes: manifest.files.filter(r => r.proposedAction === action).reduce((n, r) => n + r.bytes, 0) }]));
const authorization = 'The user instructed proceeding with implementation across all phases; parent confirmed this includes exact manifest execution. Screenshot removal also has the earlier October 8 product-owner amendment.';
manifest.authorization = authorization;
manifest.executionConditions = ['Preserve every retained non-image dependency.', 'Repair removed capture/log references without inventing new evidence.',
  'Preserve protected source bytes and paths.', 'Do not delete or relocate tools/reports output.', 'Do not synchronize retired .agent.'];
for (const row of manifest.files) {
  if (['DELETE', 'KEEP-MOVE'].includes(row.proposedAction)) row.approval = 'Authorized all-phase implementation; execute this exact baseline list subject to executionConditions.';
}
write('file-manifest.json', manifest);
const routes = read('route-manifest.json');
const missing = routes.routes.find(r => r.oldPath === 'docs/404.md');
missing.oldUrl = missing.oldHtmlUrl = missing.destinationUrl = '/tuvima_library/404.html';
write('route-manifest.json', routes);
const keep = manifest.files.filter(r => r.proposedAction === 'KEEP-MOVE');
const rows = ['# Exact cleanup actions', '', 'Captured before source cleanup. Each action is explained in `file-manifest.json`; all non-image evidence and uncertain work is retained. No FOLD actions are proposed because safe semantic removal was not proven.', '', '## KEEP-MOVE', '', ...keep.map(r => `- \`${r.path}\` → \`${r.destination}\` (${r.bytes} bytes; ${r.purpose})`), '', '## DELETE', '', ...manifest.files.filter(r => r.proposedAction === 'DELETE').map(r => `- \`${r.path}\` (${r.bytes} bytes; ${r.purpose})`), '', '## Preserve in place', '', ...manifest.files.filter(r => ['PUBLISH', 'KEEP-IN-PLACE'].includes(r.proposedAction)).map(r => `- \`${r.path}\` (${r.proposedAction})`), '', '## Plain-English completion summary', '', 'This list removes old screenshots and generated logs while keeping active plans, verification measurements, reusable design files, and current product guidance. It records exactly what moves so maintainers can repair links and review the result.', ''];
fs.writeFileSync(path.join(dir, 'cleanup-actions.md'), rows.join('\n'));
const digest = p => crypto.createHash('sha256').update(fs.readFileSync(path.join(dir, p))).digest('hex');
write('authorization.json', { recordedAt: new Date().toISOString(), authorization,
  appliesToManifestSha256: digest('file-manifest.json'), appliesToRouteManifestSha256: digest('route-manifest.json'),
  counts: manifest.counts, foldActions: 0, execution: 'Not performed by inventory worker; cleanup integrator owns source mutations.', conditions: manifest.executionConditions });
const referenceLedger = read('reference-ledger.json');
const generated = [];
const generatedDir = '.codex/context';
if (fs.existsSync(generatedDir)) {
  for (const file of fs.readdirSync(generatedDir)) {
    const p = `${generatedDir}/${file}`;
    if (!fs.statSync(p).isFile() || !/\.(md|json|toml)$/.test(file)) continue;
    const text = fs.readFileSync(p, 'utf8');
    generated.push({ path: p, sha256: crypto.createHash('sha256').update(text).digest('hex'),
      matchingLines: text.split(/\r?\n/).flatMap((line, i) => /docs\/(plans|reports|proposals|ui|design-system)\/|assets\/screenshots\//.test(line) ? [i + 1] : []),
      action: 'Regenerate using maintained context script after cleanup; never hand-edit or commit.' });
  }
}
referenceLedger.localGeneratedContext = generated;
referenceLedger.localGeneratedScope = 'Only .codex/context inspected as generated references. Ignored local screenshots/logs outside this directory are not tracked cleanup targets.';
write('reference-ledger.json', referenceLedger);
console.log(JSON.stringify({ counts: manifest.counts, files: manifest.files.length, routes: routes.routes.length, knownFragmentLinks: routes.routes.reduce((n,r) => n + r.relevantFragments.filter(h => h.inboundKnown).length, 0), generatedContextFiles: generated.length }, null, 2));
