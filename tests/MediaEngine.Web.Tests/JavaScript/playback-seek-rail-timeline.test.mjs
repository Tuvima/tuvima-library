// Run with: node --test tests/MediaEngine.Web.Tests/JavaScript
// Covers the timeline's keyboard and drag behaviour (Unified player v2 D9): arrows seek 5 s, Shift+arrow 30 s, a seek never leaves the loaded
// duration, and the bookmark ticks hide while the handle is dragged.
import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const rail = await import(pathToFileURL(path.resolve(import.meta.dirname, '../../../src/MediaEngine.Web/wwwroot/js/playback-seek-rail.js')).href);

test('arrow keys seek 5 seconds and Shift+arrow 30, back with Left or Down and forward with Right or Up', () => {
    assert.equal(rail.keySeekDelta('ArrowRight', false), 5);
    assert.equal(rail.keySeekDelta('ArrowUp', false), 5);
    assert.equal(rail.keySeekDelta('ArrowLeft', false), -5);
    assert.equal(rail.keySeekDelta('ArrowDown', false), -5);
    assert.equal(rail.keySeekDelta('ArrowRight', true), 30);
    assert.equal(rail.keySeekDelta('ArrowLeft', true), -30);
    assert.equal(rail.keySeekDelta('Home', false), null);
    assert.equal(rail.keySeekDelta('a', false), null);
});

test('a seek is clamped to the loaded duration', () => {
    assert.equal(rail.clampSeek(-12, 180), 0);
    assert.equal(rail.clampSeek(95, 180), 95);
    assert.equal(rail.clampSeek(181, 180), 180);
    assert.equal(rail.clampSeek(Number.NaN, 180), 0);
    assert.equal(rail.clampSeek(30, 0), 0);
});

function useFakeWindow() {
    globalThis.window = globalThis;
    const handlers = {};
    globalThis.addEventListener = (name, fn) => { (handlers[name] ??= []).push(fn); };
    globalThis.removeEventListener = (name, fn) => { handlers[name] = (handlers[name] ?? []).filter(f => f !== fn); };
    globalThis.matchMedia = () => ({ matches: false });
    return { fire: (name, event = {}) => (handlers[name] ?? []).forEach(fn => fn(event)) };
}

function fakeRail({ duration = 180, value = 60 } = {}) {
    const make = extra => {
        const e = {
            listeners: {}, style: {}, dataset: {}, textContent: '', value: String(value), classes: new Set(),
            classList: { add(c) { e.classes.add(c); }, remove(c) { e.classes.delete(c); } },
            addEventListener(name, fn) { (e.listeners[name] ??= []).push(fn); }, removeEventListener() { },
            getBoundingClientRect: () => ({ left: 0, width: 1000 }), matches: () => false,
            events: [], dispatchEvent(event) { e.events.push(event.type); return true; },
            ...extra,
        };
        return e;
    };
    const track = make(), input = make(), bubble = make();
    const root = make({
        dataset: { duration: String(duration), chapters: '[]' },
        querySelector: selector => ({ '.playback-seek-rail__track': track, 'input[type="range"]': input, '[data-seek-time]': bubble })[selector] ?? null,
    });
    const press = (key, shiftKey = false) => {
        const event = { key, shiftKey, prevented: false, preventDefault() { this.prevented = true; } };
        input.listeners.keydown.forEach(fn => fn(event));
        return event;
    };
    return { root, input, press };
}

test('the arrow keys set the new time and raise the same input and change events a drag does', () => {
    const window = useFakeWindow();
    globalThis.Event = class { constructor(type) { this.type = type; } };
    const view = fakeRail({ value: 60 });
    rail.attach(view.root);

    const event = view.press('ArrowRight');
    assert.ok(event.prevented);
    assert.equal(view.input.value, '65');
    assert.deepEqual(view.input.events, ['input', 'change']);

    view.press('ArrowLeft', true);
    assert.equal(view.input.value, '35');

    rail.detach(view.root);
    window.fire('pointerup');
});

test('keyboard seeks stop at the start and at the loaded duration', () => {
    useFakeWindow();
    globalThis.Event = class { constructor(type) { this.type = type; } };
    const view = fakeRail({ duration: 100, value: 98 });
    rail.attach(view.root);

    view.press('ArrowRight', true);
    assert.equal(view.input.value, '100');
    view.input.value = '3';
    view.press('ArrowLeft', true);
    assert.equal(view.input.value, '0');
    rail.detach(view.root);
});

test('a key that is not an arrow is left to the browser, and nothing seeks before the duration is known', () => {
    useFakeWindow();
    globalThis.Event = class { constructor(type) { this.type = type; } };
    const view = fakeRail({ duration: 0, value: 0 });
    rail.attach(view.root);

    assert.equal(view.press('Home').prevented, false);
    view.press('ArrowRight');
    assert.equal(view.input.value, '0');
    assert.deepEqual(view.input.events, []);
    rail.detach(view.root);
});

test('the bookmark ticks hide while the handle is dragged and return on release or cancel', () => {
    const window = useFakeWindow();
    const view = fakeRail();
    rail.attach(view.root);

    assert.equal(view.root.dataset.dragging, undefined);
    view.input.listeners.pointerdown.forEach(fn => fn({}));
    assert.equal(view.root.dataset.dragging, 'true');
    window.fire('pointerup');
    assert.equal(view.root.dataset.dragging, undefined);

    view.input.listeners.pointerdown.forEach(fn => fn({}));
    assert.equal(view.root.dataset.dragging, 'true');
    window.fire('pointercancel');
    assert.equal(view.root.dataset.dragging, undefined);
    rail.detach(view.root);
});

test('the scrub label shows the target time while dragging and goes when the handle is let go', () => {
    const window = useFakeWindow();
    const view = fakeRail({ duration: 2150, value: 1075 });
    const bubble = view.root.querySelector('[data-seek-time]');
    rail.attach(view.root);

    view.input.listeners.pointerdown.forEach(fn => fn({}));
    assert.equal(bubble.textContent, '17:55');
    assert.ok(bubble.classes.has('is-visible'));
    window.fire('pointerup');
    assert.ok(!bubble.classes.has('is-visible'));
    rail.detach(view.root);
});
