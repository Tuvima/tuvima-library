// Run with: node --test tests/MediaEngine.Web.Tests/JavaScript
// Covers the pure video touch logic (tap classification) and the shared media session arbitration.
import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const js = name => pathToFileURL(path.resolve(import.meta.dirname, '../../../src/MediaEngine.Web/wwwroot/js', name)).href;
const touch = await import(js('video-touch.js'));
const rect = { left: 0, width: 900 };

test('regions split the video into thirds', () => {
    assert.equal(touch.regionFor(10, 0, 900), 'left');
    assert.equal(touch.regionFor(450, 0, 900), 'center');
    assert.equal(touch.regionFor(890, 0, 900), 'right');
    assert.equal(touch.regionFor(310, 10, 900), 'center');
    assert.equal(touch.regionFor(5, 0, 0), 'center');
});

test('a single tap only reveals the controls', () => {
    const tracker = touch.createTapTracker();
    assert.deepEqual(tracker.tap(1000, 100, 100, rect), { kind: 'single' });
});

test('double tap on the left third seeks back ten seconds and on the right seeks forward', () => {
    const tracker = touch.createTapTracker();
    tracker.tap(1000, 100, 100, rect);
    assert.deepEqual(tracker.tap(1250, 110, 105, rect), { kind: 'seek', side: 'left', delta: -10, total: -10 });
    tracker.reset();
    tracker.tap(5000, 800, 100, rect);
    assert.deepEqual(tracker.tap(5100, 800, 100, rect), { kind: 'seek', side: 'right', delta: 10, total: 10 });
});

test('double tap in the middle toggles play', () => {
    const tracker = touch.createTapTracker();
    tracker.tap(1000, 450, 100, rect);
    assert.deepEqual(tracker.tap(1200, 450, 100, rect), { kind: 'toggle' });
});

test('taps that are too slow or too far apart are not a double tap', () => {
    const tracker = touch.createTapTracker();
    tracker.tap(1000, 100, 100, rect);
    assert.equal(tracker.tap(1301, 100, 100, rect).kind, 'single');
    tracker.reset();
    tracker.tap(1000, 100, 100, rect);
    assert.equal(tracker.tap(1100, 100 + touch.DOUBLE_TAP_PX + 1, 100, rect).kind, 'single');
});

test('further taps on the same side keep seeking and the count builds up', () => {
    const tracker = touch.createTapTracker();
    tracker.tap(1000, 800, 100, rect);
    tracker.tap(1100, 800, 100, rect);
    assert.deepEqual(tracker.tap(1500, 800, 100, rect), { kind: 'seek', side: 'right', delta: 10, total: 20 });
    assert.equal(tracker.tap(1900, 800, 100, rect).total, 30);
});

test('the seek run ends after a pause or on the other side', () => {
    const tracker = touch.createTapTracker();
    tracker.tap(1000, 800, 100, rect);
    tracker.tap(1100, 800, 100, rect);
    assert.equal(tracker.tap(1100 + touch.SEEK_RUN_MS + 1, 800, 100, rect).kind, 'single');
    tracker.reset();
    tracker.tap(1000, 800, 100, rect);
    tracker.tap(1100, 800, 100, rect);
    assert.equal(tracker.tap(1400, 100, 100, rect).kind, 'single');
});

test('seek feedback text uses a real minus sign and plain sentences', () => {
    assert.equal(touch.formatSeek(-10), '\u221210 s');
    assert.equal(touch.formatSeek(20), '+20 s');
    assert.equal(touch.describeSeek(-10), 'Skipped back 10 seconds');
    assert.equal(touch.describeSeek(30), 'Skipped forward 30 seconds');
});

function fakeElement() {
    const listeners = new Map();
    return {
        paused: true, duration: 100, currentTime: 5, playbackRate: 1,
        addEventListener(name, fn) { listeners.set(name, fn); },
        removeEventListener(name) { listeners.delete(name); },
        fire(name) { listeners.get(name)?.(); },
    };
}

function fakeReceiver(log, name) {
    return { invokeMethodAsync: (method, action) => { log.push([name, method, action]); return Promise.resolve(); } };
}

test('the video owns the media session while it has metadata and hands it back afterwards', async () => {
    const handlers = {};
    globalThis.MediaMetadata = class { constructor(value) { Object.assign(this, value); } };
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { mediaSession: { metadata: null, playbackState: 'none', setActionHandler(action, fn) { handlers[action] = fn; }, setPositionState() {} } } });
    const session = navigator.mediaSession;
    const { attach, metadata, attachVideo, videoMetadata, detachVideo } = await import(js('playback-media-session.js'));
    const log = [], audio = fakeElement(), video = fakeElement();
    attach(audio, fakeReceiver(log, 'audio'));
    attachVideo(video, fakeReceiver(log, 'video'));
    metadata({ title: 'Song' });
    assert.equal(session.metadata.title, 'Song');
    handlers.play({});
    assert.deepEqual(log.pop(), ['audio', 'MediaSessionActionAsync', 'play']);

    videoMetadata({ title: 'Episode', artist: 'Show', album: 'Season 2', artwork: [{ src: 'https://x/a.jpg' }] });
    assert.equal(session.metadata.title, 'Episode');
    assert.equal(session.metadata.album, 'Season 2');
    handlers.pause({});
    assert.deepEqual(log.pop(), ['video', 'MediaSessionActionAsync', 'pause']);
    video.paused = false; video.fire('play');
    assert.equal(session.playbackState, 'playing');

    metadata(null);
    assert.equal(session.metadata.title, 'Episode', 'the audio player must not clear the lock screen while video plays');

    videoMetadata(null);
    assert.equal(session.metadata, null);
    handlers.play({});
    assert.deepEqual(log.pop(), ['audio', 'MediaSessionActionAsync', 'play']);

    metadata({ title: 'Song 2' });
    videoMetadata({ title: 'Film' });
    detachVideo(video);
    assert.equal(session.metadata.title, 'Song 2');
});

test('attaching again with a fresh receiver keeps the owner and its metadata', async () => {
    const handlers = {};
    globalThis.MediaMetadata = class { constructor(value) { Object.assign(this, value); } };
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { mediaSession: { metadata: null, playbackState: 'none', setActionHandler(action, fn) { handlers[action] = fn; }, setPositionState() {} } } });
    const session = navigator.mediaSession;
    const { attachVideo, videoMetadata, detachVideo } = await import(js('playback-media-session.js') + '?fresh');
    const log = [], video = fakeElement();
    attachVideo(video, fakeReceiver(log, 'first'));
    videoMetadata({ title: 'Episode' });
    attachVideo(video, fakeReceiver(log, 'second'));
    assert.equal(session.metadata.title, 'Episode');
    handlers.play({});
    assert.deepEqual(log.pop(), ['second', 'MediaSessionActionAsync', 'play']);
    detachVideo(video);
    assert.equal(session.metadata, null);
});
