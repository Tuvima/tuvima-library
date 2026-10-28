import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// Loads detailOrigin from app.js with a minimal page: one details scroller, a mutation observer we can fire by hand,
// and listeners we can raise by hand.
function load() {
    const source = readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/app.js', import.meta.url), 'utf8');
    const start = source.indexOf('window.detailOrigin = (() => {');
    const end = source.indexOf('// Dismissing the details overlay', start);
    const listeners = new Map();
    const scroller = { scrollTop: 0 };
    let mutate = () => {};
    let timer = null;
    const document = {
        body: {},
        activeElement: null,
        querySelectorAll: selector => (selector.includes('details-overlay') ? [scroller] : []),
        querySelector: () => null,
        addEventListener: (name, handler) => listeners.set(name, handler),
        removeEventListener: name => listeners.delete(name),
    };
    const window = {
        location: { pathname: '/details/book/1', search: '', href: 'http://x/details/book/1' },
        scrollY: 0,
        requestAnimationFrame: callback => callback(),
        setTimeout: () => 0,
        addEventListener() {},
    };
    class MutationObserver { constructor(cb) { mutate = cb; } observe() {} disconnect() {} }
    const context = {
        window, document, MutationObserver, URL, Date, Number, Array, console,
        sessionStorage: { getItem: () => null, setItem() {} },
        requestAnimationFrame: callback => callback(),
        setTimeout: (callback) => { timer = callback; return 1; },
        HTMLAnchorElement: class {}, Element: class {},
    };
    vm.runInNewContext(source.slice(start, end).replace(/window\.detailOrigin\.initialize\(\);?/, ''), context);
    return { origin: window.detailOrigin, listeners, scroller, fire: () => mutate(), finish: () => timer && timer() };
}

test('late page sections put a fresh details page back at the top until the person scrolls', () => {
    const { origin, scroller, fire } = load();
    origin.fresh('/details/book/1');
    scroller.scrollTop = 300;
    fire();
    assert.equal(scroller.scrollTop, 0, 'before the person scrolls, a late arrival keeps the page at the top');
});

test('late page sections do not reset the scroll once the person has scrolled', () => {
    for (const gesture of ['wheel', 'touchmove', 'keydown', 'pointerdown']) {
        const { origin, scroller, listeners, fire } = load();
        origin.fresh('/details/book/1');
        listeners.get(gesture)();
        scroller.scrollTop = 420;
        fire();
        assert.equal(scroller.scrollTop, 420, `${gesture} hands the page to the person`);
    }
});

test('the takeover listeners are removed when the first second is over', () => {
    const { origin, listeners, finish } = load();
    origin.fresh('/details/book/1');
    assert.equal(listeners.size, 4);
    finish();
    assert.equal(listeners.size, 0);
});
