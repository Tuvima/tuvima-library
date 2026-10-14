import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import {
    STORAGE_KEY,
    THEME_NAMES,
    FONT_NAMES,
    PAGE_WIDTH_NAMES,
    sanitizeSettings,
    mergeSettings,
    readDeviceSettings,
    writeDeviceSettings,
    clearDeviceSettings,
} from "../../../src/MediaEngine.Web/wwwroot/js/book-reader-settings.js";

function storageWith(value) {
    const items = new Map();
    if (value !== undefined) items.set(STORAGE_KEY, typeof value === "string" ? value : JSON.stringify(value));
    return {
        getItem: key => (items.has(key) ? items.get(key) : null),
        setItem: (key, v) => items.set(key, String(v)),
        removeItem: key => items.delete(key),
        items,
    };
}

const profile = { theme: "sepia", fontSize: 24, lineHeight: 1.9, font: "serif", pageWidth: "wide", flow: "scrolled" };

test("with nothing on the device the profile values apply", () => {
    assert.deepEqual(mergeSettings(profile, readDeviceSettings(storageWith())), profile);
});

test("the device wins per key and a key it never saved uses the profile value", () => {
    const device = storageWith({ Theme: "dark", FontSize: 20 });
    const merged = mergeSettings(profile, readDeviceSettings(device));

    assert.equal(merged.theme, "dark");
    assert.equal(merged.fontSize, 20);
    assert.equal(merged.lineHeight, 1.9);
    assert.equal(merged.font, "serif");
    assert.equal(merged.pageWidth, "wide");
    assert.equal(merged.flow, "scrolled");
});

test("device values are read from PascalCase or camelCase keys, with the typeface under ReaderFont", () => {
    const merged = mergeSettings(profile, readDeviceSettings(storageWith({ ReaderFont: "sans", pageWidth: "narrow", Flow: "paginated" })));

    assert.equal(merged.font, "sans");
    assert.equal(merged.pageWidth, "narrow");
    assert.equal(merged.flow, "paginated");
});

test("unknown or unreadable device values fall back to the profile, and numbers are clamped", () => {
    const device = storageWith({ Theme: "neon", ReaderFont: "comic", PageWidth: 3, Flow: "sideways", FontSize: 99, LineHeight: "nope" });
    const merged = mergeSettings(profile, readDeviceSettings(device));

    assert.equal(merged.theme, "sepia");
    assert.equal(merged.font, "serif");
    assert.equal(merged.pageWidth, "wide");
    assert.equal(merged.flow, "scrolled");
    assert.equal(merged.fontSize, 32);
    assert.equal(merged.lineHeight, 1.9);
});

test("corrupt device storage gives the profile values", () => {
    assert.deepEqual(mergeSettings(profile, readDeviceSettings(storageWith("{not json"))), profile);
});

test("profile values are validated too", () => {
    const merged = mergeSettings({ theme: "neon", fontSize: 4, lineHeight: 9, font: "serif" }, {});

    assert.equal(merged.theme, undefined);
    assert.equal(merged.fontSize, 12);
    assert.equal(merged.lineHeight, 2.4);
    assert.equal(merged.font, "serif");
});

test("a null or empty number is left out rather than clamped to the minimum", () => {
    assert.deepEqual(sanitizeSettings({ fontSize: null, lineHeight: "" }), {});
});

test("saving keeps other keys under the entry and writes only the reader keys", () => {
    const device = storageWith({ Other: 1 });
    writeDeviceSettings(device, { theme: "dark", fontSize: 22, lineHeight: 1.6, font: "book", pageWidth: "standard", flow: "paginated" });

    assert.deepEqual(JSON.parse(device.items.get(STORAGE_KEY)), {
        Other: 1, Theme: "dark", FontSize: 22, LineHeight: 1.6, ReaderFont: "book", PageWidth: "standard", Flow: "paginated",
    });
});

test("saving one setting writes only that key into the stored blob and keeps the rest", () => {
    const device = storageWith({ Other: 1, Theme: "dark" });
    writeDeviceSettings(device, { fontSize: 22 });

    assert.deepEqual(JSON.parse(device.items.get(STORAGE_KEY)), { Other: 1, Theme: "dark", FontSize: 22 });
});

test("saving one setting on a device that has nothing stored writes just that key", () => {
    const device = storageWith();
    writeDeviceSettings(device, { fontSize: 22 });
    assert.deepEqual(JSON.parse(device.items.get(STORAGE_KEY)), { FontSize: 22 });

    const corrupt = storageWith("{not json");
    writeDeviceSettings(corrupt, { theme: "sepia" });
    assert.deepEqual(JSON.parse(corrupt.items.get(STORAGE_KEY)), { Theme: "sepia" });
});

test("a device that only changed the size still follows later changes to the profile", () => {
    const device = storageWith();
    writeDeviceSettings(device, { fontSize: 22 });

    const before = mergeSettings({ theme: "light", fontSize: 18, font: "book" }, readDeviceSettings(device));
    assert.equal(before.theme, "light");
    assert.equal(before.fontSize, 22);

    // The profile's theme and size change later: the theme reaches this device, the size stays the device's own.
    const after = mergeSettings({ theme: "sepia", fontSize: 26, font: "book" }, readDeviceSettings(device));
    assert.equal(after.theme, "sepia");
    assert.equal(after.fontSize, 22);
});

test("reset clears the reader keys so the profile applies again, and keeps the other keys", () => {
    const device = storageWith({ Other: 1, Theme: "dark", FontSize: 22, ReaderFont: "sans" });
    clearDeviceSettings(device);

    assert.deepEqual(JSON.parse(device.items.get(STORAGE_KEY)), { Other: 1 });
    assert.deepEqual(mergeSettings(profile, readDeviceSettings(device)), profile);

    const onlyReader = storageWith({ Theme: "dark" });
    clearDeviceSettings(onlyReader);
    assert.equal(onlyReader.items.has(STORAGE_KEY), false);
});

test("the accepted names match the reader's own tables", async () => {
    const reader = await readFile(new URL("../../../src/MediaEngine.Web/wwwroot/js/book-reader.js", import.meta.url), "utf8");
    const keysOf = tableName => {
        const body = new RegExp(`const ${tableName} = \\{([\\s\\S]*?)\\};`).exec(reader)[1];
        return [...body.matchAll(/^\s*([a-z]+):/gm)].map(match => match[1]).sort();
    };

    assert.deepEqual([...THEME_NAMES].sort(), keysOf("THEMES").concat("system").sort());
    assert.deepEqual(FONT_NAMES.filter(name => name !== "book").sort(), keysOf("FONT_STACKS"));
    assert.deepEqual([...PAGE_WIDTH_NAMES].sort(), [...reader.match(/const PAGE_WIDTHS = \{([^}]*)\}/)[1].matchAll(/([a-z]+):/g)].map(m => m[1]).sort());
});

test("the reader's loadSettings is the profile base with the device on top", async () => {
    const reader = await readFile(new URL("../../../src/MediaEngine.Web/wwwroot/js/book-reader.js", import.meta.url), "utf8");

    assert.match(reader, /export function loadSettings\(profile\)\s*\{\s*return mergeSettings\(profile, readDeviceSettings\(globalThis\.localStorage\)\);\s*\}/);
    assert.match(reader, /export function clearSettings\(\)/);
});
