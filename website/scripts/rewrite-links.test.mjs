import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import rewriteLinks from './rewrite-links.mjs';

test('source-relative pages, query fragments, nested assets, HTML and MDX keep the deployment base', async () => {
  const sandbox = await fs.mkdtemp(path.join(os.tmpdir(), 'tuvima-docs-links-'));
  const root = path.join(sandbox, 'website');
  try {
    await fs.mkdir(path.join(root, '.generated/content/guides'), { recursive: true });
    await fs.mkdir(path.join(sandbox, 'docs/assets/nested'), { recursive: true });
    await fs.writeFile(path.join(sandbox, 'docs/assets/nested/diagram.svg'), '<svg/>');
    await fs.writeFile(path.join(root, '.generated/routes.json'), JSON.stringify({ sourceRoutes: { 'reference/test.md': '/tuvima_library/reference/test/' } }));
    const tree = { type: 'root', children: [
      { type: 'link', url: '../reference/test.md?mode=all#heading' },
      { type: 'definition', url: '/reference/test.md#heading' },
      { type: 'image', url: '../assets/nested/diagram.svg' },
      { type: 'mdxJsxFlowElement', attributes: [{ name: 'href', value: '../reference/test.md#heading' }] },
      { type: 'html', value: '<a href="../reference/test.md#heading">Reference</a>' },
      { type: 'code', value: '[example](missing.md)' },
    ] };
    const transform = rewriteLinks({ root });
    const file = { path: path.join(root, '.generated/content/guides/test.mdx') };
    transform(tree, file);
    assert.equal(tree.children[0].url, '/tuvima_library/reference/test/?mode=all#heading');
    assert.equal(tree.children[1].url, '/tuvima_library/reference/test/#heading');
    assert.equal(tree.children[2].url, '/tuvima_library/repo-assets/docs/assets/nested/diagram.svg');
    assert.equal(tree.children[3].attributes[0].value, '/tuvima_library/reference/test/#heading');
    assert.match(tree.children[4].value, /href="\/tuvima_library\/reference\/test\/#heading"/);
    assert.equal(tree.children[5].value, '[example](missing.md)');
    assert.equal(await fs.readFile(path.join(root, '.generated/public/repo-assets/docs/assets/nested/diagram.svg'), 'utf8'), '<svg/>');
    assert.throws(() => transform({ type: 'link', url: 'missing.md' }, file), /local target does not exist/);
  } finally {
    if (path.dirname(sandbox) !== os.tmpdir() || !path.basename(sandbox).startsWith('tuvima-docs-links-')) throw new Error('Unsafe fixture cleanup path');
    await fs.rm(sandbox, { recursive: true, force: true });
  }
});
