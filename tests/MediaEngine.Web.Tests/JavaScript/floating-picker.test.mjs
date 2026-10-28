import test from 'node:test';
import assert from 'node:assert/strict';

const { clampPosition } = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/floating-picker.js', import.meta.url).href);

test('a position inside the window is kept', () => {
    assert.deepEqual(clampPosition(100, 80, 400, 500, 1280, 800), { left: 100, top: 80 });
});

test('the panel cannot be dragged off the left or top edge', () => {
    assert.deepEqual(clampPosition(-50, -20, 400, 500, 1280, 800), { left: 0, top: 0 });
});

test('the panel cannot leave the right edge, and its header stays on screen at the bottom', () => {
    assert.deepEqual(clampPosition(1200, 790, 400, 500, 1280, 800), { left: 880, top: 752 });
});

test('a panel wider than the window sits at the left edge', () => {
    assert.deepEqual(clampPosition(40, 10, 1500, 300, 1280, 800), { left: 0, top: 10 });
});
