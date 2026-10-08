import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { load } from 'cheerio';

const root = fileURLToPath(new URL('../', import.meta.url));
const urls = new Set();
// This setup destination deliberately requires the reader to sign in. Accept
// only its documented authentication response, not other errors or host paths.
const authenticatedDestinations = new Map([
  ['https://www.themoviedb.org/settings/api', { status: 401, reason: 'TMDB API settings requires a signed-in account.' }],
]);
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
        if (url.origin === 'https://tuvima.github.io' && url.pathname.startsWith('/tuvima_library/')) continue;
        url.hash = ''; urls.add(url.href);
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
      result = { url, status: response.status, outcome: response.ok ? 'verified' : auth?.status === response.status ? 'authentication-required' : response.status === 429 ? 'rate-limited-unknown' : 'failed', ...(auth?.status === response.status ? { reason: auth.reason } : {}) };
      if (response.ok || (response.status < 500 && response.status !== 429)) break;
    } catch (error) { result = { url, outcome: 'unavailable', error: error.message }; }
    if (attempt < 2) await new Promise(resolve => setTimeout(resolve, 1000 * (attempt + 1)));
  }
  results.push(result);
}
const queue = [...urls];
await Promise.all(Array.from({ length: 4 }, async () => { while (queue.length) await check(queue.shift()); }));
results.sort((a, b) => a.url.localeCompare(b.url));
await fs.writeFile(path.join(root, 'external-links-report.json'), `${JSON.stringify(results, null, 2)}\n`);
const failures = results.filter(result => !['verified', 'authentication-required'].includes(result.outcome));
console.log(`External links: ${results.filter(r => r.outcome === 'verified').length} verified; ${results.filter(r => r.outcome === 'authentication-required').length} requires sign-in; ${failures.length} failed or unknown.`);
if (failures.length) { console.error(failures); process.exitCode = 1; }
