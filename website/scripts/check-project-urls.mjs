import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { base, origin, repoUrl } from './content-contract.mjs';

// Fails `npm run check` when a document links to the docs site or the GitHub
// repository under any address other than the one in content-contract.mjs.
// A repository rename or a new docs domain then becomes a one-line contract
// change, and this check lists every stale link instead of letting it 404.

const repoRoot = fileURLToPath(new URL('../../', import.meta.url));
const repoOwner = new URL(repoUrl).pathname.split('/')[1];
const repoName = new URL(repoUrl).pathname.split('/')[2];

// Paths are repo-relative and use forward slashes.
const roots = ['README.md', 'CONTRIBUTING.md', 'SECURITY.md', 'CLAUDE.md', 'AGENTS.md', 'docs', 'engineering', '.claude', 'src/MediaEngine.Web/CLAUDE.md', 'website/README.md'];
const textExtensions = new Set(['.md', '.mdx', '.json']);
// Historical records keep the addresses they were written with.
const historicalPrefixes = ['engineering/plans/', 'engineering/proposals/', 'engineering/reports/', 'engineering/docs-overhaul/', 'engineering/reference/', 'engineering/ui/', 'engineering/architecture/archive/', 'engineering/design/mockups/'];
// Known exceptions, each with the follow-up that removes it.
const allowed = new Map([
  ['docs/reference/plugin-catalog.md', 'Plugin catalogue address changes together with config/core.json in the Docker/name follow-up.'],
  ['docs/reference/approved-plugins.json', 'Plugin catalogue address changes together with config/core.json in the Docker/name follow-up.'],
]);

// Only spellings of *this* project count as stale (tuvima_library vs tuvima-library);
// sibling repositories and sites such as tuvima-wikidata are left alone.
const normalise = name => name.replace(/\.git$/, '').replace(/[-_.]/g, '').toLowerCase();

export function findStaleUrls(text, contract = { base, origin, repoOwner, repoName }) {
  const stale = [];
  const siteHost = new URL(contract.origin).host.replaceAll('.', '\\.');
  const siteBase = contract.base.replace(/^\//, '');
  for (const match of text.matchAll(new RegExp(`https?://${siteHost}/([A-Za-z0-9_.-]+)`, 'g'))) {
    if (match[1] !== siteBase && normalise(match[1]) === normalise(siteBase)) stale.push(match[0]);
  }
  const repoPattern = new RegExp(`(?:github\\.com|raw\\.githubusercontent\\.com)/${contract.repoOwner}/([A-Za-z0-9_.-]+)`, 'gi');
  for (const match of text.matchAll(repoPattern)) {
    const name = match[1].replace(/\.git$/, '');
    if (name.toLowerCase() !== contract.repoName.toLowerCase() && normalise(name) === normalise(contract.repoName)) stale.push(match[0]);
  }
  return stale;
}

async function* walk(relative) {
  const full = path.join(repoRoot, relative);
  let stat;
  try { stat = await fs.stat(full); } catch { return; } // optional root (e.g. no .claude folder in a checkout)
  if (stat.isFile()) { yield relative; return; }
  for (const entry of await fs.readdir(full, { withFileTypes: true })) {
    if (entry.name === 'node_modules' || entry.name.startsWith('.generated')) continue;
    yield* walk(path.posix.join(relative, entry.name));
  }
}

export async function checkProjectUrls() {
  const problems = [];
  for (const root of roots) {
    for await (const file of walk(root)) {
      if (!textExtensions.has(path.extname(file))) continue;
      if (historicalPrefixes.some(prefix => file.startsWith(prefix)) || allowed.has(file)) continue;
      const text = await fs.readFile(path.join(repoRoot, file), 'utf8');
      for (const url of new Set(findStaleUrls(text))) problems.push(`${file}: ${url}`);
    }
  }
  return problems;
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const problems = await checkProjectUrls();
  if (problems.length) {
    console.error(`Stale project addresses (expected ${origin}${base}/ and ${repoUrl}; change website/scripts/content-contract.mjs only if the address really moved):`);
    for (const problem of problems) console.error(`  ${problem}`);
    process.exit(1);
  }
  console.log('Project addresses match content-contract.mjs.');
}
