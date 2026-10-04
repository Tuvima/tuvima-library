import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("../../../src/MediaEngine.Web/wwwroot/app.js", import.meta.url), "utf8");
function block(start, end) {
    const a = source.indexOf(start), b = source.indexOf(end, a);
    assert.ok(a >= 0 && b > a);
    return source.slice(a, b).trim().replace(/,$/, "");
}
function harness() {
    let stored = JSON.stringify({ queue: [{ asset_id: "asset-a" }], current_index: 0, playback_request_version: 21 });
    const audio = {
        dataset: { currentAssetId: "asset-a", playbackAssetId: "asset-a", playbackRequestVersion: "21", playbackSource: "/stream/a" },
        currentSrc: "https://dashboard.test/stream/a", currentTime: 47.25, duration: 180,
        paused: false, pauseCalls: 0, loadCalls: 0, removed: false,
        pause() { this.paused = true; this.pauseCalls++; },
        getAttribute() { return this.removed ? null : this.currentSrc; },
        removeAttribute(name) { if (name === "src") this.removed = true; },
        load() { this.loadCalls++; }
    };
    let cleared = 0, popupClosed = 0;
    const context = {
        URL, console, clearPreparedCaptions() {},
        document: { baseURI: "https://dashboard.test/", getElementById: () => audio },
        localStorage: { getItem: () => stored }, stateKey: "state",
        window: { listenPlayback: { clearState() { cleared++; stored = null; }, closePopup() { popupClosed++; } } }
    };
    runInNewContext([
        block("    function samePlaybackUrl(", "    function audioAssetId"),
        block("    function audioAssetId(", "    function audioRequestVersionMatches"),
        block("    function audioRequestVersionMatches(", "    function audioSourceMatches"),
        block("    function audioSourceMatches(", "    function readAudioPositionForSleepTimer"),
        block("    function captureAudiobookBookmarkPosition(", "    function readCurrentAudiobookPosition"),
        `globalThis.api = { ${block("        pauseAudioForClose: function", "        captureAudiobookBookmarkPosition:")} };`
    ].join("\n"), context);
    return { audio, api: context.api, setStored(value) { stored = JSON.stringify(value); }, get cleared() { return cleared; }, get popupClosed() { return popupClosed; } };
}

test("Close pauses the captured native source and preserves its stopped position before cleanup", () => {
    const h = harness();
    const stopped = h.api.pauseAudioForClose("asset-a", 21, "/stream/a");
    assert.equal(stopped.positionSeconds, 47.25);
    assert.equal(stopped.sourceVerified, true);
    assert.equal(h.audio.paused, true);
    assert.equal(h.audio.loadCalls, 0, "position capture does not reload media");
    assert.equal(h.cleared, 0, "resume persistence can finish before clearing the session");
    assert.equal(h.api.finalizeAudioClose("asset-a", 21, "/stream/a"), true);
    assert.equal(h.cleared, 1);
    assert.equal(h.popupClosed, 1);
    assert.equal(h.audio.loadCalls, 1);
});

test("A stale Close cannot pause, release, or clear a successor native session", () => {
    const h = harness();
    h.audio.dataset.currentAssetId = h.audio.dataset.playbackAssetId = "asset-b";
    h.audio.dataset.playbackRequestVersion = "22";
    assert.equal(h.api.pauseAudioForClose("asset-a", 21, "/stream/a"), null);
    assert.equal(h.api.finalizeAudioClose("asset-a", 21, "/stream/a"), false);
    assert.equal(h.audio.pauseCalls, 0);
    assert.equal(h.audio.loadCalls, 0);
    assert.equal(h.cleared, 0);
    assert.equal(h.popupClosed, 0);
});

test("Successor storage and a different native source both prevent old-session cleanup", () => {
    const h = harness();
    h.setStored({ queue: [{ asset_id: "asset-b" }], current_index: 0, playback_request_version: 22 });
    assert.equal(h.api.finalizeAudioClose("asset-a", 21, "/stream/a"), false);
    assert.equal(h.cleared, 0);
    h.setStored({ queue: [{ asset_id: "asset-a" }], current_index: 0, playback_request_version: 21 });
    h.audio.dataset.playbackSource = "/stream/b";
    h.audio.currentSrc = "https://dashboard.test/stream/b";
    assert.equal(h.api.pauseAudioForClose("asset-a", 21, "/stream/a"), null);
    assert.equal(h.api.finalizeAudioClose("asset-a", 21, "/stream/a"), false);
    assert.equal(h.audio.pauseCalls, 0);
    assert.equal(h.audio.loadCalls, 0);
    assert.equal(h.cleared, 0);
});

test("Close cancels an unresolved current request and releases only its verified predecessor source", () => {
    const h = harness();
    h.audio.dataset.currentAssetId = "asset-b";
    h.audio.dataset.playbackRequestVersion = "22";
    assert.equal(h.api.pauseAudioForClose("asset-b", 22, "/stream/b", "/stream/a", true), null,
        "predecessor position is not the new work's saved resume");
    assert.equal(h.audio.paused, true);
    assert.equal(h.api.finalizeAudioClose("asset-b", 22, "/stream/b", "/stream/a", true), true);
    assert.equal(h.audio.loadCalls, 1);
    assert.equal(h.cleared, 1);
});

test("Close with no bound source cancels the current unresolved item without a fabricated position", () => {
    const h = harness();
    h.audio.dataset.currentAssetId = "";
    h.audio.dataset.playbackRequestVersion = "22";
    h.audio.currentSrc = "";
    h.audio.removed = true;
    assert.equal(h.api.pauseAudioForClose(null, 22, null, null, true), null);
    assert.equal(h.api.finalizeAudioClose(null, 22, null, null, true), true);
    assert.equal(h.audio.loadCalls, 0);
    assert.equal(h.cleared, 1);
});

// These are the explicit JsonPropertyName fields of ListenPlaybackSnapshot and ListenQueueItem.
test("Restored snake-case snapshot at a nonzero queue index releases only its current profile and request", () => {
    const h = harness();
    const restored = {
        profile_id: "profile-a", current_index: 1, playback_request_version: 21,
        queue: [{ asset_id: "previous-asset" }, { asset_id: "asset-a" }],
        is_playing: false, current_time_seconds: 91.7
    };
    h.setStored(restored);
    h.audio.paused = true;
    h.audio.currentTime = 101.29;
    const captured = h.api.pauseAudioForClose("asset-a", 21, "/stream/a", null, false);
    assert.equal(captured.positionSeconds, 101.29);
    assert.equal(h.api.finalizeAudioClose("asset-a", 21, "/stream/a", null, false, "profile-a"), true);
    assert.equal(h.cleared, 1);
    assert.equal(h.audio.removed, true);
    assert.equal(h.audio.loadCalls, 1);
});

test("A stored successor request or another profile still blocks cleanup with actual snapshot wire names", () => {
    for (const snapshot of [
        { profile_id: "profile-a", current_index: 0, playback_request_version: 22, queue: [{ asset_id: "asset-a" }] },
        { profile_id: "profile-b", current_index: 0, playback_request_version: 21, queue: [{ asset_id: "asset-a" }] }
    ]) {
        const h = harness();
        h.setStored(snapshot);
        assert.equal(h.api.finalizeAudioClose("asset-a", 21, "/stream/a", null, false, "profile-a"), false);
        assert.equal(h.cleared, 0);
        assert.equal(h.audio.loadCalls, 0);
        assert.equal(h.audio.pauseCalls, 0);
    }
});
