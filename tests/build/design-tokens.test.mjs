import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { expected, loadTokens } from '../../tools/Generate-Tokens.mjs';

test('generated token files match design/tokens/tuvima.tokens.json (run node tools/Generate-Tokens.mjs)', () => {
    for (const [path, text] of expected()) {
        assert.equal(readFileSync(path, 'utf8'), text, `${path} is out of date; run node tools/Generate-Tokens.mjs`);
    }
});

test('token names and css names are unique', () => {
    const tokens = loadTokens();
    assert.equal(new Set(tokens.map(t => t.name)).size, tokens.length);
    assert.equal(new Set(tokens.map(t => t.css)).size, tokens.length);
});

test('device sizes and touch area follow Look v3.2', () => {
    const byName = Object.fromEntries(loadTokens().map(t => [t.name, t.value]));
    assert.equal(byName.deviceMediumMin, 600);
    assert.equal(byName.deviceExpandedMin, 840);
    assert.equal(byName.touchArea, 48);
    assert.equal(byName.controlHeight, 44);
    assert.equal(byName.controlRadius, 10);
});

test('generated CSS block is the only definition of generated tokens in :root', () => {
    const css = readFileSync(new URL('../../src/MediaEngine.Web/wwwroot/tuvima.tokens.css', import.meta.url), 'utf8');
    for (const t of loadTokens()) {
        const count = css.split('\n').filter(l => l.startsWith(`    ${t.css}:`)).length;
        assert.equal(count, 1, `${t.css} must be defined once at root level`);
    }
});
