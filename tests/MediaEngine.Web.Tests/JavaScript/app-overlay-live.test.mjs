import test from 'node:test';
import assert from 'node:assert/strict';

// The details overlay is a window the app keeps modal itself: the page behind it is inert, the player bar and the
// pop-over and dialog hosts are not, and Escape belongs to the window only when it is aimed at it
// (music-player-and-listen-fixes D5). A tiny tree stands in for the layout.
class Element {
    constructor(tag = 'DIV', attributes = {}) {
        this.tagName = tag; this.isConnected = true; this.parentElement = null; this.children = [];
        this.style = { overflow: 'auto', paddingRight: '4px', scrollbarGutter: 'stable', props: new Map(),
            setProperty(name, value) { this.props.set(name, value); }, removeProperty(name) { this.props.delete(name); } };
        this.listeners = new Map(); this.attributes = new Map(Object.entries(attributes)); this.focusCount = 0;
        this.box = { top: 0, height: 0 };
    }
    addEventListener(name, fn) { if (!this.listeners.has(name)) this.listeners.set(name, new Set()); this.listeners.get(name).add(fn); }
    removeEventListener(name, fn) { this.listeners.get(name)?.delete(fn); }
    emit(name, event) { for (const fn of [...(this.listeners.get(name) ?? [])]) fn(event); }
    append(child) { this.children.push(child); child.parentElement = this; return child; }
    get previousElementSibling() { const siblings = this.parentElement?.children ?? []; return siblings[siblings.indexOf(this) - 1] ?? null; }
    contains(target) { return target === this || this.children.some(child => child.contains(target)); }
    closest(selector) {
        if (selector === 'dialog[open]') return this instanceof Dialog && this.open ? this : this.parentElement?.closest(selector);
        if (selector === '[data-app-escape-owner="field"]') return this.attributes.get('data-app-escape-owner') === 'field' ? this : this.parentElement?.closest(selector);
        if (selector === '[inert]') return this.attributes.has('inert') ? this : this.parentElement?.closest(selector);
        return null;
    }
    focus() {
        if (this.closest('[inert]')) return;
        this.focusCount++; document.activeElement = this;
        for (let element = this; element; element = element.parentElement) element.emit('focusin', { target: this });
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
    querySelectorAll(selector) {
        const all = []; const walk = e => { for (const child of e.children) { all.push(child); walk(child); } }; walk(this);
        if (selector === '.tl-popover-open:not([hidden])') return all.filter(e => e.attributes.has('class-open') && !e.attributes.has('hidden'));
        if (selector === 'dialog[open]') return all.filter(e => e instanceof Dialog && e.open);
        if (selector === '[data-overlay-live="player-bar"]') return all.filter(e => e.attributes.get('data-overlay-live') === 'player-bar');
        if (selector === '[data-overlay-live="search"]') return all.filter(e => e.attributes.get('data-overlay-live') === 'search');
        return [];
    }
    setAttribute(name, value) { this.attributes.set(name, value); }
    removeAttribute(name) { this.attributes.delete(name); }
    hasAttribute(name) { return this.attributes.has(name); }
    matches() { return false; }
    showPopover() {} hidePopover() {}
    getBoundingClientRect() { return { left: 200, top: this.box.top, right: 300, bottom: this.box.top + this.box.height, width: 100, height: this.box.height }; }
}
class Dialog extends Element {
    constructor(attributes) { super('DIALOG', attributes); }
    show() { this.showCount = (this.showCount ?? 0) + 1; this.open = true; this.focus(); }
    showModal() { this.modalCount = (this.modalCount ?? 0) + 1; this.open = true; this.focus(); }
    close() { this.open = false; document.activeElement = document.body; }
}
const observers = [];
globalThis.HTMLElement = Element;
globalThis.HTMLDialogElement = Dialog;
globalThis.MutationObserver = class {
    constructor(callback) { this.callback = callback; observers.push(this); }
    observe() { this.connected = true; }
    disconnect() { this.connected = false; }
};
const resizers = [];
globalThis.ResizeObserver = class {
    constructor(callback) { this.callback = callback; resizers.push(this); }
    observe() { this.connected = true; } disconnect() { this.connected = false; }
};
globalThis.document = new Element(); document.documentElement = new Element('HTML'); document.documentElement.clientWidth = 1180;
document.body = document.documentElement.append(new Element('BODY')); document.activeElement = document.body;
globalThis.window = new Element(); globalThis.innerWidth = 1200; globalThis.innerHeight = 900;
globalThis.getComputedStyle = element => element.style;
document.querySelector = selector => document.body.querySelector(selector);
document.querySelectorAll = selector => document.body.querySelectorAll(selector);
const root = new URL('../../../src/MediaEngine.Web/wwwroot/js/', import.meta.url);
const dialogs = await import(new URL('app-dialog.js', root));
const live = await import(new URL('app-overlay-live.js', root));
function owner() { const calls = []; return { calls, invokeMethodAsync(name) { calls.push(name); return Promise.resolve(); } }; }
function mutate() { for (const observer of [...observers]) if (observer.connected) observer.callback(); }
function press(target) {
    return { key: 'Escape', target, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
}

// body > [popoverHost, dialogHost, frame > [layout > [topNav, content > [underlay, backdrop, overlay]], dock, intentDock], skipLink]
function layout() {
    const body = document.body; body.children.length = 0;
    const popoverHost = body.append(new Element('DIV', { 'data-app-popover-host': '' }));
    const dialogHost = body.append(new Dialog());
    const frame = body.append(new Element('DIV'));
    const shell = frame.append(new Element('DIV'));
    const topNav = shell.append(new Element('HEADER'));
    const content = shell.append(new Element('DIV'));
    const underlay = content.append(new Element('SECTION'));
    const underlayLink = underlay.append(new Element('A'));
    const backdrop = content.append(new Element('DIV', { 'data-app-dialog-backdrop': '' }));
    const overlay = content.append(new Dialog());
    const inside = overlay.append(new Element('BUTTON'));
    const dock = frame.append(new Element('DIV', { 'data-overlay-live': 'player-bar' }));
    const play = dock.append(new Element('BUTTON'));
    const intentDock = frame.append(new Element('NAV'));
    const skip = body.append(new Element('A'));
    const opener = underlay.append(new Element('BUTTON'));
    return { body, popoverHost, dialogHost, frame, shell, topNav, content, underlay, underlayLink, backdrop, overlay, inside, dock, play, intentDock, skip, opener };
}

test('opening a live window sets inert on the page and navigation but not on the player bar, the hosts or the window', () => {
    const ui = layout(); ui.opener.focus();
    dialogs.update(ui.overlay, owner(), true, true, true, 'live-1', false, true);
    assert.equal(ui.overlay.showCount, 1); assert.equal(ui.overlay.modalCount, undefined, 'it is shown non-modal, not with showModal');
    for (const inert of [ui.topNav, ui.underlay, ui.intentDock, ui.skip]) assert.ok(inert.hasAttribute('inert'), inert.tagName);
    for (const live of [ui.dock, ui.popoverHost, ui.dialogHost, ui.backdrop, ui.overlay]) assert.ok(!live.hasAttribute('inert'), live.tagName);
    for (const ancestor of [ui.content, ui.shell, ui.frame, ui.body]) assert.ok(!ancestor.hasAttribute('inert'), 'ancestors of the window are never inert');
    dialogs.detach('live-1');
});

test('a modal (not live) dialog is still shown with showModal and nothing is made inert by hand', () => {
    const ui = layout();
    dialogs.update(ui.overlay, owner(), true, true, true, 'modal-1');
    assert.equal(ui.overlay.modalCount, 1); assert.equal(ui.overlay.showCount, undefined);
    assert.ok(!ui.underlay.hasAttribute('inert'));
    dialogs.detach('modal-1');
});

test('elements added beside the window while it is open become inert unless they are live', () => {
    const ui = layout();
    dialogs.update(ui.overlay, owner(), true, true, true, 'live-2', false, true);
    const lateSection = ui.content.append(new Element('SECTION'));
    const panel = ui.body.append(new Element('DIV', { popover: 'manual' }));
    const confirm = ui.body.append(new Element('DIALOG'));
    const script = ui.body.append(new Element('SCRIPT'));
    mutate();
    assert.ok(lateSection.hasAttribute('inert'));
    for (const live of [panel, confirm, script]) assert.ok(!live.hasAttribute('inert'), live.tagName + (live.attributes.has('popover') ? ' popover' : ''));
    dialogs.detach('live-2');
});

test('closing restores every inert it set, leaves an element that was already inert, and returns focus to the opener', () => {
    const ui = layout(); ui.intentDock.setAttribute('inert', ''); ui.opener.focus();
    const dotnet = owner();
    dialogs.update(ui.overlay, dotnet, true, true, true, 'live-3', false, true);
    assert.equal(document.activeElement, ui.overlay, 'focus moves into the window');
    dialogs.update(ui.overlay, dotnet, false, true, true, 'live-3', false, true);
    assert.equal(ui.overlay.open, false);
    for (const element of [ui.topNav, ui.underlay, ui.skip]) assert.ok(!element.hasAttribute('inert'), element.tagName);
    assert.ok(ui.intentDock.hasAttribute('inert'), 'what was inert before stays inert');
    assert.equal(document.activeElement, ui.opener);
    dialogs.detach('live-3');
});

test('disposal while open (navigation away) also releases the inert page', () => {
    const ui = layout();
    dialogs.update(ui.overlay, owner(), true, true, true, 'live-4', false, true);
    assert.ok(ui.underlay.hasAttribute('inert'));
    dialogs.detach('live-4');
    for (const element of [ui.topNav, ui.underlay, ui.intentDock, ui.skip]) assert.ok(!element.hasAttribute('inert'), element.tagName);
    assert.equal(ui.overlay.style.props.has('--tl-overlay-bar-inset'), false);
});

const settle = () => new Promise(resolve => setImmediate(resolve));

test('Escape is the window\'s when focus is in it or on the page body, never when it is in the player bar or a panel', async () => {
    const ui = layout(); const dotnet = owner();
    dialogs.update(ui.overlay, dotnet, true, true, true, 'live-5', false, true);
    const fire = target => { const event = press(target); document.emit('keydown', event); return event; };
    fire(ui.inside); assert.deepEqual(dotnet.calls, ['RequestCancel'], 'focus in the window');
    await settle(); dotnet.calls.length = 0;
    fire(ui.play); assert.deepEqual(dotnet.calls, [], 'focus in the player bar');
    fire(ui.dock); assert.deepEqual(dotnet.calls, []);
    const panelButton = ui.body.append(new Element('BUTTON')); fire(panelButton); assert.deepEqual(dotnet.calls, [], 'focus in a body-level panel');
    fire(document.body); assert.deepEqual(dotnet.calls, ['RequestCancel'], 'focus on the page body');
    dialogs.detach('live-5');
});

test('Escape does not close the window while a menu, a player panel or another dialog is open above it', async () => {
    const ui = layout(); const dotnet = owner();
    dialogs.update(ui.overlay, dotnet, true, true, true, 'live-6', false, true);
    const menu = ui.popoverHost.append(new Element('DIV', { 'class-open': '' }));
    document.emit('keydown', press(document.body)); assert.deepEqual(dotnet.calls, [], 'a menu is open');
    menu.setAttribute('hidden', ''); document.emit('keydown', press(document.body)); assert.deepEqual(dotnet.calls, ['RequestCancel'], 'a closed menu does not block');
    await settle(); dotnet.calls.length = 0;
    document.documentElement.setAttribute('data-playback-panel-open', 'true');
    document.emit('keydown', press(ui.inside)); assert.deepEqual(dotnet.calls, [], 'a player panel is open');
    document.documentElement.removeAttribute('data-playback-panel-open');
    const confirm = ui.dialogHost; confirm.open = true;
    document.emit('keydown', press(ui.inside)); assert.deepEqual(dotnet.calls, [], 'another dialog is open');
    confirm.open = false;
    document.emit('keydown', press(ui.inside)); assert.deepEqual(dotnet.calls, ['RequestCancel']);
    dialogs.detach('live-6');
});

test('with the search overlay open above the window Escape is search\'s, and search and the banners are not made inert', async () => {
    const ui = layout(); const dotnet = owner();
    dialogs.update(ui.overlay, dotnet, true, true, true, 'live-search', false, true);
    const search = ui.body.append(new Element('DIV', { 'data-overlay-live': 'search' }));
    const banner = ui.shell.append(new Element('DIV', { 'data-overlay-live': 'banner' }));
    mutate();
    assert.ok(!search.hasAttribute('inert')); assert.ok(!banner.hasAttribute('inert'));
    document.emit('keydown', press(document.body)); assert.deepEqual(dotnet.calls, [], 'search owns the key');
    document.emit('keydown', press(ui.inside)); assert.deepEqual(dotnet.calls, []);
    ui.body.children.splice(ui.body.children.indexOf(search), 1);
    document.emit('keydown', press(ui.inside)); assert.deepEqual(dotnet.calls, ['RequestCancel'], 'once search is closed the key is the window\'s again');
    await settle();
    dialogs.detach('live-search');
});

test('a window that fails while opening releases the inert marks as well as the scroll lock', () => {
    const ui = layout(); ui.dock.getBoundingClientRect = () => { throw new Error('layout failed'); };
    assert.throws(() => dialogs.update(ui.overlay, owner(), true, true, true, 'live-fail', false, true), /layout failed/);
    for (const element of [ui.topNav, ui.underlay, ui.intentDock, ui.skip]) assert.ok(!element.hasAttribute('inert'), element.tagName);
    assert.equal(document.documentElement.style.overflow, 'auto', 'the scroll lock is released');
    dialogs.detach('live-fail');
});

test('a live window marks itself so a menu opened from the player bar treats a click into it as an outside click', () => {
    const ui = layout();
    dialogs.update(ui.overlay, owner(), true, true, true, 'live-mark', false, true);
    assert.ok(ui.overlay.hasAttribute('data-overlay-window'));
    dialogs.detach('live-mark');
    const modal = layout();
    dialogs.update(modal.overlay, owner(), true, true, true, 'modal-mark');
    assert.ok(!modal.overlay.hasAttribute('data-overlay-window'));
    dialogs.detach('modal-mark');
});

test('a click on the backdrop element asks to close; the window stops listening when it is detached', () => {
    const ui = layout(); const dotnet = owner();
    dialogs.update(ui.overlay, dotnet, true, true, true, 'live-7', false, true);
    ui.backdrop.emit('click', {}); assert.deepEqual(dotnet.calls, ['RequestCancel']);
    const wheel = { prevented: false, preventDefault() { this.prevented = true; } };
    ui.backdrop.emit('wheel', wheel); assert.equal(wheel.prevented, true, 'the backdrop never scrolls the page under it');
    dialogs.detach('live-7');
    assert.equal(ui.backdrop.listeners.get('click').size, 0);
    assert.equal(document.listeners.get('keydown').size, 0);
});

test('the window learns how far the player bar reaches up from the bottom, and 0 when no bar is showing', () => {
    const ui = layout(); ui.dock.box = { top: 0, height: 0 };
    dialogs.update(ui.overlay, owner(), true, true, true, 'live-8', false, true);
    assert.equal(ui.overlay.style.props.get('--tl-overlay-bar-inset'), '0px');
    ui.dock.box = { top: 812, height: 88 };
    for (const resizer of resizers) if (resizer.connected) resizer.callback();
    assert.equal(ui.overlay.style.props.get('--tl-overlay-bar-inset'), '88px');
    ui.dock.box = { top: 716, height: 56 }; // a phone: the bottom navigation sits under the bar
    for (const resizer of resizers) if (resizer.connected) resizer.callback();
    assert.equal(ui.overlay.style.props.get('--tl-overlay-bar-inset'), '184px');
    dialogs.detach('live-8');
});

test('isLive names exactly what stays interactive', () => {
    assert.equal(live.isLive(new Element('DIV', { 'data-overlay-live': 'player-bar' })), true);
    assert.equal(live.isLive(new Element('DIV', { 'data-app-popover-host': '' })), true);
    assert.equal(live.isLive(new Element('DIV', { popover: 'manual' })), true);
    assert.equal(live.isLive(new Element('DIALOG')), true);
    assert.equal(live.isLive(new Element('DIV')), false);
    assert.equal(live.isLive(new Element('NAV')), false);
});
