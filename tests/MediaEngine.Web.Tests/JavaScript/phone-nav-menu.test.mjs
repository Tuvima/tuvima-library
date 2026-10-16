import test from 'node:test';
import assert from 'node:assert/strict';

const moduleUrl = new URL('../../../src/MediaEngine.Web/wwwroot/js/phone-nav-menu.js', import.meta.url).href;
let imports = 0;

// A tiny browser: a history stack, a popstate hook that app.js registers ahead of Blazor, and a stand-in for Blazor's own listener.
function browser({ withHook = true } = {}) {
    const entries = [{ url: '/page', state: { _index: 1 } }];
    let index = 0;
    const listeners = [];
    const win = { blazorPops: 0, addEventListener: (type, fn, capture) => listeners.push({ type, fn, capture }), removeEventListener: (type, fn) => {
        const at = listeners.findIndex(item => item.fn === fn);
        if (at >= 0) listeners.splice(at, 1);
    } };
    if (withHook) win.tuvimaPopHook = null;
    const loc = { get href() { return entries[index].url; } };
    const hist = {
        get state() { return entries[index].state; },
        pushState(state, _title, url) { entries.splice(index + 1); entries.push({ url, state }); index++; },
        back() { queueMicrotask(() => pop(index - 1)); },
    };
    function pop(target) {
        index = Math.max(0, target);
        const event = { stopped: false, stopImmediatePropagation() { this.stopped = true; } };
        if (withHook) win.tuvimaPopHook?.(event);
        for (const item of [...listeners].filter(l => l.type === 'popstate' && l.capture)) { if (!event.stopped) item.fn(event); }
        if (!event.stopped) win.blazorPops++; // Blazor's own listener runs last
    }
    Object.assign(globalThis, { window: win, history: hist, location: loc });
    return { win, entries, pop, current: () => entries[index], listeners };
}

async function menu() {
    return import(`${moduleUrl}?${++imports}`);
}

function dotnet() {
    const calls = [];
    return { calls, invokeMethodAsync: async name => { calls.push(name); } };
}

test('attach adds one history entry that the menu owns', async () => {
    const env = browser();
    const { attach } = await menu();
    attach(dotnet());
    assert.equal(env.entries.length, 2);
    assert.equal(env.current().state.tuvimaPhoneMenu, true);
    assert.equal(env.current().state._index, 1, 'the entry keeps the app state it was opened from');
});

test('Back closes the menu, stays on the page and is hidden from Blazor', async () => {
    const env = browser();
    const { attach } = await menu();
    const host = dotnet();
    attach(host);
    env.pop(0);
    await Promise.resolve();
    assert.deepEqual(host.calls, ['CloseFromBack']);
    assert.equal(env.win.blazorPops, 0);
    assert.equal(env.win.tuvimaPopHook, null, 'the hook is released');
});

test('closing another way removes the entry without telling Blazor or the menu', async () => {
    const env = browser();
    const { attach, detach } = await menu();
    const host = dotnet();
    attach(host);
    await detach(true);
    assert.equal(env.current().url, '/page');
    assert.equal(env.current().state.tuvimaPhoneMenu, undefined);
    assert.deepEqual(host.calls, []);
    assert.equal(env.win.blazorPops, 0);
});

test('detach never goes back when the page moved on while the menu was open', async () => {
    const env = browser();
    const { attach, detach } = await menu();
    attach(dotnet());
    history.pushState({}, '', '/elsewhere');
    await detach(true);
    assert.equal(env.current().url, '/elsewhere');
    assert.equal(env.entries.length, 3);
});

test('jumping further back is left to the app and still closes the menu', async () => {
    const env = browser();
    const { attach } = await menu();
    const host = dotnet();
    history.pushState({ _index: 2 }, '', '/other');
    attach(host);
    env.pop(0);
    await Promise.resolve();
    assert.deepEqual(host.calls, ['CloseFromBack']);
    assert.equal(env.win.blazorPops, 1, 'Blazor handles the real navigation');
});

test('without the app.js hook a capture listener is used and removed again', async () => {
    const env = browser({ withHook: false });
    const { attach, detach } = await menu();
    attach(dotnet());
    assert.equal(env.listeners.filter(item => item.type === 'popstate' && item.capture).length, 1);
    await detach(true);
    assert.equal(env.listeners.filter(item => item.type === 'popstate').length, 0);
});
