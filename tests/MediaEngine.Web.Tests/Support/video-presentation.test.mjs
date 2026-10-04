import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

class Surface {
    constructor() {
        this.listeners = new Map(); this.children = []; this.dataset = {}; this.style = { setProperty() {}, removeProperty() {} };
        this.attributes = {}; this._connected = true; this.clientHeight = 100;
        const values = new Set(); this.classList = { contains: key => values.has(key), toggle: (key, on) => on ? values.add(key) : values.delete(key), remove: key => values.delete(key) };
    }
    addEventListener(name, callback) { if (!this.listeners.has(name)) this.listeners.set(name, new Set()); this.listeners.get(name).add(callback); }
    removeEventListener(name, callback) { this.listeners.get(name)?.delete(callback); }
    emit(name, properties = {}) { for (const callback of this.listeners.get(name) || []) callback({ target: this, ...properties }); }
    querySelector() { return null; }
    querySelectorAll() { return this.children; }
    contains(value) { return this === value || this.children.includes(value); }
    countListeners() { return [...this.listeners.values()].reduce((total, set) => total + set.size, 0); }
    get isConnected() { return this.parentNode ? this.parentNode.isConnected : this._connected; }
    setAttribute(name, value) { this.attributes[name] = value; }
    focus() { this.focusCount = (this.focusCount || 0) + 1; }
    getBoundingClientRect() { return { top: 400, right: 300, width: 280, height: 200 }; }
    append(...nodes) { for (const node of nodes) { node.remove(); this.children.push(node); node.parentNode = this; node._connected = true; } }
    before(node) { const parent = this.parentNode; node.remove(); parent.children.splice(parent.children.indexOf(this), 0, node); node.parentNode = parent; node._connected = true; }
    remove() { if (this.parentNode) this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1); this.parentNode = null; this._connected = false; }
    replaceWith(node) { const parent = this.parentNode; node.remove(); parent.children.splice(parent.children.indexOf(this), 1, node); node.parentNode = parent; node._connected = true; this.parentNode = null; this._connected = false; }
}
function runtime(file) {
    let milliseconds = 0;
    const intervals = new Map(), timeouts = new Map(); let id = 0;
    const document = new Surface(), window = new Surface(); document.body = new Surface(); document.documentElement = new Surface(); document.hidden = false;
    const observers = [];
    document.createComment = () => new Surface();
    const sandbox = { WeakMap, Map, Set, Number, Math, String, Object, document, window,
        performance: { now: () => milliseconds }, queueMicrotask: callback => callback(),
        innerWidth: 320, innerHeight: 720, requestAnimationFrame: callback => callback(),
        matchMedia: () => Object.assign(new Surface(), { matches: true }),
        ResizeObserver: class { observe() {} disconnect() {} }, MutationObserver: class { constructor(callback) { observers.push(callback); } observe() {} disconnect() {} },
        setInterval: callback => { intervals.set(++id, callback); return id; }, clearInterval: key => intervals.delete(key),
        setTimeout: (callback, delay) => { timeouts.set(++id, { callback, delay }); return id; }, clearTimeout: key => timeouts.delete(key) };
    const source = readFileSync(new URL(`../../../src/MediaEngine.Web/wwwroot/js/${file}`, import.meta.url), 'utf8').replaceAll('export function ', 'function ');
    vm.runInNewContext(`${source}\nglobalThis.exports = { attach, update, detach, ${file === 'video-presentation.js' ? 'bindSource' : file === 'playback-chrome.js' ? 'setHold, reveal' : ''} };`, sandbox);
    return { api: sandbox.exports, document, window, intervals, timeouts, observers,
        setViewport: bounds => { window.visualViewport = sandbox.visualViewport = Object.assign(new Surface(), bounds); },
        advance: seconds => { milliseconds += seconds * 1000; for (const callback of intervals.values()) callback(); },
        idle: () => { for (const [key, timer] of [...timeouts]) { timeouts.delete(key); timer.callback(); } } };
}
function media() {
    const video = new Surface(); Object.assign(video, { currentTime:0, paused: false, ended: false, readyState: 4, error: null, currentSrc: 'source-a', src: 'source-a' });
    Object.assign(video.dataset, { playbackAssetId: 'asset-a', playbackRequestVersion: '1', playbackSource: 'source-a' }); return video;
}

function captionRuntime() {
    const source = readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/app.js', import.meta.url), 'utf8');
    const inventory = source.slice(source.indexOf('    function readCaptionTrackChoices('), source.indexOf('    var nativeDefaultCuePlacementLoaded'));
    const selection = source.slice(source.indexOf('    function currentCaptionBinding('), source.indexOf('    function audioObserverFor('));
    const sandbox = {};
    vm.runInNewContext(`${inventory}\n${selection}\nglobalThis.api = { synchronizeCaptionSelection, selectCaptionTrack, readCaptionTrackChoices };`, sandbox);
    return sandbox.api;
}
function captionMedia() {
    const video = media();
    const embedded = { kind: 'subtitles', mode: 'showing', label: 'Subtitles (EN)', language: 'en' };
    const managed = { kind: 'subtitles', mode: 'showing', label: 'EN', language: 'en' };
    const nodes = [{ track: embedded, default: true, dataset: { playbackTrackKey: 'manifest:0' } },
        { track: managed, default: false, dataset: { playbackTrackKey: 'managed:preferred', playbackPreferred: 'true' } }];
    video.textTracks = [embedded, managed]; video.querySelectorAll = () => nodes;
    video._tuvimaVideoBinding = { profile: 'profile-a', asset: 'asset-a', request: 1 };
    return { video, embedded, managed, nodes };
}

test('fresh/default captions and same-item metadata rebind select preferred managed exclusively', () => {
    const api = captionRuntime(), { video, embedded, managed } = captionMedia();
    api.synchronizeCaptionSelection(video);
    assert.equal(embedded.mode, 'disabled'); assert.equal(managed.mode, 'showing');
    assert.equal(api.readCaptionTrackChoices(video).filter(choice => choice.selected).length, 1);
    // Browser metadata can activate defaults again when the mounted media is rebound.
    video.dataset.playbackRequestVersion = '2'; video._tuvimaVideoBinding.request = 2; embedded.mode = 'showing';
    api.synchronizeCaptionSelection(video);
    assert.equal(embedded.mode, 'disabled'); assert.equal(managed.mode, 'showing');
    video.dataset.playbackAssetId = 'asset-b'; video._tuvimaVideoBinding.asset = 'asset-b'; embedded.mode = 'showing';
    api.synchronizeCaptionSelection(video);
    assert.equal(embedded.mode, 'disabled'); assert.equal(managed.mode, 'showing');
});

test('explicit caption Off or embedded choice survives metadata/reopen and stale selection is rejected', () => {
    const api = captionRuntime(), { video, embedded, managed } = captionMedia();
    api.synchronizeCaptionSelection(video);
    assert.equal(api.selectCaptionTrack(video, null, 'asset-a', 1, 'profile-a'), true);
    embedded.mode = 'showing'; managed.mode = 'showing'; api.synchronizeCaptionSelection(video);
    assert.equal(embedded.mode, 'disabled'); assert.equal(managed.mode, 'disabled');
    video.dataset.playbackRequestVersion = '2'; video._tuvimaVideoBinding.request = 2;
    embedded.mode = 'showing'; api.synchronizeCaptionSelection(video); assert.equal(embedded.mode, 'disabled');
    assert.equal(api.selectCaptionTrack(video, 'manifest:0', 'asset-a', 2, 'profile-a'), true);
    managed.mode = 'showing'; api.synchronizeCaptionSelection(video);
    assert.equal(embedded.mode, 'showing'); assert.equal(managed.mode, 'disabled');
    assert.equal(api.selectCaptionTrack(video, 'managed:preferred', 'asset-a', 1, 'profile-a'), false);
    assert.equal(api.selectCaptionTrack(video, 'managed:preferred', 'asset-a', 2, 'profile-b'), false);
    assert.equal(embedded.mode, 'showing'); assert.equal(managed.mode, 'disabled');
});

test('end-card clock advances only during actual visible, unstalled playback and carries source identity', () => {
    const r = runtime('video-presentation.js'), host = new Surface(), video = media(), calls = [];
    r.api.attach(host, video, { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } });
    r.api.bindSource(host, 'profile-a', 'work-a', 'asset-a', 1); r.api.update(host, true);
    r.advance(1); assert.deepEqual(calls[0], ['HandleEndCardTick', 'profile-a', 'work-a', 'asset-a', 1, 1]);
    video.paused = true; r.advance(5); video.paused = false;
    video.emit('waiting'); r.advance(5); video.emit('playing');
    r.document.hidden = true; r.advance(5); r.document.hidden = false; r.document.emit('visibilitychange');
    assert.equal(calls.length, 1);
    r.advance(1); assert.equal(calls.length, 2); assert.equal(calls[1].at(-1), 1);
    r.api.detach(host); assert.equal(r.intervals.size, 0); assert.equal(video.countListeners(), 0); assert.equal(r.document.countListeners(), 0);
});

test('native-ended rejects mismatched and unfinished sources and captures immutable identity for queued callbacks', () => {
    const r = runtime('video-presentation.js'), host = new Surface(), video = media(), calls = [];
    r.api.attach(host, video, { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } });
    r.api.bindSource(host, 'profile-a', 'work-a', 'asset-a', 1);
    video.emit('ended'); assert.equal(calls.length, 0);
    video.ended = true; video.dataset.playbackRequestVersion = '2'; video.emit('ended'); assert.equal(calls.length, 0);
    video.dataset.playbackRequestVersion = '1'; video.emit('ended');
    assert.deepEqual(calls[0], ['HandleNativeVideoEnded', 'profile-a', 'work-a', 'asset-a', 1]);
    video.dataset.playbackSource = 'source-b'; video.dataset.playbackAssetId = 'asset-b'; video.dataset.playbackRequestVersion = '2';
    r.api.bindSource(host, 'profile-b', 'work-b', 'asset-b', 2);
    assert.equal(calls[0][3], 'asset-a'); assert.equal(calls[0][4], 1);
    r.api.detach(host);
});

test('phone sheet restores before conditional removal and stale renders or observers cannot resurrect a portal', () => {
    const r = runtime('playback-popover.js'), root = new Surface(), trigger = new Surface(), panel = new Surface();
    r.document.body.append(root); root.append(trigger, panel); panel.id = 'chapters';
    panel.classList.toggle('playback-popover--sheet', true);
    root.querySelector = () => trigger;
    r.api.attach(root, null, { invokeMethodAsync: () => Promise.resolve() });
    r.api.update(root, panel, true, true);
    assert.equal(panel.parentNode, r.document.body);
    r.api.update(root, null, false, false, true);
    assert.equal(panel.parentNode, root); assert.equal(trigger.focusCount, 1);
    panel.remove(); // Blazor's conditional removal, after the awaited release.
    for (const callback of r.observers) callback();
    r.api.update(root, panel, true, true); // An obsolete render cannot reattach a detached element.
    assert.equal(panel.isConnected, false); assert.equal(r.document.body.children.includes(panel), false);
    const replacement = new Surface(); root.append(replacement); replacement.id = 'chapters-next';
    r.api.update(root, replacement, true, true);
    root.remove(); r.api.detach(root); // Disposal after the marker's owner left also removes the orphan.
    assert.equal(replacement.isConnected, false); assert.equal(replacement.parentNode, null);
    assert.equal(trigger.countListeners(), 0); assert.equal(r.document.countListeners(), 0); assert.equal(r.window.countListeners(), 0);
});

test('audio dock popovers leave seek clear while non-dock anchoring and viewport limits stay intact', () => {
    for (const { dockTop, triggerTop, expectedBottom } of [
        { dockTop: 856, triggerTop: 886, expectedBottom: 848 },
        { dockTop: 200, triggerTop: 230, expectedBottom: 192 },
        { dockTop: null, triggerTop: 886, expectedBottom: 878 }
    ]) {
        const r = runtime('playback-popover.js'), root = new Surface(), trigger = new Surface(), panel = new Surface();
        r.setViewport({ width: 835, height: 1112, offsetLeft: 0, offsetTop: 0 });
        r.document.body.append(root); root.append(trigger, panel); panel.id = 'queue';
        root.querySelector = () => trigger;
        trigger.closest = selector => selector === '.listen-player' && dockTop !== null
            ? { getBoundingClientRect: () => ({ top: dockTop }) } : null;
        trigger.getBoundingClientRect = () => ({ top: triggerTop, right: 820, width: 44, height: 44 });
        panel.getBoundingClientRect = () => ({ width: Number.parseFloat(panel.style.width),
            height: Math.min(300, Number.parseFloat(panel.style.maxHeight)) });
        r.api.attach(root, null, { invokeMethodAsync: () => Promise.resolve() });
        r.api.update(root, panel, true, true);
        const box = panel.getBoundingClientRect();
        assert.equal(Number.parseFloat(panel.style.top) + box.height, expectedBottom);
        assert.equal(Number.parseFloat(panel.style.left), 420, 'horizontal trigger anchoring stays unchanged');
        assert.ok(Number.parseFloat(panel.style.top) >= 8, 'panel remains inside the viewport');
        if (dockTop === 200) assert.equal(Number.parseFloat(panel.style.maxHeight), 184);
        r.api.detach(root);
    }
});

test('video chrome waits three seconds and holds for pause, tool and loading, with complete disposal', () => {
    const r = runtime('playback-chrome.js'), host = new Surface(), video = media(), control = new Surface();
    control.inert = false; host.children.push(control);
    r.api.attach(host, video); assert.equal([...r.timeouts.values()][0].delay, 3000);
    r.idle(); assert.equal(host.classList.contains('playback-chrome-hidden'), true); assert.equal(control.inert, true);
    r.api.setHold(host, 'tool', true); assert.equal(control.inert, false); assert.equal(r.timeouts.size, 0);
    r.api.setHold(host, 'tool', false); assert.equal(r.timeouts.size, 1);
    video.emit('waiting'); assert.equal(r.timeouts.size, 0);
    video.emit('playing'); assert.equal(r.timeouts.size, 1);
    video.paused = true; video.emit('pause'); assert.equal(r.timeouts.size, 0); assert.equal(control.inert, false);
    r.api.detach(host); assert.equal(r.timeouts.size, 0); assert.equal(video.countListeners(), 0); assert.equal(host.countListeners(), 0); assert.equal(r.document.countListeners(), 0);
});

test('advancing media clears a stalled hold, stationary stage hides, controls hover holds, and touch toggles', () => {
    const r=runtime('playback-chrome.js'),host=new Surface(),video=media();
    let controlsHovered=false;
    host.querySelector=selector=>selector==='[data-chrome-hold]:hover' && controlsHovered ? new Surface() : null;
    r.api.attach(host,video);
    video.emit('stalled'); assert.equal(r.timeouts.size,0);
    video.currentTime=1; video.emit('timeupdate'); assert.equal(r.timeouts.size,1);
    r.idle(); assert.equal(host.classList.contains('playback-chrome-hidden'),true);
    host.emit('pointermove'); assert.equal(host.classList.contains('playback-chrome-hidden'),false);
    controlsHovered=true; host.emit('pointermove'); assert.equal(r.timeouts.size,0);
    controlsHovered=false; host.emit('pointermove');
    const target={closest:()=>null}; host.emit('pointerdown',{pointerType:'touch',target});
    assert.equal(host.classList.contains('playback-chrome-hidden'),true);
    host.emit('pointerdown',{pointerType:'touch',target});
    assert.equal(host.classList.contains('playback-chrome-hidden'),false);
    r.api.detach(host);
});
