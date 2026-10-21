import assert from 'node:assert/strict';
import test from 'node:test';

// vault-lock.js listens on `document`; a tiny stand-in records the listeners and lets a test fire events at them.
function installDocument() {
    const listeners = new Map();
    globalThis.document = {
        addEventListener(name, handler) {
            listeners.set(name, [...(listeners.get(name) ?? []), handler]);
        },
        removeEventListener(name, handler) {
            listeners.set(name, (listeners.get(name) ?? []).filter((candidate) => candidate !== handler));
        },
    };
    return {
        fire(name, event = {}) {
            for (const handler of listeners.get(name) ?? []) {
                handler(event);
            }
        },
        count(name) {
            return (listeners.get(name) ?? []).length;
        },
    };
}

async function freshModule() {
    return import(`../../../src/MediaEngine.Web/wwwroot/js/vault-lock.js?${Math.random()}`);
}

test('pressing Escape twice quickly asks the page to lock, once', async () => {
    const dom = installDocument();
    const calls = [];
    const module = await freshModule();
    const realNow = Date.now;
    let now = 1_000_000;
    Date.now = () => now;
    try {
        module.start({ invokeMethodAsync: (name) => calls.push(name) });

        dom.fire('keydown', { key: 'Escape' });
        assert.ok(!calls.includes('OnDoubleEscape'), 'one Escape locks nothing');

        now += 300;
        dom.fire('keydown', { key: 'Escape' });
        assert.equal(calls.filter((name) => name === 'OnDoubleEscape').length, 1);

        // The count starts over: a third Escape on its own does not lock again.
        now += 100;
        dom.fire('keydown', { key: 'Escape' });
        assert.equal(calls.filter((name) => name === 'OnDoubleEscape').length, 1);
    } finally {
        Date.now = realNow;
        module.stop();
    }
});

test('two Escapes far apart do not lock', async () => {
    const dom = installDocument();
    const calls = [];
    const module = await freshModule();
    const realNow = Date.now;
    let now = 2_000_000;
    Date.now = () => now;
    try {
        module.start({ invokeMethodAsync: (name) => calls.push(name) });
        dom.fire('keydown', { key: 'Escape' });
        now += 5_000;
        dom.fire('keydown', { key: 'Escape' });
        assert.ok(!calls.includes('OnDoubleEscape'));
    } finally {
        Date.now = realNow;
        module.stop();
    }
});

test('real use reports activity at most every thirty seconds, and stop lets go of every listener', async () => {
    const dom = installDocument();
    const calls = [];
    const module = await freshModule();
    const realNow = Date.now;
    let now = 3_000_000;
    Date.now = () => now;
    try {
        module.start({ invokeMethodAsync: (name) => calls.push(name) });
        dom.fire('pointerdown');
        dom.fire('pointerdown');
        assert.equal(calls.filter((name) => name === 'OnActivity').length, 1);
        now += 31_000;
        dom.fire('wheel');
        assert.equal(calls.filter((name) => name === 'OnActivity').length, 2);
    } finally {
        Date.now = realNow;
        module.stop();
    }

    for (const name of ['keydown', 'pointerdown', 'wheel', 'touchstart']) {
        assert.equal(dom.count(name), 0, `${name} listener released`);
    }
});
