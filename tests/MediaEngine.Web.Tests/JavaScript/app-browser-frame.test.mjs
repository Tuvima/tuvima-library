import test from 'node:test';
import assert from 'node:assert/strict';
import { attachListKeys, detachListKeys } from '../../../src/MediaEngine.Web/wwwroot/js/app-browser-frame.js';

function key(list, name, target = list, extra = {}) {
    let prevented = false;
    list.dispatch({ key: name, target, preventDefault() { prevented = true; }, ...extra });
    return prevented;
}

function fakeList() {
    const handlers = [];
    return {
        addEventListener: (_type, handler) => handlers.push(handler),
        removeEventListener: (_type, handler) => handlers.splice(handlers.indexOf(handler), 1),
        dispatch: event => handlers.forEach(handler => handler(event)),
        handlers,
    };
}

test('the list keeps the browser from scrolling on the keys it handles itself', () => {
    const list = fakeList();
    attachListKeys(list);
    for (const name of ['ArrowUp', 'ArrowDown', 'Home', 'End', ' ']) assert.equal(key(list, name), true, name);
    assert.equal(key(list, 'Enter'), false);
    assert.equal(key(list, 'ArrowUp', list, { altKey: true }), false);
});

test('a key typed inside a field or button in a row is left alone', () => {
    const list = fakeList();
    attachListKeys(list);
    assert.equal(key(list, ' ', { tagName: 'BUTTON' }), false);
    assert.equal(key(list, 'ArrowDown', { tagName: 'INPUT' }), false);
});

test('attaching twice listens once and detaching removes the listener', () => {
    const list = fakeList();
    attachListKeys(list);
    attachListKeys(list);
    assert.equal(list.handlers.length, 1);
    detachListKeys(list);
    assert.equal(list.handlers.length, 0);
    assert.equal(key(list, 'ArrowDown'), false);
});
