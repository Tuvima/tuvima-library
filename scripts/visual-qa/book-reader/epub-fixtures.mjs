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
    buildFixedLayoutEpub(path.join(out, 'fixed-layout.epub'));
    console.log('fixed-layout.epub');
    for (const size of [5, 50, 200]) {
        const info = buildBigEpub(path.join(out, `big-${size}mb.epub`), size);
        console.log(`big-${size}mb.epub (${info.chapters} chapters)`);
    }
}
