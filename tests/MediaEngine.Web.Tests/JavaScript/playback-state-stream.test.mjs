import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

const appJsPath = new URL("../../../src/MediaEngine.Web/wwwroot/app.js", import.meta.url);
const appJs = await readFile(appJsPath, "utf8");

function sourceBlock(startMarker, endMarker, name) {
    const start = appJs.indexOf(startMarker);
    const end = appJs.indexOf(endMarker, start);
    assert.ok(start >= 0 && end > start, `${name} source block exists`);
    return appJs.slice(start, end);
}

test("playback state stream preserves the stored snapshot bytes through initial read and live notification", async () => {
    const hasState = sourceBlock("        hasState: function ()", "        getStateStream: function ()", "hasState").trim().replace(/,$/, "");
    const getStateStream = sourceBlock("        getStateStream: function ()", "        getStoredState: function ()", "getStateStream").trim().replace(/,$/, "");
    const notifyState = sourceBlock("    function notifyState(json) {", "    function notifyCommand(json) {", "notifyState");
    const json = JSON.stringify({
        playbackRequestVersion: 81,
        queue: Array.from({ length: 7 }, (_, index) => ({
            workId: `work-${index}`,
            assetId: `asset-${index}`,
            title: `A long track ${index} — ${"preserved metadata ".repeat(320)}`,
            chapters: [{ index: 0, title: "Source chapter", startSeconds: 13.25, endSeconds: 77.5 }],
        })),
        currentIndex: 4,
    });
    const blobs = [];
    assert.ok(Buffer.byteLength(json, "utf8") > 32 * 1024);
    const liveCalls = [];
    let stateHandler = { invokeMethodAsync: (...args) => liveCalls.push(args) };
    const context = {
        Blob,
        DotNet: { createJSStreamReference: blob => { blobs.push(blob); return { blob, streamReference: blobs.length }; } },
        window: { tuvimaPopupStateSync: { getLatestState: key => key === "tuvima.playback.v2.state" ? json : null } },
        stateHandler,
        stateKey: "tuvima.playback.v2.state",
    };
    runInNewContext(`globalThis.listenPlayback = { ${hasState}, ${getStateStream} };
        ${notifyState}
        globalThis.notify = notifyState;`, context);

    assert.equal(context.listenPlayback.hasState(), true);
    const initialBlob = context.listenPlayback.getStateStream();
    assert.ok(initialBlob instanceof Blob, "a .NET JS invocation must receive a Blob for automatic stream-reference conversion");
    assert.equal(await initialBlob.text(), json);
    assert.equal(initialBlob.type, "application/json");

    context.notify(json);
    assert.equal(liveCalls.length, 1);
    assert.equal(liveCalls[0][0], "HandlePlaybackState");
    const liveReference = liveCalls[0][1];
    assert.ok(liveReference);
    assert.equal(await liveReference.blob.text(), json);
    assert.equal(liveReference.blob.type, "application/json");
    assert.equal(blobs.length, 1, "only the JS-to-.NET callback creates an explicit stream reference");

    let presenceReads = 0;
    context.window.tuvimaPopupStateSync.getLatestState = () => presenceReads++ === 0 ? json : null;
    assert.equal(context.listenPlayback.hasState(), true);
    assert.throws(() => context.listenPlayback.getStateStream(), /no longer available/i,
        "a state removed between presence check and read is an honest read failure, not an empty snapshot");
    context.window.tuvimaPopupStateSync.getLatestState = () => null;
    assert.equal(context.listenPlayback.hasState(), false, "an actually absent state is handled without a stream conversion");
});
