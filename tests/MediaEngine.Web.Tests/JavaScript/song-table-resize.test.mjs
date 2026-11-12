import test from 'node:test';
import assert from 'node:assert/strict';

const {
    clampWidth, widthFromDelta, limitForDrag, splitTemplate, trackMinimum, withTrackWidth, hasFillerTrack, withFillerTrack, wireResize, unwireResize,
} = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/song-table.js', import.meta.url).href);

const TEMPLATE = '44px minmax(56px, 56px) minmax(360px, 1fr) minmax(64px, 180px) minmax(64px, 220px) 100px';
const SONG_FIXED_TEMPLATE = '44px minmax(56px, 56px) 500px minmax(64px, 180px) minmax(64px, 220px) minmax(0, 1fr) 100px';

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

test('a column that starts wider than its limit is limited by the width it has', () => {
    assert.equal(limitForDrag(1200, 1500), 1500);
    assert.equal(limitForDrag(560, 180), 560);
});

test('splitting a template keeps each minmax together', () => {
    assert.deepEqual(splitTemplate(TEMPLATE), ['44px', 'minmax(56px, 56px)', 'minmax(360px, 1fr)', 'minmax(64px, 180px)', 'minmax(64px, 220px)', '100px']);
});

test('setting a range track makes it a fixed width so it keeps that width even when the table is squeezed', () => {
    assert.equal(
        withTrackWidth(TEMPLATE, 3, 240),
        '44px minmax(56px, 56px) minmax(360px, 1fr) 240px minmax(64px, 220px) 100px');
});

test('a track adds its minimum to the table minimum, or its width when it is fixed', () => {
    assert.equal(trackMinimum(TEMPLATE, 2), 360);
    assert.equal(trackMinimum(TEMPLATE, 3), 64);
    assert.equal(trackMinimum('44px 300px 100px', 1), 300);
    assert.equal(trackMinimum(TEMPLATE, 99), 0);
});

test('setting the flexible track makes it a fixed width that can go wider or narrower', () => {
    assert.equal(
        withTrackWidth(TEMPLATE, 2, 400),
        '44px minmax(56px, 56px) 400px minmax(64px, 180px) minmax(64px, 220px) 100px');
    assert.equal(
        withTrackWidth(TEMPLATE, 2, 200),
        '44px minmax(56px, 56px) 200px minmax(64px, 180px) minmax(64px, 220px) 100px');
});

test('setting a track that is already a fixed width changes that width', () => {
    assert.equal(withTrackWidth('44px 300px 100px', 1, 250), '44px 250px 100px');
});

test('a track past the end of the template is left as it is', () => {
    assert.equal(withTrackWidth(TEMPLATE, 99, 99), TEMPLATE);
});

test('the filler track goes right before the actions track, once', () => {
    assert.equal(hasFillerTrack(TEMPLATE), false);
    assert.equal(withFillerTrack(TEMPLATE), '44px minmax(56px, 56px) minmax(360px, 1fr) minmax(64px, 180px) minmax(64px, 220px) minmax(0, 1fr) 100px');
    assert.equal(hasFillerTrack(SONG_FIXED_TEMPLATE), true);
    assert.equal(withFillerTrack(SONG_FIXED_TEMPLATE), SONG_FIXED_TEMPLATE);
});

// ---- A small stand-in for the table, to follow a whole drag -----------------------------------------------------------------

function makeTable({ template = TEMPLATE, cellIndex = 3, column = 'artist', cellWidth = 180, max = '560' } = {}) {
    const listeners = new Map();
    const properties = new Map([['--song-table-columns', template], ['--song-table-min-width', '900px'], ['--song-table-filler', template.includes('minmax(0, 1fr)') ? 'block' : 'none']]);
    const classes = new Set();
    const holder = {
        style: {
            getPropertyValue: name => properties.get(name) ?? '',
            setProperty: (name, value) => properties.set(name, value),
            set cssText(text) {
                for (const part of text.split(';')) {
                    const at = part.indexOf(':');
                    if (at > 0) properties.set(part.slice(0, at).trim(), part.slice(at + 1).trim());
                }
            },
        },
    };
    const head = { children: [] };
    const cell = { parentElement: head, getBoundingClientRect: () => ({ width: cellWidth }) };
    for (let i = 0; i < cellIndex; i++) head.children.push({});
    head.children.push(cell);
    let captured = null;
    let released = null;
    const handle = {
        dataset: { column, min: '64', max },
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
    const press = (clientX) => fire('pointerdown', { target: { closest: () => handle }, clientX, preventDefault: () => { } });
    return { root, handle, properties, classes, listeners, fire, press, captured: () => captured, released: () => released };
}

// A stand-in for the component: records calls and answers with `reply`, or fails when `fail` is set.
function makeDotnet({ reply = undefined, fail = false } = {}) {
    const calls = [];
    return {
        calls,
        ref: {
            invokeMethodAsync: (method, ...args) => {
                calls.push([method, ...args]);
                return fail ? Promise.reject(new Error('circuit gone')) : Promise.resolve(reply);
            },
        },
    };
}

const settle = () => new Promise(resolve => setImmediate(resolve));

test('dragging a handle updates the column live and tells .NET once when released', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    let prevented = false;
    table.fire('pointerdown', { target: { closest: () => table.handle }, clientX: 100, preventDefault: () => { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(table.captured(), 1);
    assert.equal(table.classes.has('is-resizing'), true);

    table.fire('pointermove', { clientX: 120 });
    table.fire('pointermove', { clientX: 140 });
    assert.equal(table.properties.get('--song-table-columns'), '44px minmax(56px, 56px) minmax(360px, 1fr) 220px minmax(64px, 220px) 100px');
    assert.equal(table.properties.get('--song-table-filler'), 'none');
    assert.deepEqual(dotnet.calls, []);

    table.fire('pointerup', {});
    assert.deepEqual(dotnet.calls, [['ColumnResized', 'artist', 220]]);
    assert.equal(table.released(), 1);
    assert.equal(table.classes.has('is-resizing'), false);
});

test('what .NET answers with is written back so the page matches the component', async () => {
    const table = makeTable();
    const dotnet = makeDotnet({ reply: '--song-table-columns: 44px minmax(56px, 56px) minmax(360px, 1fr) minmax(64px, 220px) minmax(64px, 220px) 100px; --song-table-min-width: 950px; --song-table-filler: none' });
    wireResize(table.root, dotnet.ref);

    table.press(100);
    table.fire('pointermove', { clientX: 140 });
    table.fire('pointerup', {});
    await settle();

    assert.equal(table.properties.get('--song-table-columns'), '44px minmax(56px, 56px) minmax(360px, 1fr) minmax(64px, 220px) minmax(64px, 220px) 100px');
    assert.equal(table.properties.get('--song-table-min-width'), '950px');
});

test('a failed call to .NET is reported and the table goes back to how it was before the drag', async () => {
    const table = makeTable();
    const warnings = [];
    const warn = console.warn;
    console.warn = (...args) => warnings.push(args);
    try {
        wireResize(table.root, makeDotnet({ fail: true }).ref);
        table.press(100);
        table.fire('pointermove', { clientX: 160 });
        table.fire('pointerup', {});
        await settle();
    } finally {
        console.warn = warn;
    }

    assert.equal(warnings.length, 1);
    assert.equal(table.properties.get('--song-table-columns'), TEMPLATE);
    assert.equal(table.properties.get('--song-table-min-width'), '900px');
});

test('dragging the Song handle fixes its width, adds the filler and keeps the actions track last', () => {
    const table = makeTable({ cellIndex: 2, column: 'song', cellWidth: 360, max: '1200' });
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    table.press(400);
    table.fire('pointermove', { clientX: 320 });
    assert.equal(table.properties.get('--song-table-columns'), '44px minmax(56px, 56px) 280px minmax(64px, 180px) minmax(64px, 220px) minmax(0, 1fr) 100px');
    assert.equal(table.properties.get('--song-table-filler'), 'block');
    assert.equal(table.properties.get('--song-table-min-width'), '828px');

    table.fire('pointermove', { clientX: 460 });
    assert.equal(table.properties.get('--song-table-columns'), '44px minmax(56px, 56px) 420px minmax(64px, 180px) minmax(64px, 220px) minmax(0, 1fr) 100px');
    assert.equal(table.properties.get('--song-table-min-width'), '968px');

    table.fire('pointerup', {});
    assert.deepEqual(dotnet.calls, [['ColumnResized', 'song', 420]]);
});

test('dragging an already fixed Song handle keeps the one filler track', () => {
    const table = makeTable({ template: SONG_FIXED_TEMPLATE, cellIndex: 2, column: 'song', cellWidth: 500, max: '1200' });
    wireResize(table.root, makeDotnet().ref);

    table.press(500);
    table.fire('pointermove', { clientX: 450 });

    assert.equal(table.properties.get('--song-table-columns'), '44px minmax(56px, 56px) 450px minmax(64px, 180px) minmax(64px, 220px) minmax(0, 1fr) 100px');
    assert.equal(table.properties.get('--song-table-min-width'), '850px');
});

test('a column wider than its limit when the drag starts is not pulled narrower by the first move', () => {
    const table = makeTable({ cellIndex: 2, column: 'song', cellWidth: 1500, max: '1200' });
    wireResize(table.root, makeDotnet().ref);

    table.press(100);
    table.fire('pointermove', { clientX: 130 });
    assert.equal(table.properties.get('--song-table-columns'), TEMPLATE);

    table.fire('pointermove', { clientX: 90 });
    assert.equal(splitTemplate(table.properties.get('--song-table-columns'))[2], '1490px');
});

test('dragging past the limits stops at the column minimum', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    table.press(500);
    table.fire('pointermove', { clientX: -2000 });
    table.fire('pointerup', {});

    assert.deepEqual(dotnet.calls, [['ColumnResized', 'artist', 64]]);
});

test('releasing without moving does not call .NET', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    table.press(100);
    table.fire('pointerup', {});

    assert.deepEqual(dotnet.calls, []);
});

test('letting go where the drag started sends nothing and puts the table back as it was', () => {
    const table = makeTable({ cellIndex: 2, column: 'song', cellWidth: 360, max: '1200' });
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    table.press(400);
    table.fire('pointermove', { clientX: 330 });
    table.fire('pointermove', { clientX: 400 });
    table.fire('pointerup', {});

    assert.deepEqual(dotnet.calls, []);
    assert.equal(table.properties.get('--song-table-columns'), TEMPLATE);
    assert.equal(table.properties.get('--song-table-min-width'), '900px');
    assert.equal(table.properties.get('--song-table-filler'), 'none');
});

test('a cancelled drag puts the table back and does not call .NET', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    table.press(100);
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

// ---- Keyboard ---------------------------------------------------------------------------------------------------------------

function pressKey(table, key) {
    let prevented = false;
    table.fire('keydown', { key, target: { closest: () => table.handle }, preventDefault: () => { prevented = true; }, stopPropagation: () => { } });
    return prevented;
}

test('Right on a handle widens the column by 8 pixels from the width it has on screen and tells .NET once', () => {
    const table = makeTable();
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    assert.equal(pressKey(table, 'ArrowRight'), true);

    assert.deepEqual(dotnet.calls, [['ColumnResized', 'artist', 188]]);
});

test('Left on a handle narrows the column by 8 pixels and stops at the minimum', () => {
    const table = makeTable({ cellWidth: 66 });
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    pressKey(table, 'ArrowLeft');
    pressKey(table, 'ArrowLeft');

    assert.deepEqual(dotnet.calls, [['ColumnResized', 'artist', 64], ['ColumnResized', 'artist', 64]]);
});

test('Right on the Song handle starts from the 700 pixels on screen, not from the 360 default', () => {
    const table = makeTable({ cellIndex: 2, column: 'song', cellWidth: 700, max: '1200' });
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    pressKey(table, 'ArrowRight');
    pressKey(table, 'ArrowLeft');

    assert.deepEqual(dotnet.calls, [['ColumnResized', 'song', 708], ['ColumnResized', 'song', 692]]);
});

test('Right on a column at its maximum sends nothing, and other keys are left alone', () => {
    const table = makeTable({ cellWidth: 560 });
    const dotnet = makeDotnet();
    wireResize(table.root, dotnet.ref);

    assert.equal(pressKey(table, 'ArrowRight'), true);
    assert.equal(pressKey(table, 'Tab'), false);
    assert.equal(pressKey(table, 'Enter'), false);

    assert.deepEqual(dotnet.calls, []);
});

test('the disposer removes every listener', () => {
    const table = makeTable();
    const dispose = wireResize(table.root, makeDotnet().ref);
    assert.equal(table.listeners.size, 6);

    dispose();
    assert.equal(table.listeners.size, 0);
});

test('wiring the same table again replaces the earlier wiring and unwiring removes it', () => {
    const table = makeTable();
    wireResize(table.root, makeDotnet().ref);
    wireResize(table.root, makeDotnet().ref);
    assert.equal(table.listeners.size, 6);

    unwireResize(table.root);
    assert.equal(table.listeners.size, 0);
});
