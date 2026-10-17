import test from 'node:test';
import assert from 'node:assert/strict';

const { attach, detach } = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/roving-focus.js', import.meta.url).href);

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
