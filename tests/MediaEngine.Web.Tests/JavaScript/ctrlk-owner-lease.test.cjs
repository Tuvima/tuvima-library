const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const repositoryRoot = path.resolve(__dirname, '../../..');
const source = fs.readFileSync(path.join(repositoryRoot, 'src/MediaEngine.Web/wwwroot/app.js'), 'utf8').replace(/\r\n/g, '\n');
const start = source.indexOf('window.registerCtrlK = function');
const end = source.indexOf('// -- Device Context', start);
assert.ok(start >= 0 && end > start, 'Ctrl+K owner lease bridge is present');

function createHarness() {
    const listeners = new Set();
    const document = {
        addEventListener: (_, handler) => listeners.add(handler),
        removeEventListener: (_, handler) => listeners.delete(handler)
    };
    const window = {};
    vm.runInNewContext(source.slice(start, end), { window, document });
    return { window, listeners };
}

test('old layout cleanup cannot unregister the replacement Ctrl+K owner', () => {
    const { window, listeners } = createHarness();
    const oldRef = { invokeMethodAsync() { throw new Error('old layout must not receive the shortcut'); } };
    let opened = 0;
    const newRef = { invokeMethodAsync() { opened++; } };

    window.registerCtrlK('old-layout', oldRef, 'old-owner');
    window.registerCtrlK('new-layout', newRef, 'new-owner');
    window.unregisterCtrlK('old-owner');

    assert.equal(listeners.size, 1);
    listeners.values().next().value({ ctrlKey: true, key: 'k', preventDefault() {} });
    assert.equal(opened, 1);

    window.unregisterCtrlK('new-owner');
    assert.equal(listeners.size, 0);
});

test('legacy no-owner unregister remains unconditional', () => {
    const { window, listeners } = createHarness();
    const ref = { invokeMethodAsync() {} };

    window.registerCtrlK(ref);
    assert.equal(listeners.size, 1);
    window.unregisterCtrlK();
    assert.equal(listeners.size, 0);
});
