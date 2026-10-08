// Run once before cleanup: node engineering/docs-overhaul/build-inventory.mjs
// A frozen baseline is intentional. Re-running requires --replace-baseline.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';

const root = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
process.chdir(root);
const output = 'engineering/docs-overhaul';
if (fs.existsSync(`${output}/file-manifest.json`) && !process.argv.includes('--replace-baseline')) {
  throw new Error('Baseline already exists. Preserve it; --replace-baseline is for an explicitly reviewed refresh.');
}
const git = (...args) => execFileSync('git', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
const tracked = git('ls-files', '-z').split('\0').filter(Boolean);
const additions = ['docs/plans/documentation-overhaul-reviewed-plan-2026-10-08.md',
  'docs/product/presentation-rules.md', 'docs/architecture/architecture-summary.md', 'src/MediaEngine.Web/CLAUDE.md'];
const files = [...new Set([...tracked, ...additions.filter(p => fs.existsSync(p))])].sort();
const trackedSet = new Set(tracked);
const sourceSet = new Set(files);
const texts = new Map();
for (const p of files) {
  if (/\.(md|mdx|html|css|js|mjs|jsx|ts|tsx|json|yml|yaml|ps1|bat|cmd|sh|cs|csproj|props|targets|razor|xml|txt|iss)$/i.test(p)
      && !/^(\.agent|\.claude|\.codex)\//.test(p) && fs.existsSync(p)) {
    const data = fs.readFileSync(p);
    if (data.length < 8 * 1024 * 1024) texts.set(p, data.toString('utf8'));
  }
}
const inbound = new Map(), outgoing = new Map(), unresolved = [];
function reference(from, target, line, kind, fragment = '') {
  if (!sourceSet.has(target)) return;
  const ref = { source: from, line, kind, ...(fragment ? { fragment } : {}) };
  const key = `${from}:${line}:${kind}:${fragment}`;
  const refs = inbound.get(target) ?? new Map(); refs.set(key, ref); inbound.set(target, refs);
  const out = outgoing.get(from) ?? new Set(); out.add(target); outgoing.set(from, out);
}
for (const [from, text] of texts) {
  const lines = text.split(/\r?\n/);
  lines.forEach((line, index) => {
    for (const match of line.matchAll(/(?:docs|assets)\/[A-Za-z0-9_.\/-]+\.[A-Za-z0-9]+/g)) {
      reference(from, match[0], index + 1, 'literal-repository-path');
    }
    const targets = [...line.matchAll(/\]\(<?([^\s)>]+)>?(?:\s+[^)]*)?\)/g),
      ...line.matchAll(/(?:src|href)=["']([^"']+)["']/g),
      ...line.matchAll(/(?:url\(["']?)([^\s)'";]+)["']?\)/g)];
    for (const match of targets) {
      const raw = match[1];
      if (/^(?:https?:|data:|mailto:|#|app:|codex:|[A-Za-z]:)/.test(raw)) continue;
      let target;
      try { target = decodeURIComponent(raw.split(/[?#]/)[0]); } catch { continue; }
      if (!target) continue;
      target = target.startsWith('/') ? target.slice(1) : path.posix.normalize(path.posix.join(path.posix.dirname(from), target));
      if (!sourceSet.has(target) && from.startsWith('docs/') && sourceSet.has(`docs/${target}`)) target = `docs/${target}`;
      reference(from, target, index + 1, 'relative-link-or-asset', raw.includes('#') ? raw.split('#').slice(1).join('#') : '');
      if (!sourceSet.has(target) && !target.startsWith('..')) unresolved.push({ source: from, line: index + 1, reference: raw, resolved: target });
    }
    if (from === 'mkdocs.yml') {
      for (const match of line.matchAll(/\/?([A-Za-z0-9_./-]+\.md)\b/g)) reference(from, `docs/${match[1]}`, index + 1, 'mkdocs-published');
    }
  });
}
const dates = new Map();
let currentDate;
for (const line of git('log', '--format=DATE:%aI', '--name-only', '--', 'docs', 'assets').split(/\r?\n/)) {
  if (line.startsWith('DATE:')) currentDate = line.slice(5);
  else if (line && !dates.has(line)) dates.set(line, currentDate);
}
const namedInternal = new Set(['docs/reference/sequence-dataset-gap-report-2026-07-15.md',
  'docs/architecture/api-boundary-debt.md', 'docs/architecture/openapi-migration.md',
  'docs/architecture/storage-policy-review.md', 'docs/architecture/account-profile-authentication-plan.md']);
const protectedPaths = ['docs/reference/approved-plugins.json', 'docs/reference/wikidata-property-map.md'];
const candidate = p => /^docs\/(plans|proposals|reports|ui|design-system)\//.test(p)
  || /^assets\/screenshots\//.test(p) || namedInternal.has(p) || protectedPaths.includes(p) || additions.includes(p);
const hash = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const screenshots = files.filter(p => /^assets\/screenshots\//.test(p));
const screenshotHashes = new Set(screenshots.map(hash));
const copies = files.filter(p => /\.(png|jpe?g|webp|gif|avif)$/i.test(p) && !screenshots.includes(p)
  && fs.existsSync(p) && screenshotHashes.has(hash(p)));
const inventory = files.filter(p => candidate(p) || copies.includes(p)).map(p => {
  const content = texts.get(p) ?? '';
  const refs = [...(inbound.get(p)?.values() ?? [])].filter(r => r.source !== p);
  const image = /\.(jpe?g|png|webp|gif|avif)$/i.test(p);
  const screenshot = screenshots.includes(p) || copies.includes(p) || (image && /^docs\/(reports|plans|proposals)\//.test(p)) || /\/screenshots\//.test(p);
  const publish = ['docs/design-system/ui-consistency-standard.md', 'docs/design-system/visual-qa-checklist.md'].includes(p)
    || protectedPaths.includes(p) || (additions.includes(p) && !p.includes('/plans/'));
  const log = /\.log$/i.test(p);
  const action = screenshot || log ? 'DELETE' : p === 'src/MediaEngine.Web/CLAUDE.md' ? 'KEEP-IN-PLACE' : publish ? 'PUBLISH' : 'KEEP-MOVE';
  const reason = screenshot ? 'Existing documentation screenshot or QA capture; removal authorized. Preserve non-image measurements and repair references.'
    : log ? 'Generated QA log; preserve measured result and limitations in retained report before removal.'
    : publish ? 'Protected runtime contract, current product authority, or maintained public design reference.'
    : /\.json$/i.test(p) ? 'Retain non-image evidence and metadata dependency closure; no proof that evidence is dispensable.'
    : /^docs\/design-system\//.test(p) ? 'Reusable design working asset; retain fonts, icons, brand sources, previews and their dependencies together.'
    : /\.(md|mdx)$/i.test(p) ? 'Retain current authority, history and unfinished work conservatively; completion is not enough to prove safe deletion.'
    : 'Retain scripts and working evidence dependency closure.';
  const evidence = content.split(/\r?\n/).map((text, i) => ({ line: i + 1, text }))
    .filter(r => /^(?:Status:|## Current acceptance|## Remaining|## Follow-up|## Acceptance)|remaining|not.*verified|supersed|still.*open|not.*complete|pending/i.test(r.text)).slice(0, 10);
  return { path: p, trackedAtBaseline: trackedSet.has(p), bytes: fs.statSync(p).size,
    sha256: hash(p), lastGitAuthorDate: dates.get(p) ?? null,
    purpose: screenshot ? 'screenshot' : log ? 'qa-log' : publish ? 'public-authority' : /^docs\/design-system\//.test(p) ? 'design-working-asset' : /\.json$/.test(p) ? 'non-image-evidence' : 'engineering-record',
    proposedAction: action, destination: action === 'KEEP-MOVE' ? p.replace(/^docs\//, 'engineering/') : ['PUBLISH', 'KEEP-IN-PLACE'].includes(action) ? p : null,
    approval: screenshot ? 'Product-owner screenshot-removal amendment, 2026-10-08' : action === 'DELETE' || action === 'KEEP-MOVE' ? 'Exact manifest checkpoint required before execution' : 'Preserve',
    reason, owner: 'WP7 cleanup integrator',
    currentAuthority: refs.some(r => /^(AGENTS\.md|CLAUDE\.md|src\/.*CLAUDE\.md|scripts\/|docs\/(architecture|product|guides|reference)\/)/.test(r.source)) ? 'Referenced by current guidance or published reference; retain' : 'Completion/authority unclear; conservative retention',
    completionEvidence: evidence, inboundReferences: refs,
    dependencies: [...(outgoing.get(p) ?? [])].filter(q => q !== p).sort() };
});
const oldMkdocs = fs.readFileSync('mkdocs.yml', 'utf8');
const notInNavBlock = oldMkdocs.match(/not_in_nav:\s*\|\r?\n([\s\S]*?)(?=\n\S)/)?.[1] ?? '';
const notInNav = new Set([...notInNavBlock.matchAll(/\/([^\s]+\.md)/g)].map(m => `docs/${m[1]}`));
const navBlock = oldMkdocs.slice(oldMkdocs.indexOf('\nnav:'));
const nav = new Set([...navBlock.matchAll(/([A-Za-z0-9_./-]+\.md)/g)].map(m => `docs/${m[1]}`));
function headings(text) {
  const result = [], used = new Map(); let fence = false;
  text.split(/\r?\n/).forEach((line, i) => {
    if (/^\s*(```|~~~)/.test(line)) { fence = !fence; return; }
    const m = !fence && line.match(/^ {0,3}#{1,6}\s+(.+?)(?:\s+#+)?\s*$/);
    if (!m) return;
    const title = m[1].replace(/\s*\{[^}]*\}\s*$/, '');
    const explicit = m[1].match(/\{[^}]*#([^\s}]+)/)?.[1];
    let id = explicit ?? title.toLowerCase().replace(/<[^>]*>/g, '').replace(/[^\p{L}\p{N}_\s-]/gu, '').trim().replace(/[\s-]+/g, '-');
    const n = used.get(id) ?? 0; used.set(id, n + 1); if (n) id += `_${n}`;
    result.push({ id, title, line: i + 1, inboundKnown: [...(inbound.get(text.path)?.values() ?? [])].some(r => r.fragment === id) });
  }); return result;
}
const actionMap = new Map(inventory.map(r => [r.path, r]));
const renames = { 'docs/settings-architecture.md': 'docs/architecture/settings.md', 'docs/artwork-architecture.md': 'docs/architecture/artwork.md', 'docs/providers.md': 'docs/reference/providers.md' };
function route(p) { if (p === 'docs/404.md') return '/tuvima_library/404.html'; const relative = p.replace(/^docs\//, '').replace(/\.(md|mdx)$/, ''); return '/tuvima_library/' + (relative === 'index' ? '' : relative.replace(/\/index$/, '') + '/'); }
const routes = files.filter(p => /^docs\/.+\.(md|mdx)$/.test(p)).map(p => {
  const row = actionMap.get(p), retire = row?.proposedAction === 'KEEP-MOVE';
  const destination = retire ? null : renames[p] ?? p;
  const anchors = headings(texts.get(p) ?? '').map(h => ({ ...h, inboundKnown: [...(inbound.get(p)?.values() ?? [])].some(r => r.fragment === h.id) }));
  return { oldPath: p, oldUrl: route(p), oldHtmlUrl: p === 'docs/404.md' ? route(p) : route(p) + 'index.html',
    baselinePublication: nav.has(p) ? 'navigation' : notInNav.has(p) ? 'not_in_nav' : 'discovered-by-MkDocs',
    trackedAtBaseline: trackedSet.has(p), relevantFragments: anchors,
    destinationPath: destination, destinationUrl: destination ? route(destination) : null,
    disposition: retire ? 'intentional-retirement-engineering-record' : renames[p] ? 'canonical-replacement' : 'preserve',
    reason: retire ? 'Unpublished engineering source remains in repository; no redirect to unrelated generic page.' : 'Preserve useful guide/reference address or exact named successor.',
    audience: (texts.get(p) ?? '').match(/^audience:\s*["']?([^"'\r\n]+)/m)?.[1] ?? 'engineering',
    finalStatus: retire ? 'unpublished' : (texts.get(p) ?? '').match(/^status:\s*["']?([^"'\r\n]+)/m)?.[1] ?? 'needs-explicit-writer-decision',
    owner: retire ? 'WP7 cleanup integrator' : 'WP3 / content integrator' };
});
const counts = Object.fromEntries(['PUBLISH', 'KEEP-IN-PLACE', 'KEEP-MOVE', 'DELETE'].map(action => [action, { files: inventory.filter(r => r.proposedAction === action).length, bytes: inventory.filter(r => r.proposedAction === action).reduce((n, r) => n + r.bytes, 0) }]));
const baseline = { schemaVersion: 1, generatedAt: new Date().toISOString(), baseCommit: git('rev-parse', 'HEAD').trim(),
  sourceState: 'Tracked baseline plus explicitly preserved user-added authorities; working tree had pre-existing edits. Source hashes describe baseline working bytes.',
  scope: 'docs/plans, proposals, reports, ui, design working assets, named internal pages, protected paths, screenshot assets/copies, user-added authorities',
  rules: ['No source mutation by inventory generation.', 'No authority from retired .agent mirror.', 'All uncertain records and non-image evidence retained.', 'Runtime consumers remain unchanged.', 'tools/reports excluded from cleanup.'],
  counts, trackedScreenshotCount: screenshots.length, screenshotCopies: copies, files: inventory };
fs.mkdirSync(output, { recursive: true });
const write = (name, value) => fs.writeFileSync(`${output}/${name}`, JSON.stringify(value, null, 2) + '\n');
write('file-manifest.json', baseline);
write('route-manifest.json', { schemaVersion: 1, baseCommit: baseline.baseCommit, base: '/tuvima_library/',
  anchorMethod: 'Source ATX headings using MkDocs/Python-Markdown Unicode slug convention, duplicate _n and explicit IDs. Final old-site render check must resolve exceptional markup/setext/plugin anchors.',
  routes, notInNavMissingSources: [...notInNav].filter(p => !sourceSet.has(p)) });
write('reference-ledger.json', { schemaVersion: 1, baselineSourcesScanned: texts.size,
  scanPolicy: 'Tracked textual Markdown/MDX/HTML/CSS/scripts/config/code plus named new authority; exact repository paths and resolved local links. Dynamic/computed paths require semantic review. Retired .agent, .claude and generated .codex are excluded from authority.',
  unresolvedLocalReferences: unresolved.filter(r => candidate(r.source)),
  screenshotReferences: inventory.filter(r => r.purpose === 'screenshot').map(r => ({ path: r.path, references: r.inboundReferences })),
  protectedFiles: protectedPaths.map(p => ({ path: p, sha256: hash(p), references: [...(inbound.get(p)?.values() ?? [])] })) });
fs.writeFileSync(`${output}/mkdocs-baseline.yml`, oldMkdocs);
console.log(JSON.stringify({ baseCommit: baseline.baseCommit, counts, screenshots: screenshots.length, copies, routes: routes.length, sourcesScanned: texts.size }, null, 2));
