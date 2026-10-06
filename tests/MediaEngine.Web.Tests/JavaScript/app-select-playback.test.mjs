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
        this.dataset = {}; this.style = {}; this.handlers = new Map(); this.tabIndex = tag === "button" ? 0 : -1;
        this.parentNode = null; this.scrollHeight = 240;
    }
    addEventListener(name, handler) { if (!this.handlers.has(name)) this.handlers.set(name, new Set()); this.handlers.get(name).add(handler); }
    removeEventListener(name, handler) { this.handlers.get(name)?.delete(handler); }
    get id() { return this.getAttribute("id"); }
    set id(value) { this.setAttribute("id", value); }
    append(child) { if (child.parentNode) child.parentNode.children = child.parentNode.children.filter(item => item !== child); this.children.push(child); child.parentNode = this; }
    before(marker) { if (!this.parentNode) return; marker.parentNode = this.parentNode; this.parentNode.children.splice(this.parentNode.children.indexOf(this), 0, marker); }
    replaceWith(child) { const parent = this.parentNode; if (!parent) return; if (child.parentNode) child.parentNode.children = child.parentNode.children.filter(item => item !== child); parent.children[parent.children.indexOf(this)] = child; child.parentNode = parent; this.parentNode = null; }
    contains(child) { return child === this || this.children.some(item => item.contains(child)); }
    getBoundingClientRect() { return { left: 100, right: 320, top: 400, bottom: 444, width: 220, height: 44 }; }
    focus() { document.activeElement = this; }
    click() { this.onClick?.(); }
    hasAttribute(name) { return this.attributes.has(name); }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    setAttribute(name, value) {
        this.attributes.set(name, String(value));
        if (name === "tabindex") this.tabIndex = Number(value);
        if (name.startsWith("data-")) this.dataset[name.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = String(value);
        this.writes++;
    }
    removeAttribute(name) {
        if (!this.attributes.has(name)) return;
        this.attributes.delete(name);
        if (name.startsWith("data-")) delete this.dataset[name.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())];
        this.writes++;
    }
    getClientRects() { return this.visible && this.isConnected ? [{}] : []; }
    closest() { return null; }
    querySelector(selector) {
        return this.querySelectorAll(selector)[0] ?? null;
    }
    querySelectorAll(selector) {
        if (selector === ".tl-select-trigger") return this.children.filter(child => child.classes.has("tl-select-trigger") && child.getAttribute("tabindex") !== null);
        if (selector === ".tl-list") return this.children.filter(child => child.classes.has("tl-list"));
        if (selector === ".tl-list-item") return this.children.filter(child => child.classes.has("tl-list-item"));
        if (selector === '.tl-popover-open[data-playback-owned-menu]') return this.children.filter(child => child.classes.has("tl-popover-open") && child.getAttribute("data-playback-owned-menu"));
        if (selector.startsWith('.playback-tool-sheet__close')) return this.children.filter(child => child.classes.has("playback-tool-sheet__close"));
        if (selector.startsWith('a[href]')) return this.children.flatMap(child => [child, ...child.querySelectorAll(selector)]).filter(child => child.tabIndex >= 0);
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

const popovers = [], panels = new Map(), documentHandlers = new Map();
globalThis.window = new FakeElement();
globalThis.innerWidth = 420; globalThis.innerHeight = 780;
globalThis.document = {
    body: new FakeElement(), fullscreenElement: null, activeElement: null,
    createComment() { return new FakeElement(); },
    getElementById(id) { return panels.get(id) ?? null; },
    addEventListener(name, handler) { if (!documentHandlers.has(name)) documentHandlers.set(name, new Set()); documentHandlers.get(name).add(handler); },
    removeEventListener(name, handler) { documentHandlers.get(name)?.delete(handler); },
    documentElement: new FakeElement(),
    querySelectorAll(selector) { return selector === ".tl-popover" ? popovers.filter(item => item.isConnected) : []; }
};

const modulePath = new URL("../../../src/MediaEngine.Web/wwwroot/js/app-select-playback.js", import.meta.url);
// Import by the real file URL so the adapter's shared tooltip dependency resolves normally.
const playbackSelect = await import(modulePath.href);
const sheet = await import(new URL("../../../src/MediaEngine.Web/wwwroot/js/playback-tool-sheet.js", import.meta.url).href);

function createSelect(token, { selected = 0, triggerFirst = true, open = true } = {}) {
    const hiddenInput = new FakeElement({ classes: ["tl-select-trigger"] , visible: false, tag: "input" });
    hiddenInput.setAttribute("tabindex", "0");
    const trigger = new FakeElement({ classes: ["tl-select-trigger"], tag: "div" });
    trigger.setAttribute("tabindex", "0");
    const root = new FakeElement();
    root.children = triggerFirst ? [hiddenInput, trigger] : [trigger, hiddenInput];
    root.children.forEach(child => { child.parentNode = root; });

    const options = [0, 1, 2].map((index) => new FakeElement({ classes: ["tl-list-item", ...(index === selected ? ["tl-selected-item"] : [])] }));
    const list = new FakeElement({ classes: ["tl-list"] });
    list.children = options;
    const popover = new FakeElement({ classes: ["tl-popover", "app-select__popover", token, ...(open ? ["tl-popover-open"] : [])] });
    popover.children = [list];
    new FakeElement().append(popover);
    popovers.push(popover);
    return { root, hiddenInput, trigger, options, list, popover };
}

for (const surface of ["phone", "popup"]) {
    test(`${surface} sheet owns the visible version portal and Escape closes the menu before the sheet`, async () => {
        const select = createSelect(`app-select__playback-menu-sheet-${surface}`);
        const panel = new FakeElement();
        panel.id = `lyrics-sheet-${surface}`;
        panels.set(panel.id, panel);
        const close = new FakeElement({ tag: "button", classes: ["playback-tool-sheet__close"] });
        let closedSheets = 0, closedMenus = 0;
        close.onClick = () => { closedSheets++; };
        panel.append(close); panel.append(select.root);
        select.root.setAttribute("data-playback-parent-panel", panel.id);
        const originalParent = select.popover.parentNode;
        const reference = { invokeMethodAsync(method) {
            assert.equal(method, "ClosePlaybackMenuAsync");
            closedMenus++;
            select.popover.classes.delete("tl-popover-open");
            observers.forEach(observer => observer.flush());
            select.trigger.focus();
            return Promise.resolve();
        } };
        playbackSelect.attach(select.root, `app-select__playback-menu-sheet-${surface}`, "Lyrics version", reference);
        sheet.attachModal(panel);
        assert.equal(select.popover.parentNode, panel);
        assert.equal(select.popover.getAttribute("data-playback-owned-menu"), panel.id);
        assert.equal(select.trigger.getAttribute("aria-expanded"), "true");
        assert.equal(select.list.getAttribute("id"), select.trigger.getAttribute("aria-controls"));
        function escape() {
            let stopped = false;
            const event = { key: "Escape", target: select.trigger, preventDefault() {}, stopImmediatePropagation() { stopped = true; }, stopPropagation() { stopped = true; } };
            for (const listener of documentHandlers.get("keydown") ?? []) { listener(event); if (stopped) break; }
            if (!stopped) for (const listener of panel.handlers.get("keydown") ?? []) listener(event);
        }
        escape();
        await Promise.resolve();
        assert.equal(closedMenus, 1);
        assert.equal(closedSheets, 0);
        assert.equal(select.trigger.getAttribute("aria-expanded"), "false");
        assert.equal(document.activeElement, select.trigger);
        assert.equal(select.popover.parentNode, originalParent);
        escape();
        assert.equal(closedSheets, 1);
        sheet.restoreFocus(panel, false);
        playbackSelect.detach(select.root, `app-select__playback-menu-sheet-${surface}`);
        panels.delete(panel.id);
    });
}

test("decorates only the visible native trigger and the select's own popup", () => {
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

test("selection follows the native select state and repeated mutation delivery is bounded", () => {
    const select = createSelect("app-select__playback-menu-selection");
    playbackSelect.attach(select.root, "app-select__playback-menu-selection", "Playback speed");
    assert.equal(select.options[0].getAttribute("aria-selected"), "true");

    select.options[0].classes.delete("tl-selected-item");
    select.options[2].classes.add("tl-selected-item");
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

    select.popover.classes.add("tl-popover-open");
    select.list.visible = false;
    observer.flush();
    assert.equal(select.trigger.getAttribute("aria-expanded"), "false");

    select.list.visible = true;
    observer.flush();
    assert.equal(select.trigger.getAttribute("aria-expanded"), "true");

    select.popover.classes.delete("tl-popover-open");
    observer.flush();
    assert.equal(select.trigger.getAttribute("aria-expanded"), "false");
    playbackSelect.detach(select.root, "app-select__playback-menu-open-state");
});

test("detach, replacement, and reconnect clean only this adapter's attributes", () => {
    const select = createSelect("app-select__playback-menu-reconnect");
    select.trigger.setAttribute("aria-label", "Original label");
    playbackSelect.attach(select.root, "app-select__playback-menu-reconnect", "Playback speed");
    const oldObserver = observers.at(-1);

    playbackSelect.attach(select.root, "app-select__playback-menu-reconnect", "New playback label");
    assert.equal(oldObserver.disconnected, true);
    assert.equal(select.trigger.getAttribute("aria-label"), "New playback label");
    playbackSelect.detach(select.root, "different-owner");
    assert.equal(select.trigger.getAttribute("role"), "combobox");

    playbackSelect.detach(select.root, "app-select__playback-menu-reconnect");
    assert.equal(select.trigger.getAttribute("aria-label"), "Original label");
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
    assert.equal(pickerPrevented, false, "the picker event can continue to the native select");

    let playerPrevented = false;
    const stageTarget = { tagName: "DIV", closest: () => null };
    handler({ target: stageTarget, key: "ArrowDown", preventDefault: () => { playerPrevented = true; } });
    assert.deepEqual(actions, ["volume-down"]);
    assert.equal(playerPrevented, true);
});

test("native top-layer selectors keep their Razor owner DOM while playback owns position", () => {
    const select = createSelect("app-select__playback-menu-native-owner");
    select.popover.setAttribute("popover", "manual");
    const panel = new FakeElement();
    panel.id = "native-sheet";
    panels.set(panel.id, panel);
    panel.append(select.root);
    select.root.setAttribute("data-playback-parent-panel", panel.id);
    const originalParent = select.popover.parentNode;
    playbackSelect.attach(select.root, "app-select__playback-menu-native-owner", "Lyrics version");
    assert.equal(select.popover.parentNode, originalParent);
    assert.equal(select.popover.getAttribute("data-playback-owned-menu"), panel.id);
    assert.equal(select.popover.style.position, "fixed");
    playbackSelect.detach(select.root, "app-select__playback-menu-native-owner");
    assert.equal(select.popover.parentNode, originalParent);
    assert.equal(select.popover.getAttribute("data-playback-owned-menu"), null);
    panels.delete(panel.id);
});
