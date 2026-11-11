import test from 'node:test';
import assert from 'node:assert/strict';

class Element {
    constructor() {
        this.handlers = new Map(); this.children = []; this.parentNode = null;
        this.isConnected = true; this.style = { setProperty(name,value) { this[name]=value; } }; this.attributes = new Map();
        this.classList = { contains: () => false }; this.dataset = {};
    }
    addEventListener(name, handler) { if (!this.handlers.has(name)) this.handlers.set(name, new Set()); this.handlers.get(name).add(handler); }
    removeEventListener(name, handler) { this.handlers.get(name)?.delete(handler); }
    emit(name, event) { for (const handler of this.handlers.get(name) ?? []) handler(event); }
    setAttribute(name, value) { this.attributes.set(name, value); }
    contains(target) { return target === this || this.children.some(child => child.contains(target)); }
    querySelector() { return null; }
    querySelectorAll() { return []; }
    closest() { return null; }
    append(child) {
        if (child.parentNode) child.parentNode.children = child.parentNode.children.filter(item => item !== child);
        this.children.push(child); child.parentNode = this;
    }
    before(marker) { marker.parentNode = this.parentNode; this.parentNode.children.splice(this.parentNode.children.indexOf(this), 0, marker); }
    replaceWith(child) {
        if (child.parentNode) child.parentNode.children = child.parentNode.children.filter(item => item !== child);
        this.parentNode.children[this.parentNode.children.indexOf(this)] = child; child.parentNode = this.parentNode;
        this.parentNode = null;
    }
    getBoundingClientRect() { return { top: 700, left: 400, right: 800, bottom: 744, width: 400, height: 200 }; }
    focus() { document.activeElement = this; }
    remove() { this.parentNode?.children.splice(this.parentNode.children.indexOf(this), 1); this.parentNode = null; this.isConnected = false; }
    removeAttribute(name) { this.attributes.delete(name); }
}

globalThis.document = new Element(); document.body = new Element(); document.documentElement = new Element(); document.createComment = () => new Element();
globalThis.window = new Element(); globalThis.innerWidth = 1280; globalThis.innerHeight = 800;
globalThis.matchMedia = () => Object.assign(new Element(), { matches: false });
globalThis.ResizeObserver = globalThis.MutationObserver = class { observe() {} disconnect() {} };
globalThis.requestAnimationFrame = callback => callback();
const popover = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-popover.js', import.meta.url).href);

function openPanel(id = 'queue-panel') {
    const root = new Element(), trigger = new Element(), panel = new Element(), outside = new Element();
    panel.id = id; panel.dataset = {}; root.append(panel);
    const calls = [];
    const owner = { invokeMethodAsync(method, value) {
        calls.push([method, value]);
        if (method === 'CloseFromBrowserAsync') popover.update(root, null, false, value);
        return Promise.resolve();
    } };
    popover.attach(root, trigger, owner); popover.update(root, panel, true);
    return { root, trigger, panel, outside, calls };
}

const closes = state => state.calls.filter(([method]) => method === 'CloseFromBrowserAsync');

test('outside pointer dismissal carries false through the owner callback and retains the activated target', () => {
    const state = openPanel(); state.outside.focus();
    document.emit('pointerdown', { target: state.outside });
    assert.deepEqual(state.calls.at(-1), ['CloseFromBrowserAsync', false]);
    assert.equal(document.activeElement, state.outside);
    assert.equal(state.panel.parentNode, state.root, 'portal returns before conditional removal');
    popover.detach(state.root);
});

test('a pointer press inside the panel leaves it open and focus leaving the panel does not close it', () => {
    const state = openPanel(); const queueRow = new Element(); state.panel.append(queueRow);
    document.emit('pointerdown', { target: queueRow });
    assert.equal(closes(state).length, 0);
    assert.equal(state.trigger.attributes.get('aria-expanded'), 'true');
    state.outside.focus(); state.panel.emit('focusout', { relatedTarget: state.outside });
    assert.equal(closes(state).length, 0, 'only the trigger, the close button, an outside click and Escape close a panel');
    popover.detach(state.root);
});

test('Escape and an explicit trigger dismissal carry true and restore the trigger', () => {
    const state = openPanel(); state.outside.focus();
    const event = { key: 'Escape', preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; } };
    document.emit('keydown', event);
    assert.deepEqual(state.calls.at(-1), ['CloseFromBrowserAsync', true]);
    assert.equal(document.activeElement, state.trigger);
    assert.equal(event.prevented, true); assert.equal(event.stopped, true);
    popover.update(state.root, state.panel, true); state.outside.focus();
    state.trigger.emit('click', { detail: 1, preventDefault() {} });
    assert.deepEqual(state.calls.at(-1), ['CloseFromBrowserAsync', true]);
    assert.equal(document.activeElement, state.trigger);
    popover.detach(state.root);
});

test('clicking a closed trigger asks the owner to open it and nothing opens on hover', () => {
    const root = new Element(), trigger = new Element(), calls = [];
    popover.attach(root, trigger, { invokeMethodAsync(method, value) { calls.push([method, value]); return Promise.resolve(); } });
    assert.equal(trigger.handlers.has('pointerenter'), false);
    assert.equal(trigger.handlers.has('pointerleave'), false);
    assert.equal(trigger.handlers.has('mouseenter'), false);
    trigger.emit('pointerenter', { pointerType: 'mouse' });
    assert.equal(calls.some(([method]) => method === 'OpenAsync'), false);
    trigger.emit('click', { detail: 1, preventDefault() {} });
    assert.deepEqual(calls.at(-1), ['OpenAsync', true]);
    popover.detach(root);
});

test('opening a second panel closes the first so exactly one top-level panel is open', () => {
    const a = openPanel('panel-a'), b = openPanel('panel-b');
    assert.deepEqual(closes(a).at(-1), ['CloseFromBrowserAsync', false], 'the first panel is told to close');
    assert.equal(closes(b).length, 0);
    assert.equal(a.trigger.attributes.get('aria-expanded'), 'false');
    assert.equal(b.trigger.attributes.get('aria-expanded'), 'true');
    assert.equal(document.documentElement.attributes.get('data-playback-panel-open'), 'true');
    popover.detach(a.root); popover.detach(b.root);
    assert.equal(document.documentElement.attributes.has('data-playback-panel-open'), false, 'the open signal clears when nothing is open');
});

test('a panel opened from inside another panel is its child and does not close the parent', () => {
    const parent = openPanel('parent-panel');
    const root = new Element(), trigger = new Element(), child = new Element(), calls = [];
    child.id = 'child-panel'; child.dataset = { playbackOwnedMenu: 'parent-panel' }; root.append(child);
    popover.attach(root, trigger, { invokeMethodAsync(method, value) { calls.push([method, value]); return Promise.resolve(); } });
    popover.update(root, child, true);
    assert.equal(closes(parent).length, 0);
    assert.equal(parent.trigger.attributes.get('aria-expanded'), 'true');
    popover.detach(root); popover.detach(parent.root);
});

test('the open panel moves into the fullscreen element and returns when it closes', () => {
    const fullscreen = new Element(); document.fullscreenElement = fullscreen;
    const state = openPanel();
    assert.equal(state.panel.parentNode, fullscreen);
    popover.update(state.root, null, false);
    assert.equal(state.panel.parentNode, state.root);
    document.fullscreenElement = undefined;
    popover.detach(state.root);
});

test('placement opens above the bar, is right-aligned to the trigger and stays 12px inside the viewport', () => {
    const viewport = { x: 0, y: 0, width: 1280, height: 800 };
    const trigger = { left: 1180, right: 1224, top: 740, bottom: 784 };
    const placed = popover.computePlacement({ trigger, panel: { width: 400, height: 300 }, viewport, anchorTop: 720, alignRight: trigger.right });
    assert.equal(placed.left, 1224 - 400);
    assert.equal(placed.top, 720 - 8 - 300);
    assert.equal(placed.placement, 'above');
    const edge = popover.computePlacement({ trigger: { left: 1260, right: 1280, top: 740, bottom: 784 }, panel: { width: 400, height: 300 }, viewport, anchorTop: 720 });
    assert.equal(edge.left, 1280 - 12 - 400, 'a panel never touches the right edge');
    const left = popover.computePlacement({ trigger: { left: 0, right: 30, top: 740, bottom: 784 }, panel: { width: 400, height: 300 }, viewport, anchorTop: 720 });
    assert.equal(left.left, 12, 'a panel never touches the left edge');
});

test('a panel is at most 80% of the viewport height and flips below the trigger only when above is too short', () => {
    const viewport = { x: 0, y: 0, width: 1280, height: 800 };
    const tall = popover.computePlacement({ trigger: { left: 900, right: 944, top: 740, bottom: 784 }, panel: { width: 400, height: 2000 }, viewport, anchorTop: 740 });
    assert.ok(tall.maxHeight <= 640, `capped at 80% of 800 (got ${tall.maxHeight})`);
    assert.ok(tall.top >= 12 && tall.top + tall.maxHeight <= 740 - 8 + 0.5, 'the capped panel sits entirely above the bar');
    const nearTop = popover.computePlacement({ trigger: { left: 900, right: 944, top: 20, bottom: 64 }, panel: { width: 272, height: 300 }, viewport, anchorTop: 20 });
    assert.equal(nearTop.placement, 'below');
    assert.equal(nearTop.top, 64 + 8);
});

test('placement holds at 1024 wide and at 150 percent zoom (a 683 by 400 CSS viewport)', () => {
    for (const viewport of [{ x: 0, y: 0, width: 1024, height: 768 }, { x: 0, y: 0, width: 683, height: 400 }]) {
        for (const width of [272, 400, 560]) {
            const trigger = { left: viewport.width - 60, right: viewport.width - 16, top: viewport.height - 70, bottom: viewport.height - 20 };
            const placed = popover.computePlacement({ trigger, panel: { width, height: 900 }, viewport, anchorTop: viewport.height - 90 });
            assert.ok(placed.left >= 12, `left edge inside at ${viewport.width} (${width})`);
            assert.ok(placed.left + placed.width <= viewport.width - 12, `right edge inside at ${viewport.width} (${width})`);
            assert.ok(placed.top >= 12, `top inside at ${viewport.width} (${width})`);
            assert.ok(placed.top + placed.maxHeight <= viewport.height - 12, `bottom inside at ${viewport.width} (${width})`);
            assert.ok(placed.maxHeight <= viewport.height * 0.8, 'at most 80% of the height');
        }
    }
});

test('inside a video player the panel is limited to the player box', () => {
    const viewport = { x: 0, y: 0, width: 1280, height: 800 };
    const placed = popover.computePlacement({ trigger: { left: 1200, right: 1244, top: 500, bottom: 544 }, panel: { width: 400, height: 900 },
        viewport, area: { top: 100, bottom: 560 }, anchorTop: 480 });
    assert.ok(placed.top >= 100 + 12);
    assert.ok(placed.top + placed.maxHeight <= 480 - 8);
    assert.ok(placed.maxHeight <= (560 - 100) * 0.8);
});

test('a phone sheet fills the visual viewport and restores before conditional removal', () => {
    const state = openPanel(); state.panel.classList = { contains: name => name === 'playback-panel--sheet' };
    globalThis.innerWidth = 390; globalThis.innerHeight = 780;
    popover.update(state.root, state.panel, true);
    assert.equal(state.panel.style.width, '390px'); assert.equal(state.panel.style.height, '780px');
    popover.update(state.root, null, false, true);
    assert.equal(state.panel.parentNode, state.root);
    assert.equal(document.activeElement, state.trigger);
    globalThis.innerWidth = 1280; globalThis.innerHeight = 800;
    popover.detach(state.root);
});

// Unified player v2: a nested view opening or closing never drops keyboard focus to the page.
function nestedViews() {
    const state = openPanel('lyrics-panel');
    const control = (label, inside) => Object.assign(new Element(), {
        getClientRects: () => [{}], closest: () => null, getAttribute: name => name === 'aria-label' ? label : null,
        focus() { document.activeElement = this; state.panel.emit('focusin', { target: this }); }, parentOfPanel: inside,
    });
    let controls = [], bodyControls = [];
    state.panel.querySelectorAll = () => controls;
    state.panel.querySelector = () => ({ querySelectorAll: () => bodyControls });
    state.panel.contains = target => controls.includes(target) || target === state.panel;
    state.panel.focus = () => { document.activeElement = state.panel; };
    return { ...state, control, show(all, body = all) { controls = all; bodyControls = body; } };
}

test('opening a nested view focuses its first control, or its back button when it has none, and going back returns to what opened it', () => {
    const s = nestedViews();
    const more = s.control('Lyrics options'), back = s.control('Back'), offer = s.control('Offer');
    s.show([more]); more.focus();
    // Push into a view that has a control.
    s.show([back, offer], [offer]);
    popover.navigated(s.root, s.panel, true);
    assert.equal(document.activeElement, offer);
    // Go back: the view is redrawn and the "..." button is a new element with the same name.
    const redrawnMore = s.control('Lyrics options');
    s.show([redrawnMore]);
    popover.navigated(s.root, s.panel, false);
    assert.equal(document.activeElement, redrawnMore, 'found again by its accessible name');
    // A view with no control in the body falls back to the first control in the panel (the back button).
    s.show([back], []); popover.navigated(s.root, s.panel, true);
    assert.equal(document.activeElement, back);
    popover.detach(s.root);
});

test('going back finds a redrawn control that has no accessible name by its data-return-key', () => {
    const s = nestedViews();
    const keyed = key => Object.assign(s.control(null), { getAttribute: name => name === 'data-return-key' ? key : null });
    const custom = keyed('sleep-custom'), preset = keyed('sleep-preset'), set = s.control('Set timer');
    s.show([preset, custom]); custom.focus();
    s.show([set]); popover.navigated(s.root, s.panel, true);
    assert.equal(document.activeElement, set);
    // The timer view is redrawn: the Custom row is a new element with the same key.
    const redrawnCustom = keyed('sleep-custom');
    s.show([keyed('sleep-preset'), redrawnCustom]);
    popover.navigated(s.root, s.panel, false);
    assert.equal(document.activeElement, redrawnCustom);
    popover.detach(s.root);
});

test('going back to a control that is still there focuses that very control, and with nothing remembered the first control of the view', () => {
    const s = nestedViews();
    const row = s.control('Row'), other = s.control('Other');
    s.show([row, other]); row.focus();
    popover.navigated(s.root, s.panel, true);
    s.show([row, other]);
    row.isConnected = true; popover.navigated(s.root, s.panel, false);
    assert.equal(document.activeElement, row);
    popover.navigated(s.root, s.panel, false); // nothing left to return to
    assert.equal(document.activeElement, row, 'the first control of the view it lands on');
    assert.equal(popover.returnTarget(null, s.panel), null);
    popover.detach(s.root);
});
