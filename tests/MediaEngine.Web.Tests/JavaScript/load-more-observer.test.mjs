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

test('watches the nearest scrolling ancestor, or the viewport when nothing scrolls', () => {
    const page = { parentElement: null };
    const pane = { parentElement: page };
    const wrapper = { parentElement: pane };
    const sentinel = { parentElement: wrapper };
    const { dotnet, live } = setup(new Map([[pane, 'auto'], [page, 'scroll']]));

    observe(sentinel, dotnet, {});
    assert.equal(live()[0].options.root, pane);
    assert.equal(live()[0].options.rootMargin, '800px 0px');

    const { dotnet: second, live: secondLive } = setup();
    observe({ parentElement: { parentElement: null } }, second, {});
    assert.equal(secondLive()[0].options.root, null);
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
