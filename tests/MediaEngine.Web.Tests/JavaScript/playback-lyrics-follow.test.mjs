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

// A hand scroll is the intent (wheel, touch, key, scrollbar press) followed by a scroll the list did not cause itself.
const handScrolls = [
    ['wheel', f => f.listeners.get('wheel')({})],
    ['touch', f => f.listeners.get('touchstart')({})],
    ['keyboard', f => f.listeners.get('keydown')({ key: 'PageDown', target: {} })],
    ['scrollbar', f => f.listeners.get('pointerdown')({ target: f.scroller })],
];

test('wheel, touch, keyboard and scrollbar scrolling suspend following and offer Resume following', () => {
    for (const [name, intent] of handScrolls) {
        const f = fixture(); lyrics.update(f.root, 1, 'song:a');
        intent(f); f.scroller.scrollTop = 120; f.listeners.get('scroll')({});
        assert.deepEqual(f.changes.at(-1), ['FollowChanged', true], name);
        f.line.offsetTop = 400; lyrics.update(f.root, 2, 'song:a');
        assert.equal(f.scroller.scrollTop, 120, `${name}: the list stays where the person left it`);
    }
});

test('the intent alone does not suspend: a tap that scrolls nothing leaves following on', () => {
    for (const [name, intent] of handScrolls) {
        const f = fixture(); lyrics.update(f.root, 1, 'song:a');
        intent(f);
        assert.equal(f.changes.some(([, needed]) => needed), false, name);
        f.line.offsetTop = 400; lyrics.update(f.root, 2, 'song:a');
        assert.equal(f.scroller.scrollTop, 370, `${name}: it still follows`);
    }
});

test('a tap or a click in the empty padding does not suspend following', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'song:a');
    f.listeners.get('touchstart')({}); f.listeners.get('touchend')({});
    f.listeners.get('pointerdown')({ target: f.scroller });
    f.listeners.get('scroll')({}); // nothing moved: the list is still where center() put it
    assert.equal(f.changes.some(([, needed]) => needed), false);
});

test('a scroll without any intent, such as a reflow, does not suspend following', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'song:a');
    f.scroller.scrollTop = 50; f.listeners.get('scroll')({});
    assert.equal(f.changes.some(([, needed]) => needed), false);
});

test('a touch that is held and then drags suspends even after the intent window', () => {
    assert.equal(lyrics.intentActive(0, lyrics.INTENT_WINDOW_MS + 1, false), false);
    assert.equal(lyrics.intentActive(0, lyrics.INTENT_WINDOW_MS + 1, true), true);
    assert.equal(lyrics.intentActive(null, 5, false), false);
    assert.equal(lyrics.intentActive(0, lyrics.INTENT_WINDOW_MS, false), true);
});

test('keyboard focus on a line suspends following so Tab is not scrolled away, a mouse press does not', () => {
    const line = (visible) => ({ closest: selector => selector === '[data-lyric-index]' ? {} : null, matches: selector => selector === ':focus-visible' && visible });
    const f = fixture(); lyrics.update(f.root, 1, 'song:a');
    f.listeners.get('focusin')({ target: line(false) });
    assert.equal(f.changes.some(([, needed]) => needed), false, 'a mouse press focuses without a visible ring');
    f.listeners.get('focusin')({ target: { closest: () => null, matches: () => true } });
    assert.equal(f.changes.some(([, needed]) => needed), false, 'something that is not a line');
    f.listeners.get('focusin')({ target: line(true) });
    assert.deepEqual(f.changes.at(-1), ['FollowChanged', true]);
    f.line.offsetTop = 400; lyrics.update(f.root, 2, 'song:a');
    assert.equal(f.scroller.scrollTop, 270, 'the focused line stays in view');
    assert.equal(lyrics.focusSuspends(true, true), true);
    assert.equal(lyrics.focusSuspends(true, false), false);
    assert.equal(lyrics.focusSuspends(false, true), false);
});

test('our own move to the current line is recognised, a different position is a hand scroll', () => {
    assert.equal(lyrics.isProgrammaticScroll(270, 270), true);
    assert.equal(lyrics.isProgrammaticScroll(270, 271.5), false);
    assert.equal(lyrics.isProgrammaticScroll(null, 270), false);
});

test('Resume following centres the current line again and clears the pill', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'song:a');
    f.listeners.get('wheel')({}); f.scroller.scrollTop = 0; f.listeners.get('scroll')({});
    lyrics.follow(f.root);
    assert.equal(f.scroller.scrollTop, 270);
    assert.deepEqual(f.changes.at(-1), ['FollowChanged', false]);
    f.line.offsetTop = 400; lyrics.update(f.root, 2, 'song:a');
    assert.equal(f.scroller.scrollTop, 370, 'following is back on');
});

test('a new song starts following again', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'song:a');
    f.listeners.get('wheel')({}); f.scroller.scrollTop = 0; f.listeners.get('scroll')({});
    assert.deepEqual(f.changes.at(-1), ['FollowChanged', true]);
    lyrics.update(f.root, 1, 'song:b');
    assert.equal(f.scroller.scrollTop, 270);
    assert.deepEqual(f.changes.at(-1), ['FollowChanged', false]);
});

test('the decision functions: a hand scroll suspends, intent alone, our own scroll and unknown signals do not, resume and a new song re-enable', () => {
    for (const signal of ['scroll', 'focus']) assert.equal(lyrics.nextFollowing(true, signal), false, signal);
    for (const signal of ['intent', 'wheel', 'programmatic']) assert.equal(lyrics.nextFollowing(true, signal), true, signal);
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

test('entering Find another or About focuses their first control, and leaving puts focus back on the "..." button', () => {
    globalThis.document = { activeElement: null, body: {} };
    const control = name => ({ name, focus() { document.activeElement = this; }, contains: () => false });
    const offer = control('offer'), goBack = control('go back'), more = control('more');
    const container = { ...control('container'), contains: element => element === goBack || element === offer,
        querySelector: () => offer };
    const root = { querySelector: selector => selector.includes('alternatives') ? container : null,
        closest: () => ({ querySelector: selector => selector === '.playback-lyrics-footer__more' ? more : null }) };
    assert.equal(lyrics.focusView(root, 'alternatives'), true);
    assert.equal(document.activeElement, offer);
    document.activeElement = goBack;
    assert.equal(lyrics.focusView(root, 'alternatives', true), true, 'the offers arrived while focus was still in the view');
    assert.equal(document.activeElement, offer);
    document.activeElement = more;
    assert.equal(lyrics.focusView(root, 'alternatives', true), false, 'focus is elsewhere: leave it alone');
    assert.equal(document.activeElement, more);
    assert.equal(lyrics.focusView(root, 'about'), false, 'a view that is not drawn');
    document.activeElement = null;
    assert.equal(lyrics.focusMore(root), true);
    assert.equal(document.activeElement, more);
    delete globalThis.document;
});
