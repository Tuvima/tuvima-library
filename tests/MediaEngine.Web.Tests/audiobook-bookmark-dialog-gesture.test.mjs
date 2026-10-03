import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";

const sourceUrl = new URL("../../src/MediaEngine.Web/wwwroot/js/audiobook-bookmark-dialog.js", import.meta.url);
const source = await readFile(sourceUrl, "utf8");
const moduleUrl = `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`;
const { attachBookmarkSwipe, BOOKMARK_SWIPE_METRICS, detachBookmarkSwipe, isHorizontalBookmarkSwipe } = await import(moduleUrl);

class FakeElement {
    constructor({ row = null, excluded = false, action = false } = {}) {
        this.row = row;
        this.excluded = excluded;
        this.action = action;
    }

    closest(selector) {
        if (selector.includes("[data-bookmark-swipe-row]")) return this.row;
        if (selector.includes("[data-bookmark-swipe-action]") && this.action) return this;
        if (this.action && selector.includes("button")) return this;
        if (this.excluded && selector.includes("[data-bookmark-swipe-exclude]")) return this;
        if (this.excluded && selector.includes("button")) return this;
        return null;
    }
}

class FakeRoot {
    constructor(count = 2) {
        this.listeners = new Map();
        this.rows = Array.from({ length: count }, () => ({
            dataset: { swipeRevealed: "false" },
            setAttribute(name, value) { if (name === "data-swipe-revealed") this.dataset.swipeRevealed = value; },
        }));
        this.cssProperties = new Map();
        this.style = { setProperty: (name, value) => this.cssProperties.set(name, value) };
    }
    addEventListener(name, handler) { this.listeners.set(name, handler); }
    removeEventListener(name, handler) { if (this.listeners.get(name) === handler) this.listeners.delete(name); }
    contains(row) { return this.rows.includes(row); }
    querySelectorAll() { return this.rows; }
    dispatch(name, event) { this.listeners.get(name)?.({ preventDefault() {}, ...event }); }
}

const touch = (pointerId, x, y, target = new FakeElement({ row: null })) => ({
    pointerType: "touch", button: 0, pointerId, clientX: x, clientY: y, target,
});

test("horizontal intent starts at 12px and must exceed the 1.5:1 ratio", () => {
    assert.equal(BOOKMARK_SWIPE_METRICS.intentThreshold, 12);
    assert.equal(BOOKMARK_SWIPE_METRICS.horizontalIntentRatio, 1.5);
    assert.equal(isHorizontalBookmarkSwipe(-12, 7), true);
    assert.equal(isHorizontalBookmarkSwipe(-12, 8), false);
    assert.equal(isHorizontalBookmarkSwipe(-11, 0), false);
});

test("reveals one 48px action, keeps vertical pan native, and excludes controls", () => {
    const root = new FakeRoot();
    attachBookmarkSwipe(root);
    assert.equal(root.cssProperties.get("--bookmark-swipe-reveal-width"), "48px");
    assert.equal(root.cssProperties.get("--bookmark-swipe-action-target"), "56px");
    const [first, second] = root.rows;
    const text = new FakeElement({ row: first });
    root.dispatch("pointerdown", touch(1, 100, 20, text));
    root.dispatch("pointermove", touch(1, 88, 20, text));
    root.dispatch("pointerup", touch(1, 88, 20, text));
    assert.equal(first.dataset.swipeRevealed, "true");
    assert.equal(second.dataset.swipeRevealed, "false");

    root.dispatch("pointerdown", touch(2, 100, 20, new FakeElement({ row: second })));
    root.dispatch("pointermove", touch(2, 88, 20, new FakeElement({ row: second })));
    root.dispatch("pointerup", touch(2, 88, 20, new FakeElement({ row: second })));
    assert.equal(first.dataset.swipeRevealed, "false");
    assert.equal(second.dataset.swipeRevealed, "true");

    const prevented = { count: 0 };
    root.dispatch("pointerdown", touch(3, 20, 20, new FakeElement({ row: first })));
    root.dispatch("pointermove", { ...touch(3, 20, 40), preventDefault: () => prevented.count++ });
    root.dispatch("pointerup", touch(3, 20, 40));
    assert.equal(prevented.count, 0);
    assert.equal(second.dataset.swipeRevealed, "true");

    root.dispatch("pointerdown", touch(4, 100, 20, new FakeElement({ row: first, excluded: true })));
    root.dispatch("pointermove", touch(4, 80, 20));
    root.dispatch("pointerup", touch(4, 80, 20));
    root.dispatch("pointerdown", touch(5, 100, 20, new FakeElement({ row: first, action: true })));
    root.dispatch("pointermove", touch(5, 80, 20));
    root.dispatch("pointerup", touch(5, 80, 20));
    assert.equal(first.dataset.swipeRevealed, "false");

    const action = new FakeElement({ row: second, action: true });
    root.dispatch("click", { target: action });
    assert.equal(second.dataset.swipeRevealed, "false");
});

test("pointer cancellation and multitouch cancel intent; detached listeners stay inert", () => {
    const root = new FakeRoot(1);
    attachBookmarkSwipe(root);
    const text = new FakeElement({ row: root.rows[0] });

    root.dispatch("pointerdown", touch(1, 100, 20, text));
    root.dispatch("pointermove", touch(1, 80, 20, text));
    root.dispatch("pointercancel", touch(1, 80, 20, text));
    root.dispatch("pointerup", touch(1, 80, 20, text));
    assert.equal(root.rows[0].dataset.swipeRevealed, "false");

    root.dispatch("pointerdown", touch(2, 100, 20, text));
    root.dispatch("pointerdown", touch(3, 100, 20, text));
    root.dispatch("pointermove", touch(2, 80, 20, text));
    root.dispatch("pointerup", touch(2, 80, 20, text));
    root.dispatch("pointerup", touch(3, 80, 20, text));
    assert.equal(root.rows[0].dataset.swipeRevealed, "false");

    root.dispatch("pointerdown", touch(4, 100, 20, text));
    root.dispatch("pointermove", touch(4, 80, 20, text));
    detachBookmarkSwipe(root);
    root.dispatch("pointerup", touch(4, 80, 20, text));
    assert.equal(root.rows[0].dataset.swipeRevealed, "false");
    assert.equal(root.listeners.size, 0);
});
