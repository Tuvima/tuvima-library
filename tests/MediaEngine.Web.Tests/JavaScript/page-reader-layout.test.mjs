import test from "node:test";
import assert from "node:assert/strict";
import {
    buildViews,
    chooseWidth,
    effectiveLayout,
    isRightToLeft,
    keyAction,
    tapAction,
    viewIndexOf,
} from "../../../src/MediaEngine.Web/wwwroot/js/page-reader-layout.js";

const portrait = { width: 800, height: 1200 };
const spread = { width: 2400, height: 1200 };
const pages = count => Array.from({ length: count }, () => portrait);

test("Auto shows two pages only in a window at least 1180 wide and wider than tall", () => {
    assert.equal(effectiveLayout("auto", 1440, 900), "two");
    assert.equal(effectiveLayout("auto", 1180, 900), "two");
    assert.equal(effectiveLayout("auto", 1179, 600), "one");
    assert.equal(effectiveLayout("auto", 1300, 1400), "one");
    assert.equal(effectiveLayout("auto", 390, 800), "one");
    assert.equal(effectiveLayout("one", 1600, 900), "one");
    assert.equal(effectiveLayout("scroll", 1600, 900), "scroll");
});

test("two-page layout keeps the cover alone and pairs the rest", () => {
    assert.deepEqual(buildViews("two", pages(6)), [[1], [2, 3], [4, 5], [6]]);
    assert.deepEqual(buildViews("two", pages(1)), [[1]]);
    assert.deepEqual(buildViews("two", []), []);
});

test("a double-page spread is always alone, and the pages around it pair up again", () => {
    const list = [portrait, portrait, portrait, spread, portrait, portrait];
    assert.deepEqual(buildViews("two", list), [[1], [2, 3], [4], [5, 6]]);
    assert.deepEqual(buildViews("two", [portrait, spread, portrait]), [[1], [2], [3]]);
});

test("one-page layout shows every page by itself", () => {
    assert.deepEqual(buildViews("one", pages(3)), [[1], [2], [3]]);
});

test("a page finds the view it sits in", () => {
    const views = buildViews("two", pages(6));
    assert.equal(viewIndexOf(views, 1), 0);
    assert.equal(viewIndexOf(views, 3), 1);
    assert.equal(viewIndexOf(views, 6), 3);
    assert.equal(viewIndexOf(views, 99), 0);
});

test("a picture size is the smallest offered that is big enough, and the original past the biggest", () => {
    const widths = [160, 480, 720, 1080, 1440, 2160];
    assert.equal(chooseWidth(widths, 100), 160);
    assert.equal(chooseWidth(widths, 481), 720);
    assert.equal(chooseWidth(widths, 2160), 2160);
    assert.equal(chooseWidth(widths, 2161), 0);
    assert.equal(chooseWidth([], 500), 0);
});

test("right to left is chosen by hand, or follows the comic when left on automatic", () => {
    assert.equal(isRightToLeft("rtl", false), true);
    assert.equal(isRightToLeft("ltr", true), false);
    assert.equal(isRightToLeft("auto", true), true);
    assert.equal(isRightToLeft("auto", false), false);
});

test("the left third goes back, the right third goes forward, the middle shows the bars; right to left swaps the sides", () => {
    assert.equal(tapAction(0.1, false, false), "previous");
    assert.equal(tapAction(0.9, false, false), "next");
    assert.equal(tapAction(0.5, false, false), "chrome");
    assert.equal(tapAction(0.1, true, false), "next");
    assert.equal(tapAction(0.9, true, false), "previous");
    assert.equal(tapAction(0.1, false, true), "chrome");
});

test("keys turn pages, jump, zoom and close; right to left swaps the arrows", () => {
    assert.equal(keyAction("ArrowRight", false, false), "next");
    assert.equal(keyAction("ArrowLeft", false, false), "previous");
    assert.equal(keyAction("ArrowRight", false, true), "previous");
    assert.equal(keyAction("ArrowLeft", false, true), "next");
    assert.equal(keyAction(" ", false, false), "next");
    assert.equal(keyAction(" ", true, false), "previous");
    assert.equal(keyAction("PageDown", false, true), "next");
    assert.equal(keyAction("Home", false, false), "first");
    assert.equal(keyAction("End", false, false), "last");
    assert.equal(keyAction("+", false, false), "zoom-in");
    assert.equal(keyAction("-", false, false), "zoom-out");
    assert.equal(keyAction("Escape", false, false), "close");
    assert.equal(keyAction("q", false, false), null);
});
