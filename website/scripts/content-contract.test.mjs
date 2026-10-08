import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { validateManifest, validateMetadata } from './content-contract.mjs';
import { validateSite } from './validate-site.mjs';

const metadata = { title: 'Fixture', description: 'A meaningful fixture description.', audience: 'user', category: 'guide', product_area: 'library', status: 'current' };
test('every metadata field is mandatory; statuses are explicit', () => {
  validateMetadata(metadata, 'fixture.md');
  for (const key of Object.keys(metadata)) {
    const data = { ...metadata }; delete data[key];
    assert.throws(() => validateMetadata(data, 'fixture.md'), new RegExp(key));
  }
});
test('duplicate publication routes fail', () => {
  assert.throws(() => validateManifest({ pages: [{ source: 'one.md', slug: '', group: 'Guides', section: 'Start' }, { source: 'two.md', slug: '', group: 'Guides', section: 'Start' }] }), /Duplicate route/);
});
test('finished HTML rejects missing pages, anchors, assets and wrong base paths', async () => {
  const dist = await fs.mkdtemp(path.join(os.tmpdir(), 'tuvima-docs-validation-'));
  try {
    for (const [markup, expected] of [
      ['<a href="/tuvima_library/missing/">Bad page</a>', /missing local target/],
      ['<a href="#missing">Bad anchor</a>', /missing anchor/],
      ['<img src="/tuvima_library/missing.svg">', /missing local target/],
      ['<a href="/guide/">Bad base</a>', /wrong base path/],
    ]) {
      await fs.writeFile(path.join(dist, 'index.html'), markup);
      await assert.rejects(validateSite(dist, { pages: [] }), expected);
    }
    await fs.writeFile(path.join(dist, 'index.html'), '<h2 id="valid">Valid</h2><a href="#valid">Go</a>');
    await validateSite(dist, { pages: [] });
  } finally {
    if (path.dirname(dist) !== os.tmpdir() || !path.basename(dist).startsWith('tuvima-docs-validation-')) throw new Error('Unsafe fixture cleanup path');
    await fs.rm(dist, { recursive: true, force: true });
  }
});
