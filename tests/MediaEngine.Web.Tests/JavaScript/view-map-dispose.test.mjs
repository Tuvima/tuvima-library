import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// The Places map waits for its style choice and the lazily loaded stylesheet before it creates the map.
// If the part is disposed during that wait, no map may be created.
const source = fs.readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/js/view-map.js', import.meta.url), 'utf8')
    .replace(/^import .*;\r?\n/m, '').replace(/^export /gm, '');

function harness() {
    let release;
    const stylesheet = new Promise(resolve => { release = resolve; });
    const created = [];
    class FakeMap {
        constructor(options) { this.options = options; this.removed = false; created.push(this); }
        touchZoomRotate = { disableRotation() {} };
        addControl() {}
        on() {}
        once() {}
        remove() { this.removed = true; }
    }
    const calls = [];
    const dotnet = { invokeMethodAsync: (name) => { calls.push(name); } };
    const context = vm.createContext({
        maplibregl: { setWorkerUrl() {}, Map: FakeMap, AttributionControl: class {}, NavigationControl: class {} },
        window: { tuvimaLazy: { loadMapLibreStyles: () => stylesheet }, requestAnimationFrame() {}, clearTimeout() {}, setTimeout() { return 0; } },
        ResizeObserver: class { observe() {} disconnect() {} },
        console: { error() {}, debug() {} },
        container: {}, dotnet, Intl,
    });
    vm.runInContext(source + '\npreferredStyle = async () => ({ style: "style", attribution: "", detailed: true });', context);
    return { context, created, calls, release };
}

test('a map part disposed while its stylesheet loads never creates a map', async () => {
    const h = harness();
    const starting = vm.runInContext('initialize(container, dotnet, { mode: "location" })', h.context);
    await Promise.resolve();
    await vm.runInContext('dispose(container)', h.context);
    h.release();
    await starting;
    assert.equal(h.created.length, 0);
    assert.deepEqual(h.calls, []);
});

test('a map part that stays mounted creates its map once the stylesheet has loaded', async () => {
    const h = harness();
    const starting = vm.runInContext('initialize(container, dotnet, { mode: "location" })', h.context);
    await Promise.resolve();
    h.release();
    await starting;
    assert.equal(h.created.length, 1);
    assert.equal(h.created[0].removed, false);
});

test('mounting the same container again while the first wait is open leaves only the newer map', async () => {
    const h = harness();
    const first = vm.runInContext('initialize(container, dotnet, { mode: "location" })', h.context);
    await Promise.resolve();
    const second = vm.runInContext('initialize(container, dotnet, { mode: "location" })', h.context);
    await Promise.resolve();
    h.release();
    await Promise.all([first, second]);
    assert.equal(h.created.length, 1, 'the superseded start created nothing');
});
