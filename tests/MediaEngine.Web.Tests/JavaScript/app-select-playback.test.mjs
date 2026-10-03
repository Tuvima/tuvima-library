import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

class FakeElement {
    constructor({ classes = [], visible = true, tag = "div" } = {}) {
        this.classes = new Set(classes);
        this.classList = { contains: name => this.classes.has(name) };
        this.attributes = new Map();
        this.children = [];
        this.isConnected = true;
        this.visible = visible;
        this.tagName = tag.toUpperCase();
        this.writes = 0;
    }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    setAttribute(name, value) {
        this.attributes.set(name, String(value));
        this.writes++;
    }
    removeAttribute(name) {
        if (!this.attributes.has(name)) return;
        this.attributes.delete(name);
        this.writes++;
    }
    getClientRects() { return this.visible && this.isConnected ? [{}] : []; }
    closest() { return null; }
    querySelector(selector) {
        return this.querySelectorAll(selector)[0] ?? null;
    }
    querySelectorAll(selector) {
        if (selector === ".mud-select-input[tabindex]") return this.children.filter(child => child.classes.has("mud-select-input") && child.getAttribute("tabindex") !== null);
        if (selector === ".mud-list") return this.children.filter(child => child.classes.has("mud-list"));
        if (selector === ".mud-list-item") return this.children.filter(child => child.classes.has("mud-list-item"));
        return [];
    }
}

globalThis.HTMLElement = FakeElement;
globalThis.getComputedStyle = element => ({ display: element.visible ? "block" : "none", visibility: "visible" });
const observers = [];
globalThis.MutationObserver = class {
    constructor(callback) { this.callback = callback; this.disconnected = false; observers.push(this); }
    observe() { this.observing = true; }
    disconnect() { this.disconnected = true; this.observing = false; }
    flush() { if (this.observing) this.callback([]); }
};

const popovers = [];
globalThis.document = {
    documentElement: new FakeElement(),
    querySelectorAll(selector) { return selector === ".mud-popover" ? popovers.filter(item => item.isConnected) : []; }
};

const modulePath = new URL("../../../src/MediaEngine.Web/wwwroot/js/app-select-playback.js", import.meta.url);
const moduleText = await readFile(modulePath, "utf8");
const playbackSelect = await import(`data:text/javascript;base64,${Buffer.from(moduleText).toString("base64")}`);

function createSelect(token, { selected = 0, triggerFirst = true, open = true } = {}) {
    const hiddenInput = new FakeElement({ classes: ["mud-select-input"] , visible: false, tag: "input" });
    hiddenInput.setAttribute("tabindex", "0");
    const trigger = new FakeElement({ classes: ["mud-select-input"], tag: "div" });
    trigger.setAttribute("tabindex", "0");
    const root = new FakeElement();
    root.children = triggerFirst ? [hiddenInput, trigger] : [trigger, hiddenInput];

    const options = [0, 1, 2].map((index) => new FakeElement({ classes: ["mud-list-item", ...(index === selected ? ["mud-selected-item"] : [])] }));
    const list = new FakeElement({ classes: ["mud-list"] });
    list.children = options;
    const popover = new FakeElement({ classes: ["mud-popover", "app-select__popover", token, ...(open ? ["mud-popover-open"] : [])] });
    popover.children = [list];
    popovers.push(popover);
    return { root, hiddenInput, trigger, options, list, popover };
}

test("decorates only the visible Mud trigger and the select's own popup", () => {
    const first = createSelect("app-select__playback-menu-one", { triggerFirst: true });
    const second = createSelect("app-select__playback-menu-two", { selected: 2 });
    first.options[0].setAttribute("tabindex", "0");
    playbackSelect.attach(first.root, "app-select__popover app-select__playback-menu-one", "Playback speed");
    playbackSelect.attach(second.root, "app-select__popover app-select__playback-menu-two", "Sleep timer");

    assert.equal(first.hiddenInput.getAttribute("role"), null);
    assert.equal(first.hiddenInput.getAttribute("aria-label"), null);
    assert.equal(first.hiddenInput.getAttribute("tabindex"), "0");
    assert.equal(first.trigger.getAttribute("role"), "combobox");
    assert.equal(first.trigger.getAttribute("aria-haspopup"), "listbox");
    assert.equal(first.trigger.getAttribute("aria-expanded"), "true");
    assert.equal(first.list.getAttribute("role"), "listbox");
    assert.deepEqual(first.options.map(option => option.getAttribute("aria-selected")), ["true", "false", "false"]);
    assert.deepEqual(second.options.map(option => option.getAttribute("aria-selected")), ["false", "false", "true"]);
    assert.equal(first.list.getAttribute("id"), first.trigger.getAttribute("aria-controls"));
    assert.notEqual(first.list.getAttribute("id"), second.list.getAttribute("id"));

    playbackSelect.detach(first.root, "app-select__popover app-select__playback-menu-one");
    playbackSelect.detach(second.root, "app-select__popover app-select__playback-menu-two");
    assert.equal(first.options[0].getAttribute("tabindex"), "0");
});

test("selection follows MudBlazor state and repeated mutation delivery is bounded", () => {
    const select = createSelect("app-select__playback-menu-selection");
    playbackSelect.attach(select.root, "app-select__playback-menu-selection", "Playback speed");
    assert.equal(select.options[0].getAttribute("aria-selected"), "true");

    select.options[0].classes.delete("mud-selected-item");
    select.options[2].classes.add("mud-selected-item");
    const observer = observers.at(-1);
    observer.flush();
    assert.deepEqual(select.options.map(option => option.getAttribute("aria-selected")), ["false", "false", "true"]);

    const writes = [select.trigger, select.list, ...select.options].reduce((sum, element) => sum + element.writes, 0);
    observer.flush();
    observer.flush();
    assert.equal([select.trigger, select.list, ...select.options].reduce((sum, element) => sum + element.writes, 0), writes);
    playbackSelect.detach(select.root, "app-select__playback-menu-selection");
});

test("expanded state follows the owned portal open class and visible list", () => {
    const select = createSelect("app-select__playback-menu-open-state", { open: false });
    playbackSelect.attach(select.root, "app-select__playback-menu-open-state", "Playback speed");
    const observer = observers.at(-1);
    assert.equal(select.trigger.getAttribute("aria-expanded"), "false");

    select.popover.classes.add("mud-popover-open");
    select.list.visible = false;
    observer.flush();
    assert.equal(select.trigger.getAttribute("aria-expanded"), "false");

    select.list.visible = true;
    observer.flush();
    assert.equal(select.trigger.getAttribute("aria-expanded"), "true");

    select.popover.classes.delete("mud-popover-open");
    observer.flush();
    assert.equal(select.trigger.getAttribute("aria-expanded"), "false");
    playbackSelect.detach(select.root, "app-select__playback-menu-open-state");
});

test("detach, replacement, and reconnect clean only this adapter's attributes", () => {
    const select = createSelect("app-select__playback-menu-reconnect");
    select.trigger.setAttribute("aria-label", "Mud label");
    playbackSelect.attach(select.root, "app-select__playback-menu-reconnect", "Playback speed");
    const oldObserver = observers.at(-1);

    playbackSelect.attach(select.root, "app-select__playback-menu-reconnect", "New playback label");
    assert.equal(oldObserver.disconnected, true);
    assert.equal(select.trigger.getAttribute("aria-label"), "New playback label");
    playbackSelect.detach(select.root, "different-owner");
    assert.equal(select.trigger.getAttribute("role"), "combobox");

    playbackSelect.detach(select.root, "app-select__playback-menu-reconnect");
    assert.equal(select.trigger.getAttribute("aria-label"), "Mud label");
    assert.equal(select.trigger.getAttribute("role"), null);
    assert.equal(select.options[0].getAttribute("aria-selected"), null);

    playbackSelect.attach(select.root, "app-select__playback-menu-reconnect", "Playback speed");
    assert.equal(select.trigger.getAttribute("role"), "combobox");
    playbackSelect.detach(select.root, "app-select__playback-menu-reconnect");
});

test("a replacement adapter leaves attributes changed by another owner untouched", () => {
    const select = createSelect("app-select__playback-menu-owner-change");
    playbackSelect.attach(select.root, "app-select__playback-menu-owner-change", "Playback speed");
    select.trigger.setAttribute("aria-label", "Updated by another owner");
    playbackSelect.attach(select.root, "app-select__playback-menu-owner-change", "Playback speed");
    playbackSelect.detach(select.root, "app-select__playback-menu-owner-change");

    assert.equal(select.trigger.getAttribute("aria-label"), "Updated by another owner");
});

test("picker keyboard navigation is not consumed by playback shortcuts", async () => {
    const appJsPath = new URL("../../../src/MediaEngine.Web/wwwroot/app.js", import.meta.url);
    const appJs = await readFile(appJsPath, "utf8");
    const start = appJs.indexOf("    function isEditableShortcutTarget(target) {");
    const end = appJs.indexOf("    function audioEngineElement() {", start);
    assert.ok(start >= 0 && end > start, "player shortcut helper block exists");

    const handlers = new WeakMap();
    const source = `${appJs.slice(start, end)}\nglobalThis.testApi = { registerPlayerShortcuts, unregisterPlayerShortcuts };`;
    const context = {
        console,
        shortcutHandlerFor: element => handlers.get(element) ?? null,
        setShortcutHandler: (element, handler) => handler ? handlers.set(element, handler) : handlers.delete(element)
    };
    runInNewContext(source, context);

    const playerHandlers = new Map();
    const player = {
        addEventListener: (name, handler) => playerHandlers.set(name, handler),
        removeEventListener: (name, handler) => { if (playerHandlers.get(name) === handler) playerHandlers.delete(name); }
    };
    const actions = [];
    const dotNet = { invokeMethodAsync: (_method, action) => { actions.push(action); return Promise.resolve(); } };
    context.testApi.registerPlayerShortcuts(player, dotNet);
    const handler = playerHandlers.get("keydown");
    assert.equal(typeof handler, "function");

    let pickerPrevented = false;
    const option = {
        tagName: "LI",
        closest: selector => selector.includes('[role="option"]') ? option : null
    };
    handler({ target: option, key: "ArrowDown", preventDefault: () => { pickerPrevented = true; } });
    assert.deepEqual(actions, []);
    assert.equal(pickerPrevented, false, "the picker event can continue to MudBlazor");

    let playerPrevented = false;
    const stageTarget = { tagName: "DIV", closest: () => null };
    handler({ target: stageTarget, key: "ArrowDown", preventDefault: () => { playerPrevented = true; } });
    assert.deepEqual(actions, ["volume-down"]);
    assert.equal(playerPrevented, true);
});
