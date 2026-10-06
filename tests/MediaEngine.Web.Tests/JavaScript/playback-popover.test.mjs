import test from 'node:test';
import assert from 'node:assert/strict';

class Element {
    constructor() {
        this.handlers = new Map(); this.children = []; this.parentNode = null;
        this.isConnected = true; this.style = { setProperty(name,value) { this[name]=value; } }; this.attributes = new Map();
        this.classList = { contains: () => false };
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
    getBoundingClientRect() { return { top: 700, left: 400, right: 800, width: 400, height: 200 }; }
    focus() { document.activeElement = this; }
}

globalThis.document = new Element(); document.body = new Element(); document.createComment = () => new Element();
globalThis.window = new Element(); globalThis.innerWidth = 1280; globalThis.innerHeight = 800;
globalThis.matchMedia = () => Object.assign(new Element(), { matches: false });
globalThis.ResizeObserver = globalThis.MutationObserver = class { observe() {} disconnect() {} };
globalThis.requestAnimationFrame = callback => callback();
const popover = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-popover.js', import.meta.url).href);

function openPanel(dock = false) {
    const root = new Element(), trigger = new Element(), panel = new Element(), outside = new Element();
    panel.id = 'queue-panel'; root.dataset = { playbackHoverPreview: dock ? 'true' : 'false' }; root.append(panel);
    const calls = [];
    const owner = { invokeMethodAsync(method, value) {
        calls.push([method, value]);
        if (method === 'CloseFromBrowserAsync') popover.update(root, null, false, false, value);
        return Promise.resolve();
    } };
    popover.attach(root, trigger, owner); popover.update(root, panel, true, true);
    return { root, trigger, panel, outside, calls };
}

test('outside pointer dismissal carries false through the owner callback and retains the activated target', () => {
    const state = openPanel(); state.outside.focus();
    document.emit('pointerdown', { target: state.outside });
    assert.deepEqual(state.calls.at(-1), ['CloseFromBrowserAsync', false]);
    assert.equal(document.activeElement, state.outside);
    assert.equal(state.panel.parentNode, state.root, 'portal returns before conditional removal');
    popover.detach(state.root);
});

test('focus departure retains the destination while an inside queue mutation leaves the tool open', () => {
    const state = openPanel(); const queueRow = new Element(); state.panel.append(queueRow);
    document.emit('pointerdown', { target: queueRow });
    assert.equal(state.calls.some(([method]) => method === 'CloseFromBrowserAsync'), false);
    assert.equal(state.trigger.attributes.get('aria-expanded'), 'true');
    state.outside.focus(); state.panel.emit('focusout', { relatedTarget: state.outside });
    assert.deepEqual(state.calls.at(-1), ['CloseFromBrowserAsync', false]);
    assert.equal(document.activeElement, state.outside);
    popover.detach(state.root);
});

test('Escape and an explicit trigger dismissal carry true and restore the trigger', () => {
    const state = openPanel(); state.outside.focus();
    const event = { key: 'Escape', preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; } };
    document.emit('keydown', event);
    assert.deepEqual(state.calls.at(-1), ['CloseFromBrowserAsync', true]);
    assert.equal(document.activeElement, state.trigger);
    assert.equal(event.prevented, true); assert.equal(event.stopped, true);
    popover.update(state.root, state.panel, true, true); state.outside.focus();
    state.trigger.emit('click', { detail: 1, preventDefault() {} });
    assert.deepEqual(state.calls.at(-1), ['CloseFromBrowserAsync', true]);
    assert.equal(document.activeElement, state.trigger);
    popover.detach(state.root);
});

test('popover caret follows the actual trigger center and clamps inside the panel', () => {
    const state=openPanel();
    state.trigger.getBoundingClientRect=()=>({top:700,left:10,right:54,width:44,height:44});
    popover.update(state.root,state.panel,true,true);
    assert.equal(state.panel.style.left,'8px');
    assert.equal(state.panel.style['--playback-caret-left'],'24px');
    state.trigger.getBoundingClientRect=()=>({top:700,left:0,right:2,width:2,height:44});
    popover.update(state.root,state.panel,true,true);
    assert.equal(state.panel.style['--playback-caret-left'],'12px');
    popover.detach(state.root);
});

test('vertical volume stays a small pill above its trigger even at popup widths', () => {
    const state=openPanel(); globalThis.innerWidth=420;
    state.panel.classList={contains:name=>name==='playback-popover--volume'};
    state.trigger.getBoundingClientRect=()=>({top:700,left:290,right:334,width:44,height:44});
    popover.update(state.root,state.panel,true,true);
    assert.equal(state.panel.style.width,'44px'); assert.equal(state.panel.style.height,'152px');
    assert.equal(state.panel.style.left,'290px'); assert.equal(state.panel.style.top,'540px');
    popover.detach(state.root); globalThis.innerWidth=1280;
});

test('song menu uses intrinsic width instead of the large playback sheet width', () => {
    const root = new Element(), trigger = new Element(), panel = new Element();
    panel.classList = { contains: name => name === 'playback-popover--menu' };
    root.append(panel);
    const owner = { invokeMethodAsync: () => Promise.resolve() };
    popover.attach(root, trigger, owner);
    popover.update(root, panel, true, true);
    assert.equal(panel.style.width, 'max-content');
    assert.equal(panel.style.maxWidth, '1264px');
    popover.detach(root);
});

test('pinned dock survives outside pointer and focus loss and scopes Escape to its card', () => {
    const state = openPanel(true);
    const closed = () => state.calls.filter(([method]) => method === 'CloseFromBrowserAsync').length;
    document.emit('pointerdown', { target: state.outside });
    state.panel.emit('focusout', { relatedTarget: state.outside });
    document.emit('keydown', { key: 'Escape', target: state.outside });
    assert.equal(closed(), 0);
    document.emit('keydown', { key: 'Escape', target: state.panel, preventDefault() {}, stopImmediatePropagation() {} });
    assert.equal(closed(), 1);
    assert.equal(state.trigger.attributes.get('aria-expanded'), 'false');
    popover.detach(state.root);
});

test('speed is intrinsic and compact on a phone rather than a full viewport sheet', () => {
    const state = openPanel(); globalThis.innerWidth = 390;
    state.panel.classList = { contains: name => name === 'playback-popover--speed' };
    popover.update(state.root, state.panel, true, true);
    assert.equal(state.panel.style.width, 'max-content');
    assert.notEqual(state.panel.style.height, '800px');
    assert.ok(parseFloat(state.panel.style.maxWidth) <= 374);
    popover.detach(state.root); globalThis.innerWidth = 1280;
});
