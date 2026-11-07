// Run with: node --test tests/MediaEngine.Web.Tests/JavaScript
// Covers the scrub preview picture on the seek rail (Unified player v2 D9a): which tile shows for a moment, that the index is read
// once, and that scrubbing shows the right tile above the timestamp and hides it again.
import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const rail = await import(pathToFileURL(path.resolve(import.meta.dirname, '../../../src/MediaEngine.Web/wwwroot/js/playback-seek-rail.js')).href);

const index = {
    intervalSeconds: 10, tileWidth: 320, tileHeight: 180, columns: 10, rows: 10, frameCount: 215, durationSeconds: 2150,
    sheets: ['sheet-000.jpg', 'sheet-001.jpg', 'sheet-002.jpg'],
};

test('a moment shows the frame at or before it, on the right sheet, column and row', () => {
    assert.deepEqual(rail.tileFor(index, 0), { sheet: 'sheet-000.jpg', column: 0, row: 0 });
    assert.deepEqual(rail.tileFor(index, 9.9), { sheet: 'sheet-000.jpg', column: 0, row: 0 });
    assert.deepEqual(rail.tileFor(index, 10), { sheet: 'sheet-000.jpg', column: 1, row: 0 });
    assert.deepEqual(rail.tileFor(index, 995), { sheet: 'sheet-000.jpg', column: 9, row: 9 });
    assert.deepEqual(rail.tileFor(index, 1000), { sheet: 'sheet-001.jpg', column: 0, row: 0 });
    assert.deepEqual(rail.tileFor(index, 2150), { sheet: 'sheet-002.jpg', column: 4, row: 1 });
});

test('a moment before the start or past the end holds the first or last frame', () => {
    assert.equal(rail.tileFor(index, -30).column, 0);
    assert.deepEqual(rail.tileFor(index, 99999), { sheet: 'sheet-002.jpg', column: 4, row: 1 });
    assert.equal(rail.tileFor(index, Number.NaN).sheet, 'sheet-000.jpg');
});

test('an index with nothing to show gives no tile', () => {
    assert.equal(rail.tileFor(null, 5), null);
    assert.equal(rail.tileFor({ ...index, sheets: [] }, 5), null);
    assert.equal(rail.tileFor({ ...index, frameCount: 0 }, 5), null);
});

test('the index is read once per address and a failed read is no index', async () => {
    rail.resetIndexes();
    let reads = 0;
    const fetchImpl = async () => { reads++; return { ok: true, json: async () => index }; };
    const first = await rail.loadIndex('/engine-scrub/a/index.json', fetchImpl);
    const second = await rail.loadIndex('/engine-scrub/a/index.json', fetchImpl);
    assert.equal(first, second);
    assert.equal(reads, 1);
    rail.resetIndexes();
    assert.equal(await rail.loadIndex('/engine-scrub/b/index.json', async () => ({ ok: false })), null);
    rail.resetIndexes();
    assert.equal(await rail.loadIndex('/engine-scrub/c/index.json', async () => { throw new Error('offline'); }), null);
    assert.equal(await rail.loadIndex('', fetchImpl), null);
});

function useFakeWindow() {
    globalThis.window = globalThis;
    globalThis.addEventListener = () => { };
    globalThis.removeEventListener = () => { };
}

function fakeRail({ withTile = true, supplied = true } = {}) {
    const handlers = {};
    const element = (extra = {}) => ({
        listeners: {}, style: {}, dataset: {}, textContent: '', value: '0',
        classes: new Set(),
        classList: { add(c) { this.owner.classes.add(c); }, remove(c) { this.owner.classes.delete(c); }, owner: null },
        addEventListener(name, fn) { (this.listeners[name] ??= []).push(fn); },
        removeEventListener() { },
        getBoundingClientRect: () => ({ left: 0, width: 1000 }),
        matches: () => false,
        ...extra,
    });
    const make = extra => { const e = element(extra); e.classList.owner = e; return e; };
    const track = make(), input = make(), bubble = make(), tile = make();
    const root = make({
        dataset: {
            duration: '2150', chapters: '[]', scrubIndexUrl: '/engine-scrub/11111111-1111-1111-1111-111111111111/index.json',
            ...(supplied ? { scrubIndex: JSON.stringify(index) } : {}),
        },
        querySelector(selector) {
            return ({ '.playback-seek-rail__track': track, 'input[type="range"]': input, '[data-seek-time]': bubble, '[data-scrub-tile]': withTile ? tile : null })[selector] ?? null;
        },
    });
    return { root, track, bubble, tile, move: x => track.listeners.pointermove.forEach(fn => fn({ clientX: x, pointerType: 'mouse' })), leave: () => track.listeners.pointerleave.forEach(fn => fn({})) };
}

test('scrubbing shows the right tile above the timestamp, and leaving hides both', () => {
    useFakeWindow();
    globalThis.matchMedia = () => ({ matches: false });
    globalThis.location = { href: 'http://dashboard.local/watch/film' };
    const view = fakeRail();
    rail.attach(view.root);

    view.move(500); // halfway: 1075 s, frame 107 = sheet 001, column 7, row 0

    assert.equal(view.bubble.textContent, '17:55');
    assert.ok(view.bubble.classes.has('is-visible'));
    assert.ok(view.tile.classes.has('is-visible'));
    assert.equal(view.tile.style.width, '160px');
    assert.equal(view.tile.style.height, '90px');
    assert.equal(view.tile.style.backgroundImage, 'url("http://dashboard.local/engine-scrub/11111111-1111-1111-1111-111111111111/sheet-001.jpg")');
    assert.equal(view.tile.style.backgroundSize, '1600px 900px');
    assert.equal(view.tile.style.backgroundPosition, '-1120px -0px');

    view.move(0);
    assert.equal(view.tile.style.backgroundImage, 'url("http://dashboard.local/engine-scrub/11111111-1111-1111-1111-111111111111/sheet-000.jpg")');
    assert.equal(view.tile.style.left, '88px'); // kept inside the rail: half a tile plus a small margin

    view.leave();
    assert.ok(!view.bubble.classes.has('is-visible'));
    assert.ok(!view.tile.classes.has('is-visible'));
    rail.detach(view.root);
});

test('a video without previews shows the timestamp only', () => {
    useFakeWindow();
    globalThis.matchMedia = () => ({ matches: false });
    const view = fakeRail({ withTile: false });
    view.root.dataset.scrubIndexUrl = '';
    rail.attach(view.root);

    view.move(500);

    assert.equal(view.bubble.textContent, '17:55');
    assert.ok(view.bubble.classes.has('is-visible'));
    assert.ok(!view.tile.classes.has('is-visible'));
    rail.detach(view.root);
});
