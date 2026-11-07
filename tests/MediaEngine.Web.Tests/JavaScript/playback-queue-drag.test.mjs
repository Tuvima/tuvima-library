import test from 'node:test';
import assert from 'node:assert/strict';

const { focusHandle } = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-queue-drag.js', import.meta.url).href);

function queue(ids) {
    const handles = ids.map(id => ({ dataset: { playbackQueueDrag: id }, focused: 0, focus() { this.focused++; } }));
    const scroll = { focused: 0, focus() { this.focused++; } };
    const root = {
        querySelectorAll: selector => (selector === '[data-playback-queue-drag]' ? handles : []),
        querySelector: selector => (selector === '[data-playback-list-scroll]' ? scroll : null),
    };
    return { root, handles, scroll };
}

test('focusHandle puts focus on the drag handle of the row with that queue entry id', () => {
    const { root, handles, scroll } = queue(['a', 'b', 'c']);
    assert.equal(focusHandle(root, 'b'), true);
    assert.deepEqual(handles.map(handle => handle.focused), [0, 1, 0]);
    assert.equal(scroll.focused, 0);
});

test('focusHandle falls back to the scrolling list when there is no id or no such row, so focus never drops to the page', () => {
    const { root, handles, scroll } = queue(['a']);
    assert.equal(focusHandle(root, null), true);
    assert.equal(focusHandle(root, 'gone'), true);
    assert.equal(scroll.focused, 2);
    assert.equal(handles[0].focused, 0);
});

test('focusHandle reports false when there is nothing to focus', () => {
    assert.equal(focusHandle(null, 'a'), false);
    assert.equal(focusHandle({ querySelectorAll: () => [], querySelector: () => null }, 'a'), false);
});
