// Run with: node --test tests/MediaEngine.Web.Tests/JavaScript
// Covers the volume slider's reveal (Unified player v2 D10): hover or keyboard focus opens it, it stays open while the pointer or focus is on it
// and while the handle is held, and it closes 400 ms after both have left.
import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const volume = await import(pathToFileURL(path.resolve(import.meta.dirname, '../../../src/MediaEngine.Web/wwwroot/js/playback-volume.js')).href);

function clock() {
    let now = 0, nextId = 1;
    const timers = new Map();
    return {
        setTimer: (fn, ms) => { const id = nextId++; timers.set(id, { at: now + ms, fn }); return id; },
        clearTimer: id => { timers.delete(id); },
        advance(ms) {
            now += ms;
            for (const [id, t] of [...timers]) if (t.at <= now) { timers.delete(id); t.fn(); }
        },
        get pending() { return timers.size; },
    };
}

function reveal(time) {
    const log = [];
    const control = volume.createReveal({ show: () => log.push('show'), hide: () => log.push('hide'), setTimer: time.setTimer, clearTimer: time.clearTimer });
    return { control, log };
}

test('the linger is 400 milliseconds', () => {
    assert.equal(volume.LINGER_MS, 400);
});

test('hovering opens the slider at once and it closes 400 ms after the pointer leaves, not before', () => {
    const time = clock();
    const { control, log } = reveal(time);

    control.pointerEnter();
    assert.deepEqual(log, ['show']);
    assert.ok(control.isOpen);

    control.pointerLeave();
    time.advance(399);
    assert.ok(control.isOpen);
    assert.deepEqual(log, ['show']);
    time.advance(1);
    assert.ok(!control.isOpen);
    assert.deepEqual(log, ['show', 'hide']);
});

test('coming back during the linger keeps it open', () => {
    const time = clock();
    const { control, log } = reveal(time);

    control.pointerEnter();
    control.pointerLeave();
    time.advance(250);
    control.pointerEnter();
    time.advance(1000);

    assert.ok(control.isOpen);
    assert.deepEqual(log, ['show']);
    assert.equal(time.pending, 0);
});

test('keyboard focus opens it and keeps it open while focus stays, even after the pointer has gone', () => {
    const time = clock();
    const { control, log } = reveal(time);

    control.focusIn();
    control.pointerEnter();
    control.pointerLeave();
    time.advance(5000);
    assert.ok(control.isOpen);

    control.focusOut();
    time.advance(400);
    assert.deepEqual(log, ['show', 'hide']);
});

test('the pointer leaving while the handle is held does not close it until the handle is let go', () => {
    const time = clock();
    const { control, log } = reveal(time);

    control.pointerEnter();
    control.press();
    control.pointerLeave();
    time.advance(2000);
    assert.ok(control.isOpen);

    control.release();
    time.advance(400);
    assert.deepEqual(log, ['show', 'hide']);
});

test('letting go of something else while closed starts no timer', () => {
    const time = clock();
    const { control } = reveal(time);

    control.release();
    assert.equal(time.pending, 0);
});

function fakeRoot() {
    const listeners = {};
    return {
        dataset: {},
        listeners,
        addEventListener(name, fn) { (listeners[name] ??= []).push(fn); },
        removeEventListener(name, fn) { listeners[name] = (listeners[name] ?? []).filter(f => f !== fn); },
        contains: node => node === 'inside',
        fire(name, event = {}) { (listeners[name] ?? []).forEach(fn => fn(event)); },
    };
}

test('the control carries the open state as one attribute for the stylesheet', () => {
    globalThis.window = globalThis;
    globalThis.addEventListener = () => { };
    globalThis.removeEventListener = () => { };
    const root = fakeRoot();
    volume.attach(root);

    root.fire('pointerenter', { pointerType: 'mouse' });
    assert.equal(root.dataset.open, 'true');
    root.fire('pointerleave', { pointerType: 'mouse' });
    volume.detach(root);
    assert.equal(root.dataset.open, undefined);
    assert.equal((root.listeners.pointerenter ?? []).length, 0);
});

test('a finger never reveals the slider, and focus moving inside the control does not close it', () => {
    globalThis.window = globalThis;
    globalThis.addEventListener = () => { };
    globalThis.removeEventListener = () => { };
    const root = fakeRoot();
    volume.attach(root);

    root.fire('pointerenter', { pointerType: 'touch' });
    assert.equal(root.dataset.open, undefined);

    root.fire('focusin', { target: { matches: () => true } });
    assert.equal(root.dataset.open, 'true');
    root.fire('focusout', { relatedTarget: 'inside' });
    // focus moved to the slider inside the control: still open, no linger started
    assert.equal(root.dataset.open, 'true');
    volume.detach(root);
});

test('mouse focus on the button does not hold the slider open', () => {
    globalThis.window = globalThis;
    globalThis.addEventListener = () => { };
    globalThis.removeEventListener = () => { };
    const root = fakeRoot();
    volume.attach(root);

    root.fire('focusin', { target: { matches: () => false } });
    assert.equal(root.dataset.open, undefined);
    volume.detach(root);
});
