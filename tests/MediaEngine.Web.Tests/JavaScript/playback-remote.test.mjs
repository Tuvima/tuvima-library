// Run with: node --test tests/MediaEngine.Web.Tests/JavaScript
// Covers Play on another device (unified player v2 D28): the decisions of the Remote Playback module against a fake `remote` object.
import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const remoteModule = await import(pathToFileURL(path.resolve(import.meta.dirname, '../../../src/MediaEngine.Web/wwwroot/js/playback-remote.js')).href);
const { supportOf, stateAfter, stateOf, deviceNameOf, isQuietPromptRejection, planSourceSwap, planRestore, attach, openPicker, stop, detach } = remoteModule;

function fakeRemote({ state = 'disconnected', promptError = null, availabilityError = null } = {}) {
    const listeners = {};
    const remote = {
        state,
        prompts: 0,
        watched: 0,
        addEventListener(name, fn) { (listeners[name] ??= []).push(fn); },
        removeEventListener(name, fn) { listeners[name] = (listeners[name] ?? []).filter(f => f !== fn); },
        fire(name) { (listeners[name] ?? []).forEach(fn => fn({ type: name })); },
        listenerCount: name => (listeners[name] ?? []).length,
        async prompt() { remote.prompts++; if (promptError) throw promptError; },
        async watchAvailability(callback) {
            remote.watched++;
            if (availabilityError) throw availabilityError;
            callback(true);
            return 7;
        },
        cancelWatchAvailability() { return Promise.resolve(); },
    };
    return remote;
}

function fakeElement(remote, extra = {}) {
    const attributes = { src: 'https://dashboard.local/api/v1/stream/abc' };
    const listeners = {};
    return {
        remote,
        dataset: { playbackSource: 'https://dashboard.local/api/v1/stream/abc' },
        currentTime: 42,
        paused: false,
        disableRemotePlayback: false,
        getAttribute: name => attributes[name] ?? null,
        setAttribute: (name, value) => { attributes[name] = value; },
        removeAttribute: name => { delete attributes[name]; },
        addEventListener: (name, fn) => { (listeners[name] ??= []).push(fn); },
        fireLoaded() { (listeners.loadedmetadata ?? []).splice(0).forEach(fn => fn()); },
        load() { },
        play: () => Promise.resolve(),
        attributes,
        ...extra,
    };
}

function fakeReceiver() {
    const calls = [];
    return { calls, invokeMethodAsync: (name, ...args) => { calls.push([name, ...args]); return Promise.resolve(); } };
}

test('an element without the Remote Playback API is unsupported, so the row is disabled', () => {
    assert.equal(supportOf({}), 'unsupported');
    assert.equal(supportOf({ remote: {} }), 'unsupported');
    assert.equal(supportOf(null), 'unsupported');
});

test('an element whose remote can prompt is supported, so the row is enabled', () => {
    assert.equal(supportOf({ remote: fakeRemote() }), 'supported');
});

test('connecting, connect and disconnect move between local, connecting and remote, and forget the device when it goes', () => {
    assert.deepEqual(stateAfter('connecting', {}), { state: 'connecting', deviceName: null });
    assert.deepEqual(stateAfter('connect', {}), { state: 'remote', deviceName: null });
    assert.deepEqual(stateAfter('disconnect', { deviceName: 'Living room TV' }), { state: 'local', deviceName: null });
    assert.equal(stateAfter('something-else', {}), null);
});

test('the receiver name is used when the browser exposes one and is otherwise absent', () => {
    assert.equal(deviceNameOf({ deviceName: '  Living room TV ' }), 'Living room TV');
    assert.equal(deviceNameOf({}), null);
    assert.equal(deviceNameOf(undefined), null);
    assert.deepEqual(stateAfter('connect', { deviceName: 'Living room TV' }), { state: 'remote', deviceName: 'Living room TV' });
});

test('a remote that is already connected when the page attaches reads as remote', () => {
    assert.equal(stateOf({ state: 'connected' }).state, 'remote');
    assert.equal(stateOf({ state: 'connecting' }).state, 'connecting');
    assert.equal(stateOf({ state: 'disconnected' }).state, 'local');
});

test('a dismissed picker and a missing device are quiet rejections; other errors are not', () => {
    assert.equal(isQuietPromptRejection({ name: 'NotAllowedError' }), true);
    assert.equal(isQuietPromptRejection({ name: 'NotFoundError' }), true);
    assert.equal(isQuietPromptRejection({ name: 'InvalidStateError' }), false);
    assert.equal(isQuietPromptRejection(null), false);
});

test('the source swap points the element at the cast link and remembers the page source and the position', () => {
    const plan = planSourceSwap({ castUrl: 'http://192.168.1.5:5016/cast/v1/g/media', playbackSource: '/stream/a', currentSrc: '/stream/a', currentTime: 61.5, paused: false });
    assert.deepEqual(plan, { castUrl: 'http://192.168.1.5:5016/cast/v1/g/media', restoreSource: '/stream/a', position: 61.5, resume: true });
    assert.equal(planSourceSwap({ castUrl: '', playbackSource: '/stream/a', currentTime: 5, paused: true }), null);
    assert.equal(planSourceSwap({ castUrl: '/cast/x', currentSrc: '/cast/x', currentTime: 5, paused: true }), null);
    assert.equal(planSourceSwap({ castUrl: '/cast/x', playbackSource: '/stream/a', currentTime: Number.NaN, paused: true }).resume, false);
});

test('putting the page source back uses the position the receiver reached, or the one before the swap', () => {
    const swap = { castUrl: '/cast/x', restoreSource: '/stream/a', position: 10, resume: true };
    assert.deepEqual(planRestore(swap, { currentTime: 95 }), { source: '/stream/a', position: 95, resume: true });
    assert.deepEqual(planRestore(swap, { currentTime: 0 }), { source: '/stream/a', position: 10, resume: true });
    assert.equal(planRestore(null, { currentTime: 4 }), null);
});

test('attaching reports unsupported without touching anything when the API is missing', async () => {
    const element = fakeElement(undefined);
    const state = await attach(element, fakeReceiver());
    assert.equal(state.support, 'unsupported');
    assert.equal(state.state, 'local');
});

test('attaching to a supported element watches availability', async () => {
    const remote = fakeRemote();
    const state = await attach(fakeElement(remote), fakeReceiver());
    assert.equal(state.support, 'supported');
    assert.equal(state.availability, 'available');
    assert.equal(remote.watched, 1);
});

test('availability falls back to unknown when watching throws, and the row stays usable', async () => {
    const remote = fakeRemote({ availabilityError: { name: 'NotSupportedError' } });
    const state = await attach(fakeElement(remote), fakeReceiver());
    assert.equal(state.support, 'supported');
    assert.equal(state.availability, 'unknown');
});

test('connect tells the page the state is remote and disconnect brings it back to local', async () => {
    const remote = fakeRemote();
    const receiver = fakeReceiver();
    await attach(fakeElement(remote), receiver);
    remote.fire('connecting');
    remote.fire('connect');
    remote.fire('disconnect');
    assert.deepEqual(receiver.calls, [
        ['OnRemoteStateChanged', 'connecting', null],
        ['OnRemoteStateChanged', 'remote', null],
        ['OnRemoteStateChanged', 'local', null],
    ]);
});

test('prompting swaps in the cast link first and keeps it when a device is chosen', async () => {
    const remote = fakeRemote();
    const element = fakeElement(remote);
    await attach(element, fakeReceiver());
    const result = await openPicker(element, 'http://192.168.1.5:5016/cast/v1/g/media');
    assert.deepEqual(result, { outcome: 'prompted', swapped: true });
    assert.equal(remote.prompts, 1);
    assert.equal(element.attributes.src, 'http://192.168.1.5:5016/cast/v1/g/media');
    assert.equal(element.dataset.playbackSource, 'https://dashboard.local/api/v1/stream/abc', 'the page keeps its own source marker');
});

test('a cancelled picker is handled quietly and the page source is put back', async () => {
    const remote = fakeRemote({ promptError: { name: 'NotAllowedError' } });
    const element = fakeElement(remote);
    await attach(element, fakeReceiver());
    const result = await openPicker(element, 'http://192.168.1.5:5016/cast/v1/g/media');
    assert.deepEqual(result, { outcome: 'cancelled', reason: 'NotAllowedError' });
    assert.equal(element.dataset.playbackSource, undefined, 'the page source routine may run again');
});

test('an unexpected prompt error is reported as a failure, not swallowed', async () => {
    const remote = fakeRemote({ promptError: { name: 'InvalidStateError' } });
    const element = fakeElement(remote);
    await attach(element, fakeReceiver());
    const result = await openPicker(element, null);
    assert.deepEqual(result, { outcome: 'failed', reason: 'InvalidStateError' });
});

test('prompting an unsupported element opens nothing', async () => {
    const element = fakeElement(undefined);
    await attach(element, fakeReceiver());
    assert.deepEqual(await openPicker(element, 'http://192.168.1.5:5016/cast/v1/g/media'), { outcome: 'unsupported' });
});

test('stopping switches remote playback off for the element and the disconnect switches it back on', async () => {
    const remote = fakeRemote();
    const element = fakeElement(remote);
    await attach(element, fakeReceiver());
    assert.equal(stop(element), true);
    assert.equal(element.disableRemotePlayback, true);
    remote.fire('disconnect');
    assert.equal(element.disableRemotePlayback, false);
    detach(element);
});

test('detaching removes the listeners so a closed bar hears nothing more', async () => {
    const remote = fakeRemote();
    const element = fakeElement(remote);
    await attach(element, fakeReceiver());
    assert.equal(remote.listenerCount('connect'), 1);
    detach(element);
    assert.equal(remote.listenerCount('connect'), 0);
    assert.equal(remote.listenerCount('disconnect'), 0);
});

test('the by-id entry points find the element on the page and answer unsupported when it is missing', async () => {
    const remote = fakeRemote();
    const element = fakeElement(remote);
    globalThis.document = { getElementById: id => (id === 'listen-audio-engine' ? element : null) };
    try {
        assert.equal((await remoteModule.attachTo('listen-audio-engine', fakeReceiver())).support, 'supported');
        assert.equal((await remoteModule.attachTo('tuvima-video-engine', fakeReceiver())).support, 'unsupported');
        assert.deepEqual(await remoteModule.openPickerOn('tuvima-video-engine', null), { outcome: 'unsupported' });
        assert.equal(remoteModule.stopOn('tuvima-video-engine'), false);
        assert.equal(remoteModule.followOn('listen-audio-engine', 'http://192.168.1.5:5016/cast/v1/g/media'), true);
        remoteModule.detachFrom('listen-audio-engine');
        assert.equal(remote.listenerCount('connect'), 0);
    } finally {
        delete globalThis.document;
    }
});

test('readOn reports the current availability by element id, and unsupported for a missing element', async () => {
    const remote = fakeRemote();
    const element = fakeElement(remote);
    globalThis.document = { getElementById: id => (id === 'listen-audio-engine' ? element : null) };
    try {
        await remoteModule.attachTo('listen-audio-engine', fakeReceiver());
        assert.equal(remoteModule.readOn('listen-audio-engine').availability, 'available');
        assert.equal(remoteModule.readOn('tuvima-video-engine').support, 'unsupported');
        remoteModule.detachFrom('listen-audio-engine');
    } finally {
        delete globalThis.document;
    }
});
