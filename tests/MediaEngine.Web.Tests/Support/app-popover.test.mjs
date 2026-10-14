import { test } from 'node:test';
import assert from 'node:assert/strict';

// Blazor resolves an element reference to null once its element has left the page. A reload can do that while
// this module is still loading, so `update` must return cleanly instead of throwing into the Blazor circuit.
const popover = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/app-popover.js', import.meta.url));

const dotnet = { invokeMethodAsync: () => Promise.resolve() };
const call = (marker, surface, anchor = null) =>
    popover.update(marker, surface, anchor, dotnet, true, 'BottomLeft', 'TopLeft', false, false, 'host');

test('missing marker and surface references are ignored', () => {
    assert.doesNotThrow(() => call(null, null));
    assert.doesNotThrow(() => call(undefined, undefined));
});

test('a surface that has already left the page is ignored and can be detached again', () => {
    const surface = { isConnected: false, matches: () => false, hidePopover() {} };
    assert.doesNotThrow(() => call({ parentElement: null }, surface));
    assert.doesNotThrow(() => popover.detach(surface));
});

test('a marker that is gone does not stop a connected surface from being tracked', () => {
    const surface = { isConnected: true, matches: () => false, hidePopover() {}, showPopover() {}, style: {}, hasAttribute: () => true, querySelector: () => null, getBoundingClientRect: () => ({ width: 0, height: 0 }), contains: () => false };
    globalThis.MutationObserver = class { observe() {} disconnect() {} };
    globalThis.ResizeObserver = class { observe() {} disconnect() {} };
    globalThis.document = { documentElement: {}, addEventListener() {}, removeEventListener() {} };
    globalThis.window = { addEventListener() {}, removeEventListener() {} };
    globalThis.addEventListener = () => {}; globalThis.removeEventListener = () => {};
    assert.doesNotThrow(() => call(null, surface));
    assert.doesNotThrow(() => popover.detach(surface));
});
