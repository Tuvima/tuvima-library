import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

// Unified player v2 D17 and A10: the lyrics list follows the song until it is scrolled by hand; Resume following puts it back.
const source = readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-lyrics.js', import.meta.url), 'utf8');
globalThis.window = { matchMedia: () => ({ matches: false }) };
const lyrics = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function fixture() {
    const listeners = new Map(), changes = [];
    const line = { offsetTop: 300, clientHeight: 40 };
    const scroller = { scrollTop: 0, offsetTop: 0, clientHeight: 100,
        querySelector: () => line,
        addEventListener: (name, handler) => listeners.set(name, handler),
        removeEventListener: name => listeners.delete(name) };
    const root = { querySelector: () => scroller };
    lyrics.attach(root, { invokeMethodAsync: (name, value) => { changes.push([name, value]); return Promise.resolve(); } });
    return { root, scroller, line, listeners, changes };
}

test('the list scrolls to the current line by itself and that does not suspend following', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'song:a');
    assert.equal(f.scroller.scrollTop, 270);
    f.listeners.get('scroll')({}); // the browser reports our own scroll
    f.line.offsetTop = 400; lyrics.update(f.root, 2, 'song:a');
    assert.equal(f.scroller.scrollTop, 370, 'it still follows the next line');
    assert.equal(f.changes.some(([, needed]) => needed), false, 'no Resume following pill');
});

test('wheel, touch, keyboard and scrollbar scrolling suspend following and offer Resume following', () => {
    const signals = [['wheel', {}], ['touchstart', {}], ['keydown', { key: 'PageDown', target: {} }], ['pointerdown', null]];
    for (const [name, event] of signals) {
        const f = fixture(); lyrics.update(f.root, 1, 'song:a');
        f.listeners.get(name)(event ?? { target: f.scroller });
        assert.deepEqual(f.changes.at(-1), ['FollowChanged', true], name);
        f.line.offsetTop = 400; lyrics.update(f.root, 2, 'song:a');
        assert.equal(f.scroller.scrollTop, 270, `${name}: the list stays where the person left it`);
    }
});

test('a pointer press on a line is not a scroll', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'song:a');
    f.listeners.get('pointerdown')({ target: {} });
    assert.equal(f.changes.some(([, needed]) => needed), false);
});

test('Resume following centres the current line again and clears the pill', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'song:a');
    f.listeners.get('wheel')({}); f.scroller.scrollTop = 0;
    lyrics.follow(f.root);
    assert.equal(f.scroller.scrollTop, 270);
    assert.deepEqual(f.changes.at(-1), ['FollowChanged', false]);
    f.line.offsetTop = 400; lyrics.update(f.root, 2, 'song:a');
    assert.equal(f.scroller.scrollTop, 370, 'following is back on');
});

test('a new song starts following again', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'song:a');
    f.listeners.get('wheel')({}); f.scroller.scrollTop = 0;
    lyrics.update(f.root, 1, 'song:b');
    assert.equal(f.scroller.scrollTop, 270);
    assert.deepEqual(f.changes.at(-1), ['FollowChanged', false]);
});

test('the decision functions: manual input suspends, our own scroll and unknown signals do not, resume and a new song re-enable', () => {
    for (const signal of ['wheel', 'touch', 'key', 'scrollbar']) assert.equal(lyrics.nextFollowing(true, signal), false, signal);
    assert.equal(lyrics.nextFollowing(true, 'programmatic'), true);
    assert.equal(lyrics.nextFollowing(false, 'programmatic'), false);
    assert.equal(lyrics.nextFollowing(true, 'anything else'), true);
    for (const signal of ['resume', 'song', 'select']) assert.equal(lyrics.nextFollowing(false, signal), true, signal);
    assert.equal(lyrics.resumeNeeded(false, true), true);
    assert.equal(lyrics.resumeNeeded(true, true), false);
    assert.equal(lyrics.resumeNeeded(false, false), false);
});

test('scrolling keys suspend following, but Space on a line selects it instead', () => {
    for (const key of ['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End']) {
        assert.equal(lyrics.isManualScrollKey(key, false), true, key);
        assert.equal(lyrics.isManualScrollKey(key, true), true, `${key} on a line`);
    }
    assert.equal(lyrics.isManualScrollKey(' ', false), true);
    assert.equal(lyrics.isManualScrollKey(' ', true), false);
    assert.equal(lyrics.isManualScrollKey('Enter', false), false);
    assert.equal(lyrics.isManualScrollKey('Tab', false), false);
});

test('detach removes every listener', () => {
    const f = fixture();
    lyrics.detach(f.root);
    assert.equal(f.listeners.size, 0);
});
