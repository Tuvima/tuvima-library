import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { base, repoUrl } from './content-contract.mjs';

const defaultRoot = fileURLToPath(new URL('../', import.meta.url));
export default function rewriteLinks(options = {}) {
  const root = options.root ?? defaultRoot;
  return (tree, file) => {
    const routes = JSON.parse(fs.readFileSync(path.join(root, '.generated/routes.json'), 'utf8'));
    const source = path.relative(path.join(root, '.generated/content'), file.path).replaceAll('\\', '/');
    function rewrite(url, isImage = false) {
      if (!url || /^(?:[a-z][a-z\d+.-]*:|#|\/\/)/i.test(url)) return url;
      if (url.startsWith(`${base}/`)) return url;
      const suffixStart = url.search(/[?#]/);
      const localPath = suffixStart < 0 ? url : url.slice(0, suffixStart);
      const suffix = suffixStart < 0 ? '' : url.slice(suffixStart);
      const target = path.posix.normalize(localPath.startsWith('/') ? decodeURI(localPath.slice(1)) : path.posix.join(path.posix.dirname(source), decodeURI(localPath)));
      const document = routes.sourceRoutes[target];
      if (document) return `${document}${suffix}`;
      if (url.startsWith('/')) return `${base}${url}`;
      const repoPath = path.posix.normalize(`docs/${target}`);
      if (repoPath.startsWith('../')) throw new Error(`${source}: link escapes repository ${url}`);
      const disk = path.join(root, '..', repoPath);
      if (!fs.existsSync(disk)) throw new Error(`${source}: local target does not exist: ${url}`);
      if (isImage || /\.(?:svg|png|jpe?g|gif|webp|avif|json|pdf)$/i.test(target)) {
        if (/screenshots?\//i.test(repoPath)) throw new Error(`${source}: screenshot references are retired: ${url}`);
        const publicPath = path.join(root, '.generated/public/repo-assets', repoPath);
        fs.mkdirSync(path.dirname(publicPath), { recursive: true }); fs.copyFileSync(disk, publicPath);
        return `${base}/repo-assets/${repoPath}${suffix}`;
      }
      return `${repoUrl}/blob/main/${repoPath}${suffix}`;
    }
    function visit(node) {
      if ((node.type === 'link' || node.type === 'definition' || node.type === 'image') && node.url) node.url = rewrite(node.url, node.type === 'image');
      if (node.type === 'mdxJsxFlowElement' || node.type === 'mdxJsxTextElement') {
        for (const attr of node.attributes ?? []) if (['href', 'src'].includes(attr.name) && typeof attr.value === 'string') attr.value = rewrite(attr.value, attr.name === 'src');
      }
      if (node.type === 'html') node.value = node.value.replace(/\b(href|src)=(['"])([^'"]+)\2/g, (_, name, quote, url) => `${name}=${quote}${rewrite(url, name === 'src')}${quote}`);
      for (const child of node.children ?? []) visit(child);
    }
    visit(tree);
  };
}
