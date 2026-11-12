import test from 'node:test';
import assert from 'node:assert/strict';

const { findDropTarget, dragPictureLabel, wireDragOut } = await import(
    new URL('../../../src/MediaEngine.Web/wwwroot/js/song-table.js', import.meta.url).href);

// A tiny stand-in for the parts of the page the drag script touches: it records listeners so a test can fire them.
function fakePage() {
    const listeners = new Map();
    const classes = new Set();
    const appended = [];
    const document = {
        body: {
            classList: { add: (c) => classes.add(c), remove: (c) => classes.delete(c) },
            appendChild: (element) => appended.push(element),
        },
        createElement: () => ({ style: {}, remove() { this.removed = true; } }),
        addEventListener: (type, handler) => { listeners.set(type, handler); },
        removeEventListener: (type, handler) => { if (listeners.get(type) === handler) listeners.delete(type); },
    };
    return { document, listeners, classes, appended };
}

function element(attributes = {}, parent = null) {
    const classes = new Set();
    const node = {
        nodeType: 1,
        dataset: {},
        parentElement: parent,
        classList: { add: (c) => classes.add(c), remove: (c) => classes.delete(c), contains: (c) => classes.has(c) },
        contains: (other) => other === node || (other?.parentElement ? node.contains(other.parentElement) : false),
        closest: (selector) => {
            const attribute = /^\[([a-z-]+)\]$/.exec(selector)?.[1];
            for (let current = node; current; current = current.parentElement) {
                if (attribute && (current.attributes?.[attribute] !== undefined)) return current;
            }

            return null;
        },
        attributes,
        querySelector: () => ({ textContent: ' Song title ' }),
    };
    return node;
}

function transfer() {
    const data = {};
    return { data, dropEffect: 'none', image: null, setData: (type, value) => { data[type] = value; }, setDragImage: (el) => { transfer.image = el; } };
}

test('a drop target is the nearest element above the pointer that carries data-song-drop', () => {
    const target = element({ 'data-song-drop': 'playlist' });
    const inner = element({}, target);
    assert.equal(findDropTarget(inner), target);
    assert.equal(findDropTarget(element()), null);
    assert.equal(findDropTarget(null), null);
});

test('the drag picture says how many songs only for a ticked row', () => {
    const root = element();
    root.dataset.songDragLabel = '3 songs';
    const ticked = element();
    ticked.dataset.songTicked = 'true';
    assert.equal(dragPictureLabel(ticked, root), '3 songs');
    assert.equal(dragPictureLabel(element(), root), null);
    assert.equal(dragPictureLabel(ticked, element()), null);
});

test('starting a drag from a ticked row gives the drag its text, the "3 songs" picture and the page a dragging class', () => {
    const page = fakePage();
    globalThis.document = page.document;
    const root = element();
    root.dataset.songDragOut = 'true';
    root.dataset.songDragLabel = '3 songs';
    const handlers = {};
    root.addEventListener = (type, handler) => { handlers[type] = handler; };
    root.removeEventListener = (type) => { delete handlers[type]; };
    const row = element({ 'data-song-row-id': 'a' }, root);
    row.dataset.songTicked = 'true';
    const dt = transfer();

    const dispose = wireDragOut(root);
    handlers.dragstart({ target: row, dataTransfer: dt });

    assert.equal(dt.data['text/plain'], '3 songs');
    assert.equal(page.appended.length, 1);
    assert.equal(page.appended[0].textContent, '3 songs');
    assert.ok(page.classes.has('is-song-dragging'));
    assert.ok(page.listeners.has('dragover'));

    dispose();
    assert.ok(!page.classes.has('is-song-dragging'));
    assert.equal(page.listeners.size, 0);
});

test('an unticked row keeps the browser picture and a table that cannot drag out starts no tracking', () => {
    const page = fakePage();
    globalThis.document = page.document;
    const root = element();
    const handlers = {};
    root.addEventListener = (type, handler) => { handlers[type] = handler; };
    root.removeEventListener = () => { };
    const row = element({ 'data-song-row-id': 'a' }, root);
    const dt = transfer();
    wireDragOut(root);

    handlers.dragstart({ target: row, dataTransfer: dt });
    assert.equal(dt.data['text/plain'], 'Song title');
    assert.equal(page.appended.length, 0);
    assert.equal(page.listeners.size, 0);

    root.dataset.songDragOut = 'true';
    handlers.dragstart({ target: row, dataTransfer: transfer() });
    assert.equal(page.appended.length, 0);
    assert.ok(page.listeners.has('dragover'));
});

test('hovering a drop target highlights it and accepts the drop, leaving clears it, and a page without the attribute is not highlighted', () => {
    const page = fakePage();
    globalThis.document = page.document;
    const root = element();
    root.dataset.songDragOut = 'true';
    const handlers = {};
    root.addEventListener = (type, handler) => { handlers[type] = handler; };
    root.removeEventListener = () => { };
    const row = element({ 'data-song-row-id': 'a' }, root);
    wireDragOut(root);
    handlers.dragstart({ target: row, dataTransfer: transfer() });

    const accepting = element({ 'data-song-drop': 'playlist' });
    const refusing = element();
    let prevented = 0;
    const over = (target) => {
        const dt = transfer();
        page.listeners.get('dragover')({ target, dataTransfer: dt, preventDefault: () => { prevented++; } });
        return dt;
    };

    const dt = over(accepting);
    assert.ok(accepting.classList.contains('is-song-drop-over'));
    assert.equal(dt.dropEffect, 'copy');
    assert.equal(prevented, 1);

    over(refusing);
    assert.ok(!accepting.classList.contains('is-song-drop-over'));
    assert.equal(prevented, 1);

    over(accepting);
    page.listeners.get('dragleave')({ relatedTarget: refusing });
    assert.ok(!accepting.classList.contains('is-song-drop-over'));
});

test('a drop finishes the tracking and the first pointer move afterwards does too', () => {
    const page = fakePage();
    globalThis.document = page.document;
    const root = element();
    root.dataset.songDragOut = 'true';
    const handlers = {};
    root.addEventListener = (type, handler) => { handlers[type] = handler; };
    root.removeEventListener = () => { };
    const row = element({ 'data-song-row-id': 'a' }, root);
    wireDragOut(root);

    handlers.dragstart({ target: row, dataTransfer: transfer() });
    const accepting = element({ 'data-song-drop': 'new-playlist' });
    let prevented = false;
    page.listeners.get('drop')({ target: accepting, preventDefault: () => { prevented = true; } });
    assert.ok(prevented);
    assert.equal(page.listeners.size, 0);
    assert.ok(!page.classes.has('is-song-dragging'));

    handlers.dragstart({ target: row, dataTransfer: transfer() });
    page.listeners.get('pointermove')({});
    assert.equal(page.listeners.size, 0);
});
