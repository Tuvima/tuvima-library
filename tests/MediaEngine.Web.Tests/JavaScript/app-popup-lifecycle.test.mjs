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
function harness(screen = { availWidth: 1600, availHeight: 900 }) {
    const events = new Map(), messages = [], opens = [];
    let sequence = 0, focused = 0;
    const nativeWindow = { closed: false, focus() { focused++; } };
    const context = {
        URL, console, Date: { now: () => 1730000000000 },
        popupWindow: null, popupWindowId: null, popupUnloadHandler: null, popupName: "TuvimaListenPlayer",
        playbackConfig: { popupWidth: 420, popupHeight: 780 },
        BroadcastChannel: class {
            constructor(name) { this.name = name; }
            postMessage(message) { messages.push({ name: this.name, message: JSON.parse(JSON.stringify(message)) }); }
            close() { }
        },
        window: {
            screen, location: { href: "https://dashboard.test/listen/music" },
            crypto: { randomUUID: () => `00000000-0000-4000-8000-${String(++sequence).padStart(12, "0")}` },
            tuvimaBookmarkCommands: { getOwnerId: () => "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE" },
            open(url, name, features) { opens.push({ url, name, features }); return nativeWindow; },
            addEventListener(name, callback) { events.set(name, callback); },
            removeEventListener(name, callback) { if (events.get(name) === callback) events.delete(name); },
            opener: { closed: false, focus() { focused++; } }
        }
    };
    runInNewContext([
        block("    function openPopupWindow(", "    function focusPopup("),
        `globalThis.api = { ${block("        isCurrentPopupWindow: function", "        focusPopup: focusPopup,")}, ${block("        unregisterPopupWindow: function", "        readAudioState: function")} };`
    ].join("\n"), context);
    return { context, api: context.api, opens, messages, events, nativeWindow, get focused() { return focused; } };
}

test("Popup starts at 420 by 780 with a fresh owner/window identity and reuses the existing native window", () => {
    const h = harness();
    assert.equal(h.context.openPopupWindow("/listen/player-popup"), true);
    const initial = h.opens[0];
    assert.match(initial.features, /width=420,height=780/);
    const url = new URL(initial.url, "https://dashboard.test");
    assert.equal(url.searchParams.get("owner"), "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
    assert.ok(h.api.isCurrentPopupWindow(url.searchParams.get("window")));
    assert.equal(h.context.openPopupWindow("/listen/player-popup"), true);
    assert.equal(h.opens.length, 1);
    assert.ok(h.api.isCurrentPopupWindow(url.searchParams.get("window")));
    h.nativeWindow.closed = true;
    assert.equal(h.api.isCurrentPopupWindow(url.searchParams.get("window")), false);
});

test("Popup clamps its dimensions to the available screen", () => {
    const h = harness({ availWidth: 320, availHeight: 600 });
    h.context.openPopupWindow("/listen/player-popup");
    assert.match(h.opens[0].features, /width=320,height=600/);
});

test("Native closure sends only a fresh addressed passive notification and unregister removes the callback", () => {
    const h = harness();
    h.api.registerPopupWindow("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", "sender", "window");
    h.events.get("beforeunload")();
    assert.equal(h.messages.length, 2);
    assert.equal(h.messages[0].name, "tuvima-bookmark-commands:aaaaaaaabbbbccccddddeeeeeeeeeeee");
    const registration = h.messages[0].message.command, closure = h.messages[1].message.command;
    assert.equal(registration.action, "register-popup");
    assert.equal(closure.action, "popup-closed");
    assert.equal(closure.popupWindowId, registration.popupWindowId);
    assert.equal(closure.ownerGeneration, registration.ownerGeneration);
    assert.equal(closure.senderId, "sender");
    assert.equal(closure.recipientId, registration.recipientId);
    assert.notEqual(closure.commandId, registration.commandId);
    h.api.unregisterPopupWindow();
    assert.equal(h.events.has("beforeunload"), false);
    assert.doesNotMatch(source, /closeOwnWindow|opener\.location(?:\.href)?\s*=(?!=)/);
});
