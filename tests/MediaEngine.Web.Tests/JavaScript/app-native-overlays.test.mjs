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
    closest(selector) { if (selector === 'dialog[open]') return this instanceof Dialog && this.open ? this : this.parentElement?.closest(selector); return null; }
    focus() { this.focusCount++; document.activeElement = this; }
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
    showModal() { this.open = true; this.focus(); }
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
