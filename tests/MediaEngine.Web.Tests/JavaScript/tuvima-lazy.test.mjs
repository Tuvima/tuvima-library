import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("../../../src/MediaEngine.Web/wwwroot/js/tuvima-lazy.js", import.meta.url), "utf8");

// A stand-in page: elements fire "load" or "error" on the next tick, as the browser does after the file arrives.
function page({ hlsSrc, outcome = "load", providesHls = true, withFirstStylesheet = false } = {}) {
    const added = [];
    const head = {
        children: [],
        appendChild(element) { this.children.push(element); added.push(element); schedule(element); },
        insertBefore(element, before) { this.children.splice(this.children.indexOf(before), 0, element); added.push(element); schedule(element); },
        querySelector(selector) { return selector === 'link[rel="stylesheet"]' ? this.children.find(child => child.tag === "link") ?? null : null; }
    };
    const context = { document: { body: { dataset: hlsSrc ? { hlsSrc } : {} }, head, createElement: tag => element(tag) } };
    context.window = context;
    function element(tag) {
        const listeners = {};
        return {
            tag, listeners,
            addEventListener(name, handler) { listeners[name] = handler; },
            remove() { head.children.splice(head.children.indexOf(this), 1); }
        };
    }
    function schedule(element) {
        setImmediate(() => {
            if (outcome === "load" && element.tag === "script" && providesHls) context.Hls = { isSupported: () => true };
            element.listeners[outcome]?.();
        });
    }
    if (withFirstStylesheet) head.children.push(element("link"));
    runInNewContext(source, context);
    return { context, head, added };
}

test("loadHls injects the fingerprinted address from the page once and reuses the same promise", async () => {
    const { context, added } = page({ hlsSrc: "/vendor/hls/hls.min.abc123.js" });
    const first = context.tuvimaLazy.loadHls();
    const second = context.tuvimaLazy.loadHls();
    assert.equal(first, second);
    await first;
    await context.tuvimaLazy.loadHls();
    assert.equal(added.length, 1);
    assert.equal(added[0].src, "/vendor/hls/hls.min.abc123.js");
});

test("loadHls falls back to the plain vendor address when the page gives none", async () => {
    const { context, added } = page();
    await context.tuvimaLazy.loadHls();
    assert.equal(added[0].src, "/vendor/hls/hls.min.js");
});

test("loadHls does not add a script when hls.js is already on the page", async () => {
    const { context, added } = page();
    context.Hls = { isSupported: () => true };
    await context.tuvimaLazy.loadHls();
    assert.equal(added.length, 0);
});

test("loadHls rejects with a clear error when the file fails and lets a later call try again", async () => {
    const failing = page({ outcome: "error" });
    await assert.rejects(failing.context.tuvimaLazy.loadHls(), /failed to load script/);
    assert.equal(failing.head.children.length, 0, "the failed tag is removed");
    await assert.rejects(failing.context.tuvimaLazy.loadHls(), /failed to load script/);
    assert.equal(failing.added.length, 2, "a second attempt injects a fresh tag");
});

test("loadHls rejects when the file loads but does not provide Hls", async () => {
    const { context } = page({ providesHls: false });
    await assert.rejects(context.tuvimaLazy.loadHls(), /did not provide Hls/);
});

test("loadMapLibreStyles resolves only after the stylesheet has loaded and loads it once", async () => {
    const { context, added } = page();
    let done = false;
    const first = context.tuvimaLazy.loadMapLibreStyles().then(() => { done = true; });
    assert.equal(done, false, "nothing resolves before the load event");
    await first;
    await context.tuvimaLazy.loadMapLibreStyles();
    assert.equal(added.length, 1);
    assert.equal(added[0].href, "/vendor/maplibre/maplibre-gl.css");
    assert.equal(added[0].rel, "stylesheet");
});

test("loadStylesheet goes before the app's own stylesheets so their overrides keep winning", async () => {
    const { context, head } = page({ withFirstStylesheet: true });
    await context.tuvimaLazy.loadMapLibreStyles();
    assert.equal(head.children[0].href, "/vendor/maplibre/maplibre-gl.css");
});

test("loadStylesheet rejects with a clear error when the stylesheet fails", async () => {
    const { context } = page({ outcome: "error" });
    await assert.rejects(context.tuvimaLazy.loadMapLibreStyles(), /failed to load stylesheet/);
});
