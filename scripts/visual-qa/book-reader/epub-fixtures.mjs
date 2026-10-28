// Builds EPUB fixtures for the book reader checks. No dependencies; runs on Node 20+.
//   node scripts/visual-qa/book-reader/epub-fixtures.mjs [outDir]
// Output goes to .tmp/book-reader-fixtures by default (ignored by git).

import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

// ---------------------------------------------------------------------------
// Tiny zip writer (mimetype stored first, text deflated, binary stored)
// ---------------------------------------------------------------------------

const crcTable = (() => {
    const table = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
        let c = n;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
        table[n] = c >>> 0;
    }
    return table;
})();

export function crc32(buf) {
    if (typeof zlib.crc32 === 'function') return zlib.crc32(buf) >>> 0;
    let c = 0xFFFFFFFF;
    for (let i = 0; i < buf.length; i++) c = crcTable[(c ^ buf[i]) & 0xFF] ^ (c >>> 8);
    return (c ^ 0xFFFFFFFF) >>> 0;
}

class ZipFile {
    constructor(file) {
        this.fd = fs.openSync(file, 'w');
        this.offset = 0;
        this.entries = [];
    }
    #write(buf) {
        fs.writeSync(this.fd, buf);
        this.offset += buf.length;
    }
    add(name, data, { store = false } = {}) {
        data = Buffer.isBuffer(data) ? data : Buffer.from(data);
        const nameBuf = Buffer.from(name);
        const crc = crc32(data);
        const body = store ? data : zlib.deflateRawSync(data);
        const method = store ? 0 : 8;
        const header = Buffer.alloc(30);
        header.writeUInt32LE(0x04034b50, 0);
        header.writeUInt16LE(20, 4);
        header.writeUInt16LE(0x0800, 6);
        header.writeUInt16LE(method, 8);
        header.writeUInt32LE(0x00210000, 10);
        header.writeUInt32LE(crc, 14);
        header.writeUInt32LE(body.length, 18);
        header.writeUInt32LE(data.length, 22);
        header.writeUInt16LE(nameBuf.length, 26);
        const entry = { name: nameBuf, crc, method, csize: body.length, size: data.length, offset: this.offset };
        this.#write(header);
        this.#write(nameBuf);
        this.#write(body);
        this.entries.push(entry);
    }
    close() {
        const start = this.offset;
        for (const e of this.entries) {
            const h = Buffer.alloc(46);
            h.writeUInt32LE(0x02014b50, 0);
            h.writeUInt16LE(20, 4);
            h.writeUInt16LE(20, 6);
            h.writeUInt16LE(0x0800, 8);
            h.writeUInt16LE(e.method, 10);
            h.writeUInt32LE(0x00210000, 12);
            h.writeUInt32LE(e.crc, 16);
            h.writeUInt32LE(e.csize, 20);
            h.writeUInt32LE(e.size, 24);
            h.writeUInt16LE(e.name.length, 28);
            h.writeUInt32LE(e.offset, 42);
            this.#write(h);
            this.#write(e.name);
        }
        const size = this.offset - start;
        const end = Buffer.alloc(22);
        end.writeUInt32LE(0x06054b50, 0);
        end.writeUInt16LE(this.entries.length, 8);
        end.writeUInt16LE(this.entries.length, 10);
        end.writeUInt32LE(size, 12);
        end.writeUInt32LE(start, 16);
        this.#write(end);
        fs.closeSync(this.fd);
    }
}

// ---------------------------------------------------------------------------
// PNG helpers (valid images; optional junk chunk makes them as big as needed)
// ---------------------------------------------------------------------------

const pngChunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type), data]);
    const out = Buffer.alloc(12 + data.length);
    out.writeUInt32BE(data.length, 0);
    body.copy(out, 4);
    out.writeUInt32BE(crc32(body), 8 + data.length);
    return out;
};

/** A solid-colour PNG of width x height; `junkBytes` of an ignorable private chunk pad the file. */
export function makePng(width, height, [r, g, b], junkBytes = 0) {
    const row = Buffer.alloc(1 + width * 3);
    for (let x = 0; x < width; x++) { row[1 + x * 3] = r; row[2 + x * 3] = g; row[3 + x * 3] = b; }
    const raw = Buffer.concat(Array.from({ length: height }, () => row));
    const ihdr = Buffer.alloc(13);
    ihdr.writeUInt32BE(width, 0);
    ihdr.writeUInt32BE(height, 4);
    ihdr[8] = 8; ihdr[9] = 2;
    const parts = [Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), pngChunk('IHDR', ihdr)];
    if (junkBytes > 0) parts.push(pngChunk('prVt', crypto.randomBytes(junkBytes)));
    parts.push(pngChunk('IDAT', zlib.deflateSync(raw)), pngChunk('IEND', Buffer.alloc(0)));
    return Buffer.concat(parts);
}

const WORDS = 'the quiet harbour lantern morning river stone bridge letter garden winter orchard window candle meadow ferry'.split(' ');
const paragraph = (seed, sentences = 6) => {
    let s = seed;
    const next = () => (s = (s * 1103515245 + 12345) & 0x7fffffff);
    return Array.from({ length: sentences }, () => {
        const n = 8 + (next() % 8);
        const words = Array.from({ length: n }, () => WORDS[next() % WORDS.length]);
        words[0] = words[0][0].toUpperCase() + words[0].slice(1);
        return words.join(' ') + '.';
    }).join(' ');
};

const xhtml = (title, body, head = '') => `<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>${title}</title>${head}</head><body>${body}</body></html>`;

const CONTAINER = `<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>`;

function opf({ title, chapters, extraManifest = '' }) {
    const manifest = chapters.map((c, i) => `<item id="c${i}" href="text/${c}.xhtml" media-type="application/xhtml+xml"/>`).join('');
    const spine = chapters.map((_, i) => `<itemref idref="c${i}"/>`).join('');
    return `<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
<metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="bookid">urn:uuid:11111111-2222-3333-4444-555555555555</dc:identifier><dc:title>${title}</dc:title><dc:creator>Test Author</dc:creator><dc:language>en</dc:language><meta property="dcterms:modified">2026-10-10T00:00:00Z</meta></metadata>
<manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>${manifest}${extraManifest}</manifest>
<spine>${spine}</spine></package>`;
}

const nav = chapters => xhtml('Contents', `<nav epub:type="toc"><ol>${chapters.map(c => `<li><a href="text/${c}.xhtml">${c}</a></li>`).join('')}</ol></nav>`);

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

export const FIXTURE_TRACKER_PATH = '/tracker/pixel.png';

/**
 * Regression fixture from the 2026-10-10 baseline of the old reader. It carries: a relative image,
 * an absolute-path image (/OEBPS/...), a stylesheet with url(), an inline <style> and style="" with url(),
 * an SVG <image>, an external image, a missing image, and scripts that must never run.
 */
export function buildRegressionEpub(file, { trackerOrigin = 'http://127.0.0.1:9' } = {}) {
    const zip = new ZipFile(file);
    zip.add('mimetype', 'application/epub+zip', { store: true });
    zip.add('META-INF/container.xml', CONTAINER);
    const chapters = ['cover', 'ch1', 'ch2', 'ch3'];
    const extra = [
        ['img1', 'images/pic1.png', 'image/png'], ['img2', 'images/pic2.png', 'image/png'],
        ['bg1', 'images/bg1.png', 'image/png'], ['bg2', 'images/bg2.png', 'image/png'], ['cov', 'images/cover.png', 'image/png'],
        ['css', 'css/style.css', 'text/css'], ['js', 'js/evil.js', 'application/javascript'],
    ].map(([id, href, type]) => `<item id="${id}" href="${href}" media-type="${type}"/>`).join('');
    zip.add('OEBPS/content.opf', opf({ title: 'Regression Fixture', chapters, extraManifest: extra }));
    zip.add('OEBPS/nav.xhtml', nav(chapters));
    zip.add('OEBPS/images/pic1.png', makePng(120, 80, [200, 40, 40]), { store: true });
    zip.add('OEBPS/images/pic2.png', makePng(120, 80, [40, 160, 60]), { store: true });
    zip.add('OEBPS/images/bg1.png', makePng(16, 16, [40, 60, 200]), { store: true });
    zip.add('OEBPS/images/bg2.png', makePng(16, 16, [230, 190, 30]), { store: true });
    zip.add('OEBPS/images/cover.png', makePng(300, 450, [120, 70, 200]), { store: true });
    zip.add('OEBPS/css/style.css', `.sheet-bg { background-image: url(../images/bg2.png); padding: 12px; }\n.hero { color: #123456; }`);
    zip.add('OEBPS/js/evil.js', `window.__pwned = 'external-script'; try { window.parent.__pwned = 'external-script-parent'; } catch (e) {}`);
    zip.add('OEBPS/text/cover.xhtml', xhtml('Cover', `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" version="1.1" viewBox="0 0 300 450" width="300" height="450"><image width="300" height="450" xlink:href="../images/cover.png"/></svg>`));
    zip.add('OEBPS/text/ch1.xhtml', xhtml('Chapter one', `
<h1 class="hero">Chapter one</h1>
<p id="relative-img-wrap"><img id="relative-img" src="../images/pic1.png" alt="relative picture" width="120" height="80"/></p>
<p id="absolute-img-wrap"><img id="absolute-img" src="/OEBPS/images/pic2.png" alt="absolute path picture" width="120" height="80"/></p>
<div id="inline-style-bg" style="background-image:url('../images/bg1.png'); padding: 12px;">Inline style with url()</div>
<div id="sheet-bg" class="sheet-bg">Stylesheet with url()</div>
<div id="style-block-bg" class="block-bg">Style block with url()</div>
<p id="svg-wrap"><svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" id="svg-img" width="120" height="80" viewBox="0 0 120 80"><image id="svg-image" width="120" height="80" xlink:href="../images/pic1.png"/></svg></p>
<p id="external-wrap"><img id="external-img" src="${trackerOrigin}${FIXTURE_TRACKER_PATH}" alt="external picture" width="20" height="20"/></p>
<p id="missing-wrap"><img id="missing-img" src="../images/missing.png" alt="missing picture" width="20" height="20"/></p>
<p><a id="external-link" href="https://example.org/page">An outside link</a> and <a id="internal-link" href="ch2.xhtml#start">a link to chapter two</a>.</p>
<script>window.__pwned = 'inline-script'; try { window.parent.__pwned = 'inline-script-parent'; } catch (e) {}</script>
<img id="onerror-img" src="../images/missing.png" onerror="window.__pwned='onerror-handler'" alt="" width="1" height="1"/>
<p id="onclick-p" onclick="window.__pwned='onclick-handler'">Click me</p>
<p>${paragraph(1, 12)}</p>`,
        `<link rel="stylesheet" type="text/css" href="../css/style.css"/><style>.block-bg { background-image: url(../images/bg1.png); padding: 8px; }</style><script src="../js/evil.js"></script>`));
    zip.add('OEBPS/text/ch2.xhtml', xhtml('Chapter two', `<h1 id="start">Chapter two</h1>${Array.from({ length: 60 }, (_, i) => `<p>${paragraph(100 + i, 6)}</p>`).join('\n')}`));
    zip.add('OEBPS/text/ch3.xhtml', xhtml('Chapter three', `<h1>Chapter three</h1>${Array.from({ length: 60 }, (_, i) => `<p>${paragraph(900 + i, 6)}</p>`).join('\n')}`));
    zip.close();
}

/**
 * A hostile book (the 2026-10-10 review): scripts hidden in an SVG spine item, in an SVG that a chapter embeds
 * through <iframe>/<object>/<embed>, in an XHTML-namespace element inside an SVG, in an XML-typed spine item,
 * in a page that is not well-formed XHTML, and a meta refresh. Nothing here may ever run, and the harmless
 * parts (text, pictures) must still show.
 */
export const HOSTILE_PWN = "window.__pwned = (window.__pwned ? window.__pwned + ',' : '') + 'NAME'; try { window.parent.__pwned = 'NAME-parent'; window.top.__pwned = 'NAME-top'; } catch (e) {}";
const pwn = name => HOSTILE_PWN.replaceAll('NAME', name);

export function buildHostileEpub(file) {
    const zip = new ZipFile(file);
    zip.add('mimetype', 'application/epub+zip', { store: true });
    zip.add('META-INF/container.xml', CONTAINER);
    const items = [
        ['ch1', 'text/ch1.xhtml', 'application/xhtml+xml'],
        ['svgok', 'text/ok.svg', 'image/svg+xml'],
        ['svgbad', 'text/bad.svg', 'image/svg+xml'],
        ['xmlbad', 'text/bad.xml', 'text/xml'],
        ['broken', 'text/broken.xhtml', 'application/xhtml+xml'],
        ['evil', 'images/evil.svg', 'image/svg+xml'],
        // Media types written with parameters or stray spaces: the library compares types exactly, so these used to skip its checks.
        ['param1', 'text/param1.xhtml', 'application/xhtml+xml;charset=utf-8'],
        ['param2', 'text/param2.svg', 'image/svg+xml;x=1'],
        ['param3', 'text/param3.xhtml', ' Application/XHTML+xml '],
        ['param4', 'text/param4.xhtml', 'application/xhtml+xml'],
        ['pic', 'images/pic.png', 'image/png'],
    ].map(([id, href, type]) => `<item id="${id}" href="${href}" media-type="${type}"/>`).join('');
    const spine = ['ch1', 'svgok', 'svgbad', 'xmlbad', 'broken', 'param1', 'param2', 'param3', 'param4'].map(id => `<itemref idref="${id}"/>`).join('');
    zip.add('OEBPS/content.opf', `<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
<metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="bookid">urn:uuid:99999999-2222-3333-4444-555555555555</dc:identifier><dc:title>Hostile Fixture</dc:title><dc:creator>Test Author</dc:creator><dc:language>en</dc:language><meta property="dcterms:modified">2026-10-10T00:00:00Z</meta></metadata>
<manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>${items}</manifest><spine>${spine}</spine></package>`);
    zip.add('OEBPS/nav.xhtml', nav(['ch1']));
    zip.add('OEBPS/images/pic.png', makePng(120, 80, [200, 40, 40]), { store: true });
    const svgNs = 'xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"';
    // Same bad SVG used as a spine item and as the target of frames inside a chapter.
    const bad = name => `<?xml version="1.0" encoding="utf-8"?>
<svg ${svgNs} xmlns:h="http://www.w3.org/1999/xhtml" viewBox="0 0 300 200" width="300" height="200" onload="${pwn(name + '-onload')}">
<title>hostile</title>
<script>${pwn(name + '-script')}</script>
<h:script>${pwn(name + '-xhtml-script')}</h:script>
<foreignObject width="300" height="100"><h:div><h:script>${pwn(name + '-fo-script')}</h:script><h:iframe src="javascript:window.top.__pwned='${name}-fo-iframe'"></h:iframe></h:div></foreignObject>
<a xlink:href="javascript:window.top.__pwned='${name}-link'"><text id="link-text" x="10" y="150">a script link</text></a>
<a href="https://example.org/ok"><text id="web-link-text" x="10" y="170">a web link</text></a>
<rect width="50" height="50" fill="teal" onclick="${pwn(name + '-onclick')}"/>
<set attributeName="href" to="javascript:alert(1)" begin="0s"/>
<image id="outside-image" width="10" height="10" xlink:href="http://127.0.0.1:9/tracker/pixel.png"/>
<text id="visible-text" x="10" y="190">Visible text in a hostile svg</text>
</svg>`;
    zip.add('OEBPS/images/evil.svg', bad('evil-frame'));
    zip.add('OEBPS/text/bad.svg', bad('svg-spine'));
    zip.add('OEBPS/text/ok.svg', `<?xml version="1.0" encoding="utf-8"?>
<svg ${svgNs} viewBox="0 0 300 200" width="300" height="200"><rect width="300" height="200" fill="#ddeeff"/><image id="ok-image" width="120" height="80" xlink:href="../images/pic.png"/><text id="ok-text" x="10" y="150">A harmless svg page</text></svg>`);
    zip.add('OEBPS/text/bad.xml', `<?xml version="1.0"?>
<html xmlns="http://www.w3.org/1999/xhtml"><body><p id="xml-text">xml typed page</p><script>${pwn('xml-spine')}</script></body></html>`);
    zip.add('OEBPS/text/broken.xhtml', `<html><head><meta http-equiv="refresh" content="0;url=https://example.org/gone"/></head><body><p id="broken-text">Not well formed <b>xhtml<p>second <script>${pwn('broken-script')}</script></body>`);
    const xhtmlWithScript = (name, id) => `<?xml version="1.0"?>
<?xml-stylesheet type="text/xsl" href="data:text/xml;base64,PGE+"?>
<!DOCTYPE html [<!ENTITY e "entity">]>
<html xmlns="http://www.w3.org/1999/xhtml"><head><title>${name}</title></head><body><p id="${id}">${name} text</p><script>${pwn(name + '-script')}</script><p onclick="${pwn(name + '-onclick')}">x</p><noscript><!--</noscript><meta http-equiv="refresh" content="0;url=https://example.org/gone"/>--></noscript></body></html>`;
    zip.add('OEBPS/text/param1.xhtml', xhtmlWithScript('param1', 'param1-text'));
    zip.add('OEBPS/text/param3.xhtml', xhtmlWithScript('param3', 'param3-text'));
    zip.add('OEBPS/text/param4.xhtml', xhtmlWithScript('param4', 'param4-text'));
    zip.add('OEBPS/text/param2.svg', bad('param2').replace('<text id="visible-text"', '<text id="param2-text"'));
    zip.add('OEBPS/text/ch1.xhtml', xhtml('Chapter', `
<h1 id="hostile-title">Hostile chapter</h1>
<p>${paragraph(7, 6)}</p>
<iframe id="evil-iframe" src="../images/evil.svg" width="300" height="200"></iframe>
<object id="evil-object" data="../images/evil.svg" type="image/svg+xml" width="300" height="200"></object>
<embed id="evil-embed" src="../images/evil.svg" type="image/svg+xml" width="300" height="200"/>
<img id="evil-img" src="../images/evil.svg" alt="svg as picture" width="150" height="100"/>
<iframe id="js-iframe" src="javascript:window.top.__pwned='js-iframe'"></iframe>
<a id="js-link" href="javascript:window.top.__pwned='js-link'">script link</a>
<meta http-equiv="refresh" content="0;url=https://example.org/gone"/>
<p>${paragraph(8, 6)}</p>`));
    zip.close();
}

/**
 * Links a tap could follow out of the book's frame (the 2026-10-16 review). Each page is one way of writing a link that is
 * not a plain `<a href>` in the page, so the clean-up and the click handler must both know it: an image map (`<area>`), a
 * MathML element with an `href` (a live link in WebKit) and an anchor hidden in a declarative shadow root (`<template
 * shadowrootmode>`, only live in a page the browser reads as text/html). `LINK_PAGES` is also used by run-checks.mjs to
 * test the clean-up and the click handler one at a time, so `click` is always the element a tap should reach.
 */
export const LINK_EVIL = 'https://evil.test/';
const LINK_PIXEL = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';
const XHTML_PAGE = 'application/xhtml+xml';
const HTML_PAGE = 'text/html';
export const LINK_PAGES = [
    {
        name: 'image-map', type: XHTML_PAGE, click: '#vector',
        body: `<p><img id="vector" usemap="#m" src="${LINK_PIXEL}" width="200" height="120" alt="map"/><map name="m"><area shape="default" href="${LINK_EVIL}area" alt="area"/></map></p>`,
    },
    {
        name: 'mathml', type: XHTML_PAGE, click: '#vector',
        body: `<p><math xmlns="http://www.w3.org/1998/Math/MathML"><mtext id="vector" href="${LINK_EVIL}math">math link</mtext></math></p>`,
    },
    {
        name: 'shadow-dom', type: HTML_PAGE, click: '#vector',
        body: `<div id="host"><template shadowrootmode="open"><a id="vector" href="${LINK_EVIL}shadow">shadow link</a></template></div>`,
    },
];
/**
 * Forms (FIX-5b). The frame's policy already refuses a submission (`form-action 'none'` in the page, no `allow-forms` in the
 * sandbox), so these pages are what the clean-up and the submit handler must also stop when neither of those is there. Each is
 * one way of sending the frame to an outside address on a tap: a form whose `action` is outside, a submit button with its own
 * `formaction`, and an image button (`<input type="image">`, which submits at the point it was tapped). The last page is the
 * same attack in a page read as text/html. `click` is always the control a tap should reach.
 */
export const FORM_PAGES = [
    {
        name: 'form-action', type: XHTML_PAGE, click: '#vector',
        body: `<form id="f" action="${LINK_EVIL}form" method="get"><p><input type="text" name="q" value="x"/><button id="vector" type="submit">send</button></p></form>`,
    },
    {
        name: 'button-formaction', type: XHTML_PAGE, click: '#vector',
        body: `<form id="f" method="get"><p><button id="vector" type="submit" formaction="${LINK_EVIL}button">send</button></p></form>`,
    },
    {
        name: 'image-input', type: XHTML_PAGE, click: '#vector',
        body: `<form id="f" action="${LINK_EVIL}image" method="get"><p><input id="vector" type="image" src="${LINK_PIXEL}" width="200" height="120" alt="go"/></p></form>`,
    },
    {
        name: 'form-action-html', type: HTML_PAGE, click: '#vector',
        body: `<table><tr><td><form id="f" action="${LINK_EVIL}table" method="get"><input type="text" name="q" value="x"><input id="vector" type="submit" value="send" formaction="${LINK_EVIL}table-button"></form></td></tr></table>`,
    },
];
/** Anchors every link page also carries: only the first two may ever be opened, and the last is a link inside the book. */
export const LINK_ANCHORS = '<p><a id="web" href="https://example.org/ok">web</a> <a id="mail" href="mailto:reader@example.org">mail</a> <a id="ftp" href="ftp://example.org/file">ftp</a> <a id="tel" href="tel:+15550100">tel</a> <a id="js" href="javascript:window.__x=1">js</a> <a id="local" href="#after">local</a></p>';
/** One page as the book would ship it, in the type the manifest gives it. */
export const linkPageText = page => page.type === HTML_PAGE
    ? `<!DOCTYPE html><html><head><title>${page.name}</title></head><body><p id="top">${page.name}</p>${page.body}${LINK_ANCHORS}<p id="after">after</p></body></html>`
    : `<?xml version="1.0" encoding="utf-8"?>\n<html xmlns="http://www.w3.org/1999/xhtml"><head><title>${page.name}</title></head><body><p id="top">${page.name}</p>${page.body}${LINK_ANCHORS}<p id="after">after</p></body></html>`;

export function buildLinkEpub(file, pages = LINK_PAGES, title = 'Link Fixture') {
    const zip = new ZipFile(file);
    zip.add('mimetype', 'application/epub+zip', { store: true });
    zip.add('META-INF/container.xml', CONTAINER);
    const manifest = pages.map((page, i) => `<item id="p${i}" href="text/${page.name}.${page.type === HTML_PAGE ? 'html' : 'xhtml'}" media-type="${page.type}"/>`).join('');
    const spine = pages.map((_, i) => `<itemref idref="p${i}"/>`).join('');
    zip.add('OEBPS/content.opf', `<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
<metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="bookid">urn:uuid:88888888-2222-3333-4444-555555555555</dc:identifier><dc:title>${title}</dc:title><dc:creator>Test Author</dc:creator><dc:language>en</dc:language><meta property="dcterms:modified">2026-10-16T00:00:00Z</meta></metadata>
<manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>${manifest}</manifest><spine>${spine}</spine></package>`);
    zip.add('OEBPS/nav.xhtml', nav([pages[0].name]));
    for (const page of pages) zip.add(`OEBPS/text/${page.name}.${page.type === HTML_PAGE ? 'html' : 'xhtml'}`, linkPageText(page));
    zip.close();
}

/** A plain book of roughly `sizeMB` megabytes: many chapters, each with a valid PNG padded by an ignorable chunk. */
export function buildBigEpub(file, sizeMB) {
    const zip = new ZipFile(file);
    zip.add('mimetype', 'application/epub+zip', { store: true });
    zip.add('META-INF/container.xml', CONTAINER);
    const imageBytes = 500 * 1024;
    const count = Math.max(4, Math.round((sizeMB * 1024 * 1024) / (imageBytes + 20 * 1024)));
    const chapters = Array.from({ length: count }, (_, i) => `ch${String(i + 1).padStart(4, '0')}`);
    const extra = chapters.map((c, i) => `<item id="i${i}" href="images/${c}.png" media-type="image/png"/>`).join('');
    zip.add('OEBPS/content.opf', opf({ title: `Big book ${sizeMB} MB`, chapters, extraManifest: extra }));
    zip.add('OEBPS/nav.xhtml', nav(chapters));
    chapters.forEach((c, i) => {
        zip.add(`OEBPS/images/${c}.png`, makePng(240, 160, [(i * 37) % 255, (i * 91) % 255, (i * 53) % 255], imageBytes), { store: true });
        zip.add(`OEBPS/text/${c}.xhtml`, xhtml(c, `<h1>${c}</h1><p><img src="../images/${c}.png" alt="plate ${i}" width="240" height="160"/></p>${Array.from({ length: 30 }, (_, p) => `<p>${paragraph(i * 100 + p, 6)}</p>`).join('\n')}`));
    });
    zip.close();
    return { chapters: count };
}

/**
 * Pictures as real publishers ship them: no width/height attributes, percentage sizes, a full-page cover SVG and a
 * picture inside a figure. Every page is one pattern (`PICTURE_PAGES`), so a check can tell which pattern collapses.
 * WebKit (iPhone) lost these inside the paginator's columns while the explicitly sized pictures in the regression
 * fixture still showed.
 */
export const PICTURE_PAGES = ['bare', 'full-width', 'max-width', 'figure', 'cover-svg', 'cover-img', 'inline'];
export function buildPictureEpub(file) {
    const zip = new ZipFile(file);
    zip.add('mimetype', 'application/epub+zip', { store: true });
    zip.add('META-INF/container.xml', CONTAINER);
    const extra = '<item id="pic" href="images/pic.png" media-type="image/png"/><item id="css" href="css/style.css" media-type="text/css"/>';
    zip.add('OEBPS/content.opf', opf({ title: 'Picture Fixture', chapters: PICTURE_PAGES, extraManifest: extra }));
    zip.add('OEBPS/nav.xhtml', nav(PICTURE_PAGES));
    zip.add('OEBPS/images/pic.png', makePng(300, 450, [120, 70, 200]), { store: true });
    zip.add('OEBPS/css/style.css', [
        'html, body { height: 100%; margin: 0; padding: 0; }',
        '.cover-img { height: 100%; width: 100%; }',
        'figure { margin: 1em 0; }',
        'figure img { width: 100%; height: auto; }',
    ].join('\n'));
    const link = '<link rel="stylesheet" type="text/css" href="../css/style.css"/>';
    const img = attrs => `<img class="pic" src="../images/pic.png" alt="picture" ${attrs}/>`;
    const pages = {
        'bare': `<h1>Bare</h1><p>${paragraph(7, 2)}</p>${img('')}<p>${paragraph(8, 2)}</p>`,
        'full-width': `<h1>Full width</h1>${img('style="width:100%"')}<p>${paragraph(9, 2)}</p>`,
        'max-width': `<h1>Max width</h1>${img('style="max-width:100%;height:auto"')}<p>${paragraph(10, 2)}</p>`,
        'figure': `<h1>Figure</h1><figure>${img('')}<figcaption>A caption</figcaption></figure><p>${paragraph(11, 2)}</p>`,
        'cover-svg': '<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" version="1.1" width="100%" height="100%" viewBox="0 0 300 450" preserveAspectRatio="xMidYMid meet"><image width="300" height="450" xlink:href="../images/pic.png"/></svg>',
        'cover-img': '<div style="height:100%;text-align:center"><img class="pic cover-img" src="../images/pic.png" alt="cover" style="height:100%"/></div>',
        'inline': `<p>Some text ${img('style="height:1em"')} with a small inline picture, then text again. ${paragraph(12, 3)}</p>`,
    };
    for (const name of PICTURE_PAGES) zip.add(`OEBPS/text/${name}.xhtml`, xhtml(name, pages[name], link));
    zip.close();
}

/**
 * Front matter as publishers ship it: a printed contents list with dotted leaders made of tables, floats and
 * `white-space: nowrap`, a wide table, a `pre` block, a fixed-width div and an oversize picture. Used to measure that
 * nothing on a reflowable page spills past its column (and so shows as a clipped continuation on the next page).
 */
export const FRONT_MATTER_PAGES = ['title', 'copyright', 'contents-table', 'contents-float', 'wide-table', 'pre-block', 'fixed-width', 'oversize-image'];
export function buildFrontMatterEpub(file) {
    const zip = new ZipFile(file);
    zip.add('mimetype', 'application/epub+zip', { store: true });
    zip.add('META-INF/container.xml', CONTAINER);
    const extra = '<item id="wide" href="images/wide.png" media-type="image/png"/><item id="css" href="css/style.css" media-type="text/css"/>';
    zip.add('OEBPS/content.opf', opf({ title: 'Front Matter Fixture', chapters: FRONT_MATTER_PAGES, extraManifest: extra }));
    zip.add('OEBPS/nav.xhtml', nav(FRONT_MATTER_PAGES));
    zip.add('OEBPS/images/wide.png', makePng(1600, 400, [200, 90, 60]), { store: true });
    zip.add('OEBPS/css/style.css', [
        '.leader { white-space: nowrap; }',
        '.toc-row { width: 100%; white-space: nowrap; overflow: visible; }',
        '.toc-row .title { float: left; white-space: nowrap; }',
        '.toc-row .page { float: right; white-space: nowrap; }',
        '.dots { white-space: nowrap; letter-spacing: 2px; }',
        'table.toc { width: 900px; }',
        '.fixed { width: 700px; border: 1px solid #888; }',
        'pre { margin: 1em 0; }',
    ].join('\n'));
    const link = '<link rel="stylesheet" type="text/css" href="../css/style.css"/>';
    const dots = '. '.repeat(120);
    const tocRows = n => Array.from({ length: n }, (_, i) => `<tr><td>Chapter ${i + 1}: A title that is long enough to matter</td><td class="dots">${dots}</td><td>${i * 12 + 7}</td></tr>`).join('');
    const floatRows = n => Array.from({ length: n }, (_, i) => `<div class="toc-row"><span class="title">Chapter ${i + 1}: A title that is long enough to matter ${dots}</span><span class="page">${i * 12 + 7}</span></div>`).join('');
    const pages = {
        'title': `<h1>The Front Matter Book</h1><p class="leader">${'A very long unbreakable subtitle line for the title page '.repeat(4)}</p><p>by Test Author</p>`,
        'copyright': `<p class="leader">Copyright (c) 2026 Test Author. All rights reserved. ${'No part of this publication may be reproduced '.repeat(3)}</p><p>ISBN 978-0-00-000000-0</p><p>${paragraph(21, 2)}</p>`,
        'contents-table': `<h1>Contents</h1><table class="toc">${tocRows(18)}</table>`,
        'contents-float': `<h1>Contents</h1>${floatRows(18)}<p>${paragraph(22, 2)}</p>`,
        'wide-table': `<h1>Wide table</h1><table style="width:1200px"><tr>${Array.from({ length: 8 }, (_, i) => `<th>Column ${i + 1}</th>`).join('')}</tr><tr>${Array.from({ length: 8 }, (_, i) => `<td>Cell ${i + 1} text</td>`).join('')}</tr></table><p>${paragraph(23, 2)}</p>`,
        'pre-block': `<h1>Pre</h1><pre>${'0123456789 abcdefghij '.repeat(12)}\n${'x'.repeat(220)}</pre><p>${paragraph(24, 2)}</p>`,
        'fixed-width': `<h1>Fixed width</h1><div class="fixed"><p>${paragraph(25, 3)}</p></div><div style="width:900px">${paragraph(26, 1)}</div>`,
        'oversize-image': `<h1>Oversize picture</h1><img src="../images/wide.png" width="1600" height="400" alt="wide"/><p>${paragraph(27, 2)}</p>`,
    };
    for (const name of FRONT_MATTER_PAGES) zip.add(`OEBPS/text/${name}.xhtml`, xhtml(name, pages[name], link));
    zip.close();
}

/** An image-heavy fixed-layout (pre-paginated) book: every page is one large picture. */
export function buildFixedLayoutEpub(file, pages = 40) {
    const zip = new ZipFile(file);
    zip.add('mimetype', 'application/epub+zip', { store: true });
    zip.add('META-INF/container.xml', CONTAINER);
    const chapters = Array.from({ length: pages }, (_, i) => `p${String(i + 1).padStart(3, '0')}`);
    const extra = chapters.map((c, i) => `<item id="i${i}" href="images/${c}.png" media-type="image/png"/>`).join('');
    const base = opf({ title: `Fixed layout ${pages} pages`, chapters, extraManifest: extra })
        .replace('<meta property="dcterms:modified">', '<meta property="rendition:layout">pre-paginated</meta><meta property="dcterms:modified">');
    zip.add('OEBPS/content.opf', base);
    zip.add('OEBPS/nav.xhtml', nav(chapters));
    chapters.forEach((c, i) => {
        zip.add(`OEBPS/images/${c}.png`, makePng(1200, 1800, [(i * 37) % 255, (i * 91) % 255, (i * 53) % 255], 600 * 1024), { store: true });
        zip.add(`OEBPS/text/${c}.xhtml`, xhtml(c, `<img src="../images/${c}.png" alt="page ${i + 1}" width="1200" height="1800"/>`,
            '<meta name="viewport" content="width=1200, height=1800"/><style>body{margin:0}img{display:block}</style>'));
    });
    zip.close();
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    const out = path.resolve(process.argv[2] ?? '.tmp/book-reader-fixtures');
    fs.mkdirSync(out, { recursive: true });
    buildRegressionEpub(path.join(out, 'regression.epub'));
    console.log('regression.epub');
    buildHostileEpub(path.join(out, 'hostile.epub'));
    console.log('hostile.epub');
    buildFixedLayoutEpub(path.join(out, 'fixed-layout.epub'));
    console.log('fixed-layout.epub');
    for (const size of [5, 50, 200]) {
        const info = buildBigEpub(path.join(out, `big-${size}mb.epub`), size);
        console.log(`big-${size}mb.epub (${info.chapters} chapters)`);
    }
}

/**
 * Pictures as real publishers ship them: labelled with an empty type, a generic "binary" type or no label that matches,
 * plus one file that is not a picture at all (it must show the "Picture unavailable" note and nothing else).
 */
const SMALL_JPEG = Buffer.from('/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAA0JCgsKCA0LCgsODg0PEyAVExISEyccHhcgLikxMC4pLSwzOko+MzZGNywtQFdBRkxOUlNSMj5aYVpQYEpRUk//2wBDAQ4ODhMREyYVFSZPNS01T09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0//wAARCAAoADwDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDOoooqDwgooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKAP/Z', 'base64');
export const UNTYPED_PICTURE_PAGES = ['untyped'];
export function buildUntypedPictureEpub(file) {
    const zip = new ZipFile(file);
    zip.add('mimetype', 'application/epub+zip', { store: true });
    zip.add('META-INF/container.xml', CONTAINER);
    const extra = [
        ['png-empty', 'images/empty-label.png', ''],
        ['jpg-binary', 'images/binary-label.jpg', 'application/octet-stream'],
        ['jpg-nameless', 'images/nameless-label', 'binary/octet-stream'],
        ['png-ok', 'images/proper.png', 'image/png'],
        ['broken', 'images/damaged.png', 'image/png'],
        ['css', 'css/style.css', 'text/css'],
    ].map(([id, href, type]) => `<item id="${id}" href="${href}" media-type="${type}"/>`).join('');
    zip.add('OEBPS/content.opf', opf({ title: 'Untyped Picture Fixture', chapters: UNTYPED_PICTURE_PAGES, extraManifest: extra }));
    zip.add('OEBPS/nav.xhtml', nav(UNTYPED_PICTURE_PAGES));
    zip.add('OEBPS/images/empty-label.png', makePng(120, 80, [200, 40, 40]), { store: true });
    zip.add('OEBPS/images/binary-label.jpg', SMALL_JPEG, { store: true });
    zip.add('OEBPS/images/nameless-label', SMALL_JPEG, { store: true });
    zip.add('OEBPS/images/proper.png', makePng(120, 80, [40, 40, 200]), { store: true });
    zip.add('OEBPS/images/damaged.png', 'this is not a picture at all', { store: true });
    zip.add('OEBPS/css/style.css', 'img { display: block; }');
    const link = '<link rel="stylesheet" type="text/css" href="../css/style.css"/>';
    const body = [
        '<h1>Untyped pictures</h1>',
        '<img id="empty-label" src="../images/empty-label.png" alt="a" width="120" height="80"/>',
        '<img id="binary-label" src="../images/binary-label.jpg" alt="b" width="60" height="40"/>',
        '<img id="nameless-label" src="../images/nameless-label" alt="c" width="60" height="40"/>',
        '<img id="proper" src="../images/proper.png" alt="d" width="120" height="80"/>',
        '<img id="damaged" src="../images/damaged.png" alt="e" width="120" height="80"/>',
        `<p id="after">${paragraph(21, 2)}</p>`,
    ].join('');
    zip.add('OEBPS/text/untyped.xhtml', xhtml('untyped', body, link));
    zip.close();
}
