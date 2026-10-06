import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

test('shelf wheel guard leaves vertical scrolling native and only protects active expansion horizontally', () => {
    const source = readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/app.js', import.meta.url), 'utf8');
    const start = source.indexOf('window.registerMediaTileShelfScrollGuard =');
    const end = source.indexOf('// -- Media tile hover positioning', start);
    const handlers = new Map(), classes = new Set();
    const row = { scrollLeft: 0, scrollTop: 120,
        classList: { contains: name => classes.has(name), add: name => classes.add(name), remove: name => classes.delete(name) },
        addEventListener: (name, handler) => handlers.set(name, handler), removeEventListener: name => handlers.delete(name),
        style: { removeProperty() {} }, closest: () => null };
    const window = { matchMedia: () => ({ matches: true }), requestAnimationFrame: callback => callback(),
        updateMediaTileShelfVisibleWidth() {}, updateMediaTileShelfStableHeight() {}, addEventListener() {}, removeEventListener() {},
        isVerticalMediaTileWheel: event => Math.abs(event.deltaY) >= Math.abs(event.deltaX) };
    vm.runInNewContext(source.slice(start, end), { window, Math });
    window.registerMediaTileShelfScrollGuard(row);
    const wheel = (x, y) => {
        const event = { deltaX: x, deltaY: y, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; } };
        handlers.get('wheel')(event); return event;
    };
    assert.equal(wheel(40, 0).prevented, undefined, 'resting shelves retain native horizontal input');
    classes.add('has-active-in-row-hover');
    assert.equal(wheel(0, 120).prevented, undefined, 'expansion does not consume vertical input');
    row.scrollTop = 240; handlers.get('scroll')();
    assert.equal(row.scrollTop, 240, 'scroll restoration never resets vertical position');
    assert.equal(wheel(40, 0).prevented, true, 'only active expansion protects its horizontal position');
    window.unregisterMediaTileShelfScrollGuard(row); assert.equal(handlers.size, 0);
});
