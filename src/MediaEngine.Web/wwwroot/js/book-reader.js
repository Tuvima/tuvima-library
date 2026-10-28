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
import { Overlayer } from '../lib/foliate-js/overlayer.js';
import { inferPictureType, markBrokenPictures } from './book-reader-pictures.js';
import { mergeSettings, readDeviceSettings, writeDeviceSettings, clearDeviceSettings } from './book-reader-settings.js';

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
            const [first, last, total] = [Number(m[1]), Number(m[2]), Number(m[3])];
            // The tail asked for is the last TAIL_BYTES of the file (all of it when the file is smaller).
            if (last !== total - 1 || first !== Math.max(0, total - TAIL_BYTES)) {
                throw new ReaderError('unavailable', 'The library sent a different part of the book than asked for.');
            }
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

// Book frames are sandboxed without scripts (a documented one-line patch to the pinned foliate-js, see PINNED.md), so
// the browser itself refuses to run book code. This is the second layer, in case that is ever undone or a frame is
// missed: nothing a book ships may become a live document unless it has been made safe first. XHTML/HTML gets the
// CSP above plus a clean-up, SVG (which cannot carry a CSP) gets a stricter clean-up, and every other kind of
// document is refused.
const XHTML_TYPE = 'application/xhtml+xml';
const HTML_TYPE = 'text/html';
const SVG_TYPE = 'image/svg+xml';
const SVG_NS = 'http://www.w3.org/2000/svg';
const isXhtmlOrHtml = type => type === XHTML_TYPE || type === HTML_TYPE;

/** "Application/XHTML+xml ; charset=utf-8 " -> "application/xhtml+xml". Everything below compares only this form. */
export const normalizeType = type => String(type ?? '').split(';')[0].trim().toLowerCase();

// The only kinds of file a book may hand the browser as a picture, font, sound, video or style sheet. Everything else
// that is not a hardened page becomes an opaque download, so it can never be opened as a document.
const SAFE_TYPES = /^(image\/(?!svg)[a-z0-9.+-]+|font\/[a-z0-9.+-]+|audio\/[a-z0-9.+-]+|video\/[a-z0-9.+-]+|text\/css)$/;
const SAFE_APPLICATION_TYPES = new Set([
    'application/font-woff', 'application/font-woff2', 'application/font-sfnt', 'application/x-font-ttf', 'application/x-font-otf',
    'application/x-font-opentype', 'application/vnd.ms-opentype', 'application/vnd.ms-fontobject', 'application/ogg',
]);
const isSafeResourceType = type => SAFE_TYPES.test(type) || SAFE_APPLICATION_TYPES.has(type);

const COMPACT_CHARS = new RegExp('[\\u0000-\\u0020\\u007f-\\u009f\\u2028\\u2029]+', 'g');
const compact = value => String(value ?? '').replace(COMPACT_CHARS, '').toLowerCase();
// What the browser's URL parser sees: leading and trailing control characters and spaces go, and tabs and newlines inside go
// ("ht<TAB>tps://x" is "https://x" to a browser), so every address test below reads this form and not the raw attribute.
const urlText = value => String(value ?? '').replace(/^[\u0000-\u0020]+|[\u0000-\u0020]+$/g, '').replace(/[\t\n\r]/g, '');
const hasScheme = value => /^[a-z][a-z0-9+.-]*:/i.test(urlText(value));
const isLocalUrl = value => !hasScheme(value) || /^(blob|data):/i.test(urlText(value));
// The only addresses an element other than a link may keep in `href`: a place in this page or a picture the book carries.
const isLocalTarget = value => /^(#|blob:|data:image\/)/i.test(urlText(value));
const isScriptUrl = value => /^(javascript|vbscript|livescript):/.test(compact(value)) || /^data:(text|application)\//.test(compact(value));
const LINK_SCHEMES = /^(https?:|mailto:)/i;
const XLINK_NS = 'http://www.w3.org/1999/xlink';
const isAnchor = el => el.localName.toLowerCase() === 'a';
// Compared by LOCAL name, whatever the namespace prefix is: q:href in the XLink namespace is a link like href is.
const URL_ATTRS = new Set(['href', 'src', 'data', 'poster', 'action', 'formaction', 'from', 'to', 'by', 'values', 'srcset']);
const ANIMATION_ELEMENTS = new Set(['animate', 'set', 'animatemotion', 'animatetransform', 'animatecolor', 'handler', 'listener']);
const XHTML_NS = 'http://www.w3.org/1999/xhtml';

/** Blocks anything in CSS that would load from outside the book. */
function cleanCss(css) {
    const raw = String(css ?? '');
    // CSS escapes (u\72l(...)) can spell url( or @import without the letters; when decoding reveals one, clean the decoded text.
    const decoded = raw.replace(/\\([0-9a-f]{1,6})\s?|\\([^\n])/gi, (_, hex, ch) => (hex ? String.fromCodePoint(Math.min(parseInt(hex, 16) || 0xfffd, 0x10ffff)) : ch));
    const text = decoded !== raw && /url\(|@import|expression\(/i.test(decoded) ? decoded : raw;
    return text
        .replace(/@import[^;{]*(;|(?=\{)|$)/gi, '')
        .replace(/url\(\s*(['"]?)\s*(?![#]|blob:|data:image\/)[^)'"]*\1\s*\)/gi, 'none')
        .replace(/expression\s*\(/gi, '(');
}

/** Processing instructions (xml-stylesheet can run XSLT, which can rewrite the whole page) and the DOCTYPE never survive. */
function stripInstructions(doc) {
    const walker = doc.createTreeWalker(doc, NodeFilter.SHOW_PROCESSING_INSTRUCTION);
    const found = [];
    while (walker.nextNode()) found.push(walker.currentNode);
    for (const node of found) node.remove();
    doc.doctype?.remove();
}

/** Shared: scripts, handlers, forms and script URLs go in every kind of document. */
function stripActiveContent(doc) {
    stripInstructions(doc);
    stripForms(doc);
    for (const el of Array.from(doc.querySelectorAll('script'))) el.remove();
    // Animation elements can set an href or src to a script address a moment after load, so they go everywhere (inline SVG too).
    for (const el of Array.from(doc.querySelectorAll('*'))) {
        if (ANIMATION_ELEMENTS.has(el.localName.toLowerCase())) el.remove();
    }
    for (const el of doc.querySelectorAll('*')) {
        for (const attr of Array.from(el.attributes)) {
            const local = attr.localName.toLowerCase();
            if (/^on/.test(local) || /^on/i.test(attr.name)) el.removeAttributeNode(attr);
            else if (local === 'action' || local === 'formaction') el.removeAttributeNode(attr);   // where a submission would go: a book never submits anything
            else if (URL_ATTRS.has(local) && (isScriptUrl(attr.value) || /[;,]\s*(javascript|vbscript|livescript):/i.test(attr.value))) el.removeAttributeNode(attr);
        }
    }
}

/**
 * A book has nowhere to send a form to. The page policy (`form-action 'none'`) and the frame's sandbox (no `allow-forms`) already
 * refuse a submission, and this is the layer behind them: every `<form>` is unwrapped (its contents stay, so no text is lost) and
 * the attributes that name a destination (`action`, `formaction`) are removed in `stripActiveContent`. A button, a text box or an
 * image button (`<input type="image">`) with no form around it submits nothing, and `guardLinks` cancels a submit regardless.
 */
function stripForms(doc) {
    // Document order, so a form inside another form (which XML allows) is unwrapped as well once its parent has gone.
    for (const el of Array.from(doc.querySelectorAll('*'))) {
        if (el.localName.toLowerCase() === 'form') el.replaceWith(...Array.from(el.childNodes));
    }
}

/**
 * A link can be written with any prefix (`q:href` in the XLink namespace). The reader only intercepts and opens plain `href`
 * links safely, so every such attribute is turned into a plain `href` when it is a book-local or web link on an `<a>`, and
 * removed otherwise. Nothing is left that a browser would follow but the reader does not see.
 */
function normalizeLinkAttributes(root) {
    for (const el of Array.from(root.querySelectorAll('*'))) {
        for (const attr of Array.from(el.attributes)) {
            if (attr.localName.toLowerCase() !== 'href' || (attr.namespaceURI === null && attr.name === 'href')) continue;
            const value = attr.value;
            el.removeAttributeNode(attr);
            const allowed = isAnchor(el) ? (isLocalUrl(value) || LINK_SCHEMES.test(urlText(value))) : isLocalUrl(value);
            if (allowed && !isScriptUrl(value) && !el.hasAttribute('href')) el.setAttribute('href', value);
        }
    }
}

/**
 * Only an `<a>` is a link the reader knows how to follow (and only to a place in the book or to http, https or mailto).
 * Every other element loses its `href` unless it points inside the page or at a book picture: MathML elements are live links
 * in WebKit, and an `<area>` in an image map is a link in every browser.
 */
function restrictHrefs(root) {
    for (const el of Array.from(root.querySelectorAll('[href]'))) {
        const value = el.getAttribute('href');
        const allowed = isAnchor(el) ? (isLocalUrl(value) || LINK_SCHEMES.test(urlText(value))) : isLocalTarget(value);
        if (!allowed) el.removeAttribute('href');
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
    // A page whose root is not the XHTML html element (another namespace, another name) cannot carry a policy WebKit would apply.
    if (doc.documentElement.namespaceURI !== XHTML_NS || doc.documentElement.localName !== 'html') return { text: BLOCKED_PAGE, type: XHTML_TYPE };

    stripActiveContent(doc);
    normalizeLinkAttributes(doc);
    // No nested documents, plug-ins or redirects: a frame would be a document this clean-up never sees.
    // (noscript, noembed, noframes and xmp are raw text in HTML, which can hide markup from this clean-up.)
    // Image maps are links the tap handler would not recognise, and a template with a shadowrootmode becomes a live shadow root
    // when the frame reads the page again, holding markup this clean-up cannot reach (nothing in a book shows a template).
    for (const el of Array.from(doc.querySelectorAll('iframe, frame, frameset, object, embed, applet, base, noscript, noembed, noframes, xmp, plaintext, map, area, template'))) el.remove();
    restrictHrefs(doc);
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

    // The policy only counts in a <head> that is a direct child of the XHTML root: a head inside an SVG, nested in the body or
    // in another namespace is not the document's head. Use the real one or make one, and refuse the page if that is not so.
    const html = doc.documentElement;
    let head = Array.from(html.children).find(child => child.namespaceURI === XHTML_NS && child.localName === 'head') ?? null;
    if (!head) {
        head = doc.createElementNS(XHTML_NS, 'head');
        html.prepend(head);
    }
    const meta = doc.createElementNS(XHTML_NS, 'meta');
    meta.setAttribute('http-equiv', 'Content-Security-Policy');
    meta.setAttribute('content', BOOK_CSP);
    head.prepend(meta);
    if (meta.parentNode !== head || head.parentNode !== html || html.firstElementChild !== head || head.firstElementChild !== meta) {
        return { text: BLOCKED_PAGE, type: XHTML_TYPE };
    }
    return { text: new XMLSerializer().serializeToString(doc), type };
}

/** Pure: harden one book page (string of XHTML/HTML) and return the new string. */
export function hardenPage(text, mediaType) {
    return hardenPageDetailed(text, mediaType).text;
}

const SVG_REMOVE = new Set(['script', 'foreignobject', 'iframe', 'frame', 'embed', 'object', 'applet', 'animate', 'set', 'animatemotion', 'animatetransform', 'animatecolor', 'handler', 'listener', 'meta', 'base', 'audio', 'video', 'canvas']);

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
    normalizeLinkAttributes(doc);
    restrictHrefs(root);
    for (const el of doc.querySelectorAll('style')) el.textContent = cleanCss(el.textContent);
    for (const el of doc.querySelectorAll('[style]')) el.setAttribute('style', cleanCss(el.getAttribute('style')));
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
 * `asPage` is true for the book's own pages (spine items), which are the only documents a frame ever shows:
 * those are XHTML, HTML or SVG and always pass through the hardening, whatever their data looks like (text or a Blob)
 * and however their type was spelled; anything else is replaced by a plain notice.
 * Every other resource must be a picture, font, sound, video or style sheet; anything else is an opaque download.
 * A resource whose declared type is missing or not one of those (books ship `media-type=""` and "application/octet-stream")
 * gets its picture type from its first bytes or its file name (`name`), so the picture still shows.
 */
export async function hardenResource(data, mediaType, asPage = false, name = '') {
    let type = normalizeType(mediaType);
    if (!asPage && !isXhtmlOrHtml(type) && type !== SVG_TYPE && !isSafeResourceType(type)) {
        type = (await inferPictureType(name, data)) || type;
    }
    const isPageType = isXhtmlOrHtml(type) || type === SVG_TYPE;
    if (!isPageType) {
        if (asPage || !type) return asPage ? { data: BLOCKED_PAGE, type: XHTML_TYPE } : { data: '', type: 'application/octet-stream' };
        return { data, type: isSafeResourceType(type) ? type : 'application/octet-stream' };
    }

    // From here the data is a document: read it as text whatever form it arrived in.
    let text;
    if (typeof data === 'string') text = data;
    else if (data instanceof Blob) text = await data.text();
    else if (data instanceof ArrayBuffer || ArrayBuffer.isView(data)) text = new TextDecoder().decode(data);
    else return asPage ? { data: BLOCKED_PAGE, type: XHTML_TYPE } : { data: '', type: 'application/octet-stream' };

    if (isXhtmlOrHtml(type)) {
        const { text: page, type: pageType } = hardenPageDetailed(text, type);
        return { data: page, type: pageType };
    }
    if (asPage) {
        const { text: page, type: pageType } = hardenSvgAsPage(text);
        return { data: page, type: pageType };
    }
    const svg = hardenSvg(text);   // a picture: an unsafe one becomes an empty file
    return { data: svg ?? '', type: svg === null ? 'application/octet-stream' : SVG_TYPE };
}

function secureBook(book) {
    const target = book.transformTarget;
    if (!target) throw new ReaderError('unsupported', 'This book cannot be shown safely.');
    // The book's own pages (the only things a frame ever shows) are hardened as pages; pictures, styles and fonts as resources.
    const pages = new Set((book.sections ?? []).map(section => section.id));
    // Type spellings such as "application/xhtml+xml; charset=utf-8" would slip past the library's exact comparisons
    // (and so past its resource rewriting); say each one once, plainly, before anything is loaded.
    for (const item of book.resources?.manifest ?? []) item.mediaType = normalizeType(item.mediaType);
    // Never load script files at all.
    target.addEventListener('load', ({ detail }) => {
        if (detail.isScript) detail.allow = false;
    });
    target.addEventListener('data', ({ detail }) => {
        const mediaType = detail.type;
        const asPage = pages.has(detail.name);
        const outcome = Promise.resolve(detail.data).then(
            data => hardenResource(data, mediaType, asPage, detail.name),
            error => {
                console.error(new Error('Failed to load ' + detail.name, { cause: error }));
                return asPage ? { data: BLOCKED_PAGE, type: XHTML_TYPE } : { data: '', type: 'application/octet-stream' };
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

// Book frames cannot load web fonts, so the choices are system stacks; "book" leaves the book's own typeface alone.
const FONT_STACKS = {
    serif: "Georgia, 'Iowan Old Style', 'Times New Roman', serif",
    sans: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
};
const PAGE_WIDTHS = { narrow: 560, standard: 720, wide: 960 };

export function resolveTheme(name) {
    if (name === 'system') {
        name = globalThis.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }
    return THEMES[name] ?? THEMES.light;
}

export function bookCss({ theme, fontSize, lineHeight, font }) {
    const t = resolveTheme(theme);
    const size = Math.min(40, Math.max(10, Number(fontSize) || 18));
    const spacing = Math.min(3, Math.max(1, Number(lineHeight) || 1.6));
    const force = t.force ? ' !important' : '';
    const family = FONT_STACKS[font];
    return `
        @namespace epub "http://www.idpf.org/2007/ops";
        html { color-scheme: ${t.dark ? 'dark' : 'light'}; background: ${t.bg}${force}; color: ${t.fg}${force}; font-size: ${size}px !important; }
        ${t.force ? `body, body *:not(img):not(svg):not(svg *) { color: ${t.fg} !important; background-color: transparent !important; }
        a:link, a:visited, a:link *, a:visited * { color: ${t.link} !important; }` : ''}
        ${family ? `html, body, body *:not(code):not(pre):not(kbd):not(samp) { font-family: ${family} !important; }` : ''}
        p, li, blockquote, dd { line-height: ${spacing}; widows: 2; orphans: 2; }
        [align="left"] { text-align: left; } [align="right"] { text-align: right; }
        [align="center"] { text-align: center; } [align="justify"] { text-align: justify; }
        pre, code { white-space: pre-wrap !important; }
        /* Reflowable pages only (fixed-layout books never get this sheet): nothing may be wider than its page column,
           so printed contents lists, wide tables and fixed-width blocks wrap instead of spilling onto the next page. */
        body * { max-width: 100% !important; box-sizing: border-box; overflow-wrap: anywhere; }
        table { table-layout: fixed; width: 100% !important; }
        aside[epub|type~="endnote"], aside[epub|type~="footnote"], aside[epub|type~="note"], aside[epub|type~="rearnote"] { display: none; }
    `;
}

// ---------------------------------------------------------------------------
// The reader
// ---------------------------------------------------------------------------

const NOTIFY_DELAY_MS = 250;
const SELECTION_DELAY_MS = 250;
const MAX_SELECTED_TEXT = 2000;
const INTERACTIVE = 'a[href], button, input, select, textarea, summary, label, [role="button"], [contenteditable="true"]';

let session = null;

function emptyStats() {
    return { requests: 0, bytes: 0, openMs: 0, entries: 0, fileSize: 0 };
}

const SANDBOX_NO_SCRIPTS = 'allow-same-origin';
const SANDBOX_WITH_SCRIPTS = 'allow-same-origin allow-scripts';

/**
 * True on Apple WebKit (iPhone, iPad, Safari). There the book frame must be allowed to run scripts, because WebKit does not
 * deliver events from a script-less sandboxed frame to the page around it (WebKit bug 218086), which breaks taps, swipes,
 * selection and links. Every iOS browser is WebKit, and iPadOS asks for the desktop site and reports as a Mac, so it is told
 * apart by its touch screen. Chromium, Firefox and Android never match. `env` is a navigator-like object so this is testable.
 */
export function needsScriptSandbox(env = globalThis.navigator ?? {}) {
    const ua = String(env.userAgent ?? '');
    const vendor = String(env.vendor ?? '');
    const touchPoints = Number(env.maxTouchPoints ?? 0);
    if (/Android/i.test(ua)) return false;
    if (/iP(hone|ad|od)/.test(ua)) return true;
    if (/Macintosh/.test(ua) && touchPoints > 1) return true;
    return vendor === 'Apple Computer, Inc.' && !/Chrome|Chromium|Edg\/|OPR\/|Firefox/.test(ua);
}

/** The `sandbox` value for book frames: scripts are allowed only where WebKit needs them (see needsScriptSandbox). */
export function bookFrameSandbox(env) {
    return needsScriptSandbox(env) ? SANDBOX_WITH_SCRIPTS : SANDBOX_NO_SCRIPTS;
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
    const state = {
        host, controller, stats, dotNet, view: null, book: null, loader: null, listeners: [], timer: null, latest: null, disposed: false,
        marks: new Map(), paused: false, selection: null, selectionTimers: new Set(), markTap: false, suppressTap: false,
    };
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
        state.book = book;
        if (state.disposed) return { ok: false, code: 'cancelled', message: 'Closed.' };

        // The pinned foliate-js reads this when it builds a page frame; the book never chooses it.
        globalThis.TUVIMA_BOOK_FRAME_SANDBOX = bookFrameSandbox();
        const view = document.createElement('foliate-view');
        state.view = view;
        host.replaceChildren(view);
        view.addEventListener('load', e => onSectionLoad(state, e.detail.doc, e.detail.index));
        view.addEventListener('relocate', e => onRelocate(state, e.detail));
        view.addEventListener('external-link', e => onExternalLink(e));
        view.addEventListener('draw-annotation', e => e.detail.draw(Overlayer.highlight, { color: e.detail.annotation.color }));
        view.addEventListener('create-overlay', e => drawMarksInSection(state, e.detail.index));
        view.addEventListener('show-annotation', e => onMarkTapped(state, e.detail));
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
    const renderer = state.view?.renderer;
    renderer?.setStyles?.(css);
    if (renderer) {
        renderer.setAttribute('flow', options.flow === 'scrolled' ? 'scrolled' : 'paginated');
        renderer.setAttribute('max-inline-size', (PAGE_WIDTHS[options.pageWidth] ?? PAGE_WIDTHS.standard) + 'px');
    }
    const name = options.theme === 'system' ? (resolveTheme('system') === THEMES.dark ? 'dark' : 'light') : (THEMES[options.theme] ? options.theme : 'light');
    state.host.dataset.theme = name;
}

/**
 * The reading appearance to start with, as { theme, fontSize, lineHeight, font, pageWidth, flow }: the profile's
 * values (handed over by the page as `profile`) with whatever this device saved under the per-device key on top
 * (the same key the current reader uses). The device wins per key; a key it never saved uses the profile value;
 * anything missing or unreadable is left out so the page's defaults apply. See book-reader-settings.js.
 */
export function loadSettings(profile) {
    return mergeSettings(profile, readDeviceSettings(globalThis.localStorage));
}

/**
 * Remembers the reading appearance on this device. Only this reader's own keys are written (PascalCase, like the
 * current reader's), so everything else already stored under the key survives.
 */
export function saveSettings(options) {
    writeDeviceSettings(globalThis.localStorage, options);
}

/** Forgets this device's reader choices, so the profile's values apply again the next time the reader opens. */
export function clearSettings() {
    clearDeviceSettings(globalThis.localStorage);
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

/**
 * A reflowable page must wrap inside its column. A stylesheet that says `white-space: nowrap` (printed contents lists
 * with dotted leaders are the usual case) cannot be undone from the shared sheet without also breaking `pre-wrap` /
 * `pre-line` poetry, so only the elements that really compute to nowrap are set back to normal.
 */
export function unwrapNoWrap(doc) {
    const view = doc?.defaultView;
    if (!view || !doc.body) return 0;
    let changed = 0;
    for (const el of doc.body.querySelectorAll('*')) {
        if (view.getComputedStyle(el).whiteSpace === 'nowrap') {
            el.style.setProperty('white-space', 'normal', 'important');
            changed++;
        }
    }
    return changed;
}

function onSectionLoad(state, doc, index) {
    if (state.disposed) return;
    guardLinks(doc, openExternalLink);
    markBrokenPictures(doc);
    const win = doc.defaultView;
    const frame = win?.frameElement;
    if (!state.view?.isFixedLayout) unwrapNoWrap(doc);
    doc.addEventListener('keydown', e => onKey(state, e));
    // A tap that only dismisses a text selection is not a page turn.
    doc.addEventListener('pointerdown', () => { state.suppressTap = state.selection !== null; }, true);
    doc.addEventListener('selectionchange', () => {
        const timer = setTimeout(() => {
            state.selectionTimers.delete(timer);
            reportSelection(state, doc, index);
        }, SELECTION_DELAY_MS);
        state.selectionTimers.add(timer);
    });
    doc.addEventListener('click', e => {
        if (e.defaultPrevented) return;                       // a link the library already handled
        if (e.target.closest?.(INTERACTIVE)) return;
        if (state.suppressTap) { state.suppressTap = false; return; }
        const selection = win.getSelection?.();
        if (selection && !selection.isCollapsed) return;      // selecting text is not a page turn
        const x = toHostRect(frame, { left: e.clientX, top: 0, width: 0, height: 0 }).x;
        // The library reports a tap on a highlight in its own click listener; let it finish, then decide.
        setTimeout(() => {
            if (state.disposed) return;
            if (state.markTap) { state.markTap = false; return; }
            onTap(state, x);
        }, 0);
    });
}

/** A rectangle inside a book frame, as screen (viewport) coordinates. */
function toHostRect(frame, rect) {
    if (!frame) return { x: rect.left, y: rect.top, w: rect.width, h: rect.height };
    const box = frame.getBoundingClientRect();
    const scale = box.width / (frame.offsetWidth || box.width || 1);
    return { x: box.left + rect.left * scale, y: box.top + rect.top * scale, w: rect.width * scale, h: rect.height * scale };
}

function reportSelection(state, doc, index) {
    if (state.disposed) return;
    const selection = doc.defaultView?.getSelection?.();
    const text = selection && !selection.isCollapsed ? selection.toString().replace(/\s+/g, ' ').trim() : '';
    if (!text || selection.rangeCount === 0) {
        // Another section's selection event must not clear this section's selection.
        if (state.selection && state.selection.doc === doc) {
            state.selection = null;
            notify(state, 'OnSelectionCleared');
        }
        return;
    }
    const range = selection.getRangeAt(0);
    let cfi;
    try {
        cfi = state.view.getCFI(index, range);
    } catch (e) {
        console.debug('This selection has no usable position.', e);
        return;
    }
    const first = range.getClientRects()[0] ?? range.getBoundingClientRect();
    const box = toHostRect(doc.defaultView.frameElement, first);
    state.selection = { doc, cfi, text };
    notify(state, 'OnSelection', cfi, text.slice(0, MAX_SELECTED_TEXT), box.x, box.y, box.w, box.h);
}

/** While a panel or the highlight editor is open, taps and keys must not turn the page behind it. */
export function setInputPaused(paused) {
    if (session) session.paused = !!paused;
}

function onTap(state, clientX) {
    if (state.paused) return;
    const rect = state.host.getBoundingClientRect();
    const ratio = rect.width > 0 ? (clientX - rect.left) / rect.width : 0.5;
    if (ratio < 0.3) state.view.goLeft();
    else if (ratio > 0.7) state.view.goRight();
    else notify(state, 'OnToggleChrome');
}

function onKey(state, e) {
    if (e.defaultPrevented) return;
    // The browser's own Find cannot see inside the book's pages, so Ctrl or Cmd + F opens the reader's search instead.
    if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key?.toLowerCase() === 'f') { notify(state, 'OnOpenSearch'); e.preventDefault(); return; }
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const interactive = e.target?.closest?.('button, a[href], input, select, textarea, [contenteditable="true"], [role="button"]');
    const view = state.view;
    if (e.key === 'Escape') { notify(state, 'OnEscape'); e.preventDefault(); return; }
    // Typing in a note box, or moving between buttons with a remote's arrows, belongs to the control, not the page.
    if (e.target?.closest?.('input, select, textarea, [contenteditable="true"]')) return;
    if (interactive && e.key.startsWith('Arrow') && e.target.closest?.('.br-bar, .br-panel, .br-float')) return;
    if (state.paused) return;
    switch (e.key) {
        case 'ArrowLeft': view.goLeft(); break;
        case 'ArrowRight': view.goRight(); break;
        case 'PageUp': view.prev(); break;
        case 'PageDown': view.next(); break;
        case ' ':
            if (interactive) return;                           // Space belongs to a focused button
            if (e.shiftKey) view.prev(); else view.next();
            break;
        case 'Enter':
            if (interactive) return;                           // Enter belongs to a focused button or link
            notify(state, 'OnToggleChrome');                   // a remote's OK button shows the bars
            break;
        default: return;
    }
    e.preventDefault();
}

function openExternalLink(href) {
    if (typeof href === 'string' && /^(https?:|mailto:)/i.test(href)) {
        globalThis.open(href, '_blank', 'noopener,noreferrer');
    }
}

function onExternalLink(e) {
    e.preventDefault();                                        // the library would open anything; we decide
    openExternalLink(e.detail?.a?.href ?? e.detail?.href_);
}

/**
 * The click interceptor for one book page, and the other half of the clean-up in `restrictHrefs`: it must agree with it on
 * what a link is. The library only follows `a[href]` in the page itself, so a tap on an `<area>`, on a MathML element with an
 * `href` (a live link in WebKit) or on an anchor inside a shadow root would be followed by the frame. Here the nearest element
 * in the click's whole path that the browser would treat as a link is found (`<a>`, and anything outside SVG that has an `href`,
 * shadow roots included) and its default action is always cancelled. Only a plain `a[href]` in the page without a scheme (a
 * chapter or note link) is left to the library; an `a` to http, https or mailto is opened by `openExternal` here and never
 * reaches the library; every other address (ftp, tel, data, javascript, a link inside a shadow root) is simply dead.
 * It also cancels every form submission on the page (the same destination rule as the clean-up's removal of forms).
 * It runs before the library's own listener (capture phase) and is safe to run on a page that was not cleaned. A closed shadow root hides
 * its inside from every listener outside it, so that case is covered by the clean-up (which removes templates), not by this.
 */
export function guardLinks(doc, openExternal) {
    const hrefOf = el => el.getAttribute('href') ?? el.getAttributeNS(XLINK_NS, 'href');
    // In SVG only `<a>` links (image, use and the like point at pictures and shapes); anywhere else an href is a link.
    const isLink = el => el.nodeType === 1 && hrefOf(el) !== null && (isAnchor(el) || el.namespaceURI !== SVG_NS);
    const onClick = e => {
        const link = e.composedPath().find(isLink);
        if (!link) return;
        e.preventDefault();
        const inPage = isAnchor(link) && link.getRootNode() === doc && link.hasAttribute('href');
        const value = hrefOf(link);
        if (e.type === 'click' && inPage && !hasScheme(value)) return;    // a link inside the book: the library follows it
        e.stopImmediatePropagation();
        if (e.type !== 'click' || !inPage || !LINK_SCHEMES.test(urlText(value))) return;
        let href = null;
        try { href = new URL(urlText(value)).href; } catch { /* not an address the browser can read, so it opens nothing */ }
        if (href) openExternal(href);
    };
    doc.addEventListener('click', onClick, true);
    doc.addEventListener('auxclick', onClick, true);              // a middle click would open the link in a new tab
    // A form on a page that was not cleaned (an action, a button's formaction, an image button) must not send the frame anywhere either.
    doc.addEventListener('submit', e => { e.preventDefault(); e.stopImmediatePropagation(); }, true);
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

/** The contents list, flattened: [{ label, href, level }]. */
export function getToc() {
    const out = [];
    const walk = (items, level) => {
        for (const item of items ?? []) {
            if (out.length >= 2000) return;
            out.push({ label: String(item.label ?? '').replace(/\s+/g, ' ').trim() || 'Untitled', href: item.href ?? '', level });
            walk(item.subitems, level + 1);
        }
    };
    walk(session?.book?.toc, 0);
    return out;
}

export async function goToHref(href) {
    try {
        await session?.view?.goTo(href);
    } catch (e) {
        console.warn('That contents entry could not be opened.', e);
    }
}

// ---- Search ------------------------------------------------------------------------------------------------------

// Kept small: the whole list travels to the page in one message, and a circuit drops a message over 32 KB.
const MAX_SEARCH_HITS = 50;

/**
 * Looks for `query` in every page of the book, in the browser (the book never leaves it). Returns
 * { hits: [{ cfi, label, pre, match, post }], truncated, cancelled }; a newer search or clearSearch() ends this one early.
 * The matches on the page the reader is showing are outlined until the next search or clearSearch().
 */
export async function search(query) {
    const state = session;
    const text = String(query ?? '').trim();
    if (!state || state.disposed || !state.view || text.length < 2) return { hits: [], truncated: false, cancelled: false, failed: false };
    const token = {};
    state.searchToken = token;
    const hits = [];
    let truncated = false;
    try {
        const outline = { draw: Overlayer.outline, drawOptions: { color: '#eab308', width: 2 } };
        for await (const result of state.view.search({ query: text, ...outline })) {
            if (state.disposed || state.searchToken !== token) {
                // A newer search owns the outlines now; only a search that ended for another reason cleans up.
                if (!state.disposed && state.searchToken === token) clearSearch();
                return { hits: [], truncated: false, cancelled: true, failed: false };
            }
            if (!result?.subitems) continue;                    // progress and the final 'done' carry no matches
            for (const { cfi, excerpt } of result.subitems) {
                if (hits.length >= MAX_SEARCH_HITS) { truncated = true; break; }
                hits.push({
                    cfi,
                    label: String(result.label ?? '').replace(/\s+/g, ' ').trim(),
                    pre: excerpt?.pre ?? '',
                    match: excerpt?.match ?? text,
                    post: excerpt?.post ?? '',
                });
            }
            if (truncated) break;
        }
    } catch (e) {
        console.warn('Searching the book failed.', e);
        return { hits, truncated, cancelled: false, failed: true };
    }
    return { hits, truncated, cancelled: false, failed: false };
}

/** Ends a search in progress and removes the outlines it drew. */
export function clearSearch() {
    const state = session;
    if (!state) return;
    state.searchToken = null;
    try {
        state.view?.clearSearch();
    } catch (e) {
        console.debug('Search outlines could not be cleared.', e);
    }
}

// ---- Highlights --------------------------------------------------------------------------------------------------

function markIndex(state, cfi) {
    try {
        return state.view.resolveCFI(cfi).index;
    } catch (e) {
        console.debug('A saved highlight has a position this book cannot resolve.', e);
        return -1;
    }
}

function drawMark(state, mark) {
    if (state.disposed || mark.index < 0) return;
    state.view.addAnnotation({ value: mark.cfi, id: mark.id, color: mark.color }).catch(e => {
        console.debug('A highlight could not be drawn.', e);
    });
}

function drawMarksInSection(state, index) {
    for (const mark of state.marks.values()) if (mark.index === index) drawMark(state, mark);
}

function onMarkTapped(state, detail) {
    const mark = state.marks.get(detail.value);
    if (!mark) return;
    state.markTap = true;
    const range = detail.range;
    const frame = range?.startContainer?.ownerDocument?.defaultView?.frameElement;
    const rect = range ? (range.getClientRects()[0] ?? range.getBoundingClientRect()) : { left: 0, top: 0, width: 0, height: 0 };
    const box = toHostRect(frame, rect);
    notify(state, 'OnHighlightTapped', mark.id, box.x, box.y, box.w, box.h);
}

/** Replaces every highlight with `list` ([{ id, cfi, color }]); ones the book cannot place are skipped quietly. */
export function setHighlights(list) {
    const state = session;
    if (!state || state.disposed) return;
    for (const mark of state.marks.values()) state.view.deleteAnnotation({ value: mark.cfi }).catch(() => {
        // The section is not on screen any more; its overlay went with it.
    });
    state.marks.clear();
    for (const item of list ?? []) addHighlight(item);
}

export function addHighlight(item) {
    const state = session;
    if (!state || state.disposed || !item?.cfi) return;
    const mark = { id: item.id, cfi: item.cfi, color: item.color || '#EAB308', index: markIndex(state, item.cfi) };
    state.marks.set(mark.cfi, mark);
    drawMark(state, mark);
}

export function recolorHighlight(id, color) {
    const state = session;
    const mark = state && [...state.marks.values()].find(m => m.id === id);
    if (!mark) return;
    mark.color = color;
    drawMark(state, mark);
}

export function removeHighlight(id) {
    const state = session;
    const mark = state && [...state.marks.values()].find(m => m.id === id);
    if (!mark) return;
    state.marks.delete(mark.cfi);
    state.view.deleteAnnotation({ value: mark.cfi }).catch(() => {
        // The section is not on screen; nothing is drawn there.
    });
}

/** The text the person currently has selected, as { cfi, text }, or null. */
export function getSelection() {
    const s = session?.selection;
    return s ? { cfi: s.cfi, text: s.text } : null;
}

export function clearSelection() {
    const state = session;
    if (!state?.selection) return;
    try {
        state.selection.doc.defaultView?.getSelection?.()?.removeAllRanges();
    } catch (e) {
        console.debug('The selection could not be cleared.', e);
    }
    state.selection = null;
}

/** Latest position, for saving right before the page closes. */
export function getLocation() { return session?.latest ?? null; }
export function getStats() { return session ? { ...session.stats } : null; }

function teardown(state) {
    if (state.disposed) return;
    state.disposed = true;
    clearTimeout(state.timer);
    for (const timer of state.selectionTimers) clearTimeout(timer);
    state.selectionTimers.clear();
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
