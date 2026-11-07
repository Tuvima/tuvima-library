import test from 'node:test';
import assert from 'node:assert/strict';

const { attach, detach, attachMenu, detachMenu } = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/roving-focus.js', import.meta.url).href);

function row(count, { rtl = false } = {}) {
    const handlers = {};
    const items = Array.from({ length: count }, (_, index) => ({ index, focused: 0, focus() { this.focused++; globalThis.document.activeElement = this; } }));
    const container = {
        addEventListener: (type, fn) => { handlers[type] = fn; },
        removeEventListener: (type) => { delete handlers[type]; },
        querySelectorAll: () => items,
    };
    globalThis.document = { activeElement: null };
    globalThis.getComputedStyle = () => ({ direction: rtl ? 'rtl' : 'ltr' });
    const press = (key, extra = {}) => {
        const event = { key, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...extra };
        handlers.keydown?.(event);
        return event;
    };
    return { container, items, handlers, press };
}

test('arrow keys, Home and End move focus between items and stop at the ends', () => {
    const { container, items, press } = row(4);
    attach(container, '.item');
    items[1].focus();
    assert.equal(press('ArrowRight').defaultPrevented, true);
    assert.equal(document.activeElement, items[2]);
    press('End');
    assert.equal(document.activeElement, items[3]);
    press('ArrowRight');
    assert.equal(document.activeElement, items[3]);
    press('Home');
    assert.equal(document.activeElement, items[0]);
    press('ArrowLeft');
    assert.equal(document.activeElement, items[0]);
});

test('right to left rows swap the arrow keys', () => {
    const { container, items, press } = row(3, { rtl: true });
    attach(container, '.item');
    items[1].focus();
    press('ArrowLeft');
    assert.equal(document.activeElement, items[2]);
    press('ArrowRight');
    assert.equal(document.activeElement, items[1]);
});

test('other keys, modified keys and focus outside the row are ignored', () => {
    const { container, items, press } = row(3);
    attach(container, '.item');
    assert.equal(press('ArrowRight').defaultPrevented, false, 'focus is not inside the row');
    items[0].focus();
    assert.equal(press('Enter').defaultPrevented, false);
    assert.equal(press('ArrowRight', { ctrlKey: true }).defaultPrevented, false);
    assert.equal(document.activeElement, items[0]);
});

test('detach removes the key handler', () => {
    const { container, handlers } = row(2);
    attach(container, '.item');
    assert.ok(handlers.keydown);
    detach(container);
    assert.equal(handlers.keydown, undefined);
});

test('a vertical list also moves with Up and Down, and a horizontal row ignores them', () => {
    const column = row(3);
    attach(column.container, '.item', { vertical: true });
    column.items[0].focus();
    assert.equal(column.press('ArrowDown').defaultPrevented, true);
    assert.equal(document.activeElement, column.items[1]);
    column.press('ArrowRight');
    assert.equal(document.activeElement, column.items[2]);
    column.press('ArrowUp');
    assert.equal(document.activeElement, column.items[1]);

    const strip = row(3);
    attach(strip.container, '.item');
    strip.items[0].focus();
    assert.equal(strip.press('ArrowDown').defaultPrevented, false);
    assert.equal(document.activeElement, strip.items[0]);
});

// An actions menu: a hidden anchor inside the menu finds it, and the menuitem rows that can take focus move with Up, Down, Home and End.
function menu(count) {
    const column = row(count);
    const element = { ...column.container, closest: selector => (selector === '[role=menu]' ? element : null) };
    const anchor = { closest: selector => (selector === '[role=menu]' ? element : null) };
    return { ...column, element, anchor };
}

test('attachMenu gives the menu holding the anchor arrow-key movement down its menuitem rows', () => {
    const { element, anchor, items, press } = menu(4);
    let selector = null;
    element.querySelectorAll = value => { selector = value; return items; };
    attachMenu(anchor);
    assert.equal(selector, null, 'rows are looked up when a key is pressed, not when the menu attaches');
    items[0].focus();
    assert.equal(press('ArrowDown').defaultPrevented, true);
    assert.equal(document.activeElement, items[1]);
    press('End');
    assert.equal(document.activeElement, items[3]);
    press('ArrowUp');
    assert.equal(document.activeElement, items[2]);
    press('Home');
    assert.equal(document.activeElement, items[0]);
    assert.equal(selector, '[role=menuitem]:not(:disabled)');
});

test('attachMenu with an anchor outside any menu does nothing, and detachMenu removes the handler', () => {
    attachMenu({ closest: () => null });
    attachMenu(null);
    const { element, anchor, handlers } = menu(2);
    attachMenu(anchor);
    assert.ok(handlers.keydown);
    detachMenu(anchor);
    assert.equal(handlers.keydown, undefined);
    detachMenu(anchor);
    assert.ok(element);
});
