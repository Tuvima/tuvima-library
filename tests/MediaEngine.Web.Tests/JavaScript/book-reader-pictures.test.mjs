import test from "node:test";
import assert from "node:assert/strict";
import {
    pictureTypeFromName,
    pictureTypeFromBytes,
    inferPictureType,
} from "../../../src/MediaEngine.Web/wwwroot/js/book-reader-pictures.js";

const png = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0]);
const jpeg = Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 0, 0x10, 0x4a, 0x46, 0x49, 0x46, 0, 1]);
const gif = Uint8Array.from([...Buffer.from("GIF89a"), 0, 0, 0, 0]);
const webp = Uint8Array.from([...Buffer.from("RIFF"), 0, 0, 0, 0, ...Buffer.from("WEBP")]);
const avif = Uint8Array.from([0, 0, 0, 0x1c, ...Buffer.from("ftypavif")]);
const text = Buffer.from("this is not a picture at all");

test("a picture's file name gives its type, ignoring case, folders and query strings", () => {
    assert.equal(pictureTypeFromName("OEBPS/Images/Fig-1.JPG"), "image/jpeg");
    assert.equal(pictureTypeFromName("cover.png?x=1"), "image/png");
    assert.equal(pictureTypeFromName("map.webp"), "image/webp");
    assert.equal(pictureTypeFromName("art.svg"), "image/svg+xml");
});

test("a name that says nothing about pictures gives no type", () => {
    assert.equal(pictureTypeFromName("OEBPS/images/nameless"), "");
    assert.equal(pictureTypeFromName("styles.v2/nameless"), "");
    assert.equal(pictureTypeFromName("font.ttf"), "");
    assert.equal(pictureTypeFromName(undefined), "");
});

test("the first bytes of a picture give its type", () => {
    assert.equal(pictureTypeFromBytes(png), "image/png");
    assert.equal(pictureTypeFromBytes(jpeg), "image/jpeg");
    assert.equal(pictureTypeFromBytes(gif), "image/gif");
    assert.equal(pictureTypeFromBytes(webp), "image/webp");
    assert.equal(pictureTypeFromBytes(avif), "image/avif");
    assert.equal(pictureTypeFromBytes(text), "");
    assert.equal(pictureTypeFromBytes(null), "");
});

test("an untyped picture is recognised from its bytes even when its name is wrong", async () => {
    assert.equal(await inferPictureType("images/photo.png", new Blob([jpeg])), "image/jpeg");
    assert.equal(await inferPictureType("images/nameless", new Blob([png])), "image/png");
    assert.equal(await inferPictureType("images/nameless", jpeg.buffer.slice(jpeg.byteOffset, jpeg.byteOffset + jpeg.byteLength)), "image/jpeg");
});

test("bytes that are not a picture fall back to the name, and to nothing when the name says nothing", async () => {
    assert.equal(await inferPictureType("images/art.gif", new Blob([text])), "image/gif");
    assert.equal(await inferPictureType("data/blob.bin", new Blob([text])), "");
});

test("a font or a script is never mistaken for a picture", async () => {
    assert.equal(await inferPictureType("fonts/serif.ttf", new Blob([Uint8Array.from([0, 1, 0, 0, 0, 0])])), "");
    assert.equal(await inferPictureType("js/app.js", new Blob(["alert(1)"])), "");
});
