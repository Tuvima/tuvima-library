import test from 'node:test';
import assert from 'node:assert/strict';

// A popover sits beside its anchor, then flips sideways or up when the screen edge leaves no room.
class Element {
    constructor() {
        this.isConnected = true; this.parentElement = null; this.style = {};
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
globalThis.innerWidth = 1200; globalThis.innerHeight = 800;
const popovers = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/app-popover.js', import.meta.url));
const edge = 8;
const dotnet = { invokeMethodAsync() { return Promise.resolve(); } };

function open(anchorRect, size, anchorOrigin, transformOrigin) {
    const anchor = new Element(), surface = new Element(), marker = new Element();
    anchor.rect = anchorRect; surface.rect = { left: 0, top: 0, ...size };
    popovers.update(marker, surface, anchor, dotnet, true, anchorOrigin, transformOrigin, false, false, 'flip');
    const placed = { left: parseFloat(surface.style.left), top: parseFloat(surface.style.top) };
    popovers.detach(surface);
    return placed;
}

const button = (left, top) => ({ left, top, width: 40, height: 40 });
const menu = { width: 220, height: 160 };

test('a left-anchored popover that fits opens with its left edge on the button\'s left edge', () => {
    const placed = open(button(300, 100), menu, 'BottomLeft', 'TopLeft');
    assert.equal(placed.left, 300);
    assert.equal(placed.top, 140);
});

test('a left-anchored popover near the right screen edge flips left so the right edges line up', () => {
    const placed = open(button(1100, 100), menu, 'BottomLeft', 'TopLeft');
    assert.equal(placed.left, 1100 + 40 - 220);
    assert.equal(placed.top, 140);
});

test('a right-anchored popover near the left screen edge flips right so the left edges line up', () => {
    const placed = open(button(10, 100), menu, 'BottomRight', 'TopRight');
    assert.equal(placed.left, 10);
});

test('a right-anchored popover that fits stays where it is', () => {
    const placed = open(button(600, 100), menu, 'BottomRight', 'TopRight');
    assert.equal(placed.left, 600 + 40 - 220);
});

test('a popover that fits on neither side is clamped inside the screen instead of flipped', () => {
    globalThis.innerWidth = 300;
    try {
        const placed = open(button(150, 100), { width: 280, height: 100 }, 'BottomLeft', 'TopLeft');
        assert.equal(placed.left, 300 - 280 - edge);
    } finally {
        globalThis.innerWidth = 1200;
    }
});

test('a popover near the bottom edge still flips up above its button', () => {
    const placed = open(button(300, 700), menu, 'BottomLeft', 'TopLeft');
    assert.equal(placed.left, 300);
    assert.equal(placed.top, 700 - 160);
});

test('a popover in the bottom right corner flips both left and up', () => {
    const placed = open(button(1100, 700), menu, 'BottomLeft', 'TopLeft');
    assert.equal(placed.left, 1100 + 40 - 220);
    assert.equal(placed.top, 700 - 160);
});

test('a centred popover is not mirrored', () => {
    const placed = open(button(580, 100), menu, 'BottomCenter', 'TopCenter');
    assert.equal(placed.left, 580 + 20 - 110);
});
