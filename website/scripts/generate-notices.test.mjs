import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { generateNotices } from './generate-notices.mjs';

test('aggregate preserves complete browser dependency notices and actual font copyright', async () => {
  const website = fileURLToPath(new URL('../', import.meta.url));
  const output = await fs.mkdtemp(path.join(os.tmpdir(), 'tuvima-notices-'));
  try {
    const coverage = await generateNotices(website, output);
    assert.equal(coverage.packages, 26);
    assert.equal(coverage.fonts, 2);
    const notice = await fs.readFile(path.join(output, 'THIRD-PARTY.txt'), 'utf8');
    for (const source of ['node_modules/@astrojs/starlight/LICENSE', 'node_modules/starlight-sidebar-topics/LICENSE', 'node_modules/pagefind/LICENSE/LICENSE', 'node_modules/pagefind/LICENSE/LICENSE-vscode-ripgrep', 'node_modules/expressive-code/LICENSE', 'node_modules/shiki/LICENSE', 'scripts/licenses/Svelte-MIT.txt', 'src/fonts/Montserrat-OFL.txt', 'src/fonts/JetBrainsMono-OFL.txt']) {
      assert.ok(notice.includes(await fs.readFile(path.join(website, source), 'utf8')), `${source}: notice must be preserved verbatim`);
    }
    assert.match(notice, /Copyright 2011 The Montserrat Project Authors/);
    assert.match(notice, /Copyright 2020 The JetBrains Mono Project Authors/);
    await generateNotices(website, output);
    assert.equal(await fs.readFile(path.join(output, 'THIRD-PARTY.txt'), 'utf8'), notice, 'generation is deterministic');
  } finally {
    if (path.dirname(output) !== os.tmpdir() || !path.basename(output).startsWith('tuvima-notices-')) throw new Error('Unsafe notice fixture cleanup path');
    await fs.rm(output, { recursive: true, force: true });
  }
});
