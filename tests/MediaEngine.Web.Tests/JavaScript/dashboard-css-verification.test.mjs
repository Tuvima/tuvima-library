import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import zlib from 'node:zlib';
import { verifyBundleRatchet, verifyPublishedCss } from '../../../scripts/build/verify-dashboard-css.mjs';

const assets = [{ name: 'MediaEngine.Web.styles.css', raw: 100 }];
test('unfinished baseline permits measurement without inventing a final ceiling', () => {
    assert.deepEqual(verifyBundleRatchet({ finalized: false }, assets), { enforced: false, raw: 100 });
});
test('final bundle ceiling permits exact boundary and rejects any increase', () => {
    assert.deepEqual(verifyBundleRatchet({ finalized: true, finalBundleCeilingBytes: 100 }, assets), { enforced: true, raw: 100, ceiling: 100 });
    assert.throws(() => verifyBundleRatchet({ finalized: true, finalBundleCeilingBytes: 99 }, assets), /grew to 100 bytes/);
});
test('finalization cannot silently bypass a missing or invalid bundle ceiling', () => {
    for (const ceiling of [undefined, null, '100', 0, -1, 100.5])
        assert.throws(() => verifyBundleRatchet({ finalized: true, finalBundleCeilingBytes: ceiling }, assets), /positive integer/);
});
test('published asset verification reads the fixture and rejects a bundle above its ceiling before writing a success report', async () => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'tuvima-css-verification-'));
    try {
        const publish = path.join(root, 'publish'), wwwroot = path.join(publish, 'wwwroot');
        const baselinePath = path.join(root, 'baseline.json'), output = path.join(root, 'report.json');
        await fs.mkdir(wwwroot, { recursive: true });
        const names = ['MediaEngine.Web.styles.css', 'app.css', 'tuvima.tokens.css', 'native-fields.css', 'native-structure.css', 'native-utilities.css'];
        const bytes = Buffer.from('.a{color:red}');
        const endpoints = [];
        for (const name of names) {
            await fs.writeFile(path.join(wwwroot, name), bytes);
            await fs.writeFile(path.join(wwwroot, name + '.gz'), zlib.gzipSync(bytes));
            await fs.writeFile(path.join(wwwroot, name + '.br'), zlib.brotliCompressSync(bytes));
            endpoints.push({ AssetFile: name, Route: name.replace('.css', '.fingerprint.css'), Selectors: [],
                EndpointProperties: [{ Name: 'integrity', Value: 'sha256-' + crypto.createHash('sha256').update(bytes).digest('base64') }] });
        }
        await fs.writeFile(path.join(publish, 'MediaEngine.Web.staticwebassets.endpoints.json'), JSON.stringify({ Endpoints: endpoints }));
        await fs.writeFile(path.join(publish, 'MediaEngine.Web.deps.json'), '{}');
        await fs.writeFile(baselinePath, JSON.stringify({ finalized: true, finalBundleCeilingBytes: bytes.length - 1 }));
        await assert.rejects(verifyPublishedCss({ publish, baselinePath, output }), /above its finalized/);
        await assert.rejects(fs.stat(output), { code: 'ENOENT' });
        await fs.writeFile(baselinePath, JSON.stringify({ finalized: true, finalBundleCeilingBytes: bytes.length }));
        const report = await verifyPublishedCss({ publish, baselinePath, output });
        assert.equal(report.scopedBundleRatchet.enforced, true);
        assert.equal(JSON.parse(await fs.readFile(output, 'utf8')).scopedBundleRatchet.ceiling, bytes.length);
    } finally { await fs.rm(root, { recursive: true, force: true }); }
});
