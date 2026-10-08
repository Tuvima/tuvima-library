// Rewrites relative links to other pages (`../guides/x.md#anchor`) into site URLs,
// so pages stay clickable on GitHub and still work on the published site.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { isPublished } from './published.mjs';

const repoBlob = 'https://github.com/Tuvima/tuvima_library/blob/main';
const docsRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../docs');

export default function rehypeDocsLinks({ base = '' } = {}) {
  return (tree, file) => {
    const from = file?.path ? path.dirname(file.path) : null;
    if (!from) return;
    const walk = (node) => {
      if (node.type === 'element' && node.tagName === 'a' && typeof node.properties?.href === 'string') {
        const href = node.properties.href;
        const m = href.match(/^([^#?]+\.mdx?)([?#].*)?$/);
        if (m && !/^[a-z][a-z0-9+.-]*:/i.test(href) && !href.startsWith('/')) {
          const target = path.resolve(from, m[1]);
          const rel = path.relative(docsRoot, target).split(path.sep).join('/');
          if (!fs.existsSync(target)) {
            throw new Error(`Broken page link "${href}" in ${path.relative(docsRoot, file.path)}`);
          }
          if (!rel.startsWith('..') && !isPublished(rel)) {
            // Working documents are not on the site; point at the repository copy instead.
            node.properties.href = `${repoBlob}/docs/${rel}${m[2] ?? ''}`;
          } else if (!rel.startsWith('..')) {
            let slug = rel.replace(/\.mdx?$/, '');
            if (slug === 'index') slug = '';
            node.properties.href = `${base}/${slug ? slug + '/' : ''}${m[2] ?? ''}`;
          }
        }
      }
      node.children?.forEach(walk);
    };
    walk(tree);
  };
}
