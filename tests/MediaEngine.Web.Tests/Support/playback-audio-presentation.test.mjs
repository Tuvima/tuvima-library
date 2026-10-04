import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import vm from 'node:vm';
test('audio composition observes its 720px boundary independently and removes its listener', async () => {
    const changes = [], listeners = new Set();
    const query = { addEventListener: (name, handler) => listeners.add(handler), removeEventListener: (name, handler) => listeners.delete(handler) };
    const window = { innerWidth: 835, matchMedia: value => { assert.equal(value, '(max-width:720px)'); return query; } };
    const source = await fs.readFile(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-audio-presentation.js', import.meta.url), 'utf8');
    const context = { window, Map }; vm.runInNewContext(source.replaceAll('export function ', 'function ') + '\nglobalThis.api = { attach, detach };', context);
    const owner = { invokeMethodAsync: (name, width) => { changes.push([name, width]); return Promise.resolve(); } };
    context.api.attach('player', owner); context.api.attach('player', owner);
    assert.deepEqual(changes, [['SetAudioPresentationViewport', 835]]);
    assert.equal(listeners.size, 1);
    window.innerWidth = 720; for (const handler of listeners) handler();
    window.innerWidth = 840; for (const handler of listeners) handler();
    assert.deepEqual(changes.map(([, width]) => width), [835, 720, 840]);
    context.api.detach('player'); assert.equal(listeners.size, 0);
});
