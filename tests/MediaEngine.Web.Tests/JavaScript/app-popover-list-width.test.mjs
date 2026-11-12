import test from 'node:test';
import assert from 'node:assert/strict';

// Dropdown lists (Look v3.1 D3): at least as wide as the control, as wide as the longest option, at most 480 px or the window less 16 px each side.
class Element {
    constructor() {
        this.isConnected = true; this.parentElement = null; this.dataset = {};
        this.attributes = new Map(); this.rect = { left: 0, top: 0, width: 0, height: 0 };
        this.written = {};
        this.style = { setProperty: (name, value, priority) => { this.written[name] = { value, priority }; }, removeProperty: name => { delete this.written[name]; } };
        this.classes = new Set();
        this.classList = { toggle: (name, on) => { if (on) this.classes.add(name); else this.classes.delete(name); } };
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
const dotnet = { invokeMethodAsync() { return Promise.resolve(); } };

function open({ anchorWidth, contentWidth, listSheet = false, phone = false }) {
    const anchor = new Element(), surface = new Element(), marker = new Element();
    anchor.rect = { left: 100, top: 100, width: anchorWidth, height: 40 };
    surface.rect = { left: 0, top: 0, width: contentWidth, height: 200 };
    surface.dataset.listWidth = 'true';
    if (listSheet) surface.dataset.listSheet = 'true';
    globalThis.matchMedia = () => ({ matches: phone });
    popovers.update(marker, surface, anchor, dotnet, true, 'BottomLeft', 'TopLeft', false, false, 'list');
    popovers.detach(surface);
    return surface;
}

test('a list is never narrower than its anchor and grows to its longest option up to 480 px', () => {
    assert.equal(popovers.listWidth(320, 100, 1200), 320);
    assert.equal(popovers.listWidth(320, 400, 1200), 400);
    const wide = popovers.listWidth(320, 2000, 1200);
    assert.ok(wide >= 320 && wide <= 480);
    assert.equal(wide, 480);
});

test('an anchor wider than 480 px gets a list exactly its own width', () => {
    assert.equal(popovers.listWidth(600, 2000, 1200), 600);
    assert.equal(popovers.listWidth(600, 100, 1200), 600);
});

test('a list is never wider than the window less 16 px each side', () => {
    assert.equal(popovers.listWidth(320, 2000, 360), 328);
    assert.equal(popovers.listWidth(200, 2000, 360), 328);
    assert.equal(popovers.listWidth(700, 2000, 360), 328);
});

test('an open dropdown writes the list width over the stylesheet, from a 60 character option', () => {
    // 60 characters at about 9 px each is the natural width of the longest option.
    const surface = open({ anchorWidth: 320, contentWidth: 60 * 9 + 32 });
    assert.deepEqual(surface.written.width, { value: '480px', priority: 'important' });
    assert.deepEqual(surface.written['min-width'], { value: '480px', priority: 'important' });
    assert.deepEqual(surface.written['max-width'], { value: '480px', priority: 'important' });
});

test('a short option list stays as wide as its dropdown', () => {
    const surface = open({ anchorWidth: 320, contentWidth: 150 });
    assert.equal(surface.written.width.value, '320px');
});

test('a list that asked for the phone Sheet becomes a full-width Sheet under 600 px and is anchored above it', () => {
    const sheet = open({ anchorWidth: 320, contentWidth: 700, listSheet: true, phone: true });
    assert.ok(sheet.classes.has('app-popout--sheet'));
    assert.equal(sheet.written.width.value, '100%');
    const anchored = open({ anchorWidth: 320, contentWidth: 700, listSheet: true, phone: false });
    assert.ok(!anchored.classes.has('app-popout--sheet'));
    assert.equal(anchored.written.width.value, '480px');
});

test('a list that did not ask for the Sheet (the autocomplete) stays anchored on a phone', () => {
    const surface = open({ anchorWidth: 300, contentWidth: 700, listSheet: false, phone: true });
    assert.ok(!surface.classes.has('app-popout--sheet'));
    assert.equal(surface.written.width.value, '480px');
});
