import test from 'node:test';
import assert from 'node:assert/strict';

// Look v3 D8 / WP4 (bug 27): a tooltip goes when its control leaves the page or is replaced, on any press anywhere,
// when a menu or dialog opens, and after 4 s of rest on a mouse. Keyboard focus keeps it until focus moves.
let timers = [], pageCallback = null, observing = false;
class Element extends EventTarget {
    constructor(tag = 'span') { super(); this.tag = tag; this.attrs = new Map(); this.dataset = {}; this.style = {}; this.children = []; this.parent = null; this.isConnected = true; }
    getAttribute(key) { return this.attrs.get(key) ?? null; }
    setAttribute(key, value) { this.attrs.set(key, value); }
    hasAttribute(key) { return this.attrs.has(key); }
    removeAttribute(key) { this.attrs.delete(key); }
    querySelector() { return this.candidate ?? null; }
    closest() { return null; }
    contains(node) { return node === this || node === this.candidate; }
    matches(selector) { return selector === ':focus-visible' ? true : selector.includes(this.role ?? '\u0000'); }
    getBoundingClientRect() { return { left: 210, top: 200, bottom: 244, width: 44, height: 44 }; }
    append(child) { child.parent = this; this.children.push(child); }
    removeChild(child) { this.children = this.children.filter(item => item !== child); }
    remove() { this.parent?.removeChild(this); this.parent = null; }
    showPopover() {}
}
const body = new Element('body');
const doc = new EventTarget();
Object.assign(doc, { body, documentElement: body, fullscreenElement: null, querySelectorAll: () => [], createElement: tag => new Element(tag) });
globalThis.document = doc;
globalThis.MutationObserver = class {
    constructor(callback) { this.callback = callback; }
    observe(target) { if (target === body) { pageCallback = this.callback; observing = true; } }
    disconnect() { if (this.callback === pageCallback) observing = false; }
};
globalThis.window = new EventTarget(); globalThis.innerWidth = 1024; globalThis.innerHeight = 768;
globalThis.setTimeout = (fn, ms = 0) => { const timer = { fn, ms }; timers.push(timer); return timer; };
globalThis.clearTimeout = timer => { timers = timers.filter(item => item !== timer); };
const { attach, detach } = await import('../../../src/MediaEngine.Web/wwwroot/js/playback-tooltip.js');

function event(type, properties = {}) { const value = new Event(type); Object.assign(value, properties); return value; }
function runDue(maxMs = 1000) { const due = timers.filter(timer => timer.ms <= maxMs); timers = timers.filter(timer => timer.ms > maxMs); due.forEach(timer => timer.fn()); }
function hoverControl(label = 'Play') {
    const root = new Element(); root.dataset.playbackTooltip = label; root.candidate = new Element('button'); attach(root);
    root.dispatchEvent(event('pointerenter', { pointerType: 'mouse' })); runDue();
    return root;
}

test('a tooltip goes when its control is removed from the page', () => {
    const root = hoverControl();
    assert.equal(body.children.length, 1);
    assert.equal(observing, true, 'the page is watched only while a tooltip shows');
    root.isConnected = false; pageCallback([{ type: 'childList', addedNodes: [] }]);
    assert.equal(body.children.length, 0);
    assert.equal(observing, false);
    detach(root);
});

test('a tooltip goes when its control is replaced by a redraw', () => {
    const root = hoverControl();
    root.candidate.isConnected = false; pageCallback([]);
    assert.equal(body.children.length, 0);
    detach(root);
});

test('a press anywhere on the page hides the tooltip', () => {
    const root = hoverControl();
    doc.dispatchEvent(event('pointerdown', { pointerType: 'mouse' }));
    assert.equal(body.children.length, 0);
    detach(root);
});

test('a menu or dialog opening hides the tooltip', () => {
    let root = hoverControl();
    const menu = new Element('div'); menu.role = 'menu';
    pageCallback([{ type: 'childList', addedNodes: [menu] }]);
    assert.equal(body.children.length, 0, 'a menu added to the page');
    detach(root);

    root = hoverControl();
    const other = new Element('button'); other.setAttribute('aria-expanded', 'true');
    pageCallback([{ type: 'attributes', attributeName: 'aria-expanded', target: other }]);
    assert.equal(body.children.length, 0, 'another control opened its menu');
    detach(root);

    root = hoverControl();
    const popover = new Element('div');
    const toggle = event('toggle', { newState: 'open' }); Object.defineProperty(toggle, 'target', { value: popover });
    doc.dispatchEvent(toggle);
    assert.equal(body.children.length, 0, 'a popover or dialog toggled open');
    detach(root);
});

test('the tooltip does not hide itself when its own popover opens', () => {
    const root = hoverControl();
    const tip = body.children[0];
    const toggle = event('toggle', { newState: 'open' }); Object.defineProperty(toggle, 'target', { value: tip });
    doc.dispatchEvent(toggle);
    pageCallback([{ type: 'childList', addedNodes: [tip] }]);
    assert.equal(body.children.length, 1);
    detach(root);
});

test('a tooltip shown by the mouse goes after four seconds of rest', () => {
    const root = hoverControl();
    const rest = timers.find(timer => timer.ms === 4000);
    assert.ok(rest, 'a 4 s rest timer is started');
    rest.fn(); timers = [];
    assert.equal(body.children.length, 0);
    detach(root);
});

test('a tooltip shown by keyboard focus has no rest timer', () => {
    const root = new Element(); root.dataset.playbackTooltip = 'Queue'; root.candidate = new Element('button'); attach(root);
    root.dispatchEvent(event('focusin'));
    assert.equal(body.children.length, 1);
    assert.equal(timers.some(timer => timer.ms === 4000), false);
    root.dispatchEvent(event('focusout'));
    assert.equal(body.children.length, 0);
    detach(root);
});

test('only one tooltip shows at a time', () => {
    const first = hoverControl('First');
    const second = hoverControl('Second');
    assert.equal(body.children.length, 1);
    assert.equal(body.children[0].textContent, 'Second');
    detach(first); detach(second);
});
