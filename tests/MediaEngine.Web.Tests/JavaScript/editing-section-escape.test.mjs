import test from 'node:test';
import assert from 'node:assert/strict';

const { shouldHandleEscape, attach, detach } = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/editing-section-escape.js', import.meta.url).href);

const noPopup = { querySelector: () => null };
const openPopup = { querySelector: selector => (selector.includes('tl-popover-open') ? {} : null) };
const plain = { tagName: 'DIV', closest: () => null };
const fieldOwner = { tagName: 'INPUT', type: 'text', closest: selector => (selector.includes('data-app-escape-owner') ? {} : null) };
const press = (target, extra = {}) => ({ key: 'Escape', defaultPrevented: false, isComposing: false, target, ...extra });

test('Esc on the page is the slot\'s to use', () => {
    assert.equal(shouldHandleEscape(press(plain), noPopup), true);
});

test('Esc with a popup open (a select list or a menu) closes the popup, not the section', () => {
    assert.equal(shouldHandleEscape(press(plain), openPopup), false);
});

test('Esc in a field that owns Esc, a select or a search or date box keeps its own meaning', () => {
    assert.equal(shouldHandleEscape(press(fieldOwner), noPopup), false);
    assert.equal(shouldHandleEscape(press({ tagName: 'SELECT', closest: () => null }), noPopup), false);
    assert.equal(shouldHandleEscape(press({ tagName: 'INPUT', type: 'search', closest: () => null }), noPopup), false);
    assert.equal(shouldHandleEscape(press({ tagName: 'INPUT', type: 'date', closest: () => null }), noPopup), false);
    assert.equal(shouldHandleEscape(press({ tagName: 'INPUT', type: 'text', closest: () => null }), noPopup), true);
});

test('other keys, handled keys and composition are never the slot\'s', () => {
    assert.equal(shouldHandleEscape(press(plain, { key: 'Enter' }), noPopup), false);
    assert.equal(shouldHandleEscape(press(plain, { defaultPrevented: true }), noPopup), false);
    assert.equal(shouldHandleEscape(press(plain, { isComposing: true }), noPopup), false);
});

test('an attached slot reports Esc to .NET once, and not after it is detached', () => {
    const handlers = {};
    const root = {
        ownerDocument: noPopup,
        addEventListener: (type, fn) => { handlers[type] = fn; },
        removeEventListener: type => { delete handlers[type]; },
    };
    const calls = [];
    const dotnet = { invokeMethodAsync: name => { calls.push(name); return Promise.resolve(); } };

    attach(root, dotnet);
    attach(root, dotnet);
    handlers.keydown(press(plain));
    handlers.keydown(press(fieldOwner));
    assert.deepEqual(calls, ['HandleEscapeFromBrowser']);

    detach(root);
    assert.equal(handlers.keydown, undefined);
});
