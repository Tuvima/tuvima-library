import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { load } from 'cheerio';
import { existsSync } from 'node:fs';
import { base, origin, repoUrl } from './content-contract.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const repoRoot = path.resolve(root, '..');
const urls = new Set();
const repoLinks = new Set();
// This setup destination deliberately requires the reader to sign in. Accept
// only its documented authentication response, not other errors or host paths.
const authenticatedDestinations = new Map([
  ['https://www.themoviedb.org/settings/api', { status: 401, reason: 'TMDB API settings requires a signed-in account.' }],
]);
// These hosts refuse automated requests with a fixed status even though the page
// works in a browser. Accept only that status from that host; anything else still fails.
const botBlockedHosts = new Map([
  ['subdl.com', { status: 403, reason: 'SubDL rejects automated requests; the developer page opens normally in a browser.' }],
]);
// Links into this repository (edit links, repository files, issue and advisory pages)
// are checked against the checkout instead of over the network: every page has
// one, and GitHub rate-limits a burst of them (HTTP 429), which made the audit fail.
function checkRepoLink(href) {
  const rest = href.slice(repoUrl.length).replace(/^\//, '');
  const file = rest.match(/^(?:blob|tree|edit|raw)\/[^/]+\/(.+)$/);
  if (!file) return { url: href, outcome: 'repository-page' };
  const relative = decodeURIComponent(file[1]);
  return existsSync(path.join(repoRoot, relative))
    ? { url: href, outcome: 'verified-locally' }
    : { url: href, outcome: 'failed', reason: `Repository file ${relative} does not exist in this checkout.` };
}
async function collect(dir) {
  for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
    const file = path.join(dir, entry.name);
    if (entry.isDirectory()) await collect(file);
    else if (entry.name.endsWith('.html')) {
      const $ = load(await fs.readFile(file, 'utf8'));
      for (const element of $('a[href]').toArray()) {
        const raw = $(element).attr('href');
        if (!/^https?:\/\//.test(raw)) continue;
        const url = new URL(raw);
        // Reserved instructional hosts are examples, never availability claims.
        if (['localhost', '127.0.0.1', '[::1]', 'example.com', 'example.org', 'example.net'].includes(url.hostname) || url.hostname.endsWith('.example')) continue;
        if (url.origin === origin && url.pathname.startsWith(`${base}/`)) continue;
        url.hash = '';
        if (url.href === repoUrl || url.href.startsWith(`${repoUrl}/`)) { repoLinks.add(url.href.split('?')[0]); continue; }
        urls.add(url.href);
      }
    }
  }
}
await collect(path.join(root, 'dist'));
const results = [];
async function check(url) {
  let result;
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(15000), redirect: 'follow', headers: { 'user-agent': 'Tuvima-Documentation-Link-Check/1.0' } });
      await response.body?.cancel();
      const auth = authenticatedDestinations.get(url);
      const blocked = botBlockedHosts.get(new URL(url).hostname.replace(/^www./, ''));
      const expected = auth?.status === response.status ? { outcome: 'authentication-required', reason: auth.reason }
        : blocked?.status === response.status ? { outcome: 'bot-blocked', reason: blocked.reason } : null;
      result = { url, status: response.status, outcome: response.ok ? 'verified' : expected ? expected.outcome : response.status === 429 ? 'rate-limited-unknown' : 'failed', ...(expected ? { reason: expected.reason } : {}) };
      if (response.ok || (response.status < 500 && response.status !== 429)) break;
    } catch (error) { result = { url, outcome: 'unavailable', error: error.message }; }
    if (attempt < 2) await new Promise(resolve => setTimeout(resolve, 1000 * (attempt + 1)));
  }
  results.push(result);
}
const queue = [...urls];
await Promise.all(Array.from({ length: 4 }, async () => { while (queue.length) await check(queue.shift()); }));
for (const href of repoLinks) results.push(checkRepoLink(href));
results.sort((a, b) => a.url.localeCompare(b.url));
await fs.writeFile(path.join(root, 'external-links-report.json'), `${JSON.stringify(results, null, 2)}\n`);
const accepted = ['verified', 'verified-locally', 'repository-page', 'authentication-required', 'bot-blocked'];
const failures = results.filter(result => !accepted.includes(result.outcome));
const count = outcome => results.filter(r => r.outcome === outcome).length;
console.log(`External links: ${count('verified')} verified; ${count('verified-locally') + count('repository-page')} repository links checked locally; ${count('authentication-required')} requires sign-in; ${count('bot-blocked')} blocks automated checks; ${failures.length} failed or unknown.`);
if (failures.length) { console.error(failures); process.exitCode = 1; }
