import test from 'node:test';
import assert from 'node:assert/strict';

// Packet S2: every AppTooltip is served by ONE controller on the document (playback-tooltip.js). An owner is
// <span class="playback-tooltip-owner" data-playback-tooltip="text" ...>; it has no script or listener of its own.
let timers = [], observers = [];
const documentListeners = [], windowListeners = [];

class Node extends EventTarget {
    constructor(tag = 'span') {
        super(); this.tag = tag; this.nodeType = 1; this.attrs = new Map(); this.dataset = {}; this.style = {};
        this.children = []; this.parent = null; this.isConnected = true; this.focusVisible = true; this.perElementListeners = 0;
    }
    addEventListener(...args) { this.perElementListeners++; return super.addEventListener(...args); }
    get classList() { return { contains: name => (this.attrs.get('class') ?? '').split(' ').includes(name) }; }
    getAttribute(key) { return this.attrs.get(key) ?? null; }
    setAttribute(key, value) { this.attrs.set(key, String(value)); }
    removeAttribute(key) { this.attrs.delete(key); }
    hasAttribute(key) { return this.attrs.has(key); }
    append(child) { child.parent?.removeChild(child); child.parent = this; this.children.push(child); }
    removeChild(child) { this.children = this.children.filter(item => item !== child); }
    remove() { this.parent?.removeChild(this); this.parent = null; this.isConnected = false; }
    descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
    contains(node) { return node === this || this.descendants().includes(node); }
    matchesOne(selector) {
        if (selector === ':focus-visible') return this.focusVisible;
        if (selector.startsWith('.')) return this.classList.contains(selector.slice(1));
        const tagged = selector.match(/^(\w+)\[([\w-]+)\]$/);
        if (tagged) return this.tag === tagged[1] && this.attrs.has(tagged[2]);
        const attribute = selector.match(/^\[([\w-]+)(?:="(.*)")?\]$/);
        if (attribute) return this.attrs.has(attribute[1]) && (attribute[2] === undefined || this.attrs.get(attribute[1]) === attribute[2]);
        return this.tag === selector;
    }
    matches(selector) { return selector.split(',').some(part => this.matchesOne(part.trim())); }
    querySelectorAll(selector) { return this.descendants().filter(node => node.matches(selector)); }
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
    closest(selector) { for (let node = this; node; node = node.parent) if (node.matches(selector)) return node; return null; }
    getBoundingClientRect() { return { left: 210, top: 200, bottom: 244, width: 44, height: 44 }; }
    showPopover() { this.popoverShown = true; }
}

const body = new Node('body');
const page = new Node('html'); page.append(body);
const doc = new EventTarget();
const addDocument = doc.addEventListener.bind(doc);
doc.addEventListener = (type, handler, options) => { documentListeners.push(type); addDocument(type, handler, options); };
Object.assign(doc, {
    body, documentElement: page, fullscreenElement: null,
    querySelectorAll: selector => page.querySelectorAll(selector),
    createElement: tag => new Node(tag),
});
globalThis.document = doc;
globalThis.window = new EventTarget();
const addWindow = window.addEventListener.bind(window);
window.addEventListener = (type, handler, options) => { windowListeners.push(type); addWindow(type, handler, options); };
globalThis.innerWidth = 1024; globalThis.innerHeight = 768;
globalThis.MutationObserver = class {
    constructor(callback) { this.callback = callback; this.target = null; observers.push(this); }
    observe(target) { this.target = target; }
    disconnect() { this.target = null; }
};
globalThis.setTimeout = (fn, ms = 0) => { const timer = { fn, ms }; timers.push(timer); return timer; };
globalThis.clearTimeout = timer => { timers = timers.filter(item => item !== timer); };

await import('../../../src/MediaEngine.Web/wwwroot/js/playback-tooltip.js');
const listenersAfterLoad = { document: [...documentListeners], window: [...windowListeners] };

function fire(type, target, properties = {}) {
    const value = new Event(type);
    Object.defineProperty(value, 'target', { value: target });
    Object.assign(value, properties);
    doc.dispatchEvent(value);
}
const runDue = (maxMs = 1000) => { const due = timers.filter(timer => timer.ms <= maxMs); timers = timers.filter(timer => timer.ms > maxMs); due.forEach(timer => timer.fn()); };

function makeOwner(text = 'Play', { child = 'button', suppressed = false, parent = body } = {}) {
    const owner = new Node('span');
    owner.setAttribute('class', 'playback-tooltip-owner');
    owner.dataset.playbackTooltip = text; owner.dataset.playbackTooltipPlacement = 'top'; owner.dataset.playbackTooltipSuppressed = suppressed ? 'true' : 'false';
    const inner = new Node(child); owner.append(inner);
    parent.append(owner);
    return { owner, inner };
}
const tips = () => body.children.filter(node => node.className === 'playback-tooltip');
// The first observer made at load is the page-wide one that prepares owners as Blazor adds them.
const preparer = () => observers[0];
function reset() {
    fire('pointerdown', body); // a press anywhere closes anything shown
    timers = [];
    for (const node of [...body.children]) node.remove();
}

test('hovering an owner shows the tooltip after the delay, leaving hides it', () => {
    reset();
    const { owner, inner } = makeOwner('Play');
    fire('pointerenter', owner, { pointerType: 'mouse' });
    assert.equal(tips().length, 0, 'nothing before the hover delay');
    const delay = timers.find(timer => timer.ms === 150);
    assert.ok(delay, 'the 150 ms hover delay is armed');
    runDue();
    assert.equal(tips().length, 1);
    assert.equal(tips()[0].textContent, 'Play');
    assert.equal(tips()[0].getAttribute('popover'), 'manual');
    assert.equal(tips()[0].popoverShown, true);
    assert.match(inner.getAttribute('aria-describedby'), /^playback-tooltip-\d+$/);
    fire('pointerleave', owner);
    assert.equal(tips().length, 0);
    assert.equal(inner.getAttribute('aria-describedby'), null);
});

test('touch hovering shows nothing, a long press shows it, and it lingers after release', () => {
    reset();
    const { owner } = makeOwner('Queue');
    fire('pointerenter', owner, { pointerType: 'touch' });
    assert.equal(timers.length, 0);
    fire('pointerdown', owner, { pointerType: 'touch' });
    assert.ok(timers.find(timer => timer.ms === 500), 'the 500 ms press delay is armed');
    runDue(500);
    assert.equal(tips().length, 1);
    fire('pointerup', owner, { pointerType: 'touch' });
    assert.equal(tips().length, 1, 'the text stays readable under the thumb');
    runDue(1500);
    assert.equal(tips().length, 0);
});

test('a quick touch tap never shows the tooltip', () => {
    reset();
    const { owner } = makeOwner('Queue');
    fire('pointerdown', owner, { pointerType: 'touch' });
    fire('pointerup', owner, { pointerType: 'touch' });
    runDue(2000);
    assert.equal(tips().length, 0);
});

test('keyboard focus shows the tooltip at once without a rest timer, Escape and blur hide it', () => {
    reset();
    const { owner, inner } = makeOwner('Queue');
    fire('focusin', inner);
    assert.equal(tips().length, 1);
    assert.equal(timers.some(timer => timer.ms === 4000), false);
    fire('keydown', inner, { key: 'Escape' });
    assert.equal(tips().length, 0);
    fire('focusin', inner);
    assert.equal(tips().length, 1);
    fire('focusout', inner);
    assert.equal(tips().length, 0);
    owner.remove();
});

test('focus that is not keyboard focus does not show a tooltip', () => {
    reset();
    const { inner } = makeOwner('Queue');
    inner.focusVisible = false;
    fire('focusin', inner);
    assert.equal(tips().length, 0);
});

test('a tooltip shown by the mouse goes after four seconds of rest, and a press hides it', () => {
    reset();
    const { owner } = makeOwner('Play');
    fire('pointerenter', owner, { pointerType: 'mouse' }); runDue();
    assert.ok(timers.find(timer => timer.ms === 4000));
    runDue(4000);
    assert.equal(tips().length, 0);
    fire('pointerenter', owner, { pointerType: 'mouse' }); runDue();
    assert.equal(tips().length, 1);
    fire('pointerdown', owner, { pointerType: 'mouse' });
    assert.equal(tips().length, 0);
});

test('a suppressed owner, or one whose control has its menu open, shows nothing', () => {
    reset();
    const { owner: quiet } = makeOwner('Hidden', { suppressed: true });
    fire('pointerenter', quiet, { pointerType: 'mouse' }); runDue();
    assert.equal(tips().length, 0);
    const { owner, inner } = makeOwner('Menu');
    inner.setAttribute('aria-expanded', 'true');
    fire('pointerenter', owner, { pointerType: 'mouse' }); runDue();
    assert.equal(tips().length, 0);
});

test('the tooltip follows a redraw of its text and closes when the owner becomes suppressed', () => {
    reset();
    const { owner } = makeOwner('Play');
    fire('pointerenter', owner, { pointerType: 'mouse' }); runDue();
    // the page observer started for the shown tooltip receives the attribute records
    const pageObserver = observers.filter(item => item.target === page).at(-1);
    owner.dataset.playbackTooltip = 'Pause';
    pageObserver.callback([{ type: 'attributes', attributeName: 'data-playback-tooltip', target: owner }]);
    assert.equal(tips()[0].textContent, 'Pause');
    owner.dataset.playbackTooltipSuppressed = 'true';
    pageObserver.callback([{ type: 'attributes', attributeName: 'data-playback-tooltip-suppressed', target: owner }]);
    assert.equal(tips().length, 0);
});

test('a tooltip closes when its anchor is removed from the page while it is open', () => {
    reset();
    const { owner } = makeOwner('Play');
    fire('pointerenter', owner, { pointerType: 'mouse' }); runDue();
    assert.equal(tips().length, 1);
    owner.remove();
    observers.filter(item => item.target === page).at(-1).callback([{ type: 'childList', addedNodes: [], removedNodes: [owner] }]);
    assert.equal(tips().length, 0);
});

test('a waiting tooltip is dropped when its owner is removed before the delay ends', () => {
    reset();
    const { owner } = makeOwner('Play');
    fire('pointerenter', owner, { pointerType: 'mouse' });
    owner.remove();
    runDue();
    assert.equal(tips().length, 0);
});

test('only one tooltip shows at a time', () => {
    reset();
    const first = makeOwner('First').owner, second = makeOwner('Second').owner;
    fire('pointerenter', first, { pointerType: 'mouse' }); runDue();
    fire('pointerenter', second, { pointerType: 'mouse' }); runDue();
    assert.equal(tips().length, 1);
    assert.equal(tips()[0].textContent, 'Second');
});

test('scrolling or resizing closes the tooltip', () => {
    reset();
    const { owner } = makeOwner('Play');
    fire('pointerenter', owner, { pointerType: 'mouse' }); runDue();
    window.dispatchEvent(new Event('scroll'));
    assert.equal(tips().length, 0);
    fire('pointerenter', owner, { pointerType: 'mouse' }); runDue();
    window.dispatchEvent(new Event('resize'));
    assert.equal(tips().length, 0);
});

test('an owner around a disabled control becomes keyboard-focusable as soon as it appears on the page', () => {
    reset();
    const { owner, inner } = makeOwner('Why it is off');
    inner.disabled = true;
    preparer().callback([{ addedNodes: [owner] }]);
    assert.equal(owner.getAttribute('tabindex'), '0');
    assert.equal(owner.dataset.tooltipWrapperTarget, 'true');
    fire('focusin', owner);
    assert.equal(tips().length, 1);
    assert.match(owner.getAttribute('aria-describedby'), /^playback-tooltip-\d+$/);
});

test('an owner around an enabled control stays out of the tab order', () => {
    reset();
    const { owner } = makeOwner('Play');
    preparer().callback([{ addedNodes: [owner] }]);
    assert.equal(owner.getAttribute('tabindex'), null);
    assert.equal(owner.dataset.tooltipWrapperTarget, undefined);
});

test('a batch where Blazor adds a parent and then its children scans each subtree once', () => {
    reset();
    const outer = new Node('div'); body.append(outer);
    const middle = new Node('div'); outer.append(middle);
    const { owner } = makeOwner('Deep', { parent: middle });
    const text = new Node('span'); text.nodeType = 3;
    const scans = new Map();
    for (const node of [outer, middle, text]) {
        const original = node.querySelectorAll.bind(node);
        node.querySelectorAll = selector => { scans.set(node, (scans.get(node) ?? 0) + 1); return original(selector); };
    }
    preparer().callback([{ addedNodes: [outer] }, { addedNodes: [middle] }, { addedNodes: [owner, text] }, { addedNodes: [outer] }]);
    assert.equal(scans.get(outer), 1, 'the first added subtree is scanned');
    assert.equal(scans.get(middle) ?? 0, 0, 'a node inside a scanned subtree is skipped');
    assert.equal(scans.get(text) ?? 0, 0, 'a text node is never scanned');
});

test('a waiting tooltip is dropped by the page observer once its owner has left the page', () => {
    reset();
    const { owner } = makeOwner('Play');
    fire('pointerenter', owner, { pointerType: 'mouse' });
    assert.equal(timers.length, 1);
    owner.remove();
    preparer().callback([{ addedNodes: [], removedNodes: [owner] }]);
    assert.equal(timers.length, 0, 'the waiting timer no longer holds the removed owner');
    runDue();
    assert.equal(tips().length, 0);
});

test('a nested owner that waits does not write its text into the tooltip showing for its parent', () => {
    reset();
    const { owner: parent, inner: parentButton } = makeOwner('Parent');
    const { owner: child } = makeOwner('Child', { parent: parentButton });
    fire('pointerenter', parent, { pointerType: 'mouse' }); runDue(150);
    assert.equal(tips()[0].textContent, 'Parent');
    fire('pointerenter', child, { pointerType: 'mouse' }); // the child is now waiting to show
    const pageObserver = observers.filter(item => item.target === page).at(-1);
    child.dataset.playbackTooltip = 'Child changed';
    pageObserver.callback([{ type: 'attributes', attributeName: 'data-playback-tooltip', target: child }]);
    assert.equal(tips()[0].textContent, 'Parent', 'the waiting child is not the shown owner');
    parent.dataset.playbackTooltip = 'Parent changed';
    pageObserver.callback([{ type: 'attributes', attributeName: 'data-playback-tooltip', target: parent }]);
    assert.equal(tips()[0].textContent, 'Parent changed', 'the shown owner still refreshes its own text');
});

test('a nested owner removed while waiting leaves the parent tooltip showing and the parent in charge', () => {
    reset();
    const { owner: parent, inner: parentButton } = makeOwner('Parent');
    const { owner: child } = makeOwner('Child', { parent: parentButton });
    fire('pointerenter', parent, { pointerType: 'mouse' }); runDue(150);
    fire('pointerenter', child, { pointerType: 'mouse' });
    child.remove();
    preparer().callback([{ addedNodes: [], removedNodes: [child] }]);
    assert.equal(timers.filter(timer => timer.ms === 150).length, 0, 'the child timer is gone');
    assert.equal(tips().length, 1, 'the parent tooltip stays');
    fire('pointerleave', parent);
    assert.equal(tips().length, 0, 'leaving the parent closes it');
});

test('a tooltip inside an open modal dialog is drawn inside that dialog', () => {
    reset();
    const dialog = new Node('dialog'); dialog.setAttribute('open', ''); dialog.focusVisible = true;
    const matches = dialog.matches.bind(dialog);
    dialog.matches = selector => selector === ':modal' ? true : matches(selector);
    body.append(dialog);
    const { owner } = makeOwner('In dialog', { parent: dialog });
    fire('pointerenter', owner, { pointerType: 'mouse' }); runDue();
    assert.equal(dialog.children.filter(node => node.className === 'playback-tooltip').length, 1);
    fire('pointerleave', owner);
});

test('the document carries one set of listeners however many tooltips the page has', () => {
    reset();
    const before = { document: [...documentListeners], window: [...windowListeners] };
    const owners = Array.from({ length: 300 }, (_, index) => makeOwner(`Tooltip ${index}`));
    preparer().callback(owners.map(item => ({ addedNodes: [item.owner] })));
    for (const { owner } of owners.slice(0, 20)) { fire('pointerenter', owner, { pointerType: 'mouse' }); runDue(); fire('pointerleave', owner); }
    assert.deepEqual(documentListeners, before.document, 'no listener is added for any tooltip');
    assert.deepEqual(windowListeners, before.window);
    assert.equal(owners.reduce((sum, { owner, inner }) => sum + owner.perElementListeners + inner.perElementListeners, 0), 0, 'nothing listens on a tooltip element itself');
    assert.ok(listenersAfterLoad.document.length <= 14, `the controller registers a fixed handful of document listeners (${listenersAfterLoad.document.length})`);
});
