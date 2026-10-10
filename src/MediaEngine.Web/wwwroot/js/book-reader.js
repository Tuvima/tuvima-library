// ============================================================================
// Book reader adapter — Tuvima Library
//
// The ONLY file that imports foliate-js (the pinned copy in lib/foliate-js/).
// Blazor (Components/Pages/BookReader.razor) talks to this module; nothing else
// in the Dashboard touches the library. If foliate-js is ever replaced, this is
// the one file that changes.
//
// What it does:
//   * reads the book straight from the Dashboard's /engine-book/{id}/file route
//     with HTTP byte ranges (only the parts of the zip that are on screen),
//   * keeps the page's frames safe: scripts in a book never run, a book cannot
//     call out to the network, and links only leave through http(s)/mailto,
//   * turns pages by tap zones, keys and swipe (the paginator owns the swipe),
//   * reports the reading position as a standard EPUB CFI.
// ============================================================================

import { configure, ZipReader, BlobReader, BlobWriter, TextWriter } from '../lib/foliate-js/vendor/zip.js';
import { EPUB } from '../lib/foliate-js/epub.js';
import '../lib/foliate-js/view.js'; // registers <foliate-view>

configure({ useWebWorkers: false });

// ---------------------------------------------------------------------------
// Errors the page can explain to a person
// ---------------------------------------------------------------------------

class ReaderError extends Error {
    constructor(code, message, cause) {
        super(message, cause ? { cause } : undefined);
        this.code = code;
    }
}

const statusError = status => {
    switch (status) {
        case 401: return new ReaderError('unauthorized', 'Your session has ended. Sign in again to keep reading.');
        case 403:
        case 404: return new ReaderError('notfound', 'This book is not available.');
        case 429: return new ReaderError('busy', 'The library is busy. Try again in a moment.');
        default: return new ReaderError('unavailable', 'The library could not send this book (' + status + ').');
    }
};

// ---------------------------------------------------------------------------
// Reading a zip over HTTP ranges
// ---------------------------------------------------------------------------

const TAIL_BYTES = 256 * 1024;       // first request: the end of the file, where the zip directory lives
const MIN_BLOCK = 256 * 1024;        // smallest read-ahead
const MAX_BLOCK = 4 * 1024 * 1024;   // largest read-ahead while one file is read straight through
const CACHE_LIMIT = 24 * 1024 * 1024;

/**
 * A zip.js reader that fetches byte ranges on demand and keeps a small block cache. It extends the library's
 * BlobReader (the only reader class zip.js exports) so zip.js gets the stream interface it needs; the Blob it is
 * built on is only a size holder, nothing is ever read from it.
 */
class RangedFile extends BlobReader {
    #url;
    #signal;
    #chunks = [];      // { start, end, bytes, used }
    #pending = [];     // { start, end, promise }
    #lastEnd = -1;
    #run = 0;
    #canFetch;
    #ifRange;          // the validator (ETag or Last-Modified) of the file as first seen; sent with every later range
    #etag;
    stats;
    onFatal = null;    // called once when the book can no longer be read (replaced on disk, sign-in ended)

    constructor(url, size, firstChunk, canFetch, signal, stats, validators = {}) {
        super({ size });
        this.#url = url;
        this.size = size;
        this.#signal = signal;
        this.#canFetch = canFetch;
        this.stats = stats;
        // A weak ETag cannot be used with If-Range, so fall back to Last-Modified in that case.
        const strong = validators.etag && !/^W\//.test(validators.etag) ? validators.etag : null;
        this.#etag = strong;
        this.#ifRange = strong ?? validators.lastModified ?? null;
        this.#remember(firstChunk.start, firstChunk.bytes);
    }

    static async open(url, signal, stats) {
        let res;
        try {
            res = await fetchWithRetry(url, { Range: 'bytes=-' + TAIL_BYTES }, signal, stats);
        } catch (e) {
            if (e instanceof ReaderError || signal?.aborted) throw e;
            throw new ReaderError('offline', 'The library could not be reached.', e);
        }
        if (res.status === 206) {
            const m = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(res.headers.get('Content-Range') ?? '');
            if (!m) throw new ReaderError('unavailable', 'The library sent an unreadable range answer.');
            const bytes = new Uint8Array(await res.arrayBuffer());
            if (bytes.length !== Number(m[2]) - Number(m[1]) + 1) {
                throw new ReaderError('unavailable', 'The library sent an incomplete part of the book.');
            }
            stats.bytes += bytes.length;
            const validators = { etag: res.headers.get('ETag'), lastModified: res.headers.get('Last-Modified') };
            return new RangedFile(url, Number(m[3]), { start: Number(m[1]), bytes }, true, signal, stats, validators);
        }
        if (res.status === 200) {
            // The server ignored the range and sent the whole book. Keep it in memory and read from there.
            const bytes = new Uint8Array(await res.arrayBuffer());
            stats.bytes += bytes.length;
            return new RangedFile(url, bytes.length, { start: 0, bytes }, false, signal, stats);
        }
        if (res.status === 416) throw new ReaderError('notfound', 'This book file is empty.');
        throw statusError(res.status);
    }

    #remember(start, bytes) {
        this.#chunks.push({ start, end: start + bytes.length, bytes, used: ++RangedFile.#clock });
        let total = this.#chunks.reduce((n, c) => n + c.bytes.length, 0);
        if (total <= CACHE_LIMIT) return;
        // Drop the least recently used chunks, but keep the newest one.
        const byAge = this.#chunks.slice(0, -1).sort((a, b) => a.used - b.used);
        for (const old of byAge) {
            if (total <= CACHE_LIMIT) break;
            total -= old.bytes.length;
            this.#chunks.splice(this.#chunks.indexOf(old), 1);
        }
    }
    static #clock = 0;

    #find(start, end) {
        const hit = this.#chunks.find(c => c.start <= start && c.end >= end);
        if (hit) hit.used = ++RangedFile.#clock;
        return hit;
    }

    async readUint8Array(offset, length) {
        const end = Math.min(this.size, offset + length);
        if (end <= offset) return new Uint8Array(0);
        for (let attempt = 0; attempt < 3; attempt++) {
            const hit = this.#find(offset, end);
            if (hit) {
                this.#lastEnd = end;
                // zip.js reads results through .buffer, so hand back a copy that owns its own buffer.
                return hit.bytes.slice(offset - hit.start, end - hit.start);
            }
            if (!this.#canFetch) throw new ReaderError('unavailable', 'The book data is incomplete.');
            const waiting = this.#pending.find(p => p.start <= offset && p.end >= end);
            if (waiting) { await waiting.promise; continue; }
            await this.#fetchAround(offset, end);
        }
        throw new ReaderError('unavailable', 'The library could not send this part of the book.');
    }

    async #fetchRange(start, stop) {
        const headers = { Range: 'bytes=' + start + '-' + (stop - 1) };
        if (this.#ifRange) headers['If-Range'] = this.#ifRange;
        let res;
        try {
            res = await fetchWithRetry(this.#url, headers, this.#signal, this.stats);
        } catch (e) {
            if (e instanceof ReaderError || this.#signal?.aborted) throw e;
            throw new ReaderError('offline', 'The library could not be reached.', e);
        }
        // A full answer to a conditional range request means the file is no longer the one we started with.
        if (res.status === 200) throw this.#ifRange ? RangedFile.#changed() : statusError(502);
        if (res.status !== 206) throw statusError(res.status);
        const m = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(res.headers.get('Content-Range') ?? '');
        if (!m) throw new ReaderError('unavailable', 'The library sent an unreadable range answer.');
        if (Number(m[3]) !== this.size) throw RangedFile.#changed();
        const etag = res.headers.get('ETag');
        if (this.#etag && etag && etag !== this.#etag) throw RangedFile.#changed();
        if (Number(m[1]) !== start || Number(m[2]) > stop - 1) {
            throw new ReaderError('unavailable', 'The library sent a different part of the book than asked for.');
        }
        const bytes = new Uint8Array(await res.arrayBuffer());
        if (bytes.length !== Number(m[2]) - Number(m[1]) + 1) {
            throw new ReaderError('unavailable', 'The library sent an incomplete part of the book.');
        }
        this.stats.bytes += bytes.length;
        this.#remember(start, bytes);
    }

    static #changed() {
        return new ReaderError('changed', 'This book was replaced while you were reading it. Open it again to continue.');
    }

    #fetchAround(offset, end) {
        // Reading straight through a large file? Grow the read-ahead; a jump resets it.
        this.#run = offset === this.#lastEnd ? this.#run + 1 : 0;
        const block = Math.min(MAX_BLOCK, MIN_BLOCK * 2 ** this.#run);
        const start = offset;
        const stop = Math.min(this.size, Math.max(end, offset + block));
        const promise = (async () => {
            try {
                await this.#fetchRange(start, stop);
            } catch (e) {
                if (e instanceof ReaderError && (e.code === 'changed' || e.code === 'unauthorized')) this.onFatal?.(e);
                throw e;
            }
        })();
        const entry = { start, end: stop, promise };
        this.#pending.push(entry);
        const done = () => this.#pending.splice(this.#pending.indexOf(entry), 1);
        return promise.finally(done);
    }
}

async function fetchWithRetry(url, headers, signal, stats) {
    for (let attempt = 0; ; attempt++) {
        stats.requests++;
        const res = await fetch(url, { headers, signal, credentials: 'same-origin' });
        if (res.status === 429 && attempt < 1) {
            // One polite retry, honouring Retry-After (capped at 5 s).
            const wait = Math.min(5, Number(res.headers.get('Retry-After')) || 1);
            await new Promise(resolve => setTimeout(resolve, wait * 1000));
            continue;
        }
        return res;
    }
}

async function makeZipLoader(file) {
    const reader = new ZipReader(file);
    let entries;
    try {
        entries = await reader.getEntries();
    } catch (e) {
        if (e instanceof ReaderError) throw e;
        throw new ReaderError('unsupported', 'This file is not a book the new reader can open yet.', e);
    }
    const map = new Map(entries.map(entry => [entry.filename, entry]));
    const load = f => (name, ...args) => map.has(name) ? f(map.get(name), ...args) : null;
    return {
        entries,
        loadText: load(entry => entry.getData(new TextWriter())),
        loadBlob: load((entry, type) => entry.getData(new BlobWriter(type))),
        getSize: name => map.get(name)?.uncompressedSize ?? 0,
        close: () => reader.close(),
    };
}

// ---------------------------------------------------------------------------
// SHA-1 for obfuscated book fonts. Browsers only offer crypto.subtle on https/localhost,
// and the Dashboard is often opened over plain http on a home network.
// ---------------------------------------------------------------------------

export function sha1Fallback(data) {
    const rotl = (n, s) => (n << s) | (n >>> (32 - s));
    const h = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0];
    const bits = data.length * 8;
    const padded = new Uint8Array(((data.length + 9 + 63) >> 6) << 6);
    padded.set(data);
    padded[data.length] = 0x80;
    const view = new DataView(padded.buffer);
    view.setUint32(padded.length - 8, Math.floor(bits / 2 ** 32));
    view.setUint32(padded.length - 4, bits >>> 0);
    const w = new Uint32Array(80);
    for (let off = 0; off < padded.length; off += 64) {
        for (let i = 0; i < 16; i++) w[i] = view.getUint32(off + i * 4);
        for (let i = 16; i < 80; i++) w[i] = rotl(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        let [a, b, c, d, e] = h;
        for (let i = 0; i < 80; i++) {
            let f, k;
            if (i < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
            else if (i < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
            else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
            else { f = b ^ c ^ d; k = 0xCA62C1D6; }
            const t = (rotl(a, 5) + f + e + k + w[i]) >>> 0;
            e = d; d = c; c = rotl(b, 30) >>> 0; b = a; a = t;
        }
        h[0] = (h[0] + a) >>> 0; h[1] = (h[1] + b) >>> 0; h[2] = (h[2] + c) >>> 0;
        h[3] = (h[3] + d) >>> 0; h[4] = (h[4] + e) >>> 0;
    }
    const out = new Uint8Array(20);
    const outView = new DataView(out.buffer);
    h.forEach((word, i) => outView.setUint32(i * 4, word));
    return out;
}

const sha1 = async str => {
    const data = new TextEncoder().encode(str);
    const subtle = globalThis.crypto?.subtle;
    return subtle ? new Uint8Array(await subtle.digest('SHA-1', data)) : sha1Fallback(data);
};

// ---------------------------------------------------------------------------
// Safe book frames
// ---------------------------------------------------------------------------

// Applied to every book page before it is shown. The book can show its own pictures, fonts and
// styles (they are blob: URLs made from the zip) and nothing else: no scripts, no network, no frames.
export const BOOK_CSP = [
    "default-src 'none'",
    "script-src 'none'",
    "connect-src 'none'",
    "object-src 'none'",
    "frame-src 'none'",
    "base-uri 'none'",
    "form-action 'none'",
    'img-src blob: data:',
    'media-src blob: data:',
    'font-src blob: data:',
    "style-src blob: 'unsafe-inline'",
].join('; ');

// foliate-js shows every chapter in an iframe that is sandboxed with allow-same-origin AND allow-scripts (the
// library needs allow-scripts for events in WebKit). That means a script that runs inside a chapter would run with
// the Dashboard's own rights. So nothing a book ships may ever become a live document unless it has been made safe
// first: XHTML/HTML gets the CSP above plus a clean-up, SVG (which cannot carry a CSP) gets a stricter clean-up,
// and every other kind of document is refused.
const XHTML_TYPE = 'application/xhtml+xml';
const HTML_TYPE = 'text/html';
const SVG_TYPE = 'image/svg+xml';
const SVG_NS = 'http://www.w3.org/2000/svg';
const isXhtmlOrHtml = type => type === XHTML_TYPE || type === HTML_TYPE;
// Types a browser would render as a document. Anything of these kinds that is not hardened below is refused.
const isDocumentType = type => /(^|\/|\+)(x?html|xml)$/i.test(type ?? '') || /^text\/(html|xml)/i.test(type ?? '');

const COMPACT_CHARS = new RegExp('[\\u0000-\\u0020\\u007f-\\u009f\\u2028\\u2029]+', 'g');
const compact = value => String(value ?? '').replace(COMPACT_CHARS, '').toLowerCase();
const isLocalUrl = value => !value || !/^[a-z][a-z0-9+.-]*:/i.test(value.trim()) || /^(blob|data):/i.test(value.trim());
const isScriptUrl = value => /^(javascript|vbscript|livescript):/.test(compact(value)) || /^data:(text|application)\//.test(compact(value));
const LINK_SCHEMES = /^(https?:|mailto:)/i;
const URL_ATTRS = ['href', 'src', 'data', 'poster', 'action', 'formaction', 'xlink:href', 'from', 'to', 'values', 'srcset'];

/** Blocks anything in CSS that would load from outside the book. */
function cleanCss(css) {
    return String(css ?? '')
        .replace(/@import[^;{]*(;|(?=\{)|$)/gi, '')
        .replace(/url\(\s*(['"]?)\s*(?![#]|blob:|data:image\/)[^)'"]*\1\s*\)/gi, 'none')
        .replace(/expression\s*\(/gi, '(');
}

/** Shared: scripts, handlers and script URLs go in every kind of document. */
function stripActiveContent(doc) {
    for (const el of Array.from(doc.querySelectorAll('script'))) el.remove();
    for (const el of doc.querySelectorAll('*')) {
        for (const attr of Array.from(el.attributes)) {
            if (/^on/i.test(attr.name)) el.removeAttribute(attr.name);
            else if (URL_ATTRS.includes(attr.name.toLowerCase()) && isScriptUrl(attr.value)) el.removeAttribute(attr.name);
        }
    }
}

const BLOCKED_PAGE = `<html xmlns="http://www.w3.org/1999/xhtml"><head><meta http-equiv="Content-Security-Policy" content="${BOOK_CSP}"/><title></title></head><body><p>This page could not be shown safely.</p></body></html>`;

/**
 * Pure: harden one book page and return { text, type }. Fails closed: a page that cannot be parsed is
 * replaced by a plain notice rather than shown as it is.
 */
export function hardenPageDetailed(text, mediaType) {
    let type = mediaType === HTML_TYPE ? HTML_TYPE : XHTML_TYPE;
    let doc = new DOMParser().parseFromString(text, type);
    if (type === XHTML_TYPE && (doc.querySelector('parsererror') || !doc.documentElement)) {
        // Not valid XHTML: read it as forgiving HTML (the same fallback the library uses), then write it back out as XML.
        type = HTML_TYPE;
        doc = new DOMParser().parseFromString(text, type);
    }
    if (!doc.documentElement || doc.querySelector('parsererror')) return { text: BLOCKED_PAGE, type: XHTML_TYPE };

    stripActiveContent(doc);
    // No nested documents, plug-ins or redirects: a frame would be a document this clean-up never sees.
    for (const el of Array.from(doc.querySelectorAll('iframe, frame, frameset, object, embed, applet, base'))) el.remove();
    for (const el of Array.from(doc.querySelectorAll('meta[http-equiv]'))) {
        if (/^(refresh|set-cookie|content-security-policy|x-frame-options)$/i.test(el.getAttribute('http-equiv'))) el.remove();
    }
    // A book must not reach out to the web for pictures, styles or media (privacy and tracking pixels).
    for (const el of doc.querySelectorAll('img[src], source[src], video[src], audio[src], track[src], input[src]')) {
        if (!isLocalUrl(el.getAttribute('src'))) el.removeAttribute('src');
    }
    for (const el of doc.querySelectorAll('[srcset]')) {
        const external = el.getAttribute('srcset').split(',').some(part => !isLocalUrl(part.trim().split(/\s+/)[0]));
        if (external) el.removeAttribute('srcset');
    }
    for (const el of Array.from(doc.querySelectorAll('link[href]'))) {
        if (!isLocalUrl(el.getAttribute('href'))) el.remove();
    }
    for (const el of doc.querySelectorAll('style')) el.textContent = cleanCss(el.textContent);
    for (const el of doc.querySelectorAll('[style]')) el.setAttribute('style', cleanCss(el.getAttribute('style')));

    let head = doc.querySelector('head');
    if (!head) {
        head = doc.createElementNS(doc.documentElement.namespaceURI, 'head');
        doc.documentElement.prepend(head);
    }
    const meta = doc.createElementNS(doc.documentElement.namespaceURI, 'meta');
    meta.setAttribute('http-equiv', 'Content-Security-Policy');
    meta.setAttribute('content', BOOK_CSP);
    head.prepend(meta);
    return { text: new XMLSerializer().serializeToString(doc), type };
}

/** Pure: harden one book page (string of XHTML/HTML) and return the new string. */
export function hardenPage(text, mediaType) {
    return hardenPageDetailed(text, mediaType).text;
}

const SVG_REMOVE = new Set(['script', 'foreignobject', 'iframe', 'frame', 'embed', 'object', 'applet', 'animate', 'set', 'handler', 'listener', 'meta', 'base', 'audio', 'video', 'canvas']);

/**
 * Pure: harden one SVG document, which a browser would run scripts in if it were opened as a page, and which cannot
 * carry a CSP. Returns the new string, or null when the file cannot be made safe (so it is refused, never shown as is).
 */
export function hardenSvg(text) {
    const doc = new DOMParser().parseFromString(text, SVG_TYPE);
    const root = doc.documentElement;
    if (!root || doc.querySelector('parsererror') || root.localName !== 'svg' || root.namespaceURI !== SVG_NS) return null;

    // Only elements in the SVG namespace stay (an XHTML <script> or <iframe> inside an SVG is still a script/frame).
    for (const el of Array.from(root.querySelectorAll('*'))) {
        if (el.namespaceURI !== SVG_NS || SVG_REMOVE.has(el.localName.toLowerCase())) el.remove();
    }
    stripActiveContent(doc);
    for (const el of root.querySelectorAll('*')) {
        for (const name of ['href', 'xlink:href']) {
            const value = el.getAttribute(name);
            if (value === null) continue;
            const allowed = el.localName === 'a' ? (isLocalUrl(value) || LINK_SCHEMES.test(value.trim())) : isLocalUrl(value);
            if (!allowed) el.removeAttribute(name);
        }
    }
    for (const el of doc.querySelectorAll('style')) el.textContent = cleanCss(el.textContent);
    for (const el of doc.querySelectorAll('[style]')) el.setAttribute('style', cleanCss(el.getAttribute('style')));
    // xml-stylesheet instructions can pull in a stylesheet from anywhere; keep only the ones inside the book.
    for (const node of Array.from(doc.childNodes)) {
        if (node.nodeType === Node.PROCESSING_INSTRUCTION_NODE) {
            const href = /href\s*=\s*["']([^"']*)["']/i.exec(node.data)?.[1];
            if (node.target !== 'xml-stylesheet' || (href !== undefined && !isLocalUrl(href))) node.remove();
        }
    }
    return new XMLSerializer().serializeToString(doc);
}

/**
 * Pure: an SVG that is a page of the book (a spine item). The paginator measures pages through their <body>, which an
 * SVG document does not have, so the cleaned SVG is shown inside a plain XHTML page; that page also carries the CSP.
 * Returns { text, type } and fails closed like hardenPage.
 */
export function hardenSvgAsPage(text) {
    const svg = hardenSvg(text);
    if (svg === null) return { text: BLOCKED_PAGE, type: XHTML_TYPE };
    const root = new DOMParser().parseFromString(svg, SVG_TYPE).documentElement;
    const box = (root.getAttribute('viewBox') ?? '').trim().split(/[\s,]+/).map(Number);
    const number = value => { const n = parseFloat(value); return Number.isFinite(n) && n > 0 ? n : null; };
    const width = number(root.getAttribute('width')) ?? (box.length === 4 ? number(box[2]) : null);
    const height = number(root.getAttribute('height')) ?? (box.length === 4 ? number(box[3]) : null);
    const viewport = width && height ? `<meta name="viewport" content="width=${width}, height=${height}"/>` : '';
    const page = `<html xmlns="http://www.w3.org/1999/xhtml"><head>${viewport}<title></title></head><body>${new XMLSerializer().serializeToString(root)}</body></html>`;
    return hardenPageDetailed(page, XHTML_TYPE);
}

/**
 * What happens to one resource the library has read from the book, just before it becomes a blob: address.
 * `asPage` is true for the book's own pages (spine items), which are the only documents a frame ever shows.
 */
export function hardenResource(data, mediaType, asPage = false) {
    // Refused: a plain notice where a page was expected (an empty document would break the page layout), nothing otherwise.
    const refused = () => (asPage ? { data: BLOCKED_PAGE, type: XHTML_TYPE } : { data: '', type: mediaType });
    if (typeof data !== 'string') {
        // Binary data is only safe if a browser would never open it as a document.
        return isDocumentType(mediaType) ? refused() : { data, type: mediaType };
    }
    if (isXhtmlOrHtml(mediaType)) {
        const { text, type } = hardenPageDetailed(data, mediaType);
        return { data: text, type };
    }
    if (mediaType === SVG_TYPE && asPage) {
        const { text, type } = hardenSvgAsPage(data);
        return { data: text, type };
    }
    if (mediaType === SVG_TYPE) return { data: hardenSvg(data) ?? '', type: mediaType };   // a picture: unsafe ones become empty
    if (isDocumentType(mediaType)) return refused();   // some other XML/HTML flavour
    return { data, type: mediaType };
}

function secureBook(book) {
    const target = book.transformTarget;
    if (!target) throw new ReaderError('unsupported', 'This book cannot be shown safely.');
    // The book's own pages (the only things a frame ever shows) are hardened as pages; pictures, styles and fonts as resources.
    const pages = new Set((book.sections ?? []).map(section => section.id));
    // Never load script files at all.
    target.addEventListener('load', ({ detail }) => {
        if (detail.isScript) detail.allow = false;
    });
    target.addEventListener('data', ({ detail }) => {
        const mediaType = detail.type;
        const outcome = Promise.resolve(detail.data).then(
            data => hardenResource(data, mediaType, pages.has(detail.name)),
            error => {
                console.error(new Error('Failed to load ' + detail.name, { cause: error }));
                return pages.has(detail.name) ? { data: BLOCKED_PAGE, type: XHTML_TYPE } : { data: '', type: mediaType };
            });
        detail.data = outcome.then(r => r.data);
        detail.type = outcome.then(r => r.type);
    });
}

// ---------------------------------------------------------------------------
// Look of the page
// ---------------------------------------------------------------------------

const THEMES = {
    light: { bg: '#faf8f2', fg: '#161b2a', link: '#4b2cc4', dark: false, force: false },
    dark: { bg: '#101319', fg: '#e8e2d9', link: '#8fb4ff', dark: true, force: true },
    sepia: { bg: '#eee0bd', fg: '#3a2c1b', link: '#7a3b12', dark: false, force: true },
};

export function resolveTheme(name) {
    if (name === 'system') {
        name = globalThis.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }
    return THEMES[name] ?? THEMES.light;
}

export function bookCss({ theme, fontSize, lineHeight }) {
    const t = resolveTheme(theme);
    const size = Math.min(40, Math.max(10, Number(fontSize) || 18));
    const spacing = Math.min(3, Math.max(1, Number(lineHeight) || 1.6));
    const force = t.force ? ' !important' : '';
    return `
        @namespace epub "http://www.idpf.org/2007/ops";
        html { color-scheme: ${t.dark ? 'dark' : 'light'}; background: ${t.bg}${force}; color: ${t.fg}${force}; font-size: ${size}px !important; }
        ${t.force ? `body, body *:not(img):not(svg):not(svg *) { color: ${t.fg} !important; background-color: transparent !important; }
        a:link, a:visited, a:link *, a:visited * { color: ${t.link} !important; }` : ''}
        p, li, blockquote, dd { line-height: ${spacing}; widows: 2; orphans: 2; }
        [align="left"] { text-align: left; } [align="right"] { text-align: right; }
        [align="center"] { text-align: center; } [align="justify"] { text-align: justify; }
        pre { white-space: pre-wrap !important; }
        aside[epub|type~="endnote"], aside[epub|type~="footnote"], aside[epub|type~="note"], aside[epub|type~="rearnote"] { display: none; }
    `;
}

// ---------------------------------------------------------------------------
// The reader
// ---------------------------------------------------------------------------

const NOTIFY_DELAY_MS = 250;
const INTERACTIVE = 'a[href], button, input, select, textarea, summary, label, [role="button"], [contenteditable="true"]';

let session = null;

function emptyStats() {
    return { requests: 0, bytes: 0, openMs: 0, entries: 0, fileSize: 0 };
}

/**
 * Opens a book into `host`. Never throws across interop: returns
 * { ok: true, title, author, language, dir, layout, fraction, stats } or { ok: false, code, message }.
 * options: { lastLocation, theme, fontSize, lineHeight }
 */
export async function open(host, url, options, dotNet) {
    dispose();
    const started = performance.now();
    const controller = new AbortController();
    const stats = emptyStats();
    const state = { host, controller, stats, dotNet, view: null, loader: null, listeners: [], timer: null, latest: null, disposed: false };
    session = state;
    try {
        const file = await RangedFile.open(url, controller.signal, stats);
        stats.fileSize = file.size;
        file.onFatal = err => notify(state, 'OnFailed', err.code, err.message);
        const loader = await makeZipLoader(file);
        state.loader = loader;
        stats.entries = loader.entries.length;
        if (!loader.entries.some(e => e.filename === 'META-INF/container.xml')) {
            throw new ReaderError('unsupported', 'The new reader opens EPUB books for now.');
        }
        let book;
        try {
            book = await new EPUB({ loadText: loader.loadText, loadBlob: loader.loadBlob, getSize: loader.getSize, sha1 }).init();
        } catch (e) {
            if (e instanceof ReaderError) throw e;
            console.error('The book package could not be read.', e);
            throw new ReaderError('corrupt', 'This book could not be read. The file may be damaged.', e);
        }
        secureBook(book);
        if (state.disposed) return { ok: false, code: 'cancelled', message: 'Closed.' };

        const view = document.createElement('foliate-view');
        state.view = view;
        host.replaceChildren(view);
        view.addEventListener('load', e => onSectionLoad(state, e.detail.doc));
        view.addEventListener('relocate', e => onRelocate(state, e.detail));
        view.addEventListener('external-link', e => onExternalLink(e));
        await view.open(book);
        if (!globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches) view.renderer.setAttribute?.('animated', '');
        applyAppearance(state, options ?? {});
        fitMargins(state);
        bindInput(state);

        let restored = false;
        if (options?.lastLocation) {
            try {
                await view.init({ lastLocation: options.lastLocation, showTextStart: false });
                restored = true;
            } catch (e) {
                console.warn('Saved reading position could not be used; opening at the start.', e);
            }
        }
        if (!restored) await view.init({ lastLocation: null, showTextStart: false });

        stats.openMs = Math.round(performance.now() - started);
        return {
            ok: true,
            title: pickText(book.metadata?.title),
            author: pickPeople(book.metadata?.author),
            language: pickText(book.metadata?.language),
            dir: book.dir === 'rtl' ? 'rtl' : 'ltr',
            layout: view.isFixedLayout ? 'fixed' : 'reflowable',
            fraction: view.lastLocation?.fraction ?? 0,
            restored,
            stats: { ...stats },
        };
    } catch (e) {
        if (state.disposed || controller.signal.aborted) return { ok: false, code: 'cancelled', message: 'Closed.' };
        const err = e instanceof ReaderError ? e : new ReaderError('corrupt', 'This book could not be opened.', e);
        if (!(e instanceof ReaderError)) console.error(e);
        teardown(state);
        return { ok: false, code: err.code, message: err.message };
    }
}

function pickText(value) {
    if (!value) return '';
    if (typeof value === 'string') return value;
    const first = Object.values(value)[0];
    return typeof first === 'string' ? first : '';
}

function pickPeople(value) {
    if (!value) return '';
    const list = Array.isArray(value) ? value : [value];
    return list.map(p => typeof p === 'string' ? p : pickText(p?.name)).filter(Boolean).join(', ');
}

function applyAppearance(state, options) {
    const css = bookCss(options);
    state.view?.renderer?.setStyles?.(css);
    const name = options.theme === 'system' ? (resolveTheme('system') === THEMES.dark ? 'dark' : 'light') : (THEMES[options.theme] ? options.theme : 'light');
    state.host.dataset.theme = name;
}

/**
 * The reading appearance this device saved (the same per-device key the current reader uses), as
 * { theme, fontSize, lineHeight }. Anything missing or unreadable is left out so the page's defaults apply.
 */
export function loadSettings() {
    try {
        const stored = JSON.parse(globalThis.localStorage?.getItem('tuvima-reader-settings') ?? 'null');
        if (!stored || typeof stored !== 'object') return {};
        const pick = name => stored[name] ?? stored[name[0].toLowerCase() + name.slice(1)];
        const out = {};
        const theme = pick('Theme');
        if (typeof theme === 'string') out.theme = theme;
        const size = Number(pick('FontSize'));
        if (Number.isFinite(size) && size > 0) out.fontSize = size;
        const spacing = Number(pick('LineHeight'));
        if (Number.isFinite(spacing) && spacing > 0) out.lineHeight = spacing;
        return out;
    } catch (e) {
        console.debug('Reader settings could not be read; using the defaults.', e);
        return {};
    }
}

export function setAppearance(options) {
    if (session && !session.disposed) applyAppearance(session, options ?? {});
}

// The bars float over the book, so the paginator's header/footer margin must be at least as tall as the bars
// (which grow with a phone's safe-area insets). Measured, not guessed, and kept up to date on resize.
function fitMargins(state) {
    const fit = () => {
        if (state.disposed || !state.view?.renderer) return;
        const root = state.host.parentElement;
        const top = root?.querySelector('.br-bar--top')?.offsetHeight ?? 0;
        const bottom = root?.querySelector('.br-bar--bottom')?.offsetHeight ?? 0;
        const margin = Math.max(48, Math.ceil(Math.max(top, bottom)));
        const value = margin + 'px';
        if (state.view.renderer.getAttribute('margin') !== value) state.view.renderer.setAttribute('margin', value);
    };
    fit();
    const observer = new ResizeObserver(fit);
    observer.observe(state.host);
    state.listeners.push(() => observer.disconnect());
}

function listen(state, target, type, handler, options) {
    target.addEventListener(type, handler, options);
    state.listeners.push(() => target.removeEventListener(type, handler, options));
}

function bindInput(state) {
    listen(state, document, 'keydown', e => onKey(state, e));
    // Clicks in the host's own margins (outside the book frame) behave like taps on the page.
    listen(state, state.host, 'click', e => {
        if (e.target !== state.host && e.target.tagName !== 'FOLIATE-VIEW') return;
        onTap(state, e.clientX);
    });
}

function onSectionLoad(state, doc) {
    if (state.disposed) return;
    const win = doc.defaultView;
    const frame = win?.frameElement;
    doc.addEventListener('keydown', e => onKey(state, e));
    doc.addEventListener('click', e => {
        if (e.defaultPrevented) return;                       // a link the library already handled
        if (e.target.closest?.(INTERACTIVE)) return;
        const selection = win.getSelection?.();
        if (selection && !selection.isCollapsed) return;      // selecting text is not a page turn
        let x = e.clientX;
        if (frame) {
            const rect = frame.getBoundingClientRect();
            const scale = rect.width / (frame.offsetWidth || rect.width || 1);
            x = rect.left + e.clientX * scale;
        }
        onTap(state, x);
    });
}

function onTap(state, clientX) {
    const rect = state.host.getBoundingClientRect();
    const ratio = rect.width > 0 ? (clientX - rect.left) / rect.width : 0.5;
    if (ratio < 0.3) state.view.goLeft();
    else if (ratio > 0.7) state.view.goRight();
    else notify(state, 'OnToggleChrome');
}

function onKey(state, e) {
    if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
    const interactive = e.target?.closest?.('button, a[href], input, select, textarea, [contenteditable="true"], [role="button"]');
    const view = state.view;
    switch (e.key) {
        case 'ArrowLeft': view.goLeft(); break;
        case 'ArrowRight': view.goRight(); break;
        case 'PageUp': view.prev(); break;
        case 'PageDown': view.next(); break;
        case ' ':
            if (interactive) return;                           // Space belongs to a focused button
            if (e.shiftKey) view.prev(); else view.next();
            break;
        case 'Escape': notify(state, 'OnEscape'); break;
        default: return;
    }
    e.preventDefault();
}

function onExternalLink(e) {
    e.preventDefault();                                        // the library would open anything; we decide
    const href = e.detail?.a?.href ?? e.detail?.href_;
    if (typeof href === 'string' && /^(https?:|mailto:)/i.test(href)) {
        globalThis.open(href, '_blank', 'noopener,noreferrer');
    }
}

function onRelocate(state, detail) {
    if (state.disposed) return;
    state.latest = {
        cfi: detail.cfi ?? '',
        fraction: Number.isFinite(detail.fraction) ? detail.fraction : 0,
        label: detail.tocItem?.label ?? '',
    };
    clearTimeout(state.timer);
    state.timer = setTimeout(() => notify(state, 'OnRelocated', state.latest.cfi, state.latest.fraction, state.latest.label), NOTIFY_DELAY_MS);
}

function notify(state, method, ...args) {
    if (state.disposed || !state.dotNet) return;
    state.dotNet.invokeMethodAsync(method, ...args).catch(() => {
        // The .NET side is gone (page closed or connection lost); nothing is left to tell.
    });
}

// ---------------------------------------------------------------------------
// Controls and teardown
// ---------------------------------------------------------------------------

export function next() { return session?.view?.next(); }
export function prev() { return session?.view?.prev(); }
export function goLeft() { return session?.view?.goLeft(); }
export function goRight() { return session?.view?.goRight(); }
export function goTo(target) { return session?.view?.goTo(target); }

/** Latest position, for saving right before the page closes. */
export function getLocation() { return session?.latest ?? null; }
export function getStats() { return session ? { ...session.stats } : null; }

function teardown(state) {
    if (state.disposed) return;
    state.disposed = true;
    clearTimeout(state.timer);
    state.controller.abort();
    for (const off of state.listeners.splice(0)) off();
    try {
        state.view?.close();
        state.view?.remove();
    } catch (e) {
        console.warn('The book view did not close cleanly.', e);
    }
    state.loader?.close?.()?.catch?.(() => {
        // Closing the zip reader only frees memory; nothing can go wrong that matters.
    });
    state.dotNet = null;
}

export function dispose() {
    if (!session) return;
    const state = session;
    session = null;
    teardown(state);
}
