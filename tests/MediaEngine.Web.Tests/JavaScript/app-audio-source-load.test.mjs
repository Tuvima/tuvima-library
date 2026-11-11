import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

// hls.js loads on first use, so a playback start can wait several seconds for it. These tests drive that wait by hand and
// check what a Pause, Close, release, second start, cast swap or failed load does to the start that is still waiting.
const source = await readFile(new URL("../../../src/MediaEngine.Web/wwwroot/js/playback-core.js", import.meta.url), "utf8");
function block(start, end) {
    const a = source.indexOf(start), b = source.indexOf(end, a);
    assert.ok(a >= 0 && b > a, `block ${start}`);
    return source.slice(a, b).trim().replace(/,$/, "");
}

const STREAM = "/stream/a.m3u8";

function harness() {
    const loads = [];
    const attached = [];
    class FakeHls {
        static isSupported() { return true; }
        static Events = { ERROR: "error" };
        constructor() { this.destroyed = false; }
        on() {}
        loadSource(url) { this.url = url; }
        attachMedia(media) { this.media = media; attached.push(this); }
        destroy() { this.destroyed = true; }
    }
    const audio = {
        dataset: { currentAssetId: "asset-a", playbackAssetId: "asset-a", playbackRequestVersion: "21" },
        attrs: new Map(), readyState: 0, currentTime: 0, volume: 1, muted: false, playbackRate: 1,
        playCalls: 0, pauseCalls: 0, loadCalls: 0, events: [], listeners: [],
        play() { this.playCalls++; return Promise.resolve(); },
        pause() { this.pauseCalls++; },
        load() { this.loadCalls++; },
        getAttribute(name) { return this.attrs.get(name) ?? null; },
        setAttribute(name, value) { this.attrs.set(name, value); },
        removeAttribute(name) { this.attrs.delete(name); },
        addEventListener(type, handler) { this.listeners.push({ type, handler }); },
        dispatchEvent(event) { this.events.push(event.type); return true; },
        get currentSrc() { return this.attrs.get("src") ?? ""; },
    };
    const context = {
        URL, console, Event: class { constructor(type) { this.type = type; } }, Number, String, Math, Promise, JSON,
        Hls: FakeHls,
        clearPreparedCaptions() {}, discoverPreparedCaptions() {}, scheduleNativeSleepTimer() {}, audioObserverFor() { return null; },
        playbackConfig: { seekToleranceSeconds: 1 },
        document: { baseURI: "https://dashboard.test/", getElementById: () => audio },
        localStorage: { getItem: () => null }, stateKey: "state",
        window: {
            MediaSource: {}, Hls: FakeHls,
            tuvimaLazy: { loadHls() { return new Promise((resolve, reject) => loads.push({ resolve, reject })); } },
            listenPlayback: { clearState() {} },
            location: { href: "https://dashboard.test/" },
        },
    };
    runInNewContext([
        block("    function samePlaybackUrl(", "    function audioAssetId"),
        block("    function audioAssetId(", "    function audioRequestVersionMatches"),
        block("    function audioRequestVersionMatches(", "    function audioSourceMatches"),
        block("    function audioSourceMatches(", "    function readAudioPositionForSleepTimer"),
        block("    function closeAudioSourceMatches(", "    function readCurrentAudiobookPosition"),
        block("    async function startAudioElement(", "    function clearPreparedCaptions("),
        block("    function browserMayUseHlsJs(", "    function numberFromDataset("),
        `function captureAudiobookBookmarkPosition() { return null; }`,
        `globalThis.api = { ensureAudioSource, invalidateAudioSource, startAudio: startAudioElement, `
            + `${block("        pauseAudio: function", "        seekAudio:")}, `
            + `${block("        finalizeAudioClose: function", "        captureAudiobookBookmarkPosition:")} };`,
    ].join("\n"), context);
    return { audio, api: context.api, loads, attached, lazyWaiting: () => loads.length };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

test("A start whose source is replaced during the hls.js load plays nothing and leaves the newer source alone", async () => {
    const h = harness();
    const first = h.api.startAudio(h.audio, { streamUrl: STREAM, positionSeconds: 30, volume: 0.2 });
    await flush();
    const second = h.api.startAudio(h.audio, { streamUrl: "/stream/b.m3u8", volume: 0.9 });
    await flush();
    h.loads[0].resolve();
    assert.equal(await first, false);
    assert.equal(h.audio.dataset.playbackStartFailure, "AbortError", "the Dashboard ignores a superseded start");
    assert.equal(h.audio.volume, 1, "the replaced start applied no volume");
    assert.equal(h.audio.listeners.length, 0, "the replaced start applied no seek");
    h.loads[1].resolve();
    assert.equal(await second, true);
    assert.equal(h.audio.volume, 0.9);
    assert.equal(h.audio.playCalls, 1);
    assert.equal(h.attached.length, 1);
    assert.equal(h.attached[0].url, "/stream/b.m3u8");
});

test("Release during the hls.js load attaches nothing and plays nothing", async () => {
    const h = harness();
    const started = h.api.startAudio(h.audio, { streamUrl: STREAM });
    await flush();
    assert.equal(h.api.releaseMedia(h.audio), true);
    h.loads[0].resolve();
    assert.equal(await started, false);
    assert.equal(h.attached.length, 0);
    assert.equal(h.audio.playCalls, 0);
    assert.equal(h.audio.dataset.playbackSource, undefined);
});

test("Close during the hls.js load cancels the pending source even though no source is bound yet", async () => {
    const h = harness();
    const started = h.api.startAudio(h.audio, { streamUrl: STREAM });
    await flush();
    assert.equal(h.api.finalizeAudioClose("asset-a", 21, STREAM), true);
    h.loads[0].resolve();
    assert.equal(await started, false);
    assert.equal(h.attached.length, 0, "no player attaches after Close");
    assert.equal(h.audio.playCalls, 0, "audio does not play after Close");
    assert.equal(h.audio.dataset.playbackSource, undefined);
});

test("Pause during the hls.js load stops the waiting start from playing", async () => {
    const h = harness();
    const started = h.api.startAudio(h.audio, { streamUrl: STREAM });
    await flush();
    h.api.pauseAudio(h.audio);
    h.loads[0].resolve();
    assert.equal(await started, false);
    assert.equal(h.audio.playCalls, 0);
    assert.equal(h.attached.length, 1, "the source itself is still attached, so a later Play is quick");
});

test("Two starts for one address during the load share one load and only the later one plays", async () => {
    const h = harness();
    const first = h.api.startAudio(h.audio, { streamUrl: STREAM, positionSeconds: 12, volume: 0.1 });
    await flush();
    const second = h.api.startAudio(h.audio, { streamUrl: STREAM, positionSeconds: 0, volume: 0.7 });
    await flush();
    assert.equal(h.loads.length, 1, "hls.js is requested once");
    h.loads[0].resolve();
    assert.equal(await first, false);
    assert.equal(await second, true);
    assert.equal(h.audio.volume, 0.7, "the earlier start did not write its volume");
    assert.equal(h.audio.listeners.length, 0, "the earlier start did not queue its seek");
    assert.equal(h.audio.playCalls, 1);
    assert.equal(h.attached.length, 1);
});

test("A cast source swap during the load stops the hls.js attach from overwriting the cast address", async () => {
    const h = harness();
    const started = h.api.startAudio(h.audio, { streamUrl: STREAM });
    await flush();
    h.api.invalidateAudioSource(h.audio); // what playback-remote swapSource does before pointing the element at the cast link
    h.audio.setAttribute("src", "https://cast.test/link");
    h.loads[0].resolve();
    await started;
    assert.equal(h.attached.length, 0);
    assert.equal(h.audio.getAttribute("src"), "https://cast.test/link");
});

test("After hls.js fails to load, the same address is loaded again on the next start", async () => {
    const h = harness();
    const failed = h.api.startAudio(h.audio, { streamUrl: STREAM });
    await flush();
    h.loads[0].reject(new Error("offline"));
    assert.equal(await failed, false);
    assert.deepEqual(h.audio.events, ["error"]);
    assert.equal(h.audio.dataset.playbackSource, undefined, "the marker is gone so the retry does not skip the load");
    assert.equal(h.audio.playCalls, 0, "no play() with no source");
    const retry = h.api.startAudio(h.audio, { streamUrl: STREAM });
    await flush();
    assert.equal(h.loads.length, 2, "the second start asked for hls.js again");
    h.loads[1].resolve();
    assert.equal(await retry, true);
    assert.equal(h.attached.length, 1);
    assert.equal(h.audio.playCalls, 1);
});
