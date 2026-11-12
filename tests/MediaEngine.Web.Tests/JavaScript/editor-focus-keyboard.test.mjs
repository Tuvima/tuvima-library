import test from 'node:test';
import assert from 'node:assert/strict';

// The keyboard fit is shared by the editor window and the collection builder (editor v6 section 4a): each owner names itself, the listeners and both CSS
// properties stay while any owner is left, and a repeated watch or a stop from an owner that never watched changes nothing.
function listeners() {
    const map = new Map();
    return {
        add(type, fn) { if (!map.has(type)) map.set(type, new Set()); map.get(type).add(fn); },
        remove(type, fn) { map.get(type)?.delete(fn); },
        count() { let total = 0; for (const set of map.values()) total += set.size; return total; },
    };
}

const viewportListeners = listeners();
const documentListeners = listeners();
const properties = new Map();

class Element {}
globalThis.HTMLElement = Element;
globalThis.window = { innerHeight: 800, setTimeout: () => 0 };
globalThis.document = {
    documentElement: { style: { setProperty: (name, value) => properties.set(name, value), removeProperty: name => properties.delete(name) } },
    addEventListener: (type, fn) => documentListeners.add(type, fn),
    removeEventListener: (type, fn) => documentListeners.remove(type, fn),
};
// A keyboard 300 px tall covers the bottom of the layout viewport.
window.visualViewport = {
    scale: 1, height: 500, offsetTop: 0,
    addEventListener: (type, fn) => viewportListeners.add(type, fn),
    removeEventListener: (type, fn) => viewportListeners.remove(type, fn),
};

const focus = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/editor-focus.js', import.meta.url));
const attached = () => viewportListeners.count() + documentListeners.count();

test('the editor opens, the builder opens, the editor closes: the watcher stays, and closes with the last owner', () => {
    focus.watchKeyboard('editor');
    assert.ok(attached() > 0);
    assert.equal(properties.get('--tl-editor-keyboard'), '300px');
    assert.equal(properties.get('--tl-editor-safe-bottom'), '0px');

    focus.watchKeyboard('builder');
    focus.stopKeyboard('editor');
    assert.ok(attached() > 0);
    assert.equal(properties.get('--tl-editor-keyboard'), '300px');

    focus.stopKeyboard('builder');
    assert.equal(attached(), 0);
    assert.equal(properties.has('--tl-editor-keyboard'), false);
    assert.equal(properties.has('--tl-editor-safe-bottom'), false);
});

test('a second watch from the same owner adds nothing, so one stop from it ends the watch', () => {
    focus.watchKeyboard('builder');
    const afterFirst = attached();
    focus.watchKeyboard('builder');
    assert.equal(attached(), afterFirst);

    focus.stopKeyboard('builder');
    assert.equal(attached(), 0);
    assert.equal(properties.has('--tl-editor-keyboard'), false);
});

test('a stop from an owner that never watched is harmless, with or without a watcher running', () => {
    focus.stopKeyboard('nobody');
    assert.equal(attached(), 0);

    focus.watchKeyboard('editor');
    const running = attached();
    focus.stopKeyboard('nobody');
    assert.equal(attached(), running);
    assert.equal(properties.get('--tl-editor-keyboard'), '300px');

    focus.stopKeyboard('editor');
    assert.equal(attached(), 0);
});

test('a stop sent twice does not take another owner away', () => {
    focus.watchKeyboard('editor');
    focus.watchKeyboard('builder');
    focus.stopKeyboard('editor');
    focus.stopKeyboard('editor');
    assert.ok(attached() > 0);

    focus.stopKeyboard('builder');
    assert.equal(attached(), 0);
});
