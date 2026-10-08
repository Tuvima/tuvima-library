import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';
import YAML from 'yaml';
import { validateManifest, validateMetadata, repoUrl, base, normalizeSource, pageRoute } from './content-contract.mjs';
import { generateNotices } from './generate-notices.mjs';

export const websiteRoot = fileURLToPath(new URL('../', import.meta.url));
export const repoRoot = path.resolve(websiteRoot, '..');
const docsRoot = path.join(repoRoot, 'docs');
const generatedRoot = path.join(websiteRoot, '.generated');
const manifest = JSON.parse(await fs.readFile(path.join(websiteRoot, 'publication.json'), 'utf8'));
validateManifest(manifest);
if (path.dirname(generatedRoot) !== path.resolve(websiteRoot) || path.basename(generatedRoot) !== '.generated') throw new Error('Unsafe generated output path');
await fs.rm(generatedRoot, { recursive: true, force: true });
await fs.mkdir(path.join(generatedRoot, 'content'), { recursive: true });
await fs.cp(path.join(websiteRoot, 'public'), path.join(generatedRoot, 'public'), { recursive: true });
await fs.mkdir(path.join(generatedRoot, 'public/licenses'), { recursive: true });
await generateNotices(websiteRoot, path.join(generatedRoot, 'public/licenses'));
for (const fontLicense of ['Montserrat-OFL.txt', 'JetBrainsMono-OFL.txt']) await fs.copyFile(path.join(websiteRoot, 'src/fonts', fontLicense), path.join(generatedRoot, 'public/licenses', fontLicense));
const sourceRoutes = Object.fromEntries(manifest.pages.map(p => [p.source, pageRoute(p.slug)]));
const records = [];
for (const page of manifest.pages) {
  const source = normalizeSource(page.source);
  const sourcePath = path.join(docsRoot, source);
  const text = await fs.readFile(sourcePath, 'utf8');
  const match = text.match(/^\uFEFF?---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/);
  if (!match) throw new Error(`${source}: YAML front matter is required`);
  const metadata = YAML.parse(match[1]);
  // This machine-consumed source is immutable; this is the only metadata exception.
  if (source === 'reference/wikidata-property-map.md') {
    metadata.description = metadata.summary;
    metadata.status = 'current';
    delete metadata.summary;
  }
  validateMetadata(metadata, source);
  const destination = path.join(generatedRoot, 'content', source);
  await fs.mkdir(path.dirname(destination), { recursive: true });
  metadata.slug = page.slug;
  metadata.editUrl = `${repoUrl}/edit/main/docs/${source}`;
  try {
    const gitDate = execFileSync('git', ['log', '-1', '--format=%cI', '--', `docs/${source}`], { cwd: repoRoot, encoding: 'utf8' }).trim();
    metadata.lastUpdated = gitDate || false;
  } catch { metadata.lastUpdated = false; }
  function rewriteMetadata(value) {
    if (Array.isArray(value)) return value.map(rewriteMetadata);
    if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, entry]) => [key, ['link', 'href', 'src'].includes(key) && typeof entry === 'string' && entry.startsWith('/') && !entry.startsWith('//') && !entry.startsWith(`${base}/`) ? `${base}${entry}` : rewriteMetadata(entry)]));
    return value;
  }
  // Generated policy copies may put provenance comments before their title.
  // Starlight owns the visible H1; retain comments but remove that authored title.
  const body = text.slice(match[0].length).replace(/^(\s*(?:<!--[\s\S]*?-->\s*)*)# [^\n]+\r?\n/, '$1');
  await fs.writeFile(destination, `---\n${YAML.stringify(rewriteMetadata(metadata))}---\n${body}`);
  records.push({ ...page, route: pageRoute(page.slug), title: metadata.title, description: metadata.description, status: metadata.status, audience: metadata.audience, category: metadata.category, product_area: metadata.product_area, tags: metadata.tags ?? [], lastUpdated: metadata.lastUpdated });
}
await fs.writeFile(path.join(generatedRoot, 'routes.json'), JSON.stringify({ base, pages: records, sourceRoutes, redirects: manifest.redirects ?? {} }, null, 2));
// Astro may reuse cached Markdown rendering; prepare referenced assets independently
// so repeated builds never depend on a remark transform running again.
const { default: rewriteLinks } = await import('./rewrite-links.mjs');
const { createMarkdownProcessor } = await import('@astrojs/markdown-remark');
const processor = await createMarkdownProcessor({ remarkPlugins: [rewriteLinks], syntaxHighlight: false });
for (const page of records) {
  const stagedPath = path.join(generatedRoot, 'content', page.source);
  const text = await fs.readFile(stagedPath, 'utf8');
  await processor.render(text.replace(/^---\n[\s\S]*?\n---\n/, ''), { fileURL: pathToFileURL(stagedPath) });
}
// Keep the machine-consumed catalog available at its historical public URL.
await fs.mkdir(path.join(generatedRoot, 'public', 'reference'), { recursive: true });
await fs.copyFile(path.join(docsRoot, 'reference/approved-plugins.json'), path.join(generatedRoot, 'public/reference/approved-plugins.json'));
console.log(`Prepared ${records.length} canonical documentation pages.`);
