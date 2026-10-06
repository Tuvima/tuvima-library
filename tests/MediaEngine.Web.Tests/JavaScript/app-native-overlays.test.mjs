import test from 'node:test';
import assert from 'node:assert/strict';

class Element {
    constructor() {
        this.isConnected = true; this.parentElement = null; this.children = [];
        this.style = { overflow: 'auto', paddingRight: '4px', scrollbarGutter: 'stable' };
        this.listeners = new Map(); this.attributes = new Map(); this.focusCount = 0;
    }
    addEventListener(name, fn) { if (!this.listeners.has(name)) this.listeners.set(name, new Set()); this.listeners.get(name).add(fn); }
    removeEventListener(name, fn) { this.listeners.get(name)?.delete(fn); }
    emit(name, event) { for (const fn of this.listeners.get(name) ?? []) fn(event); }
    append(child) { this.children.push(child); child.parentElement = this; }
    contains(target) { return target === this || this.children.some(child => child.contains(target)); }
    closest(selector) {
        if (selector === 'dialog[open]') return this instanceof Dialog && this.open ? this : this.parentElement?.closest(selector);
        if (selector === '[data-app-escape-owner="field"]') return this.attributes.get('data-app-escape-owner') === 'field' ? this : this.parentElement?.closest(selector);
        return null;
    }
    focus() {
        this.focusCount++; document.activeElement = this;
        for (let element = this; element; element = element.parentElement) element.emit('focusin', { target: this });
    }
    querySelector() { return null; }
    querySelectorAll() { return []; }
    setAttribute(name, value) { this.attributes.set(name, value); }
    hasAttribute(name) { return this.attributes.has(name); }
    matches(selector) { return selector === ':popover-open' && this.popoverOpen; }
    showPopover() { this.popoverOpen = true; }
    hidePopover() { this.popoverOpen = false; }
    getBoundingClientRect() { return { left: 200, top: 200, right: 300, bottom: 240, width: 100, height: 40 }; }
}
class Dialog extends Element {
    showModal() { this.showCount = (this.showCount ?? 0) + 1; this.open = true; this.focus(); }
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
globalThis.ResizeObserver = class { observe() {} disconnect() {} };
globalThis.document = new Element(); document.documentElement = new Element(); document.documentElement.clientWidth = 1180;
document.body = new Element(); document.activeElement = document.body;
globalThis.window = new Element(); globalThis.innerWidth = 1200; globalThis.innerHeight = 800;
globalThis.getComputedStyle = element => element.style;
const root = new URL('../../../src/MediaEngine.Web/wwwroot/js/', import.meta.url);
const dialogs = await import(new URL('app-dialog.js', root));
const popovers = await import(new URL('app-popover.js', root));
const focusTraps = await import(new URL('app-focus-trap.js', root));
function owner() { const calls = []; return { calls, invokeMethodAsync(name) { calls.push(name); return Promise.resolve(); } }; }
function removal() { for (const observer of [...observers]) if (observer.connected) observer.callback(); }

test('nested modal removal restores its parent trigger before .NET disposal and leaves parent open', () => {
    const pageTrigger = new Element(), parent = new Dialog(), pickerTrigger = new Element(), child = new Dialog();
    parent.append(pickerTrigger); pageTrigger.focus();
    dialogs.update(parent, owner(), true, true, true, 'parent'); pickerTrigger.focus();
    const childOwner = owner(); dialogs.update(child, childOwner, true, true, true, 'child');
    const cancel = { preventDefault() { this.prevented = true; } }; child.emit('cancel', cancel);
    assert.equal(cancel.prevented, true); assert.deepEqual(childOwner.calls, ['RequestCancel']);
    assert.equal(child.open, true, 'native cancellation waits for the application guard');
    child.isConnected = false; removal();
    assert.equal(document.activeElement, pickerTrigger); assert.equal(parent.open, true);
    const focusCount = pickerTrigger.focusCount; dialogs.detach('child');
    assert.equal(pickerTrigger.focusCount, focusCount, 'late disposal cannot steal focus a second time');
    dialogs.detach('parent'); assert.equal(document.activeElement, pageTrigger);
    assert.equal(document.documentElement.style.overflow, 'auto'); assert.equal(document.documentElement.style.paddingRight, '4px');
});

test('frame identity cleanup works after the DOM reference is gone', () => {
    const trigger = new Element(), dialog = new Dialog(); trigger.focus();
    dialogs.update(dialog, owner(), true, true, true, 'missing-reference');
    dialog.isConnected = false; dialogs.detach('missing-reference');
    assert.equal(document.activeElement, trigger); assert.equal(dialog.listeners.get('cancel').size, 0);
    removal();
});

test('guard-vetoed native Escape leaves the modal and focus intact', () => {
    const dialog = new Dialog(), dialogOwner = owner();
    dialogs.update(dialog, dialogOwner, true, true, true, 'veto');
    dialog.emit('cancel', { preventDefault() {} });
    assert.equal(dialog.open, true); assert.equal(document.activeElement, dialog);
    dialogs.detach('veto');
});

function escape(target, overrides = {}) {
    return { key: 'Escape', target, defaultPrevented: false,
        preventDefault() { this.defaultPrevented = true; }, ...overrides };
}

test('native close recovery restores the previous field after showModal moves focus', () => {
    const dialog = new Dialog(), field = new Element(), dialogOwner = owner();
    dialog.append(field);
    dialogs.update(dialog, dialogOwner, true, true, false, 'recovery-focus');
    field.focus();
    dialog.close(); dialog.emit('close');
    assert.equal(dialog.open, true);
    assert.equal(document.activeElement, field);
    assert.deepEqual(dialogOwner.calls, []);
    dialogs.detach('recovery-focus');
});

test('repeated Escape cannot close an escape-disabled modal or duplicate its lock', () => {
    const dialog = new Dialog(), dialogOwner = owner();
    dialogs.update(dialog, dialogOwner, true, true, false, 'disabled-escape');
    assert.equal(dialog.attributes.get('closedby'), 'none');
    for (let index = 0; index < 2; index++) {
        const key = escape(dialog); dialog.emit('keydown', key);
        assert.equal(key.defaultPrevented, true);
    }
    assert.deepEqual(dialogOwner.calls, []); assert.equal(dialog.open, true);
    dialog.close(); dialog.emit('close');
    assert.equal(dialog.open, true); assert.equal(dialog.showCount, 2);
    assert.equal(document.documentElement.style.paddingRight, '24px');
    dialogs.update(dialog, dialogOwner, false, true, false, 'disabled-escape');
    dialog.emit('close'); assert.equal(dialog.open, false, 'expected close is not recovered');
    assert.equal(document.documentElement.style.overflow, 'auto');
    dialogs.detach('disabled-escape');
});

test('keydown, fallback cancel, and unexpected close coalesce while the close guard is pending', async () => {
    const dialog = new Dialog(), calls = []; let finish;
    const dotnet = { invokeMethodAsync(name) { calls.push(name); return new Promise(resolve => { finish = resolve; }); } };
    dialogs.update(dialog, dotnet, true, true, true, 'pending-guard');
    dialog.emit('keydown', escape(dialog));
    dialog.emit('cancel', { preventDefault() {} });
    dialog.close(); dialog.emit('close');
    assert.deepEqual(calls, ['RequestCancel']); assert.equal(dialog.open, true);
    finish(); await new Promise(resolve => setImmediate(resolve));
    dialog.emit('keydown', escape(dialog)); assert.equal(calls.length, 2);
    finish(); dialogs.detach('pending-guard');
});

test('only the topmost modal consumes Escape and already consumed/composing keys are ignored', () => {
    const parent = new Dialog(), child = new Dialog(), parentOwner = owner(), childOwner = owner();
    dialogs.update(parent, parentOwner, true, true, true, 'key-parent');
    dialogs.update(child, childOwner, true, true, true, 'key-child');
    const parentKey = escape(parent); parent.emit('keydown', parentKey);
    assert.equal(parentKey.defaultPrevented, false); assert.deepEqual(parentOwner.calls, []);
    child.emit('keydown', escape(child, { defaultPrevented: true }));
    const composing = escape(child, { isComposing: true }); child.emit('keydown', composing);
    assert.equal(composing.defaultPrevented, false); assert.deepEqual(childOwner.calls, []);
    child.emit('keydown', escape(child)); assert.deepEqual(childOwner.calls, ['RequestCancel']);
    dialogs.detach('key-child'); dialogs.detach('key-parent');
});

test('inline fields receive Escape without also requesting dialog cancellation', () => {
    const dialog = new Dialog(), field = new Element(), input = new Element(), dotnet = owner();
    field.setAttribute('data-app-escape-owner', 'field'); field.append(input); dialog.append(field);
    dialogs.update(dialog, dotnet, true, true, true, 'inline-key');
    const key = escape(input); dialog.emit('keydown', key);
    assert.equal(key.defaultPrevented, true, 'browser close is prevented without stopping field dispatch');
    assert.deepEqual(dotnet.calls, []);
    input.focus(); dialog.emit('cancel', { preventDefault() {} }); assert.deepEqual(dotnet.calls, []);
    dialogs.detach('inline-key');
});

test('native select and search controls keep their own Escape behaviour', () => {
    const dialog = new Dialog(), dotnet = owner(); dialogs.update(dialog, dotnet, true, true, true, 'native-key');
    for (const [tagName, type] of [['SELECT', 'select-one'], ['INPUT', 'search'], ['INPUT', 'date']]) {
        const input = new Element(); Object.assign(input, { tagName, type }); dialog.append(input);
        const key = escape(input); dialog.emit('keydown', key); assert.equal(key.defaultPrevented, false);
        assert.deepEqual(dotnet.calls, []);
    }
    dialogs.detach('native-key');
});

test('a first-party popover consumes Escape before the dialog capture handler', () => {
    const dialog = new Dialog(), trigger = new Element(), marker = new Element(), surface = new Element(), dotnet = owner(), popupOwner = owner();
    dialog.append(trigger); dialog.append(marker); dialog.append(surface);
    dialogs.update(dialog, dotnet, true, true, true, 'popup-key');
    popovers.update(marker, surface, trigger, popupOwner, true, 'BottomLeft', 'TopLeft', false, false, 'popup-key');
    const key = escape(surface, { stopImmediatePropagation() { this.stopped = true; } });
    document.emit('keydown', key); if (!key.stopped) dialog.emit('keydown', key);
    assert.equal(key.stopped, true); assert.deepEqual(popupOwner.calls, ['Dismiss']); assert.deepEqual(dotnet.calls, []);
    popovers.detach(surface); dialogs.detach('popup-key');
});

test('an unexpected child close restores its focused input and releases only its own lock on disposal', () => {
    const parent = new Dialog(), child = new Dialog(), input = new Element(); child.append(input);
    dialogs.update(parent, owner(), true, true, true, 'recovery-parent');
    dialogs.update(child, owner(), true, true, false, 'recovery-child');
    input.focus(); child.emit('focusin', { target: input }); child.close(); child.emit('close');
    assert.equal(document.activeElement, input); assert.equal(child.open, true);
    dialogs.detach('recovery-child'); child.emit('close');
    assert.equal(child.open, false); assert.equal(parent.open, true);
    assert.equal(document.documentElement.style.overflow, 'hidden');
    dialogs.detach('recovery-parent'); assert.equal(document.documentElement.style.overflow, 'auto');
});

test('closing an older modal cannot steal focus from a newer unrelated modal', () => {
    const first = new Dialog(), second = new Dialog(), pageTrigger = new Element(); pageTrigger.focus();
    dialogs.update(first, owner(), true, true, true, 'older');
    dialogs.update(second, owner(), true, true, true, 'newer');
    // Real browsers retain focus in the topmost modal when the older one closes.
    first.close = () => { first.open = false; };
    dialogs.detach('older'); assert.equal(document.activeElement, second);
    first.isConnected = false; dialogs.detach('newer');
});

test('inline modal close restores focus once and later disposal retains the new destination', () => {
    const trigger = new Element(), destination = new Element(), dialog = new Dialog(); trigger.focus();
    dialogs.update(dialog, owner(), true, true, true, 'inline');
    dialogs.update(dialog, owner(), false, true, true, 'inline'); assert.equal(document.activeElement, trigger);
    destination.focus(); dialogs.detach('inline'); assert.equal(document.activeElement, destination);
});

test('parent popover yields child-dialog Escape and restores its own trigger on its Escape', () => {
    const parent = new Dialog(), child = new Dialog(), trigger = new Element(), marker = new Element(), surface = new Element(), childInput = new Element();
    parent.open = child.open = true; parent.append(trigger); parent.append(marker); parent.append(surface); child.append(childInput);
    const popupOwner = owner(); popovers.update(marker, surface, trigger, popupOwner, true, 'BottomLeft', 'TopLeft', false, false, 'popup');
    const nestedEvent = { key: 'Escape', target: childInput, preventDefault() { this.prevented = true; }, stopImmediatePropagation() {} };
    document.emit('keydown', nestedEvent);
    assert.equal(nestedEvent.prevented, undefined); assert.deepEqual(popupOwner.calls, []);
    const ownEvent = { key: 'Escape', target: surface, preventDefault() { this.prevented = true; }, stopImmediatePropagation() {} };
    document.emit('keydown', ownEvent);
    assert.equal(ownEvent.prevented, true); assert.deepEqual(popupOwner.calls, ['Dismiss']); assert.equal(document.activeElement, trigger);
    popovers.detach(surface);
});

test('removed popup releases document Escape handling even when .NET disposal has no element', () => {
    const trigger = new Element(), marker = new Element(), surface = new Element(), popupOwner = owner();
    popovers.update(marker, surface, trigger, popupOwner, true, 'BottomLeft', 'TopLeft', false, false, 'removed-popup');
    surface.isConnected = false; removal();
    const event = { key: 'Escape', target: trigger, preventDefault() { this.prevented = true; }, stopImmediatePropagation() {} };
    document.emit('keydown', event);
    assert.equal(event.prevented, undefined); assert.deepEqual(popupOwner.calls, []);
    assert.equal(surface.popoverOpen, false);
});

test('removed focus trap restores opener and releases global focus handlers before late disposal', () => {
    const opener = new Element(), root = new Element(); opener.focus();
    focusTraps.update(root, true, false); root.focus(); root.isConnected = false; removal();
    assert.equal(document.activeElement, opener);
    const before = opener.focusCount; focusTraps.detach(null);
    assert.equal(opener.focusCount, before);
    assert.equal(document.listeners.get('focusin').size, 0);
});
