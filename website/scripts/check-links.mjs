// Fails the build when a page links to an internal page, file, or section that does not exist.
// (starlight-links-validator cannot see content that lives outside src/content/docs.)
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const dist = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../dist');
const base = '/tuvima_library/';
const htmlFiles = [];
(function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (e.name.endsWith('.html')) htmlFiles.push(p);
  }
})(dist);

const idCache = new Map();
const idsOf = (file) => {
  if (!idCache.has(file)) {
    const html = fs.readFileSync(file, 'utf8');
    idCache.set(file, new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1])));
  }
  return idCache.get(file);
};

const resolveTarget = (urlPath) => {
  const rel = decodeURIComponent(urlPath.slice(base.length));
  const candidates = [path.join(dist, rel), path.join(dist, rel, 'index.html')];
  return candidates.find((c) => fs.existsSync(c) && fs.statSync(c).isFile());
};

const problems = [];
for (const file of htmlFiles) {
  const html = fs.readFileSync(file, 'utf8');
  const pageUrl = base + path.relative(dist, file).split(path.sep).join('/').replace(/index\.html$/, '');
  for (const m of html.matchAll(/\s(?:href|src)="([^"]+)"/g)) {
    let href = m[1];
    if (/^([a-z][a-z0-9+.-]*:|\/\/)/i.test(href) || href.startsWith('data:')) continue;
    if (href.startsWith('#')) href = pageUrl + href;
    else if (!href.startsWith('/')) href = new URL(href, 'http://x' + pageUrl).pathname + new URL(href, 'http://x' + pageUrl).hash;
    const [p, hash] = href.split('#');
    if (!p.startsWith(base)) {
      problems.push(`${pageUrl}: link outside site base: ${m[1]}`);
      continue;
    }
    const target = resolveTarget(p.split('?')[0]);
    if (!target) {
      problems.push(`${pageUrl}: broken link ${m[1]}`);
    } else if (hash && target.endsWith('.html') && !idsOf(target).has(decodeURIComponent(hash))) {
      problems.push(`${pageUrl}: missing section ${m[1]}`);
    }
  }
}

if (problems.length) {
  const unique = [...new Set(problems)];
  console.error(`\nLink check failed (${unique.length}):\n` + unique.map((p) => '  ' + p).join('\n'));
  process.exit(1);
}
console.log(`Link check passed: ${htmlFiles.length} pages.`);
