import test from 'node:test';
import assert from 'node:assert/strict';

// A song row in a playlist, and a queue row, reordered by finger (listen-playlists WP5, issue 1201). The grip starts the drag; the release tells
// .NET which row moved and which row it landed before or after, and .NET works the new place out. The generic grip logic is in
// touch-reorder.test.mjs; these two prove the song table and the queue are wired to it with their own grip, row and method names.

const songTable = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/song-table.js', import.meta.url).href);
const queue = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-queue-drag.js', import.meta.url).href);

function marks() {
    const set = new Set();
    return { set, list: { add: (...c) => c.forEach(x => set.add(x)), remove: (...c) => c.forEach(x => set.delete(x)), contains: c => set.has(c) } };
}

// A list of rows, each with an attribute that names it, and the grip inside each row.
function list(rowSelector, handleSelector, keyAttribute, keys, extraAttribute = null) {
    const rows = keys.map((key, index) => {
        const classList = marks();
        const row = {
            key,
            classList: classList.list,
            classes: classList.set,
            getAttribute: name => (name === keyAttribute ? key : null),
            dataset: {},
            getBoundingClientRect: () => ({ top: index * 52, height: 52, bottom: index * 52 + 52 }),
            closest: selector => (selector === rowSelector ? row : null),
        };
        row.grip = {
            captured: null,
            setPointerCapture(id) { this.captured = id; },
            releasePointerCapture() {},
            closest: selector => (selector === handleSelector ? row.grip : selector === rowSelector ? row : null),
            parentElement: null,
            ownerDocument: null,
        };
        return row;
    });
    let under = null;
    const listeners = new Map();
    const root = {
        ownerDocument: { elementFromPoint: () => under, defaultView: { innerHeight: 800, getComputedStyle: () => ({ overflowY: 'visible' }) } },
        contains: () => true,
        addEventListener: (type, handler) => listeners.set(type, handler),
        removeEventListener: (type, handler) => { if (listeners.get(type) === handler) listeners.delete(type); },
        querySelectorAll: () => rows,
        dataset: {},
    };
    const calls = [];
    const dotnet = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } };
    return { rows, root, listeners, calls, dotnet, point: node => { under = node; } };
}

const finger = (target, id = 4) => ({ pointerType: 'touch', pointerId: id, button: 0, clientX: 8, clientY: 8, target });

test('a finger on a song row\'s grip moves the song to the place under it, reported once as row, row and side', () => {
    const { rows, root, listeners, calls, dotnet, point } = list('[data-song-row-key]', '[data-song-grip]', 'data-song-row-key', ['s1', 's2', 's3', 's4']);
    songTable.wireReorder(root, dotnet);

    listeners.get('pointerdown')(finger(rows[0].grip));
    point(rows[2]);
    listeners.get('pointermove')({ pointerId: 4, clientX: 8, clientY: 2 * 52 + 40, preventDefault() {} });
    assert.ok(rows[2].classes.has('is-touch-drop-after'), 'the lower half of the third row marks the gap after it');
    listeners.get('pointerup')({ pointerId: 4, clientX: 8, clientY: 2 * 52 + 40 });

    assert.deepEqual(calls, [['TouchReorder', 's1', 's3', true]]);
    assert.ok(!rows[0].classes.has('is-touch-dragging'));
});

test('a finger on a song row that is not its grip never starts a drag, so the list keeps scrolling', () => {
    const { rows, root, listeners, calls, dotnet } = list('[data-song-row-key]', '[data-song-grip]', 'data-song-row-key', ['s1', 's2']);
    songTable.wireReorder(root, dotnet);

    listeners.get('pointerdown')(finger({ closest: () => null }));

    assert.deepEqual(calls, []);
    assert.ok(!rows[0].classes.has('is-touch-dragging'));
});

test('a mouse on a song row\'s grip is left to the browser\'s own drag', () => {
    const { rows, root, listeners, calls, dotnet } = list('[data-song-row-key]', '[data-song-grip]', 'data-song-row-key', ['s1', 's2']);
    songTable.wireReorder(root, dotnet);

    listeners.get('pointerdown')({ pointerType: 'mouse', pointerId: 1, button: 0, target: rows[0].grip });

    assert.equal(rows[0].grip.captured, null);
    assert.deepEqual(calls, []);
});

test('unwiring a song table lets go of its listeners', () => {
    const { root, listeners, dotnet } = list('[data-song-row-key]', '[data-song-grip]', 'data-song-row-key', ['s1']);
    songTable.wireReorder(root, dotnet);
    assert.ok(listeners.has('pointerdown'));

    songTable.unwireReorder(root);

    assert.equal(listeners.size, 0);
});

test('a finger on a queue row\'s handle tells the panel which row it took, then which row it was dropped on and on which side', () => {
    const { rows, root, listeners, calls, dotnet, point } = list('[data-queue-index]', '[data-playback-queue-drag]', 'data-queue-index', ['0', '1', '2']);
    root.addEventListener = (type, handler) => listeners.set(type, handler);
    queue.attach(root, dotnet);

    listeners.get('pointerdown')(finger(rows[2].grip));
    point(rows[0]);
    listeners.get('pointermove')({ pointerId: 4, clientX: 8, clientY: 4, preventDefault() {} });
    assert.ok(rows[0].classes.has('is-touch-drop-before'));
    listeners.get('pointerup')({ pointerId: 4, clientX: 8, clientY: 4 });

    assert.deepEqual(calls, [['TouchDragStarted', '2'], ['TouchDropped', '2', '0', false]]);
    queue.detach(root);
});

test('a cancelled queue drag tells the panel to forget the row it took', () => {
    const { rows, root, listeners, calls, dotnet } = list('[data-queue-index]', '[data-playback-queue-drag]', 'data-queue-index', ['0', '1']);
    queue.attach(root, dotnet);

    listeners.get('pointerdown')(finger(rows[1].grip));
    listeners.get('pointercancel')({ pointerId: 4 });

    assert.deepEqual(calls, [['TouchDragStarted', '1'], ['TouchDragCancelled']]);
    queue.detach(root);
});

test('a queue without a .NET reference still gets the mouse drag and no finger wiring', () => {
    const { root, listeners } = list('[data-queue-index]', '[data-playback-queue-drag]', 'data-queue-index', ['0']);
    queue.attach(root);

    assert.ok(listeners.has('dragstart'));
    assert.ok(!listeners.has('pointerdown'));
    queue.detach(root);
});
