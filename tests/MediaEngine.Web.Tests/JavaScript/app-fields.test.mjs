import test from 'node:test';
import assert from 'node:assert/strict';
import { attachKeyboard, detachKeyboard } from '../../../src/MediaEngine.Web/wwwroot/js/app-fields.js';

test('autocomplete preserves closed Home and End editing but owns open list navigation', () => {
    let handler;
    const root = {
        addEventListener: (_event, callback) => { handler = callback; },
        removeEventListener: (_event, callback) => { assert.equal(callback, handler); handler = undefined; }
    };
    const target = {
        closest: () => null,
        matches: () => true,
        getAttribute: () => 'false'
    };
    attachKeyboard(root);
    for (const key of ['Home', 'End']) {
        let prevented = false;
        handler({ key, target, preventDefault: () => { prevented = true; } });
        assert.equal(prevented, false);
    }
    target.getAttribute = () => 'true';
    for (const key of ['Home', 'End', 'ArrowUp', 'ArrowDown', 'Enter', 'Escape']) {
        let prevented = false;
        handler({ key, target, preventDefault: () => { prevented = true; } });
        assert.equal(prevented, true);
    }
    detachKeyboard(root);
    assert.equal(handler, undefined);
});
