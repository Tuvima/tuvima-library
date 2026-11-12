import test from 'node:test';
import assert from 'node:assert/strict';

const { clampWidth, widthFromDelta, splitTemplate, isFlexibleTrack, withTrackWidth, wireResize, unwireResize } =
    await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/song-table.js', import.meta.url).href);

const TEMPLATE = '44px minmax(56px, 56px) minmax(360px, 1fr) minmax(64px, 180px) minmax(64px, 220px) 100px';

test('a width below the minimum is raised to the minimum', () => {
    assert.equal(clampWidth(20, 64, 560), 64);
});

test('a width above the maximum is lowered to the maximum', () => {
    assert.equal(clampWidth(900, 64, 560), 560);
});

test('a width inside the limits is left alone', () => {
    assert.equal(clampWidth(200, 64, 560), 200);
});

test('dragging right makes the column wider by the distance moved', () => {
    assert.equal(widthFromDelta(180, 40, 64, 560), 220);
});

test('dragging left makes the column narrower and stops at the minimum', () => {
    assert.equal(widthFromDelta(180, -40, 64, 560), 140);
    assert.equal(widthFromDelta(180, -500, 64, 560), 64);
});

test('dragging far right stops at the maximum', () => {
    assert.equal(widthFromDelta(180, 900, 64, 560), 560);
});

test('a fractional pointer distance gives a whole number of pixels', () => {
    assert.equal(widthFromDelta(180, 10.6, 64, 560), 191);
});

test('splitting a template keeps each minmax together', () => {
    assert.deepEqual(splitTemplate(TEMPLATE), ['44px', 'minmax(56px, 56px)', 'minmax(360px, 1fr)', 'minmax(64px, 180px)', 'minmax(64px, 220px)', '100px']);
});

test('setting a fixed track changes its maximum and keeps its minimum', () => {
    assert.equal(
        withTrackWidth(TEMPLATE, 3, 240),
        '44px minmax(56px, 56px) minmax(360px, 1fr) minmax(64px, 240px) minmax(64px, 220px) 100px');
});

test('setting the flexible track changes its minimum and keeps the 1fr', () => {
    assert.equal(
        withTrackWidth(TEMPLATE, 2, 400),
        '44px minmax(56px, 56px) minmax(400px, 1fr) minmax(64px, 180px) minmax(64px, 220px) 100px');
});

test('a track that is not a column width is left as it is', () => {
    assert.equal(withTrackWidth(TEMPLATE, 0, 99), TEMPLATE);
    assert.equal(withTrackWidth(TEMPLATE, 99, 99), TEMPLATE);
});

test('only the Song track is told apart as flexible', () => {
    assert.equal(isFlexibleTrack(TEMPLATE, 2), true);
    assert.equal(isFlexibleTrack(TEMPLATE, 3), false);
    assert.equal(isFlexibleTrack(TEMPLATE, 99), false);
});

// ---- A small stand-in for the table, to follow a whole drag -----------------------------------------------------------------

function makeTable(template = TEMPLATE) {
    const listeners = new Map();
    const properties = new Map([['--song-table-columns', template], ['--song-table-min-width', '900px']]);
    const classes = new Set();
    const holder = {
        style: {
            getPropertyValue: name => properties.get(name) ?? '',
            setProperty: (name, value) => properties.set(name, value),
        },
    };
    const head = { children: [] };
    const cell = { parentElement: head, getBoundingClientRect: () => ({ width: 180 }) };
    head.children.push({}, {}, {}, cell);
    let captured = null;
    let released = null;
    const handle = {
        dataset: { column: 'artist', min: '64', max: '560' },
        closest: selector => (selector === '.song-table__head-cell' ? cell : handle),
        setPointerCapture: id => { captured = id; },
        releasePointerCapture: id => { released = id; },
    };
    const root = {
        parentElement: holder,
        classList: { add: name => classes.add(name), remove: name => classes.delete(name) },
        addEventListener: (type, listener) => listeners.set(type, listener),
        removeEventListener: type => listeners.delete(type),
        contains: element => element === handle,
    };
    const fire = (type, event) => listeners.get(type)?.({ pointerId: 1, pointerType: 'mouse', button: 0, ...event });
    return { root, handle, properties, classes, listeners, fire, captured: () => captured, released: () => released };
}

function makeDotnet() {
    const calls = [];
    return { calls, ref: { invokeMethodAsync: (method, ...args) => { calls.push([method, ...args]); return Promise.resolve(); } } };
}

test('dragging a handle updates the column live and tells .NET once when released', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    let prevented = false;
    table.fire('pointerdown', { target: { closest: () => table.handle }, clientX: 100, preventDefault: () => { prevented = true; }, stopPropagation: () => { } });
    assert.equal(prevented, true);
    assert.equal(table.captured(), 1);
    assert.equal(table.classes.has('is-resizing'), true);

    table.fire('pointermove', { clientX: 120 });
    table.fire('pointermove', { clientX: 140 });
    assert.equal(table.properties.get('--song-table-columns'), '44px minmax(56px, 56px) minmax(360px, 1fr) minmax(64px, 220px) minmax(64px, 220px) 100px');
    assert.deepEqual(dotnet.calls, []);

    table.fire('pointerup', {});
    assert.deepEqual(dotnet.calls, [['ColumnResized', 'artist', 220]]);
    assert.equal(table.released(), 1);
    assert.equal(table.classes.has('is-resizing'), false);
});

test('dragging past the limits stops at the column minimum', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    table.fire('pointerdown', { target: { closest: () => table.handle }, clientX: 500, preventDefault: () => { }, stopPropagation: () => { } });
    table.fire('pointermove', { clientX: -2000 });
    table.fire('pointerup', {});

    assert.deepEqual(dotnet.calls, [['ColumnResized', 'artist', 64]]);
});

test('releasing without moving does not call .NET', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    table.fire('pointerdown', { target: { closest: () => table.handle }, clientX: 100, preventDefault: () => { }, stopPropagation: () => { } });
    table.fire('pointerup', {});

    assert.deepEqual(dotnet.calls, []);
});

test('a cancelled drag puts the table back and does not call .NET', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    table.fire('pointerdown', { target: { closest: () => table.handle }, clientX: 100, preventDefault: () => { }, stopPropagation: () => { } });
    table.fire('pointermove', { clientX: 160 });
    table.fire('pointercancel', {});

    assert.equal(table.properties.get('--song-table-columns'), TEMPLATE);
    assert.deepEqual(dotnet.calls, []);
});

test('double-clicking a handle asks .NET to reset that column', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    table.fire('dblclick', { target: { closest: () => table.handle }, preventDefault: () => { } });

    assert.deepEqual(dotnet.calls, [['ColumnResetWidth', 'artist']]);
});

test('the disposer removes every listener', () => {
    const table = makeTable();
    const dispose = wireResize(table.root, makeDotnet().ref);
    assert.equal(table.listeners.size, 5);

    dispose();
    assert.equal(table.listeners.size, 0);
});

test('wiring the same table again replaces the earlier wiring and unwiring removes it', () => {
    const table = makeTable();
    wireResize(table.root, makeDotnet().ref);
    wireResize(table.root, makeDotnet().ref);
    assert.equal(table.listeners.size, 5);

    unwireResize(table.root);
    assert.equal(table.listeners.size, 0);
});
