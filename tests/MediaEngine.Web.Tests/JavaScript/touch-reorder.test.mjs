import test from 'node:test';
import assert from 'node:assert/strict';

// Reordering by finger (listen-playlists WP5, issue 1201). A grip on each row is pressed with a finger, follows the pointer, marks the row it is
// over (before or after by which half), and the release reports the drop. A mouse is left to the browser's own drag; only the grip starts it,
// so the rest of a row still scrolls the page.

const { wire, wireToDotnet, placementFor, edgeDirection, BEFORE_CLASS, AFTER_CLASS, DRAGGING_CLASS } =
    await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/touch-reorder.js', import.meta.url).href);

const HANDLE = '[data-grip]';
const ROW = '[data-row]';

function classes() {
    const set = new Set();
    return { set, list: { add: (...c) => c.forEach(x => set.add(x)), remove: (...c) => c.forEach(x => set.delete(x)), contains: c => set.has(c) } };
}

function row(key, top) {
    const marks = classes();
    const node = {
        key,
        classList: marks.list,
        classes: marks.set,
        getAttribute: name => (name === 'data-row' ? key : null),
        dataset: { touchReorderKey: key },
        getBoundingClientRect: () => ({ top, height: 40, bottom: top + 40 }),
        closest: selector => (selector === ROW ? node : null),
    };
    return node;
}

function grip(owner) {
    return {
        captured: null,
        released: null,
        setPointerCapture(id) { this.captured = id; },
        releasePointerCapture(id) { this.released = id; },
        closest(selector) { return selector === HANDLE ? this : selector === ROW ? owner : null; },
        parentElement: null,
        ownerDocument: null,
    };
}

function setup(options = {}) {
    const rows = [row('a', 0), row('b', 40), row('c', 80)];
    let under = null;
    const listeners = new Map();
    const documentListeners = new Map();
    const view = { innerHeight: 600, scrolled: 0, scrollBy(_x, y) { this.scrolled += y; }, getComputedStyle: () => ({ overflowY: 'visible' }) };
    const doc = {
        elementFromPoint: () => under,
        defaultView: view,
        addEventListener: (type, handler) => documentListeners.set(type, handler),
        removeEventListener: (type, handler) => { if (documentListeners.get(type) === handler) documentListeners.delete(type); },
    };
    const root = {
        ownerDocument: doc,
        contains: () => true,
        addEventListener: (type, handler) => listeners.set(type, handler),
        removeEventListener: (type, handler) => { if (listeners.get(type) === handler) listeners.delete(type); },
        querySelectorAll: () => rows,
    };
    const events = [];
    const dispose = wire(root, {
        handle: HANDLE,
        row: ROW,
        keyOf: node => node.key,
        onStart: key => events.push(['start', key]),
        onDrop: (from, to, placement) => events.push(['drop', from, to, placement]),
        onCancel: () => events.push(['cancel']),
        ...options,
    });
    return { rows, root, listeners, documentListeners, events, view, dispose, point: node => { under = node; } };
}

const touchDown = handle => ({ pointerType: 'touch', pointerId: 7, button: 0, clientX: 5, clientY: 5, target: handle });

test('placementFor says before in the upper half of a row and after in the lower half', () => {
    const box = { top: 100, height: 40 };
    assert.equal(placementFor(110, box), 'before');
    assert.equal(placementFor(130, box), 'after');
});

test('edgeDirection scrolls up near the top, down near the bottom, and not at all when the area is unknown', () => {
    assert.equal(edgeDirection(10, 0, 600), -1);
    assert.equal(edgeDirection(590, 0, 600), 1);
    assert.equal(edgeDirection(300, 0, 600), 0);
    assert.equal(edgeDirection(300, 0, Number.NaN), 0);
});

test('a finger on the grip drags the row to the gap under it and reports the drop once', () => {
    const { rows, listeners, events, point } = setup();
    const handle = grip(rows[0]);

    listeners.get('pointerdown')(touchDown(handle));
    assert.equal(handle.captured, 7);
    assert.ok(rows[0].classes.has(DRAGGING_CLASS));
    assert.deepEqual(events, [['start', 'a']]);

    point(rows[2]);
    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 110, preventDefault() {} });
    assert.ok(rows[2].classes.has(AFTER_CLASS), 'the lower half of the last row is the gap after it');

    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 110 });
    assert.deepEqual(events, [['start', 'a'], ['drop', 'a', 'c', 'after']]);
    assert.equal(handle.released, 7);
    assert.ok(!rows[0].classes.has(DRAGGING_CLASS));
    assert.ok(!rows[2].classes.has(AFTER_CLASS));
});

test('the upper half of a row marks the gap before it', () => {
    const { rows, listeners, events, point } = setup();
    listeners.get('pointerdown')(touchDown(grip(rows[2])));

    point(rows[0]);
    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 5, preventDefault() {} });
    assert.ok(rows[0].classes.has(BEFORE_CLASS));
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 5 });

    assert.deepEqual(events.at(-1), ['drop', 'c', 'a', 'before']);
});

test('letting go over the row that was picked up, or away from every row, reports no move', () => {
    const { rows, listeners, events, point } = setup();
    listeners.get('pointerdown')(touchDown(grip(rows[1])));
    point(rows[1]);
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 50 });

    listeners.get('pointerdown')(touchDown(grip(rows[1])));
    point(null);
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 500 });

    assert.deepEqual(events.filter(event => event[0] === 'drop'), []);
    assert.deepEqual(events.filter(event => event[0] === 'cancel').length, 2);
});

test('a cancelled touch clears the marks and reports a cancel, not a drop', () => {
    const { rows, listeners, events, point } = setup();
    listeners.get('pointerdown')(touchDown(grip(rows[0])));
    point(rows[1]);
    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 50, preventDefault() {} });

    listeners.get('pointercancel')({ pointerId: 7 });
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 50 });

    assert.deepEqual(events, [['start', 'a'], ['cancel']]);
    assert.ok(!rows[0].classes.has(DRAGGING_CLASS));
    assert.ok(!rows[1].classes.has(BEFORE_CLASS) && !rows[1].classes.has(AFTER_CLASS));
});

test('a mouse does not start the finger drag; the browser drag stays its way', () => {
    const { rows, listeners, events } = setup();
    const handle = grip(rows[0]);

    listeners.get('pointerdown')({ pointerType: 'mouse', pointerId: 1, button: 0, target: handle });

    assert.equal(handle.captured, null);
    assert.deepEqual(events, []);
});

test('a finger on a row but not on its grip does nothing, so the page still scrolls', () => {
    const { rows, listeners, events } = setup();
    const notGrip = { closest: () => null };

    listeners.get('pointerdown')({ pointerType: 'touch', pointerId: 3, button: 0, target: notGrip });

    assert.deepEqual(events, []);
    assert.ok(!rows[0].classes.has(DRAGGING_CLASS));
});

test('a second finger cancelling does not end the first finger\'s drag', () => {
    const { rows, listeners, events, point } = setup();
    listeners.get('pointerdown')(touchDown(grip(rows[0])));
    point(rows[1]);
    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 50, preventDefault() {} });

    listeners.get('pointercancel')({ pointerId: 9 });

    assert.ok(rows[0].classes.has(DRAGGING_CLASS));
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 50 });
    assert.deepEqual(events.at(-1)[0], 'drop');
});

test('a long press on a grip does not open the row menu or the browser menu', () => {
    const { rows, listeners } = setup();
    let prevented = 0;
    let stopped = 0;

    listeners.get('contextmenu')({ target: grip(rows[0]), preventDefault() { prevented++; }, stopPropagation() { stopped++; } });
    listeners.get('contextmenu')({ target: { closest: () => null }, preventDefault() { prevented++; }, stopPropagation() { stopped++; } });

    assert.equal(prevented, 1);
    assert.equal(stopped, 1);
});

test('the end of a touch is also heard on the document, for a grip that left the page while the list scrolled', () => {
    const { rows, listeners, documentListeners, events, point } = setup();
    listeners.get('pointerdown')(touchDown(grip(rows[0])));
    point(rows[1]);

    documentListeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 45 });

    assert.deepEqual(events.at(-1), ['drop', 'a', 'b', 'before']);
    assert.equal(documentListeners.size, 0, 'the document listeners go with the drag');
});

test('a finger held at the bottom of the screen keeps the page scrolling until it moves away or lets go', (t) => {
    t.mock.timers.enable({ apis: ['setInterval'] });
    const { rows, listeners, view, point } = setup();
    listeners.get('pointerdown')(touchDown(grip(rows[0])));
    point(rows[1]);

    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 590, preventDefault() {} });
    t.mock.timers.tick(60);
    assert.ok(view.scrolled > 0, 'the page scrolls down on the timer while the finger does not move');

    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 300, preventDefault() {} });
    const away = view.scrolled;
    t.mock.timers.tick(90);
    assert.equal(view.scrolled, away, 'it stops once the finger is away from the edge');

    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 5, preventDefault() {} });
    t.mock.timers.tick(30);
    assert.ok(view.scrolled < away, 'the top edge scrolls up');
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 5 });
    const atRelease = view.scrolled;
    t.mock.timers.tick(90);
    assert.equal(view.scrolled, atRelease, 'letting go ends the scrolling');
});

test('disposing removes every listener and lets go of a drag in progress', () => {
    const { rows, listeners, dispose } = setup();
    const handle = grip(rows[0]);
    listeners.get('pointerdown')(touchDown(handle));

    dispose();

    assert.equal(listeners.size, 0);
    assert.equal(handle.released, 7);
    assert.ok(!rows[0].classes.has(DRAGGING_CLASS));
});

test('wireToDotnet tells the page which row moved and whether it landed after the other, in order', async () => {
    const rows = [row('a', 0), row('b', 40)];
    const listeners = new Map();
    let under = null;
    const root = {
        ownerDocument: { elementFromPoint: () => under, defaultView: { innerHeight: 600, getComputedStyle: () => ({ overflowY: 'visible' }) } },
        contains: () => true,
        addEventListener: (type, handler) => listeners.set(type, handler),
        removeEventListener: (type) => listeners.delete(type),
        querySelectorAll: () => rows,
    };
    const calls = [];
    wireToDotnet(root, { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } },
        { handle: HANDLE, row: ROW, keyAttribute: 'data-row', startMethod: 'Started', dropMethod: 'Dropped', cancelMethod: 'Cancelled' });

    listeners.get('pointerdown')(touchDown(grip(rows[0])));
    under = rows[1];
    listeners.get('pointermove')({ pointerId: 7, clientX: 5, clientY: 75, preventDefault() {} });
    listeners.get('pointerup')({ pointerId: 7, clientX: 5, clientY: 75 });

    assert.deepEqual(calls, [['Started', 'a'], ['Dropped', 'a', 'b', true]]);
});
