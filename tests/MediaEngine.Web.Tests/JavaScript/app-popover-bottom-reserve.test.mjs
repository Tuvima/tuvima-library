import test from 'node:test';
import assert from 'node:assert/strict';

// A popover that names a bottom reserve (the Reorder bar's height, editor v6 D23) keeps clear of it: it is no taller than the room under its anchor
// and never sits in the reserved strip. Without a reserve nothing changes.
class Element {
    constructor() {
        this.isConnected = true; this.parentElement = null; this.style = {}; this.vars = {};
        this.attributes = new Map(); this.rect = { left: 0, top: 0, width: 0, height: 0 };
    }
    addEventListener() {}
    removeEventListener() {}
    contains(target) { return target === this; }
    closest() { return null; }
    querySelector() { return null; }
    hasAttribute(name) { return this.attributes.has(name); }
    matches(selector) { return selector === ':popover-open' && this.popoverOpen; }
    showPopover() { this.popoverOpen = true; }
    hidePopover() { this.popoverOpen = false; }
    getBoundingClientRect() {
        const { left, top, width, height } = this.rect;
        return { left, top, width, height, right: left + width, bottom: top + height };
    }
}
globalThis.HTMLElement = Element;
globalThis.MutationObserver = class { observe() {} disconnect() {} };
globalThis.ResizeObserver = class { observe() {} disconnect() {} };
globalThis.document = new Element(); document.documentElement = new Element();
globalThis.window = new Element();
globalThis.getComputedStyle = element => ({ getPropertyValue: name => element.vars[name] ?? '' });
globalThis.innerWidth = 1280; globalThis.innerHeight = 720;
const popovers = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/app-popover.js', import.meta.url));
const edge = 8;
const dotnet = { invokeMethodAsync() { return Promise.resolve(); } };

function open(reserve, anchorRect, height) {
    const anchor = new Element(), surface = new Element(), marker = new Element();
    anchor.rect = anchorRect; surface.rect = { left: 0, top: 0, width: 400, height };
    if (reserve) surface.vars['--tl-popover-bottom-reserve'] = `${reserve}px`;
    popovers.update(marker, surface, anchor, dotnet, true, 'BottomLeft', 'TopLeft', false, false, 'reserve');
    const placed = { top: parseFloat(surface.style.top), maxHeight: parseFloat(surface.style.maxHeight) };
    popovers.detach(surface);
    return placed;
}

const trigger = { left: 300, top: 160, width: 300, height: 44 };

test('without a reserve the popover may use the whole screen', () => {
    const placed = open(0, trigger, 300);
    assert.equal(placed.maxHeight, 720 - edge * 2);
    assert.equal(placed.top, 204);
});

test('with a reserve the popover is no taller than the room between its anchor and the reserve', () => {
    const placed = open(72, trigger, 300);
    assert.equal(placed.maxHeight, 720 - edge - 72 - 204);
});

test('with a reserve a popover too tall for the room under its anchor is lifted clear of the reserved strip', () => {
    const placed = open(72, trigger, 500);
    assert.ok(placed.top + 500 <= 720 - edge - 72);
});

test('maxSurfaceHeight ignores the room under the anchor when almost none is left', () => {
    assert.equal(popovers.maxSurfaceHeight(720, edge, 72, 600), 720 - edge * 2 - 72);
});

test('a missing, zero or unreadable reserve counts as none', () => {
    const surface = new Element();
    assert.equal(popovers.bottomReserve(surface), 0);
    surface.vars['--tl-popover-bottom-reserve'] = '0px';
    assert.equal(popovers.bottomReserve(surface), 0);
    surface.vars['--tl-popover-bottom-reserve'] = 'abc';
    assert.equal(popovers.bottomReserve(surface), 0);
    surface.vars['--tl-popover-bottom-reserve'] = ' 72px';
    assert.equal(popovers.bottomReserve(surface), 72);
});
