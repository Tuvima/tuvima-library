import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

class FakeElement {
    constructor(name) {
        this.name = name;
        this.isConnected = true;
        this.hidden = false;
        this.tabIndex = 0;
        this.attributes = new Map();
        this.listeners = new Map();
        this.focusCount = 0;
    }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    closest() { return null; }
    getClientRects() { return this.isConnected ? [{}] : []; }
    focus() {
        this.focusCount++;
        document.activeElement = this;
    }
    addEventListener(name, listener) { this.listeners.set(name, listener); }
    removeEventListener(name, listener) {
        if (this.listeners.get(name) === listener) this.listeners.delete(name);
    }
    querySelectorAll() { return [this.closeButton]; }
    contains(element) { return element === this.closeButton; }
    closest() { return null; }
}

globalThis.HTMLElement = FakeElement;
globalThis.document = { body: new FakeElement("body"), activeElement: null };
globalThis.getComputedStyle = element => ({ visibility: element.hidden ? "hidden" : "visible" });
globalThis.requestAnimationFrame = callback => setTimeout(callback, 0);

const root = new URL("../../../src/MediaEngine.Web/wwwroot/js/", import.meta.url);
const contextSource = await readFile(new URL("context-sidebar.js", root), "utf8");
const sheetSource = await readFile(new URL("playback-tool-sheet.js", root), "utf8");
const contextModule = await import(`data:text/javascript;base64,${Buffer.from(contextSource).toString("base64")}`);
const sheetModule = await import(`data:text/javascript;base64,${Buffer.from(sheetSource).toString("base64")}`);
const injectableContextSource = contextSource.replace(
    "const contextSidebarModalLifecycle = createContextSidebarModalLifecycle();",
    "const contextSidebarModalLifecycle = createContextSidebarModalLifecycle(() => Promise.resolve(globalThis.__sidebarModalTestModule));");
assert.notEqual(injectableContextSource, contextSource, "default lifecycle seam exists for the public adapter test");
globalThis.__sidebarModalTestModule = sheetModule;
const publicContextModule = await import(`data:text/javascript;base64,${Buffer.from(injectableContextSource).toString("base64")}`);

function createAside(name) {
    const aside = new FakeElement(name);
    aside.closeButton = new FakeElement(`${name}-close`);
    aside.closeButton.attributes.set("aria-label", `Close ${name}`);
    return aside;
}

test("captures the opener before import, then restores that opener from the exact attached aside", async () => {
    const shell = new FakeElement("persistent-shell");
    const aside = createAside("original-aside");
    let conditionalAsideRef = aside;
    const opener = new FakeElement("focused-opener");
    const changedDuringImport = new FakeElement("other-control");
    document.activeElement = opener;
    let completeImport;
    const lifecycle = contextModule.createContextSidebarModalLifecycle(
        () => new Promise(resolve => { completeImport = resolve; }),
        () => document.activeElement);

    const attaching = lifecycle.attachContextSidebarModal(shell, conditionalAsideRef, "owner-1:4");
    document.activeElement = changedDuringImport;
    completeImport(sheetModule);
    await attaching;
    assert.equal(document.activeElement, aside.closeButton);

    const replacementAside = createAside("replacement-aside");
    conditionalAsideRef = replacementAside;
    aside.isConnected = false;
    assert.equal(lifecycle.restoreContextSidebarModal(shell, "owner-1:4"), true);
    assert.equal(aside.listeners.size, 0);
    assert.equal(replacementAside.listeners.size, 0);
    assert.equal(document.activeElement, opener);
    assert.equal(lifecycle.restoreContextSidebarModal(shell, "owner-1:4"), false);
});

test("stale and repeated restores cannot detach a replacement lease or steal focus", async () => {
    const shell = new FakeElement("persistent-shell");
    const firstAside = createAside("first-aside");
    const secondAside = createAside("second-aside");
    const opener = new FakeElement("opener");
    const replacementOpener = new FakeElement("replacement-opener");
    document.activeElement = opener;
    const lifecycle = contextModule.createContextSidebarModalLifecycle(async () => sheetModule,
        () => document.activeElement);

    await lifecycle.attachContextSidebarModal(shell, firstAside, "owner-1:1");
    document.activeElement = replacementOpener;
    await lifecycle.attachContextSidebarModal(shell, secondAside, "owner-2:2");
    assert.equal(firstAside.listeners.size, 0);
    assert.equal(lifecycle.restoreContextSidebarModal(shell, "owner-1:1"), false);
    assert.equal(secondAside.listeners.size, 1);

    assert.equal(lifecycle.restoreContextSidebarModal(shell, "owner-2:2"), true);
    assert.equal(lifecycle.restoreContextSidebarModal(shell, "owner-2:2"), false);
    assert.equal(secondAside.listeners.size, 0);
    assert.equal(document.activeElement, replacementOpener);
});

test("retained-open generation replacement keeps the original outside focus return target", async () => {
    const shell = new FakeElement("persistent-shell");
    const aside = createAside("same-aside");
    const opener = new FakeElement("outside-opener");
    document.activeElement = opener;
    const lifecycle = contextModule.createContextSidebarModalLifecycle(async () => sheetModule,
        () => document.activeElement);

    await lifecycle.attachContextSidebarModal(shell, aside, "owner:panel-generation-1");
    assert.equal(document.activeElement, aside.closeButton);
    await lifecycle.attachContextSidebarModal(shell, aside, "owner:panel-generation-2");
    assert.equal(aside.listeners.size, 1);
    assert.equal(document.activeElement, aside.closeButton);
    assert.equal(lifecycle.restoreContextSidebarModal(shell, "owner:panel-generation-2"), true);

    assert.equal(aside.listeners.size, 0);
    assert.equal(document.activeElement, opener);
});

test("public restore adapter forwards the no-focus-restore replacement option", async () => {
    const shell = new FakeElement("persistent-shell");
    const aside = createAside("replacement-aside");
    const opener = new FakeElement("old-opener");
    const replacementFocus = new FakeElement("replacement-focus");
    document.activeElement = opener;
    const lifecycle = contextModule.createContextSidebarModalLifecycle(async () => sheetModule,
        () => document.activeElement);

    await lifecycle.attachContextSidebarModal(shell, aside, "owner:old");
    document.activeElement = replacementFocus;
    assert.equal(lifecycle.restoreContextSidebarModal(shell, "owner:old", false), true);

    assert.equal(aside.listeners.size, 0);
    assert.equal(document.activeElement, replacementFocus);
});

test("exported restore function forwards its third argument through the production adapter", async () => {
    const shell = new FakeElement("persistent-shell");
    const aside = createAside("public-adapter-aside");
    const opener = new FakeElement("old-opener");
    const replacementFocus = new FakeElement("replacement-focus");
    document.activeElement = opener;

    await publicContextModule.attachContextSidebarModal(shell, aside, "owner:public");
    document.activeElement = replacementFocus;
    publicContextModule.restoreContextSidebarModal(shell, "owner:public", false);

    assert.equal(aside.listeners.size, 0);
    assert.equal(document.activeElement, replacementFocus);
});

test("canceling a pending import removes the lease before the late module can attach", async () => {
    const shell = new FakeElement("persistent-shell");
    const aside = createAside("pending-aside");
    let completeImport;
    const lifecycle = contextModule.createContextSidebarModalLifecycle(
        () => new Promise(resolve => { completeImport = resolve; }),
        () => document.activeElement);

    const attaching = lifecycle.attachContextSidebarModal(shell, aside, "owner:pending");
    assert.equal(lifecycle.restoreContextSidebarModal(shell, "owner:pending"), true);
    completeImport(sheetModule);
    await attaching;
    assert.equal(aside.listeners.size, 0);
});

test("hidden or disconnected openers are never focused during restore", async () => {
    for (const state of ["hidden", "disconnected"]) {
        const shell = new FakeElement("persistent-shell");
        const aside = createAside(`${state}-aside`);
        const opener = new FakeElement(`${state}-opener`);
        const currentControl = new FakeElement("current-control");
        document.activeElement = opener;
        const lifecycle = contextModule.createContextSidebarModalLifecycle(async () => sheetModule,
            () => document.activeElement);
        await lifecycle.attachContextSidebarModal(shell, aside, `owner:${state}`);
        if (state === "hidden") opener.hidden = true;
        else opener.isConnected = false;
        document.activeElement = currentControl;

        assert.equal(lifecycle.restoreContextSidebarModal(shell, `owner:${state}`), true);
        assert.equal(document.activeElement, currentControl);
        assert.equal(opener.focusCount, 0);
    }
});
