import test from 'node:test';
import assert from 'node:assert/strict';
import { connect, disconnect, choose } from '../src/MediaEngine.Web/wwwroot/js/artwork-preview.js';

class Transfer {
    files = [];
    items = { add: file => this.files.push(file) };
}
globalThis.DataTransfer = Transfer;

function fixture() {
    const preview = new EventTarget();
    preview.classList = { toggle() {}, remove() {} };
    preview.contains = () => false;
    const input = new EventTarget();
    input.disabled = false;
    input.files = [];
    let changes = 0;
    input.addEventListener('change', () => changes++);
    const drop = files => {
        const event = new Event('drop', { cancelable: true });
        event.dataTransfer = { files };
        preview.dispatchEvent(event);
        return event;
    };
    return { preview, input, drop, changes: () => changes };
}

test('dropping an image triggers the same upload input once, even after rerender', () => {
    const f = fixture();
    connect(f.preview, f.input);
    connect(f.preview, f.input);
    const image = { name: 'cover.png', type: 'image/png' };
    assert.equal(f.drop([image]).defaultPrevented, true);
    assert.deepEqual(f.input.files, [image]);
    assert.equal(f.changes(), 1);
    disconnect(f.preview);
});

test('busy uploads and disposed previews do not start additional uploads', () => {
    const f = fixture();
    connect(f.preview, f.input);
    f.input.disabled = true;
    f.drop([{ name: 'cover.png' }]);
    assert.equal(f.changes(), 0);
    f.input.disabled = false;
    disconnect(f.preview);
    f.drop([{ name: 'cover.png' }]);
    assert.equal(f.changes(), 0);
});

test('one file is uploaded per drop and empty drops are ignored', () => {
    const f = fixture();
    connect(f.preview, f.input);
    f.drop([]);
    assert.equal(f.changes(), 0);
    f.drop([{ name: 'first.png' }, { name: 'second.png' }]);
    assert.deepEqual(f.input.files, [{ name: 'first.png' }]);
    assert.equal(f.changes(), 1);
    disconnect(f.preview);
});

test('device picker resets the input so the same file can be chosen again', () => {
    let clicked = false;
    const input = { value: 'old.png', click() { clicked = true; } };
    choose(input);
    assert.equal(input.value, '');
    assert.equal(clicked, true);
});
