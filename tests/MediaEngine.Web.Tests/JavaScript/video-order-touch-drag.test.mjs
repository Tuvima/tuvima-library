import test from 'node:test';
import assert from 'node:assert/strict';

// The video order list's drag by finger (universal editor v6 WP17, D21): pressing the handle with a touch pointer follows the pointer, marks the gap
// it is over, and releasing there reports the same drop the mouse's drag-and-drop reports. A mouse still uses the browser's own drag.

const classesOf = () => {
    const set = new Set();
    return { set, list: { add: (c) => set.add(c), remove: (...c) => c.forEach((x) => set.delete(x)), contains: (c) => set.has(c) } };
};

function row(list, index, top) {
    const classes = classesOf();
    const node = {
        dataset: { voList: String(list), voRow: String(index), voTitle: `Video ${index}` },
        classList: classes.list,
        classes: classes.set,
        getBoundingClientRect: () => ({ top, height: 40, bottom: top + 40 }),
        closest(selector) { return selector.includes('[data-vo-row]') ? node : null; },
    };
    return node;
}

function handleOf(owner) {
    return {
        captured: null,
        released: null,
        getAttribute: (name) => (name === 'draggable' ? 'true' : null),
        setPointerCapture(id) { this.captured = id; },
        releasePointerCapture(id) { this.released = id; },
        closest(selector) { return selector === '[data-vo-handle]' ? this : owner; },
        getBoundingClientRect: () => ({ top: 0, bottom: 400 }),
        parentElement: null,
    };
}

async function setup() {
    globalThis.document = { querySelectorAll: () => [], removeEventListener() {}, addEventListener() {} };
    const { attach, detach } = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/video-order-drag.js', import.meta.url).href + `?${Math.random()}`);
    const rows = [row(0, 0, 0), row(0, 1, 40), row(0, 2, 80)];
    let under = null;
    const listeners = new Map();
    const root = {
        ownerDocument: { elementFromPoint: () => under, defaultView: { getComputedStyle: () => ({ overflowY: 'visible' }) } },
        addEventListener: (type, handler) => listeners.set(type, handler),
        removeEventListener: (type, handler) => { if (listeners.get(type) === handler) listeners.delete(type); },
        querySelectorAll: () => rows,
    };
    const calls = [];
    const dotnet = { invokeMethodAsync: (...args) => calls.push(args) };
    attach(root, dotnet);
    return { root, rows, listeners, calls, detach, point: (element) => { under = element; } };
}

const touchDown = (handle) => ({ pointerType: 'touch', pointerId: 7, button: 0, target: handle });

test('a finger on the handle drags the video to the gap under it and reports the drop once', async () => {
    const { rows, listeners, calls, point } = await setup();
    const handle = handleOf(rows[0]);

    listeners.get('pointerdown')(touchDown(handle));
    assert.equal(handle.captured, 7);
    assert.ok(rows[0].classes.has('is-dragging'));

    point(rows[2]);
    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 110, preventDefault() {} });
    assert.ok(rows[2].classes.has('is-drop-after'), 'the lower half of the last row is the gap after it');

    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 110 });
    assert.deepEqual(calls, [['OnDropAsync', 0, 0, 0, 3]]);
    assert.equal(handle.released, 7);
    assert.ok(!rows[0].classes.has('is-dragging'));
});

test('letting go away from every row reports nothing', async () => {
    const { rows, listeners, calls, point } = await setup();
    listeners.get('pointerdown')(touchDown(handleOf(rows[1])));

    point(null);
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 500 });

    assert.deepEqual(calls, []);
});

test('a cancelled touch clears the marks and reports nothing', async () => {
    const { rows, listeners, calls, point } = await setup();
    listeners.get('pointerdown')(touchDown(handleOf(rows[0])));
    point(rows[1]);
    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 50, preventDefault() {} });

    listeners.get('pointercancel')({ pointerId: 7 });
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 50 });

    assert.deepEqual(calls, []);
    assert.ok(!rows[0].classes.has('is-dragging'));
});

test('a mouse does not start the pointer drag; the browser drag-and-drop stays its way', async () => {
    const { rows, listeners, calls } = await setup();
    const handle = handleOf(rows[0]);

    listeners.get('pointerdown')({ pointerType: 'mouse', pointerId: 1, button: 0, target: handle });

    assert.equal(handle.captured, null);
    assert.ok(!rows[0].classes.has('is-dragging'));
    assert.deepEqual(calls, []);
});

test('a handle that cannot change the order does not start a drag', async () => {
    const { rows, listeners } = await setup();
    const handle = handleOf(rows[0]);
    handle.getAttribute = () => 'false';

    listeners.get('pointerdown')(touchDown(handle));

    assert.equal(handle.captured, null);
});

test('the browser drag does not start beside a finger drag', async () => {
    const { rows, listeners } = await setup();
    listeners.get('pointerdown')(touchDown(handleOf(rows[0])));
    let prevented = false;

    listeners.get('dragstart')({ target: handleOf(rows[0]), preventDefault() { prevented = true; } });

    assert.ok(prevented);
});

test('detaching removes the pointer listeners too', async () => {
    const { root, listeners, detach } = await setup();
    assert.ok(listeners.has('pointerdown'));

    detach(root);

    assert.ok(!listeners.has('pointerdown'));
    assert.ok(!listeners.has('pointermove'));
    assert.ok(!listeners.has('pointerup'));
    assert.ok(!listeners.has('pointercancel'));
});

test('a second finger cancelling does not end the first finger\'s drag', async () => {
    const { rows, listeners, calls, point } = await setup();
    listeners.get('pointerdown')(touchDown(handleOf(rows[0])));
    point(rows[1]);
    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 50, preventDefault() {} });

    listeners.get('pointercancel')({ pointerId: 9 });

    assert.ok(rows[0].classes.has('is-dragging'), 'the first finger is still dragging');
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 50 });
    assert.deepEqual(calls, [['OnDropAsync', 0, 0, 0, 1]]);
});

test('a finger held still at the list\'s edge keeps the list scrolling, and stops when it leaves the edge or lets go', async (t) => {
    t.mock.timers.enable({ apis: ['setInterval'] });
    const { rows, listeners, point } = await setup();
    const view = { getComputedStyle: () => ({ overflowY: 'auto' }) };
    const scroller = { scrollHeight: 1000, clientHeight: 200, scrollTop: 100, parentElement: null, getBoundingClientRect: () => ({ top: 0, bottom: 200 }) };
    const handle = handleOf(rows[0]);
    handle.ownerDocument = { defaultView: view };
    handle.parentElement = scroller;
    listeners.get('pointerdown')(touchDown(handle));
    point(rows[1]);

    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 190, preventDefault() {} });
    const afterMove = scroller.scrollTop;
    t.mock.timers.tick(30);
    t.mock.timers.tick(30);
    assert.ok(scroller.scrollTop > afterMove, 'the list scrolls on the timer while the finger does not move');

    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 100, preventDefault() {} });
    const leftEdge = scroller.scrollTop;
    t.mock.timers.tick(90);
    assert.equal(scroller.scrollTop, leftEdge, 'it stops once the finger is away from the edge');

    listeners.get('pointermove')({ pointerId: 7, clientX: 10, clientY: 5, preventDefault() {} });
    t.mock.timers.tick(30);
    assert.ok(scroller.scrollTop < leftEdge, 'the top edge scrolls up');
    listeners.get('pointerup')({ pointerId: 7, clientX: 10, clientY: 5 });
    const atRelease = scroller.scrollTop;
    t.mock.timers.tick(90);
    assert.equal(scroller.scrollTop, atRelease, 'letting go ends the scrolling');
});
