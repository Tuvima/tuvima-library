import test from 'node:test';
import assert from 'node:assert/strict';

class Dialog {
    constructor() { this.open = false; this.isConnected = true; this.listeners = new Map(); }
    addEventListener(name, fn) { if (!this.listeners.has(name)) this.listeners.set(name, new Set()); this.listeners.get(name).add(fn); }
    removeEventListener(name, fn) { this.listeners.get(name)?.delete(fn); }
    emit(name, event) { for (const fn of this.listeners.get(name) ?? []) fn(event); }
    showModal() { this.open = true; }
    close() { this.open = false; }
    focus() { }
}
globalThis.document = { activeElement: null };

const { setDrawerOpen, disposeDrawer } = await import(new URL('../../../src/MediaEngine.Web/wwwroot/native-structure.js', import.meta.url).href);

function dotnetSpy() {
    const calls = [];
    return { calls, invokeMethodAsync: name => { calls.push(name); return Promise.resolve(); } };
}

function cancelEvent() {
    return { defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
}

test('Escape keeps the browser from closing the drawer and asks .NET to close it', () => {
    const dialog = new Dialog();
    const dotnet = dotnetSpy();
    setDrawerOpen(dialog, true, dotnet);
    setDrawerOpen(dialog, true, dotnet);

    const event = cancelEvent();
    dialog.emit('cancel', event);

    assert.equal(event.defaultPrevented, true);
    assert.deepEqual(dotnet.calls, ['CloseFromEscapeAsync']);
    assert.equal(dialog.open, true);
});

test('Disposing the drawer stops listening for Escape', () => {
    const dialog = new Dialog();
    const dotnet = dotnetSpy();
    setDrawerOpen(dialog, true, dotnet);
    disposeDrawer(dialog);

    dialog.emit('cancel', cancelEvent());

    assert.equal(dialog.open, false);
    assert.deepEqual(dotnet.calls, []);
});
