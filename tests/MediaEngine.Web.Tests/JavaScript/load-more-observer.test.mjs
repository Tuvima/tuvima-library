import test from 'node:test';
import assert from 'node:assert/strict';

const { observe, disconnect } = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/load-more-observer.js', import.meta.url).href);

// A stand-in for the browser's IntersectionObserver: it keeps what it was asked to watch and lets a test deliver entries.
class FakeIntersectionObserver {
    static instances = [];
    constructor(callback, options) {
        this.callback = callback;
        this.options = options;
        this.targets = [];
        this.disconnected = false;
        FakeIntersectionObserver.instances.push(this);
    }
    observe(target) { this.targets.push(target); }
    disconnect() { this.disconnected = true; this.targets = []; }
    deliver(isIntersecting) { this.callback([{ isIntersecting }]); }
}

function setup(overflowByElement = new Map()) {
    FakeIntersectionObserver.instances = [];
    globalThis.IntersectionObserver = FakeIntersectionObserver;
    globalThis.getComputedStyle = element => ({ overflowY: overflowByElement.get(element) ?? 'visible' });
    const calls = [];
    const dotnet = { invokeMethodAsync: method => { calls.push(method); return Promise.resolve(); } };
    const live = () => FakeIntersectionObserver.instances.filter(instance => !instance.disconnected);
    return { calls, dotnet, live };
}

test('asks .NET for more when the sentinel comes near the screen and stays quiet otherwise', () => {
    const { calls, dotnet, live } = setup();
    const sentinel = { parentElement: null };
    observe(sentinel, dotnet, {});

    assert.equal(live().length, 1);
    assert.deepEqual(live()[0].targets, [sentinel]);
    live()[0].deliver(false);
    assert.deepEqual(calls, []);
    live()[0].deliver(true);
    assert.deepEqual(calls, ['LoadMoreFromObserverAsync']);
});

// An element as the page sees it: a tag, a parent, and how much content it holds compared with its box.
function el(tag, parent, { scrollHeight = 100, clientHeight = 100 } = {}) {
    return { tagName: tag, parentElement: parent, scrollHeight, clientHeight };
}

test('watches the nearest container that really scrolls', () => {
    const body = el('BODY', null, { scrollHeight: 5000, clientHeight: 800 });
    const outer = el('DIV', body, { scrollHeight: 4000, clientHeight: 700 });
    const pane = el('DIV', outer, { scrollHeight: 3000, clientHeight: 600 });
    const sentinel = el('DIV', el('DIV', pane));
    const { dotnet, live } = setup(new Map([[pane, 'auto'], [outer, 'scroll']]));

    observe(sentinel, dotnet, {});

    assert.equal(live()[0].options.root, pane);
    assert.equal(live()[0].options.rootMargin, '800px 0px');
});

test('an overflow-x hidden wrapper that does not really scroll is not the root', () => {
    // overflow-x: hidden makes overflow-y compute to auto on a wrapper with no fixed height: its content is as tall as itself.
    const wrapper = el('DIV', el('BODY', el('HTML', null)), { scrollHeight: 2400, clientHeight: 2400 });
    const sentinel = el('DIV', wrapper);
    const { dotnet, live } = setup(new Map([[wrapper, 'auto']]));

    observe(sentinel, dotnet, {});

    assert.equal(live()[0].options.root, null);
});

test('body and html never become the root, even when they report scrolling', () => {
    const html = el('HTML', null, { scrollHeight: 9000, clientHeight: 800 });
    const body = el('BODY', html, { scrollHeight: 9000, clientHeight: 800 });
    const sentinel = el('DIV', body);
    const { dotnet, live } = setup(new Map([[body, 'auto'], [html, 'scroll']]));

    observe(sentinel, dotnet, {});

    assert.equal(live()[0].options.root, null);
});

test('with no scrolling ancestor at all the viewport is watched', () => {
    const { dotnet, live } = setup();
    observe(el('DIV', el('DIV', null)), dotnet, {});
    assert.equal(live()[0].options.root, null);
});

test('a failed call into .NET is swallowed', async () => {
    const { live } = setup();
    const failing = { invokeMethodAsync: () => Promise.reject(new Error('disposed')) };
    observe(el('DIV', null), failing, {});

    live()[0].deliver(true);
    await new Promise(resolve => setImmediate(resolve));
});

test('observing the same sentinel again replaces the observer and asks again while the sentinel is still near', () => {
    const { calls, dotnet, live } = setup();
    const sentinel = { parentElement: null };
    observe(sentinel, dotnet, {});
    live()[0].deliver(true);
    observe(sentinel, dotnet, {});

    assert.equal(live().length, 1, 'the first observer was released');
    live()[0].deliver(true);
    assert.deepEqual(calls, ['LoadMoreFromObserverAsync', 'LoadMoreFromObserverAsync']);
});

test('options choose the margin and the method that is called', () => {
    const { calls, dotnet, live } = setup();
    observe({ parentElement: null }, dotnet, { rootMargin: '300px 0px', method: 'NextPage' });

    assert.equal(live()[0].options.rootMargin, '300px 0px');
    live()[0].deliver(true);
    assert.deepEqual(calls, ['NextPage']);
});

test('disconnect stops the sentinel from asking and is safe to repeat', () => {
    const { dotnet, live } = setup();
    const sentinel = { parentElement: null };
    observe(sentinel, dotnet, {});
    disconnect(sentinel);

    assert.equal(live().length, 0);
    disconnect(sentinel);
    disconnect(null);
});
