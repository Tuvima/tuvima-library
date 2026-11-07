import test from 'node:test';
import assert from 'node:assert/strict';
import { AUTO_HIDE_MS, autoHideDelay, chromeHeld } from '../../../src/MediaEngine.Web/wwwroot/js/playback-chrome.js';

// Unified player v2 D25: while playing the controls fade after three seconds of inactivity, and never while a panel is open, a control has
// focus, the pointer is over the controls, or the video is paused. The decision is a pure function of those facts.
const playingAndIdle = () => ({ panelOpen: false, holds: 0, pressing: false, paused: false, ended: false, loading: false, error: false, controlsHovered: false, controlFocused: false });

test('controls fade after three seconds when the video plays and nothing holds them', () => {
    assert.equal(AUTO_HIDE_MS, 3000);
    assert.equal(chromeHeld(playingAndIdle()), false);
    assert.equal(autoHideDelay(playingAndIdle()), 3000);
});

test('controls never fade while a panel is open', () => {
    assert.equal(chromeHeld({ ...playingAndIdle(), panelOpen: true }), true);
    assert.equal(autoHideDelay({ ...playingAndIdle(), panelOpen: true }), null);
});

test('controls never fade while a control has focus', () => {
    assert.equal(chromeHeld({ ...playingAndIdle(), controlFocused: true }), true);
    assert.equal(autoHideDelay({ ...playingAndIdle(), controlFocused: true }), null);
});

test('controls never fade while the pointer is over them or a finger is held on one', () => {
    assert.equal(autoHideDelay({ ...playingAndIdle(), controlsHovered: true }), null);
    assert.equal(autoHideDelay({ ...playingAndIdle(), pressing: true }), null);
});

test('controls stay while the video is paused, loading, ended or broken, and while another hold is active', () => {
    for (const change of [{ paused: true }, { loading: true }, { ended: true }, { error: true }, { holds: 1 }]) {
        assert.equal(autoHideDelay({ ...playingAndIdle(), ...change }), null, JSON.stringify(change));
    }
});

test('closing the panel lets the three second timer run again', () => {
    const open = { ...playingAndIdle(), panelOpen: true };
    assert.equal(autoHideDelay(open), null);
    assert.equal(autoHideDelay({ ...open, panelOpen: false }), 3000);
});
