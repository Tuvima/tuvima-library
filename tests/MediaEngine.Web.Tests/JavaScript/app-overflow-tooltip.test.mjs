import test from 'node:test';
import assert from 'node:assert/strict';
let tips = [], timers = [];
class Element extends EventTarget {
    constructor() { super(); this.attrs = new Map(); this.dataset = {}; this.style = {}; this.scrollWidth = 0; this.clientWidth = 0; this.scrollHeight = 0; this.clientHeight = 0; }
    getAttribute(key) { return this.attrs.get(key) ?? null; }
    setAttribute(key, value) { this.attrs.set(key, value); }
    removeAttribute(key) { this.attrs.delete(key); }
    querySelector() { return null; }
    closest(selector) { return selector === '[data-overflow-tooltip]' && this.attrs.has('data-overflow-tooltip') ? this : null; }
    matches() { return true; }
    getBoundingClientRect() { return { left: 100, top: 100, bottom: 120, width: 60, height: 20 }; }
    remove() { tips = tips.filter(tip => tip !== this); }
}
globalThis.Element = Element;
globalThis.PointerEvent = class extends Event { constructor(type, init) { super(type); Object.assign(this, init); } };
globalThis.MutationObserver = class { constructor() {} observe() {} disconnect() {} };
globalThis.document = Object.assign(new EventTarget(), { body: { append: tip => tips.push(tip) }, querySelectorAll: () => [], createElement: () => new Element() });
globalThis.window = new EventTarget(); globalThis.innerWidth = 800; globalThis.innerHeight = 600;
globalThis.setTimeout = fn => { const timer = { fn }; timers.push(timer); return timer; };
globalThis.clearTimeout = timer => { timers = timers.filter(item => item !== timer); };
const { take } = await import('../../../src/MediaEngine.Web/wwwroot/js/app-overflow-tooltip.js');
const over = target => { const event = new Event('pointerover'); Object.defineProperty(event, 'target', { value: target }); take(event); };
const flush = () => { const due = timers; timers = []; due.forEach(timer => timer.fn()); };

test('text that fits never gets a tooltip', () => {
    const cell = new Element(); cell.setAttribute('data-overflow-tooltip', 'A long folder name'); cell.scrollWidth = 100; cell.clientWidth = 100;
    over(cell); flush();
    assert.equal(cell.dataset.overflowTooltipWired, undefined); assert.equal(tips.length, 0);
});

test('text that is cut off shows its full text in the shared tooltip', () => {
    const cell = new Element(); cell.setAttribute('data-overflow-tooltip', 'A long folder name'); cell.scrollWidth = 240; cell.clientWidth = 100;
    over(cell); flush();
    assert.equal(cell.dataset.playbackTooltip, 'A long folder name'); assert.equal(tips.length, 1); assert.equal(tips[0].textContent, 'A long folder name');
});

test('a redraw that removes the clipping switches the tooltip off', () => {
    const cell = new Element(); cell.setAttribute('data-overflow-tooltip', 'Name'); cell.scrollWidth = 240; cell.clientWidth = 100;
    over(cell); cell.scrollWidth = 100; over(cell);
    assert.equal(cell.dataset.playbackTooltip, '');
});
