import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

const appJsPath = new URL("../../../src/MediaEngine.Web/wwwroot/app.js", import.meta.url);
const appJs = await readFile(appJsPath, "utf8");

function extractFunction(name, nextMarker) {
    const start = appJs.indexOf(`    function ${name}(`);
    const end = appJs.indexOf(nextMarker, start);
    assert.ok(start >= 0 && end > start, `${name} source block exists`);
    return appJs.slice(start, end);
}

function extractObjectMethod(name, nextMarker) {
    const starts = [
        appJs.indexOf(`        ${name}: function (`),
        appJs.indexOf(`        ${name}: async function (`)
    ].filter(index => index >= 0);
    const start = starts.length ? Math.min(...starts) : -1;
    const end = appJs.indexOf(nextMarker, start);
    assert.ok(start >= 0 && end > start, `${name} source method exists`);
    return appJs.slice(start, end).trim().replace(/,$/, "");
}

function extractNestedMethod(name, nextMarker) {
    const marker = `                ${name}: function (`;
    const start = appJs.indexOf(marker);
    const end = appJs.indexOf(nextMarker, start);
    assert.ok(start >= 0 && end > start, `${name} source block exists`);
    const methodStart = appJs.indexOf("function (", start);
    return appJs.slice(methodStart, end).trim().replace(/,$/, "");
}

function createTimerHarness() {
    const timeouts = new Map();
    let nextTimeoutId = 1;
    let monotonicNow = 0;
    const reports = [];
    const element = {
        dataset: { playbackAssetId: "asset-a", playbackSource: "/stream/a", playbackRequestVersion: "21" },
        currentSrc: "https://dashboard.test/stream/a",
        paused: true,
        waiting: false,
        playbackRate: 1,
        currentTime: 0,
        pauseCalls: 0,
        getAttribute() { return this.currentSrc; },
        pause() { this.pauseCalls++; this.paused = true; }
    };
    const observer = {
        element,
        sleepTimer: null,
        sleepTimerTimeout: null,
        dotNetRef: {
            invokeMethodAsync: (...args) => {
                reports.push(args);
                return Promise.resolve();
            }
        }
    };
    const source = [
        extractFunction("samePlaybackUrl", "    function audioAssetId"),
        extractFunction("audioAssetId", "    function audioRequestVersionMatches"),
        extractFunction("audioRequestVersionMatches", "    function audioSourceMatches"),
        extractFunction("audioSourceMatches", "    function notifyNativeSleepTimerExpired"),
        extractFunction("notifyNativeSleepTimerExpired", "    function scheduleNativeSleepTimer"),
        extractFunction("scheduleNativeSleepTimer", "    function waitForNativeSleepTimerSource"),
        `globalThis.schedule = scheduleNativeSleepTimer;
         globalThis.expire = notifyNativeSleepTimerExpired;`
    ].join("\n");
    const context = {
        console,
        URL,
        document: { baseURI: "https://dashboard.test/" },
        performance: { now: () => monotonicNow },
        window: {
            setTimeout(callback, delay) {
                const id = nextTimeoutId++;
                timeouts.set(id, { callback, delay });
                return id;
            },
            clearTimeout(id) { timeouts.delete(id); }
        }
    };
    runInNewContext(source, context);
    return {
        context,
        element,
        observer,
        reports,
        timeouts,
        setMonotonicNow(value) { monotonicNow = value; },
        runNextTimeout() {
            const entry = timeouts.entries().next().value;
            assert.ok(entry, "a native timer callback is scheduled");
            const [id, timeout] = entry;
            timeouts.delete(id);
            timeout.callback();
            return timeout.delay;
        }
    };
}

function createRegistrationHarness() {
    const timeouts = new Map();
    const listeners = new Map();
    let nextTimeoutId = 1;
    const element = {
        dataset: { playbackAssetId: "asset-a", playbackSource: "/stream/a", playbackRequestVersion: "21" },
        currentSrc: "https://dashboard.test/stream/a",
        paused: false,
        waiting: false,
        playbackRate: 1,
        currentTime: 0,
        getAttribute() { return this.currentSrc; },
        pause() { this.paused = true; },
        addEventListener(name, listener) {
            if (!listeners.has(name)) listeners.set(name, new Set());
            listeners.get(name).add(listener);
        },
        removeEventListener(name, listener) { listeners.get(name)?.delete(listener); },
        emit(name, event = {}) {
            for (const listener of [...(listeners.get(name) ?? [])]) listener(event);
        }
    };
    const observer = {
        element,
        sleepTimer: null,
        sleepTimerTimeout: null,
        sleepTimerGeneration: 4,
        pendingSleepTimer: null,
        sleepTimerBindingCancel: null
    };
    element.observer = observer;
    const source = [
        extractFunction("cancelPendingSleepTimerBinding", "    function samePlaybackUrl"),
        extractFunction("samePlaybackUrl", "    function audioAssetId"),
        extractFunction("audioAssetId", "    function audioRequestVersionMatches"),
        extractFunction("audioRequestVersionMatches", "    function audioSourceMatches"),
        extractFunction("audioSourceMatches", "    function readAudioPositionForSleepTimer"),
        extractFunction("notifyNativeSleepTimerExpired", "    function scheduleNativeSleepTimer"),
        extractFunction("scheduleNativeSleepTimer", "    function waitForNativeSleepTimerSource"),
        extractFunction("waitForNativeSleepTimerSource", "    function unregisterPlayerShortcuts"),
        `globalThis.timerApi = ({ ${extractObjectMethod("setAudiobookSleepTimer", "        clearAudiobookSleepTimer:")} });`
    ].join("\n");
    const context = {
        console,
        URL,
        Date,
        document: { baseURI: "https://dashboard.test/" },
        performance: { now: () => 0 },
        audioObserverFor: target => target && target.observer,
        window: {
            setTimeout(callback, delay) {
                const id = nextTimeoutId++;
                timeouts.set(id, { callback, delay });
                return id;
            },
            clearTimeout(id) { timeouts.delete(id); }
        }
    };
    runInNewContext(source, context);
    return { context, element, observer, timeouts };
}

test("a scoped pause acknowledges the exact native pause without pausing twice", () => {
    const source = [
        extractFunction("samePlaybackUrl", "    function notifyNativeSleepTimerExpired"),
        extractFunction("audioAssetId", "    function audioSourceMatches"),
        extractFunction("audioSourceMatches", "    function notifyNativeSleepTimerExpired"),
        extractFunction("notifyNativeSleepTimerExpired", "    function scheduleNativeSleepTimer"),
        `globalThis.pauseApi = ({ ${extractObjectMethod("pauseAudioForSleepTimer", "        registerPlayerShortcuts:")} });`
    ].join("\n");

    const context = {
        console,
        URL,
        document: { baseURI: "https://dashboard.test/" },
        window: { clearTimeout() {} },
        audioObserverFor: element => element.observer
    };
    runInNewContext(source, context);

    let pauseCalls = 0;
    const element = {
        dataset: { playbackAssetId: "asset-a", playbackSource: "/stream/a", playbackRequestVersion: "21" },
        currentSrc: "https://dashboard.test/stream/a",
        paused: false,
        currentTime: 12,
        getAttribute() { return this.currentSrc; },
        pause() { pauseCalls++; this.paused = true; },
        observer: null
    };
    const reports = [];
    const timer = {
        mode: "timer",
        timerGeneration: 7,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        playbackRequestVersion: 21,
        sourceUrl: "/stream/a",
        fired: false
    };
    const observer = {
        element,
        dotNetRef: { invokeMethodAsync: (...args) => { reports.push(args); return Promise.resolve(); } },
        sleepTimer: timer,
        sleepTimerTimeout: null
    };
    element.observer = observer;

    context.notifyNativeSleepTimerExpired(observer, timer);
    assert.equal(timer.fired, true);
    assert.equal(element.paused, true);
    assert.equal(pauseCalls, 1);
    assert.equal(reports.length, 1);

    const acknowledged = context.pauseApi.pauseAudioForSleepTimer(element, 7, "asset-a", 21);
    assert.equal(acknowledged, true);
    assert.equal(pauseCalls, 1, "acknowledgement does not issue a second native pause");

    element.dataset.playbackSource = "/stream/replacement";
    element.currentSrc = "https://dashboard.test/stream/replacement";
    assert.equal(context.pauseApi.pauseAudioForSleepTimer(element, 7, "asset-a", 21), false,
        "a fired timer cannot acknowledge a pause against a replacement source");
    assert.equal(pauseCalls, 1);

    element.dataset.playbackSource = "/stream/a";
    element.currentSrc = "https://dashboard.test/stream/a";
    element.dataset.playbackRequestVersion = "22";
    assert.equal(context.pauseApi.pauseAudioForSleepTimer(element, 7, "asset-a", 21), false,
        "an old timer cannot acknowledge a pause after the playback request changes");
    assert.equal(pauseCalls, 1);
});

test("an armed HLS timer remains bound to the exact HLS instance", () => {
    const source = [
        extractFunction("samePlaybackUrl", "    function notifyNativeSleepTimerExpired"),
        extractFunction("audioAssetId", "    function audioSourceMatches"),
        extractFunction("audioSourceMatches", "    function notifyNativeSleepTimerExpired"),
        "globalThis.matches = audioSourceMatches;"
    ].join("\n");
    const context = { URL, document: { baseURI: "https://dashboard.test/" } };
    runInNewContext(source, context);
    const element = {
        dataset: { playbackAssetId: "asset-a", playbackSource: "/stream/a", playbackRequestVersion: "21" },
        currentSrc: "blob:https://dashboard.test/first",
        getAttribute() { return this.currentSrc; }
    };
    const firstHls = { url: "/stream/a", media: element };
    const timer = { sourceUrl: "/stream/a", playbackRequestVersion: 21, hlsInstance: null };
    element._tuvimaHls = firstHls;
    assert.equal(context.matches(element, timer), true);
    assert.equal(timer.hlsInstance, firstHls);
    element._tuvimaHls = { url: "/stream/a", media: element };
    assert.equal(context.matches(element, timer), false,
        "a same-URL replacement HLS instance cannot inherit the existing arm");
});

test("the boundary capture returns a position only for the exact asset, request, URL, and HLS instance", () => {
    const source = [
        extractFunction("samePlaybackUrl", "    function audioAssetId"),
        extractFunction("audioAssetId", "    function audioRequestVersionMatches"),
        extractFunction("audioRequestVersionMatches", "    function audioSourceMatches"),
        extractFunction("audioSourceMatches", "    function readAudioPositionForSleepTimer"),
        extractFunction("readAudioPositionForSleepTimer", "    function notifyNativeSleepTimerExpired"),
        "globalThis.readPosition = readAudioPositionForSleepTimer;"
    ].join("\n");
    const context = {
        URL,
        document: { baseURI: "https://dashboard.test/" },
        audioObserverFor: element => element.observer
    };
    runInNewContext(source, context);
    const element = {
        dataset: { playbackAssetId: "asset-a", playbackSource: "/stream/a", playbackRequestVersion: "21" },
        currentSrc: "https://dashboard.test/stream/a",
        currentTime: 23.5,
        getAttribute() { return this.currentSrc; },
        observer: { sleepTimer: null }
    };
    const read = () => context.readPosition(element, "asset-a", 21, "/stream/a");

    assert.equal(read(), 23.5, "the exact direct source returns its live position");
    element.currentSrc = "https://dashboard.test/stream/replacement";
    assert.equal(read(), null, "a mismatched actual source cannot provide a chapter capture position");
    element.currentSrc = "https://dashboard.test/stream/a";
    element.dataset.playbackRequestVersion = "22";
    assert.equal(read(), null, "a stale playback request cannot provide a position");

    element.dataset.playbackRequestVersion = "21";
    element.currentSrc = "blob:https://dashboard.test/hls-a";
    const firstHls = { url: "/stream/a", media: element };
    element._tuvimaHls = firstHls;
    element.observer.sleepTimer = {
        boundAssetId: "asset-a",
        playbackRequestVersion: 21,
        sourceUrl: "/stream/a",
        hlsInstance: firstHls
    };
    assert.equal(read(), 23.5, "the armed HLS instance can provide its verified position");
    element._tuvimaHls = { url: "/stream/a", media: element };
    assert.equal(read(), null, "a same-URL replacement HLS instance cannot inherit the capture");
});

test("a paused minute timer follows its monotonic deadline despite wall-clock changes and early wakeups", () => {
    const harness = createTimerHarness();
    const { context, element, observer, reports, timeouts, setMonotonicNow, runNextTimeout } = harness;
    observer.sleepTimer = {
        mode: "timer",
        timerGeneration: 9,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        playbackRequestVersion: 21,
        sourceUrl: "/stream/a",
        monotonicDeadline: 60_000,
        hlsInstance: null,
        fired: false
    };

    context.schedule(observer);
    assert.equal(element.paused, true, "a minute timer is retained while audio is paused");
    assert.equal([...timeouts.values()][0].delay, 60_000);

    // Wall-clock movement does not change the live monotonic deadline.
    context.Date = { now: () => -86_400_000 };
    setMonotonicNow(59_000);
    runNextTimeout();
    assert.equal(reports.length, 0, "an early timer wake cannot expire the arm");
    assert.equal([...timeouts.values()][0].delay, 1_000);

    context.Date.now = () => 86_400_000;
    setMonotonicNow(60_000);
    runNextTimeout();
    assert.equal(observer.sleepTimer.fired, true);
    assert.equal(element.pauseCalls, 1);
    assert.equal(reports.length, 1);
    assert.equal(reports[0][0], "HandleNativeSleepTimerExpired");
    assert.equal(reports[0][1], 9);
    assert.equal(reports[0][2], "asset-a");
    assert.equal(reports[0][3], 21);
    assert.equal(reports[0][4], 0);
    assert.equal(reports[0][5], false, "a minute timer expiry is not a native ended event");
});

test("a chapter boundary wake rechecks seek and rate changes and expires only at the exact boundary", () => {
    const harness = createTimerHarness();
    const { context, element, observer, reports, timeouts, setMonotonicNow, runNextTimeout } = harness;
    element.paused = false;
    element.currentTime = 4;
    element.playbackRate = 2;
    observer.sleepTimer = {
        mode: "end-current",
        timerGeneration: 10,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        targetEndSeconds: 10,
        playbackRequestVersion: 21,
        sourceUrl: "/stream/a",
        hlsInstance: null,
        fired: false
    };

    context.schedule(observer);
    assert.equal([...timeouts.values()][0].delay, 3_000);

    // A seek and speed change happen before the scheduled wake. The wake must
    // calculate again from the current position/rate rather than its old delay.
    element.currentTime = 6;
    element.playbackRate = 1.5;
    setMonotonicNow(1_000);
    runNextTimeout();
    assert.equal([...timeouts.values()][0].delay, (4 / 1.5) * 1_000);
    assert.equal(reports.length, 0);

    // Seeking exactly to the captured end is sufficient even before that new
    // predicted timeout elapses.
    element.currentTime = 10;
    setMonotonicNow(1_100);
    runNextTimeout();
    assert.equal(observer.sleepTimer.fired, true);
    assert.equal(element.pauseCalls, 1);
    assert.equal(reports.length, 1);
});

test("native ended proof expires a captured target and dispatches valid playback completion once per play cycle", () => {
    const endedMethod = extractNestedMethod("onNativeAudioEnded", "                captionTracks:");
    const restartMethod = extractNestedMethod("onNativePlaybackRestart", "                onNativeAudioEnded:");
    const source = [
        extractFunction("samePlaybackUrl", "    function audioAssetId"),
        extractFunction("audioAssetId", "    function audioRequestVersionMatches"),
        extractFunction("audioRequestVersionMatches", "    function audioSourceMatches"),
        extractFunction("audioSourceMatches", "    function notifyNativeSleepTimerExpired"),
        extractFunction("notifyNativeSleepTimerExpired", "    function scheduleNativeSleepTimer"),
        extractFunction("scheduleNativeSleepTimer", "    function waitForNativeSleepTimerSource"),
        `globalThis.runNativeEnded = function (observer, event) {
            var element = observer.element;
            var onNativeAudioEnded = ${endedMethod};
            onNativeAudioEnded.call(observer, event);
        };
        globalThis.restartPlayback = function (observer) {
            var onNativePlaybackRestart = ${restartMethod};
            onNativePlaybackRestart.call(observer);
        };`
    ].join("\n");
    const context = {
        console,
        URL,
        document: { baseURI: "https://dashboard.test/" },
        window: { clearTimeout() {}, setTimeout() { return 1; } }
    };
    runInNewContext(source, context);

    const element = {
        dataset: { playbackAssetId: "asset-a", playbackSource: "/stream/a", playbackRequestVersion: "21" },
        currentSrc: "https://dashboard.test/stream/a",
        currentTime: 20.700998,
        duration: 20.700998,
        ended: true,
        paused: true,
        pauseCalls: 0,
        getAttribute() { return this.currentSrc; },
        pause() { this.pauseCalls++; this.paused = true; }
    };
    const reports = [];
    const observer = {
        element,
        sleepTimer: null,
        sleepTimerGeneration: 14,
        sleepTimerTimeout: null,
        lastNativeEndedBinding: null,
        dotNetRef: { invokeMethodAsync: (...args) => { reports.push(args); return Promise.resolve(); } },
        onNativePlaybackRestart: null,
        onNativeAudioEnded: null
    };
    const makeEvent = () => ({
        prevented: false,
        stopped: false,
        preventDefault() { this.prevented = true; },
        stopImmediatePropagation() { this.stopped = true; }
    });

    // A captured 21-second chapter can report 20.700998 seconds as its native
    // finite duration. The genuine `ended` proof must win before normal EOF.
    observer.sleepTimer = {
        mode: "end-current",
        timerGeneration: 15,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        targetEndSeconds: 21,
        playbackRequestVersion: 21,
        sourceUrl: "/stream/a",
        hlsInstance: null,
        fired: false
    };
    const chapterEnded = makeEvent();
    context.runNativeEnded(observer, chapterEnded);
    assert.equal(chapterEnded.prevented, true);
    assert.equal(chapterEnded.stopped, true);
    assert.equal(reports.length, 1);
    assert.deepEqual(reports[0], ["HandleNativeSleepTimerExpired", 15, "asset-a", 21, 20.700998, true]);
    assert.equal(element.pauseCalls, 0, "native ended already paused the media element");

    // EndNext lets the origin file finish normally; native EOF is dispatched
    // once. A later real play resets that per-cycle duplicate guard.
    observer.sleepTimer = {
        mode: "end-next",
        timerGeneration: 16,
        boundAssetId: "asset-a",
        targetAssetId: "asset-b",
        playbackRequestVersion: 21,
        sourceUrl: "/stream/a",
        hlsInstance: null,
        fired: false
    };
    element.ended = true;
    const originEnded = makeEvent();
    context.runNativeEnded(observer, originEnded);
    assert.equal(originEnded.prevented, false);
    assert.equal(originEnded.stopped, false);
    assert.deepEqual(reports[1], ["HandleNativeAudioEnded", "asset-a", 21, 16, 20.700998]);

    context.runNativeEnded(observer, makeEvent());
    assert.equal(reports.length, 2, "duplicate native ended callbacks in one play cycle are ignored");
    context.restartPlayback(observer);
    context.runNativeEnded(observer, makeEvent());
    assert.equal(reports.length, 3, "a later play cycle can complete the same asset and request again");
    assert.deepEqual(reports[2], reports[1]);

    // Stale source/request observations never dispatch completion.
    context.restartPlayback(observer);
    element.currentSrc = "https://dashboard.test/stream/replacement";
    context.runNativeEnded(observer, makeEvent());
    element.currentSrc = "https://dashboard.test/stream/a";
    element.dataset.playbackRequestVersion = "22";
    context.runNativeEnded(observer, makeEvent());
    assert.equal(reports.length, 3, "replacement source and stale request are rejected");

    // Ordinary music playback has no timer binding and still completes at EOF.
    observer.sleepTimer = null;
    element.dataset.playbackRequestVersion = "21";
    context.restartPlayback(observer);
    context.runNativeEnded(observer, makeEvent());
    assert.equal(reports.length, 4);
    assert.deepEqual(reports[3], ["HandleNativeAudioEnded", "asset-a", 21, 14, 20.700998]);

    // Mere timeupdate/end-shaped callbacks are insufficient without the native
    // element's actual ended flag and a finite positive duration.
    context.restartPlayback(observer);
    element.ended = false;
    context.runNativeEnded(observer, makeEvent());
    element.ended = true;
    element.duration = 0;
    context.runNativeEnded(observer, makeEvent());
    assert.equal(reports.length, 4);
});

test("native registration stages transactionally, preserves the old arm on failure, and accepts authoritative equal generations", async () => {
    const { context, element, observer, timeouts } = createRegistrationHarness();
    const oldTimer = {
        mode: "end-current",
        timerGeneration: 4,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        targetEndSeconds: 80,
        playbackRequestVersion: 21,
        sourceUrl: "/stream/a",
        hlsInstance: null,
        fired: false
    };
    observer.sleepTimer = oldTimer;

    const rejected = context.timerApi.setAudiobookSleepTimer(element, {
        mode: "end-current",
        timerGeneration: 5,
        boundAssetId: "asset-b",
        targetAssetId: "asset-b",
        targetEndSeconds: 40,
        playbackRequestVersion: 22
    }, "/stream/b");
    assert.equal(observer.sleepTimer, oldTimer, "the candidate is staged without replacing the current arm");
    assert.equal(observer.sleepTimerGeneration, 4);
    element.emit("error");
    assert.equal(await rejected, false, "a source error rejects the candidate");
    assert.equal(observer.sleepTimer, oldTimer, "a rejected candidate leaves the existing arm intact");
    assert.equal(observer.sleepTimerGeneration, 4);
    assert.equal(observer.pendingSleepTimer, null);
    assert.equal(timeouts.size, 0, "the failed wait timeout is cleaned up");

    const olderPending = context.timerApi.setAudiobookSleepTimer(element, {
        mode: "end-current",
        timerGeneration: 5,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        targetEndSeconds: 90,
        playbackRequestVersion: 21
    }, "/stream/a-pending");
    assert.notEqual(observer.pendingSleepTimer, null, "the first candidate waits for its exact source");

    element.dataset.playbackSource = "/stream/c";
    element.currentSrc = "https://dashboard.test/stream/c";
    const latest = context.timerApi.setAudiobookSleepTimer(element, {
        mode: "end-current",
        timerGeneration: 6,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        targetEndSeconds: 100,
        playbackRequestVersion: 21
    }, "/stream/c");
    assert.equal(await olderPending, false, "a superseded candidate resolves as rejected");
    assert.equal(await latest, true, "the latest candidate binds to its matching source");
    assert.equal(observer.sleepTimer.timerGeneration, 6,
        "the older completion cannot clear or replace the newer successful arm");
    assert.equal(observer.sleepTimerGeneration, 6);

    const equalGeneration = await context.timerApi.setAudiobookSleepTimer(element, {
        mode: "end-current",
        timerGeneration: 6,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        targetEndSeconds: 100,
        playbackRequestVersion: 21
    }, "/stream/c");
    assert.equal(equalGeneration, true, "an identical retry at the current generation is allowed");
    assert.equal(observer.sleepTimerGeneration, 6);
    assert.equal(observer.sleepTimer.targetEndSeconds, 100);

    const mutatedSameGeneration = await context.timerApi.setAudiobookSleepTimer(element, {
        mode: "end-current",
        timerGeneration: 6,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        targetEndSeconds: 110,
        playbackRequestVersion: 21
    }, "/stream/c");
    assert.equal(mutatedSameGeneration, false, "an equal-generation request cannot mutate the captured target");
    assert.equal(observer.sleepTimer.targetEndSeconds, 100, "the captured boundary stays immutable for its arm");
});

test("a canonical Off DTO clears the matching native arm and pending candidate, while an older Off cannot clear a newer arm", async () => {
    const { context, element, observer } = createRegistrationHarness();
    observer.sleepTimerGeneration = 10;
    observer.sleepTimer = {
        mode: "end-current",
        timerGeneration: 10,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        targetEndSeconds: 80,
        playbackRequestVersion: 21,
        sourceUrl: "/stream/a",
        hlsInstance: null,
        fired: false
    };
    observer.sleepTimerTimeout = 77;

    const pending = context.timerApi.setAudiobookSleepTimer(element, {
        mode: "end-current",
        timerGeneration: 11,
        boundAssetId: "asset-b",
        targetAssetId: "asset-b",
        targetEndSeconds: 40,
        playbackRequestVersion: 22
    }, "/stream/b");
    assert.notEqual(observer.pendingSleepTimer, null);

    const canonicalOff = {
        mode: "off",
        timerGeneration: 12,
        boundAssetId: "00000000-0000-0000-0000-000000000000",
        playbackRequestVersion: 0
    };
    assert.equal(await context.timerApi.setAudiobookSleepTimer(element, canonicalOff, "/stream/a"), true,
        "zero-GUID/zero-version DTO defaults mean no optional cancellation scope");
    assert.equal(observer.sleepTimer, null);
    assert.equal(observer.sleepTimerGeneration, 12);
    assert.equal(observer.pendingSleepTimer, null);
    assert.equal(await pending, false, "Off cancels an older staged registration");

    const newerArm = await context.timerApi.setAudiobookSleepTimer(element, {
        mode: "end-current",
        timerGeneration: 13,
        boundAssetId: "asset-a",
        targetAssetId: "asset-a",
        targetEndSeconds: 90,
        playbackRequestVersion: 21
    }, "/stream/a");
    assert.equal(newerArm, true);
    const staleOff = await context.timerApi.setAudiobookSleepTimer(element, {
        ...canonicalOff,
        timerGeneration: 12
    }, "/stream/a");
    assert.equal(staleOff, false, "an older Off generation cannot clear the newer arm");
    assert.equal(observer.sleepTimer.timerGeneration, 13);
});
