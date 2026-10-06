import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const handlers = new Set(), modals = [], ids = new Map();
class Element {
    constructor({ focusable = false, classes = [] } = {}) {
        this.children = []; this.parent = null; this.attributes = new Map();
        this.classes = new Set(classes); this.tabIndex = focusable ? 0 : -1;
        this.isConnected = true; this.shown = true; this.hidden = false;
    }
    append(element) { this.children.push(element); element.parent = this; }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    setAttribute(name, value) { this.attributes.set(name, value); }
    closest(selector) {
        if (selector === '.tl-popover-open' && this.classes.has('tl-popover-open')) return this;
        return this.parent?.closest(selector) ?? null;
    }
    getClientRects() { return this.shown && this.isConnected ? [{}] : []; }
    contains(target) { return target === this || this.children.some(element => element.contains(target)); }
    querySelectorAll(selector) {
        const descendants = this.children.flatMap(element => [element, ...element.querySelectorAll('*')]);
        if (selector === '*') return descendants;
        if (selector === '[role="combobox"][aria-controls]') return descendants.filter(element => element.getAttribute('role') === 'combobox' && element.getAttribute('aria-controls'));
        return descendants.filter(element => element.tabIndex >= 0);
    }
    focus() { document.activeElement = this; }
}
globalThis.HTMLElement = Element;
globalThis.getComputedStyle = () => ({ visibility: 'visible' });
globalThis.document = {
    activeElement: null,
    addEventListener(name, callback) { assert.equal(name, 'keydown'); handlers.add(callback); },
    removeEventListener(name, callback) { assert.equal(name, 'keydown'); handlers.delete(callback); },
    querySelectorAll() { return modals; },
    getElementById(id) { return ids.get(id) ?? null; }
};
const focus = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-full-focus.js', import.meta.url).href);
function key(key = 'Tab', shiftKey = false) {
    const event = { key, shiftKey, prevented: false, stopped: false,
        preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; } };
    for (const callback of handlers) { callback(event); if (event.stopped) break; }
    return event;
}
function player() {
    const root = new Element(), first = new Element({ focusable: true }), last = new Element({ focusable: true });
    root.append(first); root.append(last);
    return { root, first, last };
}

test('phone Tab and Shift+Tab wrap within the player and recover focus from the obscured page', () => {
    const { root, first, last } = player();
    const expand = new Element({ focusable: true }); expand.focus();
    focus.attach(root, 'profile:asset:1', false);
    assert.equal(document.activeElement, expand, 'the phone host can still capture originating Expand');
    last.focus(); assert.equal(key().prevented, true); assert.equal(document.activeElement, first);
    key('Tab', true); assert.equal(document.activeElement, last);
    expand.focus(); key(); assert.equal(document.activeElement, first);
    const hidden = new Element({ focusable: true }); hidden.shown = false; root.append(hidden);
    last.focus(); key(); assert.equal(document.activeElement, first, 'responsive hidden controls are skipped');
    focus.detach(root); assert.equal(handlers.size, 0);
});

test('popup establishes focus but Escape remains an ordinary tool event without an exit action', () => {
    const { root, first } = player();
    new Element({ focusable: true }).focus(); focus.attach(root, 'popup:asset:1', true);
    assert.equal(document.activeElement, root);
    assert.equal(key('Escape').prevented, false);
    assert.equal(key('Escape').stopped, false);
    key(); assert.equal(document.activeElement, first);
    focus.detach(root);
});

test('only selectors controlled by this player participate in its Tab boundary', () => {
    const { root, first, last } = player();
    const menu = new Element({ classes: ['tl-popover-open'] }), list = new Element(), option = new Element({ focusable: true });
    menu.append(list); list.append(option); ids.set('owned-options', list);
    first.setAttribute('role', 'combobox'); first.setAttribute('aria-controls', 'owned-options');
    const unrelatedMenu = new Element({ classes: ['tl-popover-open'] }); unrelatedMenu.append(new Element({ focusable: true }));
    focus.attach(root, 'asset:1'); last.focus(); key(); assert.equal(document.activeElement, option);
    key(); assert.equal(document.activeElement, first);
    menu.shown = false; last.focus(); key(); assert.equal(document.activeElement, first);
    focus.detach(root); ids.clear();
});

test('sheets and bookmark host dialogs keep their own focus, including their owned menus', () => {
    const { root } = player(); focus.attach(root, 'asset:1');
    for (const host of ['sheet', 'bookmark']) {
        const dialog = new Element(), control = new Element({ focusable: true }); dialog.append(control); modals.push(dialog);
        control.focus(); assert.equal(key().prevented, false, `${host} keeps its own Tab handler`);
        const menu = new Element({ classes: ['tl-popover-open'] }), list = new Element(), option = new Element({ focusable: true });
        menu.append(list); list.append(option); ids.set(host, list);
        control.setAttribute('role', 'combobox'); control.setAttribute('aria-controls', host);
        option.focus(); assert.equal(key().prevented, false, `${host} owns its external selector`);
        new Element({ focusable: true }).focus(); key(); assert.equal(document.activeElement, control);
        modals.pop(); ids.clear();
    }
    focus.detach(root);
});

test('subject changes replace the handler and disposal or resize removal releases the boundary', () => {
    const { root } = player(); focus.attach(root, 'asset:1');
    const old = [...handlers][0]; focus.attach(root, 'asset:1'); assert.equal(handlers.size, 1);
    focus.attach(root, 'asset:2'); assert.equal(handlers.size, 1); assert.equal(handlers.has(old), false);
    root.isConnected = false; assert.equal(key().prevented, false); assert.equal(handlers.size, 0);
    root.isConnected = true; focus.attach(root, 'asset:3'); focus.detach(root);
    assert.equal(key().prevented, false); assert.equal(handlers.size, 0);
});

test('the phone host captures Expand before focus enters the boundary and restores it after Collapse', async () => {
    const source = await readFile(new URL('../../../src/MediaEngine.Web/wwwroot/app.js', import.meta.url), 'utf8');
    const start = source.indexOf('    capturePlayerFocus: function'), end = source.indexOf('    observeDock: function', start);
    const context = { document, HTMLElement: Element };
    runInNewContext(`globalThis.api = { ${source.slice(start, end)} };`, context);
    const { root } = player(), expand = new Element({ focusable: true }); expand.focus();
    focus.attach(root, 'asset:1', false); context.api.capturePlayerFocus(root);
    assert.equal(document.activeElement, root);
    focus.detach(root); context.api.restorePlayerFocus();
    assert.equal(document.activeElement, expand); assert.equal(context.api.playerReturnFocus, null);
});
