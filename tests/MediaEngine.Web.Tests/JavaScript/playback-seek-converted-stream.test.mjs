import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

// A converted film that is started partway has its own clock that begins at zero at that second of the film. The player script keeps
// everything in the film's own time and says "no" to a seek the stream cannot hold, so the Dashboard can ask the Engine for a start there.
const source = await readFile(new URL("../../../src/MediaEngine.Web/wwwroot/js/playback-core.js", import.meta.url), "utf8");
function block(start, end) {
    const a = source.indexOf(start), b = source.indexOf(end, a);
    assert.ok(a >= 0 && b > a, `block ${start}`);
    return source.slice(a, b).trim().replace(/,$/, "");
}

function player({ offset = 0, ready = null, live = true } = {}) {
    const element = {
        tagName: "VIDEO", currentTime: 0, duration: 600, _tuvimaStreamOffset: offset,
        _tuvimaHls: ready === null ? null : { currentLevel: 0, levels: [{ details: { live, totalduration: ready } }] },
        addEventListener() {},
    };
    const context = { console };
    runInNewContext([
        block("    function streamOffsetOf(", "    function readAudioElementState("),
        `globalThis.api = { ${block("        seekAudio: function", "        setVolume:")} };`,
    ].join("\n"), context);
    return { element, seek: seconds => context.api.seekAudio(element, seconds) };
}

test("A seek inside what is ready lands on the stream's own clock", () => {
    const { element, seek } = player({ offset: 6000, ready: 120 });
    assert.equal(seek(6050), true);
    assert.equal(element.currentTime, 50);
});

test("A seek past what is ready is refused so the Engine can start the stream there", () => {
    const { element, seek } = player({ offset: 0, ready: 120 });
    assert.equal(seek(3000), false);
    assert.equal(element.currentTime, 0);
});

test("A seek before the second a stream started at is refused", () => {
    const { seek } = player({ offset: 6000, ready: 120 });
    assert.equal(seek(100), false);
});

test("A finished stream takes any seek it covers, and music or direct play is untouched", () => {
    assert.equal(player({ offset: 0, ready: 600, live: false }).seek(580), true);
    const direct = player();
    assert.equal(direct.seek(4000), true);
    assert.equal(direct.element.currentTime, 4000);
});
