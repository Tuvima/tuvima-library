// Browser checks for the book reader (BR2). Runs the real adapter in headless Chromium against a stand-in
// Dashboard that mimics /engine-book/{id}/file, using the regression EPUB built by epub-fixtures.mjs.
//
//   node scripts/visual-qa/book-reader/run-checks.mjs
//
// Needs Playwright with a Chromium (PLAYWRIGHT_MODULE=<path to playwright> if it is not installed globally).

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { buildBigEpub, buildHostileEpub, buildRegressionEpub } from './epub-fixtures.mjs';
import { startServer } from './serve.mjs';
import {
    GUIDS, bookFrames, bookUrl, ensureDir, fixturesDir, frameWith, lastRelocation, loadPlaywright,
    nextRelocation, openBook, openCheckPage, relocationCount, sleep, waitFor,
} from './harness.mjs';

const results = [];
const check = async (name, fn) => {
    try {
        const detail = await fn();
        results.push({ name, ok: true, detail: detail ?? '' });
        console.log(`  ok    ${name}${detail ? ` — ${detail}` : ''}`);
    } catch (error) {
        results.push({ name, ok: false, detail: String(error.message ?? error) });
        console.log(`  FAIL  ${name} — ${error.message ?? error}`);
    }
};
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const dir = ensureDir(fixturesDir());
const epub = path.join(dir, 'regression.epub');
const hostile = path.join(dir, 'hostile.epub');
const big = path.join(dir, 'big-3mb.epub');
const textFile = path.join(dir, 'not-a-book.epub');
fs.writeFileSync(textFile, 'This is plain text, not a zip file. '.repeat(40));

const { chromium } = loadPlaywright();
const browser = await chromium.launch();

// The "web" (an outside tracker) is a second server so we can see whether the book ever reaches out.
const outside = await startServer();
const server = await startServer({ books: { [GUIDS.regression]: epub, [GUIDS.notABook]: textFile, [GUIDS.hostile]: hostile, [GUIDS.big5]: big } });
buildRegressionEpub(epub, { trackerOrigin: outside.origin });
buildHostileEpub(hostile);
buildBigEpub(big, 3);

const phone = { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, deviceScaleFactor: 2 };
const desktop = { viewport: { width: 1280, height: 800 } };

async function withBook(contextOptions, options, body) {
    const context = await browser.newContext(contextOptions);
    await context.route(/example\.org/, route => route.fulfill({ contentType: 'text/html', body: '<p>outside page</p>' }));
    const { page, consoleMessages } = await openCheckPage(context, server.origin);
    try {
        const result = await openBook(page, bookUrl(GUIDS.regression), options);
        return await body({ page, context, result, consoleMessages });
    } finally {
        await context.close();
    }
}

const imageLoads = (frame, selector) => frame.evaluate(async selector => {
    const el = document.querySelector(selector);
    const load = src => new Promise(resolve => {
        const image = new Image();
        image.onload = () => resolve(image.naturalWidth);
        image.onerror = () => resolve(0);
        image.src = src;
    });
    const style = getComputedStyle(el).backgroundImage;
    const bg = /url\("?(blob:[^")]+)"?\)/.exec(style)?.[1];
    return {
        naturalWidth: el.naturalWidth ?? null,
        complete: el.complete ?? null,
        backgroundBlob: bg ?? null,
        backgroundWidth: bg ? await load(bg) : null,
        svgHref: el.getAttribute?.('xlink:href') ?? el.getAttribute?.('href') ?? null,
    };
}, selector);

console.log('Opening the regression book');

await check('opens a never-read book at the start with no error', () => withBook(phone, {}, async ({ result, page }) => {
    assert(result.ok, `open failed: ${result.code} ${result.message}`);
    assert(result.restored === false, 'should not claim a restored position');
    assert(result.title === 'Regression Fixture', `title was "${result.title}"`);
    assert(result.dir === 'ltr' && result.layout === 'reflowable', `dir/layout ${result.dir}/${result.layout}`);
    const frame = await frameWith(page, 'svg image');
    const svg = await frame.evaluate(() => {
        const image = document.querySelector('svg image');
        return image.getAttribute('href') ?? image.getAttribute('xlink:href');
    });
    assert(svg?.startsWith('blob:'), `cover SVG <image> was not rewritten to a blob: URL (${svg})`);
    return `${result.stats.requests} request(s), ${result.stats.bytes} bytes for a ${result.stats.fileSize} byte file`;
}));

await check('pictures, stylesheets, url() and SVG <image> all show (old reader: 401 / 404 / stripped)', () => withBook(phone, {}, async ({ page }) => {
    await page.evaluate(() => window.br.goTo(1));
    const frame = await frameWith(page, '#relative-img');
    const rel = await imageLoads(frame, '#relative-img');
    assert(rel.complete && rel.naturalWidth === 120, `relative image not loaded: ${JSON.stringify(rel)}`);
    const abs = await imageLoads(frame, '#absolute-img');
    assert(abs.complete && abs.naturalWidth === 120, `absolute-path image not loaded: ${JSON.stringify(abs)}`);
    for (const selector of ['#inline-style-bg', '#sheet-bg', '#style-block-bg']) {
        const info = await imageLoads(frame, selector);
        assert(info.backgroundBlob && info.backgroundWidth === 16, `${selector} url() did not load: ${JSON.stringify(info)}`);
    }
    const svg = await imageLoads(frame, '#svg-image');
    assert(svg.svgHref?.startsWith('blob:'), `SVG <image> kept href ${svg.svgHref}`);
    const svgWidth = await frame.evaluate(href => new Promise(resolve => {
        const image = new Image(); image.onload = () => resolve(image.naturalWidth); image.onerror = () => resolve(0); image.src = href;
    }), svg.svgHref);
    assert(svgWidth === 120, `SVG <image> target does not load (${svgWidth})`);
    const sheetLinked = await frame.evaluate(() => getComputedStyle(document.querySelector('.hero')).color);
    assert(sheetLinked === 'rgb(18, 52, 86)', `linked stylesheet not applied (${sheetLinked})`);
}));

await check('an outside image is never fetched and the layout survives', () => withBook(phone, {}, async ({ page }) => {
    outside.clearLog();
    await page.evaluate(() => window.br.goTo(1));
    const frame = await frameWith(page, '#external-img');
    await sleep(500);
    const hasSrc = await frame.evaluate(() => document.querySelector('#external-img').hasAttribute('src'));
    assert(!hasSrc, 'external <img> kept its src');
    assert(outside.log.filter(e => e.path.startsWith('/tracker')).length === 0, 'the tracker was contacted');
    const box = await frame.evaluate(() => { const r = document.querySelector('#relative-img').getBoundingClientRect(); return [r.width, r.height]; });
    assert(box[0] === 120 && box[1] === 80, `layout shifted: ${box}`);
}));

await check('a genuinely missing picture is just missing (no auth error, nothing requested)', () => withBook(phone, {}, async ({ page }) => {
    server.clearLog();
    await page.evaluate(() => window.br.goTo(1));
    const frame = await frameWith(page, '#missing-img');
    await sleep(500);
    const info = await imageLoads(frame, '#missing-img');
    assert(info.naturalWidth === 0, 'a missing picture claims to be loaded');
    const bad = server.log.filter(e => e.status === 401 || e.path.includes('missing'));
    assert(bad.length === 0, `unexpected requests: ${JSON.stringify(bad)}`);
}));

await check('book scripts never run (inline, handler attributes, external file)', () => withBook(desktop, {}, async ({ page, consoleMessages }) => {
    await page.evaluate(() => window.br.goTo(1));
    const frame = await frameWith(page, '#onclick-p');
    // Non-bubbling events reach the element's own handler attribute but not the reader's tap zones.
    await frame.evaluate(() => {
        document.querySelector('#onclick-p').dispatchEvent(new MouseEvent('click'));
        document.querySelector('#onerror-img').dispatchEvent(new Event('error'));
    });
    await sleep(300);
    const pwned = await page.evaluate(() => window.__pwned ?? null);
    const inner = await frame.evaluate(() => window.__pwned ?? null);
    assert(pwned === null && inner === null, `a book script ran: top=${pwned} frame=${inner}`);
    const meta = await frame.evaluate(() => document.querySelector('meta[http-equiv="Content-Security-Policy"]')?.content ?? null);
    assert(meta?.includes("script-src 'none'"), 'the page carries no CSP');
    const scripts = await frame.evaluate(() => document.querySelectorAll('script').length);
    assert(scripts === 0, `${scripts} <script> element(s) survived`);
    return 'CSP: ' + consoleMessages.filter(m => /Content Security Policy/i.test(m.text)).length + ' violation message(s) while testing';
}));

await check('the page rules alone stop scripts and outside requests, even if the cleaning step were skipped', async () => {
    const context = await browser.newContext(desktop);
    const { page } = await openCheckPage(context, server.origin);
    outside.clearLog();
    const out = await page.evaluate(async trackerOrigin => {
        const html = `<html xmlns="http://www.w3.org/1999/xhtml"><head><meta http-equiv="Content-Security-Policy" content="${window.br.BOOK_CSP}"/></head><body><p id="p" onclick="window.__csp='handler'">x</p><img id="out" src="${trackerOrigin}/tracker/pixel.png"/><script>window.__csp='inline'; fetch('${trackerOrigin}/tracker/fetch');</script></body></html>`;
        const url = URL.createObjectURL(new Blob([html], { type: 'application/xhtml+xml' }));
        const iframe = document.createElement('iframe');
        iframe.setAttribute('sandbox', 'allow-same-origin allow-scripts');
        document.body.append(iframe);
        await new Promise(resolve => { iframe.onload = resolve; iframe.src = url; });
        iframe.contentDocument.getElementById('p').dispatchEvent(new MouseEvent('click'));
        await new Promise(r => setTimeout(r, 400));
        return { ran: iframe.contentWindow.__csp ?? null, imageWidth: iframe.contentDocument.getElementById('out').naturalWidth };
    }, outside.origin);
    assert(out.ran === null, `a script ran: ${out.ran}`);
    assert(outside.log.length === 0, `the page reached the outside: ${JSON.stringify(outside.log)}`);
    await context.close();
});

await check('book links: outside links open safely, chapter links navigate', () => withBook(desktop, {}, async ({ page, context }) => {
    await page.evaluate(() => window.br.goTo(1));
    const frame = await frameWith(page, '#external-link');
    const popupPromise = context.waitForEvent('page', { timeout: 5000 });
    await frame.click('#external-link');
    const popup = await popupPromise;
    assert(popup.url().startsWith('https://example.org') || popup.url() === 'about:blank', `popup went to ${popup.url()}`);
    const before = await relocationCount(page);
    await frame.click('#internal-link');
    await frameWith(page, '#start');
    await nextRelocation(page, before);
}));

console.log('Turning pages');

await check('tap zones, keys and swipe turn pages; the centre toggles the bars', () => withBook(phone, {}, async ({ page, context }) => {
    await page.evaluate(() => window.br.goTo(2));
    await frameWith(page, '#start');
    const first = await nextRelocation(page, 0);
    const width = 390;
    const height = 844;

    let count = await relocationCount(page);
    await page.mouse.click(width * 0.9, height * 0.5);
    const afterTap = await nextRelocation(page, count);
    assert(afterTap[2] > first[2], `right tap did not advance (${first[2]} -> ${afterTap[2]})`);

    count = await relocationCount(page);
    await page.mouse.click(width * 0.1, height * 0.5);
    const afterBack = await nextRelocation(page, count);
    assert(afterBack[2] < afterTap[2], 'left tap did not go back');

    count = await relocationCount(page);
    await page.keyboard.press('ArrowRight');
    const afterKey = await nextRelocation(page, count);
    assert(afterKey[2] > afterBack[2], 'ArrowRight did not advance');

    count = await relocationCount(page);
    await page.keyboard.press('Space');
    const afterSpace = await nextRelocation(page, count);
    assert(afterSpace[2] > afterKey[2], 'Space did not advance');

    count = await relocationCount(page);
    await page.keyboard.press('ArrowLeft');
    const afterLeft = await nextRelocation(page, count);
    assert(afterLeft[2] < afterSpace[2], 'ArrowLeft did not go back');

    // A touch swipe (right to left = next page). Chromium's remote touch input reports screenX as 0, which the
    // library reads, so the touch events are built in the page with real screen positions instead.
    count = await relocationCount(page);
    const frame = bookFrames(page).at(-1);
    await frame.evaluate(async () => {
        const target = document.body;
        const fire = (type, x) => {
            const touch = new Touch({ identifier: 1, target, clientX: x, clientY: 420, screenX: x, screenY: 420 });
            const list = type === 'touchend' ? [] : [touch];
            target.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true, touches: list, targetTouches: list, changedTouches: [touch] }));
        };
        fire('touchstart', 320);
        for (let x = 320; x >= 80; x -= 40) { fire('touchmove', x); await new Promise(r => setTimeout(r, 16)); }
        fire('touchend', 80);
    });
    await nextRelocation(page, count);
    await sleep(900);                                   // let the page settle into place (scroll, then snap)
    const afterSwipe = await lastRelocation(page);
    assert(afterSwipe[2] > afterLeft[2], `swipe did not advance (${afterLeft[2]} -> ${afterSwipe[2]})`);

    count = await page.evaluate(() => window.events.filter(e => e[0] === 'OnToggleChrome').length);
    await page.mouse.click(width * 0.5, height * 0.5);
    await waitFor(async () => (await page.evaluate(() => window.events.filter(e => e[0] === 'OnToggleChrome').length)) > count, { label: 'centre tap' });
}));

await check('Escape asks the page to close', () => withBook(desktop, {}, async ({ page }) => {
    await page.keyboard.press('Escape');
    await waitFor(() => page.evaluate(() => window.events.some(e => e[0] === 'OnEscape')), { label: 'Escape' });
}));

console.log('Remembering the place');

await check('leave and return reopens at the same place from the saved position', () => withBook(desktop, {}, async ({ page }) => {
    await page.evaluate(() => window.br.goTo(2));
    await frameWith(page, '#start');
    for (let i = 0; i < 3; i++) {
        const count = await relocationCount(page);
        await page.keyboard.press('ArrowRight');
        await nextRelocation(page, count);
    }
    const saved = await page.evaluate(() => window.br.getLocation());
    assert(saved?.cfi?.startsWith('epubcfi('), `no CFI to save: ${JSON.stringify(saved)}`);
    await page.evaluate(() => window.br.dispose());
    await page.evaluate(() => { window.events.length = 0; });
    const reopened = await openBook(page, bookUrl(GUIDS.regression), { lastLocation: saved.cfi });
    assert(reopened.ok && reopened.restored, `not restored: ${JSON.stringify(reopened)}`);
    await waitFor(() => page.evaluate(() => window.br.getLocation()?.cfi), { label: 'reopened position' });
    const again = await page.evaluate(() => window.br.getLocation());
    assert(again.cfi === saved.cfi, `position moved: ${saved.cfi} -> ${again.cfi}`);
    return saved.cfi;
}));

await check('a saved position that no longer fits opens at the start instead of failing', () => withBook(desktop, { lastLocation: 'epubcfi(/6/999!/4/2)' }, async ({ result }) => {
    assert(result.ok, `open failed: ${result.message}`);
}));

console.log('Failure messages');

for (const [status, code] of [[401, 'unauthorized'], [404, 'notfound'], [429, 'busy'], [503, 'unavailable']]) {
    await check(`${status} from the library says "${code}"`, async () => {
        server.setMode(status);
        try {
            return await withBook(desktop, {}, async ({ result }) => {
                assert(!result.ok && result.code === code, `got ${JSON.stringify(result)}`);
                assert(typeof result.message === 'string' && result.message.length > 5, 'no readable message');
            });
        } finally {
            server.setMode('range');
        }
    });
}

await check('a file that is not a book says so plainly', async () => {
    const context = await browser.newContext(desktop);
    const { page } = await openCheckPage(context, server.origin);
    const result = await openBook(page, bookUrl(GUIDS.notABook));
    await context.close();
    assert(!result.ok && result.code === 'unsupported', `got ${JSON.stringify(result)}`);
});

await check('a server that ignores byte ranges still works (whole file once)', async () => {
    server.setMode('no-range');
    try {
        return await withBook(desktop, {}, async ({ result }) => {
            assert(result.ok, `open failed: ${result.message}`);
            return `${result.stats.requests} request(s), ${result.stats.bytes} bytes`;
        });
    } finally {
        server.setMode('range');
    }
});


console.log('Hostile book (SVG pages, frames, odd page types)');

/** Every window (top and all frames) must be free of what the hostile scripts would have written. */
const pwnedEverywhere = async page => {
    const found = [await page.evaluate(() => window.__pwned ?? null)];
    for (const frame of page.frames()) {
        try { found.push(await frame.evaluate(() => window.__pwned ?? null)); } catch { /* frame went away */ }
    }
    return found.filter(Boolean);
};

await check('hostile SVG pages, SVG frames, odd page types: nothing runs, harmless parts still show', async () => {
    const context = await browser.newContext(desktop);
    await context.route(/example\.org/, route => route.fulfill({ contentType: 'text/html', body: '<p>outside page</p>' }));
    const { page } = await openCheckPage(context, server.origin);
    outside.clearLog();
    const result = await openBook(page, bookUrl(GUIDS.hostile), {});
    assert(result.ok, `open failed: ${result.message}`);
    const seen = [];

    // Chapter with <iframe>/<object>/<embed> pointing at a hostile SVG, <img> of it, javascript: frame and link, meta refresh.
    await sleep(500);
    let frame = await frameWith(page, '#hostile-title');
    const chapter = await frame.evaluate(() => ({
        frames: document.querySelectorAll('iframe, frame, object, embed').length,
        scripts: document.querySelectorAll('script').length,
        jsLink: document.querySelector('#js-link')?.getAttribute('href') ?? null,
        refresh: document.querySelectorAll('meta[http-equiv="refresh" i]').length,
        csp: document.querySelector('meta[http-equiv="Content-Security-Policy"]')?.content ?? '',
    }));
    assert(chapter.frames === 0, `${chapter.frames} frame/object/embed element(s) survived in the chapter`);
    assert(chapter.scripts === 0 && chapter.refresh === 0, `scripts=${chapter.scripts} refresh=${chapter.refresh}`);
    assert(chapter.jsLink === null, `the javascript: link survived: ${chapter.jsLink}`);
    assert(chapter.csp.includes("frame-src 'none'"), 'the chapter CSP allows frames');
    seen.push('chapter');

    // The hostile SVG as a book page of its own: harmless text stays, everything active is gone.
    for (const [index, name] of [[1, 'harmless svg'], [2, 'hostile svg']]) {
        await page.evaluate(i => window.br.goTo(i), index);
        await sleep(700);
        const f = await frameWith(page, 'svg');
        const svg = await f.evaluate(() => ({
            text: document.querySelector('#ok-text, #visible-text')?.textContent ?? null,
            scripts: document.querySelectorAll('script').length,
            foreign: document.querySelectorAll('foreignObject, set, animate').length,
            nonSvg: Array.from(document.querySelector('svg').querySelectorAll('*')).filter(e => e.namespaceURI !== 'http://www.w3.org/2000/svg').length,
            handlers: Array.from(document.querySelectorAll('*')).filter(e => Array.from(e.attributes).some(a => /^on/i.test(a.name))).length,
            jsLink: Array.from(document.querySelectorAll('a')).some(a => /javascript:/i.test(a.getAttribute('href') ?? a.getAttribute('xlink:href') ?? '')),
            outsideImage: document.querySelector('#outside-image')?.getAttribute('href') ?? document.querySelector('#outside-image')?.getAttribute('xlink:href') ?? null,
            webLink: document.querySelector('#web-link-text')?.parentElement?.getAttribute('href') ?? null,
        }));
        assert(svg.text, `${name}: its harmless text is missing`);
        assert(svg.scripts === 0 && svg.foreign === 0 && svg.handlers === 0 && !svg.jsLink, `${name}: ${JSON.stringify(svg)}`);
        assert(svg.nonSvg === 0, `${name}: ${svg.nonSvg} element(s) outside the SVG namespace`);
        if (index === 2) {
            assert(svg.outsideImage === null, `${name}: outside image kept: ${svg.outsideImage}`);
            assert(svg.webLink === 'https://example.org/ok', `${name}: a normal web link was lost: ${svg.webLink}`);
        }
        seen.push(name);
    }

    // An XML-typed page is refused; a not-well-formed XHTML page is read as HTML, its script removed, its refresh removed.
    await page.evaluate(() => window.br.goTo(3));
    await sleep(600);
    assert((await pwnedEverywhere(page)).length === 0, 'something ran on the xml-typed page');
    await page.evaluate(() => window.br.goTo(4));
    await sleep(700);
    frame = await frameWith(page, '#broken-text');
    const broken = await frame.evaluate(() => ({ scripts: document.querySelectorAll('script').length, refresh: document.querySelectorAll('meta[http-equiv="refresh" i]').length }));
    assert(broken.scripts === 0 && broken.refresh === 0, `broken page: ${JSON.stringify(broken)}`);
    seen.push('xml-typed and broken pages');

    const pwned = await pwnedEverywhere(page);
    assert(pwned.length === 0, `book code ran: ${pwned.join(' | ')}`);
    assert(outside.log.length === 0, `the book reached the outside: ${JSON.stringify(outside.log)}`);
    await context.close();
    return seen.join(', ');
});

await check('hardenSvg / hardenResource / hardenPage fail closed', async () => {
    const context = await browser.newContext(desktop);
    const { page } = await openCheckPage(context, server.origin);
    const out = await page.evaluate(() => {
        const svgNs = 'xmlns="http://www.w3.org/2000/svg"';
        return {
            broken: window.br.hardenSvg('<svg ' + svgNs + '><script>1</svg'),
            notSvg: window.br.hardenSvg('<html xmlns="http://www.w3.org/1999/xhtml"><body><script>1</script></body></html>'),
            clean: window.br.hardenSvg('<svg ' + svgNs + ' onload="x()"><script>1</script><circle r="1"/></svg>'),
            xml: window.br.hardenResource('<x/>', 'text/xml'),
            xmlBlob: window.br.hardenResource(new Blob(['<x/>']), 'application/xml'),
            xmlPage: window.br.hardenResource('<x/>', 'text/xml', true),
            image: window.br.hardenResource(new Uint8Array([1, 2]), 'image/png'),
            css: window.br.hardenResource('a{}', 'text/css'),
            brokenXhtml: window.br.hardenResource('<html><body><p>one<script>1</script></body>', 'application/xhtml+xml'),
            cssClean: window.br.hardenPage('<html xmlns="http://www.w3.org/1999/xhtml"><head><style>@import url(https://e.example/x.css); a{background:url(https://e.example/y.png)} b{background:url(blob:http://a/1)}</style></head><body style="background:url(http://e.example/z)"><meta http-equiv="refresh" content="0;url=http://e.example"/><iframe src="blob:http://a/2"></iframe></body></html>', 'application/xhtml+xml'),
        };
    });
    assert(out.broken === null && out.notSvg === null, 'a broken or non-SVG file was let through');
    assert(out.clean && !/script|onload/.test(out.clean) && /circle/.test(out.clean), `clean svg: ${out.clean}`);
    assert(out.xml.data === '' && out.xmlBlob.data === '' && /safely/.test(out.xmlPage.data), 'an XML-typed document was let through');
    assert(out.image.data.length === 2 && out.css.data === 'a{}', 'ordinary resources were altered');
    assert(out.brokenXhtml.type === 'text/html' && !/<script/i.test(out.brokenXhtml.data) && /one/.test(out.brokenXhtml.data), `broken xhtml: ${JSON.stringify(out.brokenXhtml)}`);
    assert(!/e\.example/.test(out.cssClean) && /blob:http:\/\/a\/1/.test(out.cssClean), `css: ${out.cssClean}`);
    assert(!/refresh|<iframe/i.test(out.cssClean), `refresh or iframe remains: ${out.cssClean}`);
    await context.close();
});

await check('a book replaced on disk while it is being read reports "changed" (If-Range) and does not mix old and new bytes', async () => {
    const context = await browser.newContext(desktop);
    const { page } = await openCheckPage(context, server.origin);
    const result = await openBook(page, bookUrl(GUIDS.big5), {});
    assert(result.ok, `open failed: ${result.message}`);
    server.clearLog();
    server.setEtag('"v2"');
    try {
        await page.evaluate(() => window.br.goTo(5).catch(() => {}));
        await waitFor(() => page.evaluate(() => window.events.some(e => e[0] === 'OnFailed')), { label: 'a changed-book report', timeout: 8000 });
        const event = await page.evaluate(() => window.events.find(e => e[0] === 'OnFailed'));
        assert(event[1] === 'changed', `got ${JSON.stringify(event)}`);
        assert(server.log.some(e => e.range), 'no range request was made');
    } finally {
        server.setEtag('"v1"');
    }
    await context.close();
});

console.log('Helpers');

await check('built-in SHA-1 (used on plain http, where the browser has none) matches the real one', async () => {
    const context = await browser.newContext(desktop);
    const { page } = await openCheckPage(context, server.origin);
    for (const text of ['', 'abc', 'urn:uuid:11111111-2222-3333-4444-555555555555', 'x'.repeat(55), 'x'.repeat(56), 'x'.repeat(64), 'x'.repeat(1000), 'héllo wörld 日本語']) {
        const got = await page.evaluate(text => Array.from(window.br.sha1Fallback(new TextEncoder().encode(text)), b => b.toString(16).padStart(2, '0')).join(''), text);
        const want = crypto.createHash('sha1').update(text).digest('hex');
        assert(got === want, `SHA-1 differs for ${JSON.stringify(text.slice(0, 20))}`);
    }
    await context.close();
});

await check('hardenPage strips scripts and handlers and blocks outside addresses', async () => {
    const context = await browser.newContext(desktop);
    const { page } = await openCheckPage(context, server.origin);
    const out = await page.evaluate(() => window.br.hardenPage(
        '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>t</title><script>1</script><link rel="stylesheet" href="https://evil.example/x.css"/></head><body onload="x()"><img src="https://evil.example/p.png" srcset="https://evil.example/a.png 2x"/><img src="blob:http://a/1" srcset="blob:http://a/2 2x"/><object data="x"/></body></html>',
        'application/xhtml+xml'));
    assert(!/<script/i.test(out) && !/onload/i.test(out), 'scripts or handlers remain');
    assert(!/evil\.example/.test(out), 'an outside address remains');
    assert(/blob:http:\/\/a\/1/.test(out) && /blob:http:\/\/a\/2/.test(out), 'local pictures were removed');
    assert(/Content-Security-Policy/.test(out), 'no CSP meta');
    assert(!/<object/.test(out), 'object remains');
    await context.close();
});

await browser.close();
await server.close();
await outside.close();

const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
process.exit(failed.length ? 1 : 0);
