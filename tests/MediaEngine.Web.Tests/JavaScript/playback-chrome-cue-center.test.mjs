import test from 'node:test';
import assert from 'node:assert/strict';
import { centerCue } from '../../../src/MediaEngine.Web/wwwroot/js/playback-chrome.js';

// A subtitle with no position of its own is centered across the picture, whatever cue box the browser built.
const cue = (over = {}) => ({ position: 'auto', positionAlign: 'center', size: 62, align: 'start', ...over });

test('a cue with no position of its own is centered across the whole picture', () => {
    const target = cue();
    centerCue(target, { position: 'auto' });
    assert.deepEqual(target, { position: 'auto', positionAlign: 'auto', size: 100, align: 'center' });
});

test('a cue that names its own position keeps it', () => {
    const target = cue({ position: 20 });
    centerCue(target, { position: 20 });
    assert.equal(target.size, 62);
    assert.equal(target.align, 'start');
});
