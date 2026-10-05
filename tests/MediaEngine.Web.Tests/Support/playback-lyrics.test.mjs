import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const source = await fs.readFile(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-lyrics.js', import.meta.url), 'utf8');
globalThis.window = { matchMedia: () => ({ matches: false }) };
const lyrics = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
function fixture() {
    const listeners = new Map(), changes = [];
    const line = { offsetTop: 300, clientHeight: 40, getBoundingClientRect: () => ({ top: line.offsetTop - scroller.scrollTop, bottom: line.offsetTop + 40 - scroller.scrollTop }) };
    const scroller = { scrollTop: 0, offsetTop: 0, clientHeight: 100,
        getBoundingClientRect: () => ({ top: 0, bottom: 100 }),
        querySelector: () => line,
        addEventListener: (name, handler) => listeners.set(name, handler),
        removeEventListener: name => listeners.delete(name) };
    const root = { querySelector: () => scroller };
    lyrics.attach(root, { invokeMethodAsync: (name, value) => { changes.push([name, value]); return Promise.resolve(); } });
    return { root, scroller, line, listeners, changes };
}
test('manual scroll pauses following until the current line is requested', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'asset:version');
    assert.equal(f.scroller.scrollTop, 270);
    f.listeners.get('wheel')({}); f.scroller.scrollTop = 0; f.listeners.get('scroll')({});
    assert.deepEqual(f.changes.at(-1), ['FollowChanged', true]);
    f.line.offsetTop = 400; lyrics.update(f.root, 2, 'asset:version');
    assert.equal(f.scroller.scrollTop, 0);
    lyrics.follow(f.root);
    assert.equal(f.scroller.scrollTop, 370);
    assert.deepEqual(f.changes.at(-1), ['FollowChanged', false]);
});
test('version change restores follow and detach removes every interaction listener', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'asset:first');
    f.listeners.get('keydown')({ key: 'PageDown' }); f.scroller.scrollTop = 0;
    lyrics.update(f.root, 1, 'asset:second'); assert.equal(f.scroller.scrollTop, 270);
    lyrics.detach(f.root); assert.equal(f.listeners.size, 0);
    f.scroller.scrollTop = 0; lyrics.update(f.root, 2, 'asset:third'); assert.equal(f.scroller.scrollTop, 0);
});
test('return action is unnecessary while the manually browsed current line remains visible', () => {
    const f = fixture(); lyrics.update(f.root, 1, 'asset:first');
    f.listeners.get('touchstart')({}); f.listeners.get('scroll')({});
    assert.equal(f.changes.some(([, needed]) => needed), false);
});

test('clock selection supports fractional lines, offsets, duplicate tags and reverse seeks',()=>{
    assert.equal(lyrics.activeIndexAt([-.25,1.25,1.25,2.5],1.24),0);
    assert.equal(lyrics.activeIndexAt([-.25,1.25,1.25,2.5],1.25),2);
    assert.equal(lyrics.activeIndexAt([-.25,1.25,1.25,2.5],2.5),3);
    assert.equal(lyrics.activeIndexAt([-.25,1.25,1.25,2.5],0),0);
    assert.equal(lyrics.activeIndexAt([null,NaN,1],.5),-1);
});
