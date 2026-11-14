import test from 'node:test';
import assert from 'node:assert/strict';
import { fitCount } from '../../../src/MediaEngine.Web/wwwroot/js/working-now-fit.js';

test('Working now counts only whole tiles that fit across the row', () => {
    // 107 px tiles with a 12 px gap: n tiles need n * 107 + (n - 1) * 12.
    assert.equal(fitCount(1100, 107, 12), 9);
    assert.equal(fitCount(1099, 107, 12), 9);
    assert.equal(fitCount(118, 107, 12), 1);
    assert.equal(fitCount(226, 107, 12), 2);
    assert.equal(fitCount(225, 107, 12), 1);
});

test('Working now on a phone fits three or four small tiles and never fewer than one', () => {
    assert.equal(fitCount(328, 76, 8), 4);
    assert.equal(fitCount(288, 76, 8), 3);
    assert.equal(fitCount(10, 76, 8), 1);
    assert.equal(fitCount(0, 76, 8), 1);
    assert.equal(fitCount(500, Number.NaN, 8), 1);
});
