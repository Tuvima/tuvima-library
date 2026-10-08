import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { load } from 'cheerio';
import { base, origin } from './content-contract.mjs';

const website = fileURLToPath(new URL('../', import.meta.url));
export async function validateSite(dist, records) {
  const errors = [];
  const parsed = new Map();
  async function getPage(file) {
    if (!parsed.has(file)) parsed.set(file, load(await fs.readFile(file, 'utf8')));
    return parsed.get(file);
  }
  async function resolveLocal(raw, from, fragment = true) {
    if (!raw || /^(?:mailto:|tel:|data:|javascript:)/i.test(raw)) return;
    const url = new URL(raw, `${origin}${from}`);
    if (url.origin !== origin) return;
    if (!url.pathname.startsWith(`${base}/`)) { errors.push(`${from}: wrong base path ${raw}`); return; }
    let relative = decodeURIComponent(url.pathname.slice(base.length + 1));
    if (!relative || relative.endsWith('/')) relative += 'index.html';
    let disk = path.resolve(dist, relative);
    if (!disk.startsWith(`${path.resolve(dist)}${path.sep}`)) { errors.push(`${from}: path escapes output ${raw}`); return; }
    try {
      const stat = await fs.stat(disk);
      if (stat.isDirectory()) disk = path.join(disk, 'index.html');
      await fs.access(disk);
    } catch { errors.push(`${from}: missing local target ${raw}`); return; }
    if (fragment && url.hash && disk.endsWith('.html') && !url.hash.startsWith('#:~:text=')) {
      const $ = await getPage(disk);
      const id = decodeURIComponent(url.hash.slice(1));
      if (!$('[id], a[name]').toArray().some(e => $(e).attr('id') === id || $(e).attr('name') === id)) errors.push(`${from}: missing anchor ${raw}`);
    }
  }
  async function walk(dir) {
    for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
      const file = path.join(dir, entry.name);
      if (entry.isDirectory()) { await walk(file); continue; }
      const route = `${base}/${path.relative(dist, file).replaceAll('\\', '/').replace(/index\.html$/, '')}`;
      if (entry.name.endsWith('.html')) {
        const $ = await getPage(file);
        for (const element of $('[href], [src], [srcset]').toArray()) {
          for (const attribute of ['href', 'src']) {
            const value = $(element).attr(attribute);
            if (value) {
              const resource = attribute === 'src' || (element.tagName === 'link' && /stylesheet|preload|prefetch|modulepreload/.test($(element).attr('rel') ?? ''));
              if (resource && /^(?:https?:)?\/\//.test(value) && new URL(value, origin).origin !== origin) errors.push(`${route}: third-party resource ${value}`);
              await resolveLocal(value, route);
            }
          }
          for (const value of ($(element).attr('srcset') ?? '').split(',').map(s => s.trim().split(/\s+/)[0]).filter(Boolean)) await resolveLocal(value, route, false);
        }
        const refresh = $('meta[http-equiv="refresh"]').attr('content');
        if (refresh) await resolveLocal(refresh.replace(/^.*url=/i, ''), route);
      } else if (entry.name.endsWith('.css')) {
        const css = await fs.readFile(file, 'utf8');
        for (const match of css.matchAll(/url\(\s*['"]?([^'"\s)]+)['"]?\s*\)/g)) {
          if (/^(?:https?:)?\/\//.test(match[1]) && new URL(match[1], origin).origin !== origin) errors.push(`${route}: third-party CSS resource ${match[1]}`);
          await resolveLocal(match[1], route, false);
        }
      }
    }
  }
  await walk(dist);
  for (const page of records.pages) {
    const disk = path.join(dist, page.slug === '404' ? '404.html' : page.slug, page.slug === '404' ? '' : 'index.html');
    try {
      const $ = await getPage(disk);
      if (!$('title').text().includes(page.title)) errors.push(`${page.route}: page identity does not match ${page.title}`);
      const canonical = $('link[rel="canonical"]').attr('href');
      if (page.slug !== '404' && canonical !== `${origin}${page.route}`) errors.push(`${page.route}: incorrect canonical ${canonical}`);
      if (page.slug !== '404' && !$(`a[href="https://github.com/Tuvima/tuvima_library/edit/main/docs/${page.source}"]`).length) errors.push(`${page.route}: original-source edit link is missing`);
    } catch (error) { errors.push(`${page.route}: page was not emitted (${error.message})`); }
  }
  for (const [from, to] of Object.entries(records.redirects ?? {})) {
    try {
      const $ = await getPage(path.join(dist, from.slice(1), 'index.html'));
      const canonical = $('link[rel="canonical"]').attr('href');
      if (canonical !== `${origin}${base}${to}`) errors.push(`${from}: wrong redirect canonical ${canonical}`);
      const refresh = $('meta[http-equiv="refresh"]').attr('content');
      if (!refresh?.endsWith(`${base}${to}`)) errors.push(`${from}: wrong redirect refresh ${refresh}`);
      await resolveLocal(`${base}${to}`, `${base}${from}`);
    } catch (error) { errors.push(`${from}: redirect missing (${error.message})`); }
  }
  if (errors.length) throw new Error(`Site validation failed (${errors.length}):\n${[...new Set(errors)].join('\n')}`);
  return { pages: records.pages.length, redirects: Object.keys(records.redirects ?? {}).length };
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const records = JSON.parse(await fs.readFile(path.join(website, '.generated/routes.json'), 'utf8'));
  console.log('Validated output:', await validateSite(path.join(website, 'dist'), records));
}
