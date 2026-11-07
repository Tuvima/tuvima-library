import test from 'node:test';
import assert from 'node:assert/strict';

// Cut-off text wires the shared tooltip, which adds window resize and scroll listeners. Leaving the page must remove them again.
const added = [], removed = [];
class Element extends EventTarget {
    constructor() { super(); this.attrs = new Map(); this.dataset = {}; this.style = {}; this.scrollWidth = 240; this.clientWidth = 100; this.scrollHeight = 0; this.clientHeight = 0; this.isConnected = true; }
    getAttribute(key) { return this.attrs.get(key) ?? null; }
    setAttribute(key, value) { this.attrs.set(key, value); }
    removeAttribute(key) { this.attrs.delete(key); }
    querySelector() { return null; }
    closest(selector) { return selector === '[data-overflow-tooltip]' && this.attrs.has('data-overflow-tooltip') ? this : null; }
    getBoundingClientRect() { return { left: 0, top: 0, bottom: 0, width: 0, height: 0 }; }
}
globalThis.Element = Element;
globalThis.PointerEvent = class extends Event { constructor(type, init) { super(type); Object.assign(this, init); } };
globalThis.MutationObserver = class { observe() {} disconnect() {} };
globalThis.document = Object.assign(new EventTarget(), { body: {}, querySelectorAll: () => [] });
globalThis.window = new EventTarget();
const addWindow = window.addEventListener.bind(window), removeWindow = window.removeEventListener.bind(window);
window.addEventListener = (type, handler, ...rest) => { added.push(type); addWindow(type, handler, ...rest); };
window.removeEventListener = (type, handler, ...rest) => { removed.push(type); removeWindow(type, handler, ...rest); };
globalThis.innerWidth = 800; globalThis.innerHeight = 600;
globalThis.setTimeout = () => 0; globalThis.clearTimeout = () => {};
const { take, sweepDisconnected } = await import('../../../src/MediaEngine.Web/wwwroot/js/app-overflow-tooltip.js');

test('window listeners are removed once a wired element leaves the page', () => {
    const cell = new Element(); cell.setAttribute('data-overflow-tooltip', 'A long folder name');
    const event = new Event('pointerover'); Object.defineProperty(event, 'target', { value: cell });
    take(event);
    assert.deepEqual([...added].sort(), ['resize', 'scroll']);
    assert.equal(removed.length, 0);
    sweepDisconnected();
    assert.equal(removed.length, 0, 'a connected element keeps its listeners');
    cell.isConnected = false;
    sweepDisconnected();
    assert.deepEqual([...removed].sort(), ['resize', 'scroll']);
    assert.equal(cell.dataset.overflowTooltipWired, undefined);
});
