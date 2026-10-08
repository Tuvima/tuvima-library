import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { base, origin } from './content-contract.mjs';
const root = fileURLToPath(new URL('../', import.meta.url));
const { redirects } = JSON.parse(await fs.readFile(path.join(root, '.generated/routes.json'), 'utf8'));
for (const [from, to] of Object.entries(redirects ?? {})) {
  const file = path.join(root, 'dist', from.slice(1), 'index.html');
  const html = await fs.readFile(file, 'utf8');
  const target = JSON.stringify(`${base}${to}`).replaceAll('<', '\\u003c');
  // Preserve a reader's heading bookmark unless the manifest deliberately replaces it.
  const script = `<script>location.replace(${target}${to.includes('#') ? '' : ' + location.hash'});</script>`;
  await fs.writeFile(file, html.includes('</head>') ? html.replace('</head>', `${script}</head>`) : `${html}${script}`);
}
// GitHub Pages serves this file for arbitrary missing nested paths. Its canonical
// must identify the emitted file rather than Astro's internal /404/ route.
const notFound = path.join(root, 'dist', '404.html');
const notFoundHtml = await fs.readFile(notFound, 'utf8');
await fs.writeFile(notFound, notFoundHtml.replace(/(<link\s+rel="canonical"\s+href=")[^"]+("[^>]*>)/, `$1${origin}${base}/404.html$2`));
