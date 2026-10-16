// Browser checks for the book reader (BR2). Runs the real adapter in a headless browser against a stand-in
// Dashboard that mimics /engine-book/{id}/file, using the regression EPUB built by epub-fixtures.mjs.
//
//   node scripts/visual-qa/book-reader/run-checks.mjs [chromium|webkit]
//   BOOK_READER_BROWSER=webkit node scripts/visual-qa/book-reader/run-checks.mjs
//
// The browser is the first argument or BOOK_READER_BROWSER; the default is chromium. WebKit stands in for iPhone
// Safari (every iOS browser uses it). Needs Playwright (PLAYWRIGHT_MODULE=<path to playwright> if it is not
// installed globally) and the browser itself: Chromium may be preinstalled, WebKit usually needs
// `npx playwright install webkit` once (it downloads the browser into the Playwright browsers folder, which is
// %LOCALAPPDATA%\ms-playwright on Windows).

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { buildBigEpub, buildHostileEpub, buildFrontMatterEpub, buildPictureEpub, buildRegressionEpub, FRONT_MATTER_PAGES, PICTURE_PAGES } from './epub-fixtures.mjs';
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
const sortedKeys = object => Object.fromEntries(Object.entries(object).sort(([a], [b]) => a.localeCompare(b)));

const dir = ensureDir(fixturesDir());
const epub = path.join(dir, 'regression.epub');
const hostile = path.join(dir, 'hostile.epub');
const big = path.join(dir, 'big-3mb.epub');
const pictures = path.join(dir, 'pictures.epub');
const frontMatter = path.join(dir, 'front-matter.epub');
const textFile = path.join(dir, 'not-a-book.epub');
fs.writeFileSync(textFile, 'This is plain text, not a zip file. '.repeat(40));

const browserName = (process.argv[2] ?? process.env.BOOK_READER_BROWSER ?? 'chromium').toLowerCase();
if (!['chromium', 'webkit'].includes(browserName)) {
    console.error(`Unknown browser "${browserName}": use chromium or webkit.`);
    process.exit(2);
}
const browser = await loadPlaywright()[browserName].launch();
console.log(`Browser: ${browserName} ${browser.version()}`);

// The "web" (an outside tracker) is a second server so we can see whether the book ever reaches out.
const outside = await startServer();
const server = await startServer({ books: { [GUIDS.regression]: epub, [GUIDS.notABook]: textFile, [GUIDS.hostile]: hostile, [GUIDS.big5]: big, [GUIDS.pictures]: pictures, [GUIDS.frontMatter]: frontMatter } });
buildRegressionEpub(epub, { trackerOrigin: outside.origin });
buildHostileEpub(hostile);
buildBigEpub(big, 3);
buildPictureEpub(pictures);
buildFrontMatterEpub(frontMatter);

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

// The book frames cannot run scripts (that is the point), so anything asynchronous is done from the page around them.
const imageLoads = async (page, frame, selector) => {
    const info = await frame.evaluate(selector => {
        const el = document.querySelector(selector);
        const style = getComputedStyle(el).backgroundImage;
        return {
            naturalWidth: el.naturalWidth ?? null,
            complete: el.complete ?? null,
            backgroundBlob: /url\("?(blob:[^")]+)"?\)/.exec(style)?.[1] ?? null,
            svgHref: el.getAttribute?.('xlink:href') ?? el.getAttribute?.('href') ?? null,
        };
    }, selector);
    info.backgroundWidth = info.backgroundBlob ? await pictureWidth(page, info.backgroundBlob) : null;
    return info;
};
const pictureWidth = (page, src) => page.evaluate(src => new Promise(resolve => {
    const image = new Image();
    image.onload = () => resolve(image.naturalWidth);
    image.onerror = () => resolve(0);
    image.src = src;
}), src);

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
    const rel = await imageLoads(page, frame, '#relative-img');
    assert(rel.complete && rel.naturalWidth === 120, `relative image not loaded: ${JSON.stringify(rel)}`);
    const abs = await imageLoads(page, frame, '#absolute-img');
    assert(abs.complete && abs.naturalWidth === 120, `absolute-path image not loaded: ${JSON.stringify(abs)}`);
    for (const selector of ['#inline-style-bg', '#sheet-bg', '#style-block-bg']) {
        const info = await imageLoads(page, frame, selector);
        assert(info.backgroundBlob && info.backgroundWidth === 16, `${selector} url() did not load: ${JSON.stringify(info)}`);
    }
    const svg = await imageLoads(page, frame, '#svg-image');
    assert(svg.svgHref?.startsWith('blob:'), `SVG <image> kept href ${svg.svgHref}`);
    const svgWidth = await pictureWidth(page, svg.svgHref);
    assert(svgWidth === 120, `SVG <image> target does not load (${svgWidth})`);
    const sheetLinked = await frame.evaluate(() => getComputedStyle(document.querySelector('.hero')).color);
    assert(sheetLinked === 'rgb(18, 52, 86)', `linked stylesheet not applied (${sheetLinked})`);
}));

await check('pictures without sizes, at 100%, in figures and as full-page covers keep a visible size on every page', async () => {
    const context = await browser.newContext(phone);
    const { page } = await openCheckPage(context, server.origin);
    try {
        const opened = await openBook(page, bookUrl(GUIDS.pictures), {});
        assert(opened.ok, `open failed: ${opened.code} ${opened.message}`);
        const sizes = [];
        for (const [index, name] of PICTURE_PAGES.entries()) {
            await page.evaluate(i => window.br.goTo(i), index);
            // A picture is "there" when it has loaded AND still has a real box: WebKit can load one and lay it out 0px high.
            const size = await waitFor(async () => {
                for (const frame of bookFrames(page)) {
                    try {
                        const found = await frame.evaluate(() => {
                            const el = document.querySelector('img, svg');
                            if (!el) return null;
                            const box = el.getBoundingClientRect();
                            return { w: Math.round(box.width), h: Math.round(box.height), loaded: el.tagName === 'IMG' ? el.complete && el.naturalWidth > 0 : true };
                        });
                        if (found?.loaded) return found;
                    } catch { /* frame navigating */ }
                }
                return null;
            }, { label: `the ${name} picture` });
            assert(size.w >= 10 && size.h >= 10, `${name} picture collapsed to ${size.w}x${size.h}`);
            sizes.push(`${name} ${size.w}x${size.h}`);
        }
        return sizes.join(', ');
    } finally {
        await context.close();
    }
});

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
    const info = await imageLoads(page, frame, '#missing-img');
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

await check('front matter (printed contents, wide table, pre, fixed width, oversize picture) never spills past its page column at 390, 820, 1280 and 1920', async () => {
    const report = [];
    for (const width of [390, 820, 1280, 1920]) {
        const context = await browser.newContext({ viewport: { width, height: width < 600 ? 844 : 900 }, hasTouch: width < 600, isMobile: width < 600, deviceScaleFactor: 1 });
        const { page } = await openCheckPage(context, server.origin);
        try {
            const opened = await openBook(page, bookUrl(GUIDS.frontMatter), {});
            assert(opened.ok, `open failed at ${width}: ${opened.code} ${opened.message}`);
            for (const [index, name] of FRONT_MATTER_PAGES.entries()) {
                await page.evaluate(i => window.br.goTo(i), index);
                await sleep(600);
                const found = await waitFor(async () => {
                    for (const frame of bookFrames(page)) {
                        try {
                            const text = await frame.evaluate(() => document.body?.textContent ?? '');
                            if (text.length > 20) {
                                return await frame.evaluate(() => {
                                    const root = document.documentElement;
                                    const columnWidth = parseFloat(getComputedStyle(root).columnWidth) || root.clientWidth;
                                    const gap = parseFloat(getComputedStyle(root).columnGap) || 0;
                                    const stride = columnWidth + gap;
                                    const offenders = [];
                                    for (const el of document.body.querySelectorAll('*')) {
                                        // A block that runs across columns has one rectangle per column; judge each piece.
                                        for (const box of el.getClientRects()) {
                                            if (box.width === 0 || box.height === 0) continue;
                                            const column = Math.floor((box.left - gap / 2 + 0.5) / stride);
                                            const columnEnd = gap / 2 + column * stride + columnWidth;
                                            if (box.right > columnEnd + 4 || box.width > columnWidth + 4) {
                                                offenders.push(`${el.tagName.toLowerCase()}${el.className ? '.' + String(el.className).split(' ')[0] : ''} +${Math.round(Math.max(box.right - columnEnd, box.width - columnWidth))}px`);
                                                break;
                                            }
                                        }
                                    }
                                    return { columnWidth: Math.round(columnWidth), offenders: offenders.slice(0, 6), count: offenders.length, title: document.title };
                                });
                            }
                        } catch { /* frame navigating */ }
                    }
                    return null;
                }, { label: `the ${name} page at ${width}` });
                assert(found.count === 0, `${name} at ${width}px (column ${found.columnWidth}px): ${found.count} element(s) spill: ${found.offenders.join(', ')}`);
            }
            report.push(String(width));
        } finally {
            await context.close();
        }
    }
    return `no spill at ${report.join(', ')}`;
});

console.log('Script sandbox on Apple devices');

await check('needsScriptSandbox is true only for Apple WebKit (iPhone, iPad, iPadOS as a Mac, Safari)', async () => {
    const context = await browser.newContext(desktop);
    const { page } = await openCheckPage(context, server.origin);
    const apple = 'Apple Computer, Inc.';
    const cases = [
        ['iPhone Safari', { userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1', vendor: apple, maxTouchPoints: 5 }, true],
        ['iPhone Chrome (CriOS)', { userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/126.0 Mobile/15E148 Safari/604.1', vendor: apple, maxTouchPoints: 5 }, true],
        ['iPad as iPad', { userAgent: 'Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1', vendor: apple, maxTouchPoints: 5 }, true],
        ['iPadOS reporting as a Mac', { userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15', vendor: apple, maxTouchPoints: 5 }, true],
        ['Mac Safari', { userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15', vendor: apple, maxTouchPoints: 0 }, true],
        ['Mac Chrome', { userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36', vendor: 'Google Inc.', maxTouchPoints: 0 }, false],
        ['Windows Chrome', { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36', vendor: 'Google Inc.', maxTouchPoints: 0 }, false],
        ['Windows Edge touch', { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0', vendor: 'Google Inc.', maxTouchPoints: 10 }, false],
        ['Firefox', { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:127.0) Gecko/20100101 Firefox/127.0', vendor: '', maxTouchPoints: 0 }, false],
        ['Android Chrome', { userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36', vendor: 'Google Inc.', maxTouchPoints: 5 }, false],
        ['Android WebView', { userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/126.0.0.0 Mobile Safari/537.36', vendor: 'Google Inc.', maxTouchPoints: 5 }, false],
        ['no navigator', {}, false],
    ];
    for (const [name, env, expected] of cases) {
        const got = await page.evaluate(e => window.br.needsScriptSandbox(e), env);
        assert(got === expected, `${name}: expected ${expected}, got ${got}`);
    }
    assert(await page.evaluate(() => window.br.bookFrameSandbox({ userAgent: 'iPhone', maxTouchPoints: 5 }) === 'allow-same-origin allow-scripts'), 'an iPhone frame is not allowed scripts');
    assert(await page.evaluate(() => window.br.bookFrameSandbox({ userAgent: 'Windows Chrome', vendor: 'Google Inc.' }) === 'allow-same-origin'), 'a Chrome frame is allowed scripts');
    await context.close();
    return `${cases.length} environments`;
});

await check('even with scripts allowed in the frame, a page that tries every trick runs nothing and reaches nothing outside', async () => {
    const context = await browser.newContext(desktop);
    const { page } = await openCheckPage(context, server.origin);
    outside.clearLog();
    const out = await page.evaluate(async trackerOrigin => {
        const tricks = `
            <script>window.__raw = 'inline';</script>
            <p id="click" onclick="window.__raw='onclick'">x</p>
            <img src="x:y" onerror="window.__raw='onerror'"/>
            <a id="jslink" href="javascript:window.__raw='jslink'">link</a>
            <iframe id="jsframe" src="javascript:window.__raw='jsframe'"></iframe>
            <iframe id="nested" src="${trackerOrigin}/frame.html"></iframe>
            <object id="obj" data="${trackerOrigin}/object.html"></object>
            <svg xmlns="http://www.w3.org/2000/svg"><script>window.__raw='svgscript'</script></svg>
            <script>
              // A page that deletes its own policy first, then tries to run and fetch.
              document.querySelector('meta[http-equiv="Content-Security-Policy"]')?.remove();
              window.__raw = 'after-csp-delete';
              fetch('${trackerOrigin}/fetch').catch(() => {});
              new Image().src = '${trackerOrigin}/image.png';
            </script>`;
        const html = `<html xmlns="http://www.w3.org/1999/xhtml"><head><meta http-equiv="Content-Security-Policy" content="${window.br.BOOK_CSP}"/></head><body>${tricks}</body></html>`;
        const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }));
        const iframe = document.createElement('iframe');
        // The Apple value: scripts allowed in the frame, so the page's own policy is the only barrier.
        iframe.setAttribute('sandbox', 'allow-same-origin allow-scripts');
        document.body.append(iframe);
        await new Promise(resolve => { iframe.onload = resolve; iframe.src = url; });
        iframe.contentDocument.getElementById('click')?.dispatchEvent(new MouseEvent('click'));
        iframe.contentDocument.getElementById('jslink')?.click();
        await new Promise(r => setTimeout(r, 700));
        const result = { ran: iframe.contentWindow.__raw ?? null };
        iframe.remove();
        return result;
    }, outside.origin);
    assert(out.ran === null, `a script ran: ${out.ran}`);
    assert(outside.log.length === 0, `the page reached the outside: ${JSON.stringify(outside.log)}`);
    await context.close();
    return 'nothing ran, nothing left the page';
});

await check('book links: outside links open safely, chapter links navigate', () => withBook(desktop, {}, async ({ page, context }) => {
    await page.evaluate(() => window.br.goTo(1));
    const frame = await frameWith(page, '#external-link');
    const popupPromise = context.waitForEvent('page', { timeout: 5000 });
    await frame.click('#external-link');
    const popup = await popupPromise;
    const target = popup.url() === 'about:blank' ? null : new URL(popup.url());
    assert(target === null || (target.protocol === 'https:' && target.hostname === 'example.org'), `popup went to ${popup.url()}`);
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
    const frameElement = await frame.frameElement();
    await page.evaluate(async el => {
        const win = el.contentWindow;
        const target = win.document.body;
        const fire = (type, x) => {
            const init = { identifier: 1, target, clientX: x, clientY: 420, screenX: x, screenY: 420 };
            // Desktop WebKit has no Touch constructor, so a plain event carrying the same touch lists stands in for it there.
            let touch = init;
            let real = false;
            try { touch = new win.Touch(init); real = typeof win.TouchEvent === 'function'; } catch { /* no usable Touch constructor */ }
            const list = type === 'touchend' ? [] : [touch];
            if (real) {
                target.dispatchEvent(new win.TouchEvent(type, { bubbles: true, cancelable: true, touches: list, targetTouches: list, changedTouches: [touch] }));
            } else {
                const event = new win.Event(type, { bubbles: true, cancelable: true });
                Object.assign(event, { touches: list, targetTouches: list, changedTouches: [touch] });
                target.dispatchEvent(event);
            }
        };
        fire('touchstart', 320);
        for (let x = 320; x >= 80; x -= 40) { fire('touchmove', x); await new Promise(r => setTimeout(r, 16)); }
        fire('touchend', 80);
    }, frameElement);
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

    // The frame itself must not be allowed to run scripts, whatever the clean-up did (defence in depth).
    // The frame's sandbox is chosen by the adapter: scripts are allowed only on Apple WebKit, where the page CSP and the clean-up are the barrier.
    const sandbox = await (await frame.frameElement()).getAttribute('sandbox');
    const expectedSandbox = browserName === 'webkit' ? 'allow-same-origin allow-scripts' : 'allow-same-origin';
    assert(sandbox === expectedSandbox, `${browserName} book frames use sandbox="${sandbox}", expected "${expectedSandbox}"`);
    const ran = await page.evaluate(async sandbox => {
        // Where scripts are allowed the page carries the book CSP, as every real book page does; without scripts the sandbox alone must stop it.
        const csp = /allow-scripts/.test(sandbox) ? `<head><meta http-equiv="Content-Security-Policy" content="${window.br.BOOK_CSP}"/></head>` : '';
        const html = `<html xmlns="http://www.w3.org/1999/xhtml">${csp}<body><p id="p" onclick="window.__raw='handler'">x</p><script>window.__raw='inline';</script><img src="x:y" onerror="window.__raw='onerror'"/></body></html>`;
        const url = URL.createObjectURL(new Blob([html], { type: 'application/xhtml+xml' }));
        const iframe = document.createElement('iframe');
        iframe.setAttribute('sandbox', sandbox);
        document.body.append(iframe);
        await new Promise(resolve => { iframe.onload = resolve; iframe.src = url; });
        iframe.contentDocument.getElementById('p').dispatchEvent(new MouseEvent('click'));
        await new Promise(r => setTimeout(r, 300));
        const result = iframe.contentWindow.__raw ?? null;
        iframe.remove();
        return result;
    }, sandbox);
    assert(ran === null, `an unhardened page ran script in the book frame sandbox: ${ran}`);

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

    // Media types with parameters / stray spaces, a stylesheet instruction (XSLT), DOCTYPE entities and a noscript trick.
    for (const [index, selector] of [[5, '#param1-text'], [6, '#param2-text'], [7, '#param3-text'], [8, '#param4-text']]) {
        await page.evaluate(i => window.br.goTo(i), index);
        await sleep(700);
        const f = await frameWith(page, selector);
        const doc = await f.evaluate(() => ({
            scripts: document.querySelectorAll('script').length,
            instructions: Array.from(document.childNodes).filter(n => n.nodeType === Node.PROCESSING_INSTRUCTION_NODE).length,
            doctype: !!document.doctype && document.doctype.internalSubset ? 1 : 0,
            refresh: document.querySelectorAll('meta[http-equiv="refresh" i]').length,
            noscript: document.querySelectorAll('noscript').length,
            csp: !!document.querySelector('meta[http-equiv="Content-Security-Policy"]'),
            svg: document.documentElement.localName === 'svg',
        }));
        assert(doc.scripts === 0 && doc.instructions === 0 && doc.doctype === 0 && doc.refresh === 0 && doc.noscript === 0, `${selector}: ${JSON.stringify(doc)}`);
        assert(doc.csp && !doc.svg, `${selector}: shown without a policy or as a bare SVG: ${JSON.stringify(doc)}`);
        seen.push(selector.slice(1));
    }

    const pwned = await pwnedEverywhere(page);
    assert(pwned.length === 0, `book code ran: ${pwned.join(' | ')}`);
    assert(outside.log.length === 0, `the book reached the outside: ${JSON.stringify(outside.log)}`);
    await context.close();
    return seen.join(', ');
});

await check('hardenSvg / hardenResource / hardenPage fail closed', async () => {
    const context = await browser.newContext(desktop);
    const { page } = await openCheckPage(context, server.origin);
    const out = await page.evaluate(async () => {
        const svgNs = 'xmlns="http://www.w3.org/2000/svg"';
        const bad = '<svg ' + svgNs + ' onload="x()"><script>window.__x=1</script><circle r="1"/></svg>';
        const xhtml = '<html xmlns="http://www.w3.org/1999/xhtml"><body><script>window.__x=1</script><p>hi</p></body></html>';
        const types = ['application/xhtml+xml;charset=utf-8', 'application/xhtml+xml ', ' Application/XHTML+XML', 'text/html;x=1', 'image/svg+xml;x=1', 'IMAGE/SVG+XML '];
        const parameterised = {};
        for (const type of types) {
            const body = /svg/i.test(type) ? bad : xhtml;
            const asPage = await window.br.hardenResource(new Blob([body], { type }), type, true);
            const asResource = await window.br.hardenResource(new Blob([body], { type }), type, false);
            parameterised[type] = { page: asPage.data, pageType: asPage.type, resource: asResource.data, resourceType: asResource.type };
        }
        return {
            normalized: window.br.normalizeType(' Application/XHTML+xml ; charset=UTF-8 '),
            broken: window.br.hardenSvg('<svg ' + svgNs + '><script>1</svg'),
            notSvg: window.br.hardenSvg('<html xmlns="http://www.w3.org/1999/xhtml"><body><script>1</script></body></html>'),
            clean: window.br.hardenSvg(bad),
            xml: await window.br.hardenResource('<x/>', 'text/xml'),
            xmlBlob: await window.br.hardenResource(new Blob(['<x/>']), 'application/xml; charset=utf-8'),
            xmlPage: await window.br.hardenResource('<x/>', 'text/xml', true),
            noType: await window.br.hardenResource(new Blob(['<x/>']), '', false),
            noTypePage: await window.br.hardenResource(new Blob(['<x/>']), undefined, true),
            image: await window.br.hardenResource(new Uint8Array([1, 2]), 'IMAGE/PNG; q=1'),
            font: await window.br.hardenResource(new Uint8Array([1]), 'font/woff2'),
            css: await window.br.hardenResource('a{}', 'text/css'),
            text: await window.br.hardenResource('hello', 'text/plain'),
            xsl: await window.br.hardenResource(new Blob(['<x/>']), 'application/xslt+xml'),
            parameterised,
            brokenXhtml: await window.br.hardenResource('<html><body><p>one<script>1</script></body>', 'application/xhtml+xml'),
            instructions: await window.br.hardenResource('<?xml version="1.0"?><?xml-stylesheet type="text/xsl" href="data:text/xml,x"?><!DOCTYPE html [<!ENTITY a "b">]><html xmlns="http://www.w3.org/1999/xhtml"><head></head><body><p>x</p></body></html>', 'application/xhtml+xml'),
            noscript: await window.br.hardenResource('<html><body><noscript><!--</noscript><meta http-equiv="refresh" content="0;url=http://e.example"/>--></noscript><xmp><meta http-equiv="refresh" content="0;url=http://e.example"/></xmp><p>ok</p></body></html>', 'text/html'),
            cssClean: window.br.hardenPage('<html xmlns="http://www.w3.org/1999/xhtml" style="background:u\\72l(http://e.example/esc.png)"><head style="background-image:\\75rl(\'http://e.example/esc2.png\')"><style>@import url(https://e.example/x.css); a{background:url(https://e.example/y.png)} b{background:url(blob:http://a/1)}</style></head><body style="background:url(http://e.example/z)"><meta http-equiv="refresh" content="0;url=http://e.example"/><iframe src="blob:http://a/2"></iframe></body></html>', 'application/xhtml+xml'),
        };
    });
    assert(out.normalized === 'application/xhtml+xml', `normalizeType: ${out.normalized}`);
    assert(out.broken === null && out.notSvg === null, 'a broken or non-SVG file was let through');
    assert(out.clean && !/script|onload/.test(out.clean) && /circle/.test(out.clean), `clean svg: ${out.clean}`);
    assert(out.xml.type === 'application/octet-stream' && out.xmlBlob.type === 'application/octet-stream' && /safely/.test(out.xmlPage.data), 'an XML-typed document was let through');
    assert(out.xsl.type === 'application/octet-stream', `an XSLT document kept its type: ${out.xsl.type}`);
    assert(out.noType.data === '' && out.noType.type === 'application/octet-stream' && /safely/.test(out.noTypePage.data), 'a missing type was let through');
    assert(out.image.type === 'image/png' && out.image.data.length === 2 && out.font.type === 'font/woff2' && out.css.data === 'a{}', 'ordinary resources were altered');
    assert(out.text.type === 'application/octet-stream', `text/plain kept its type: ${out.text.type}`);
    // Every spelling of a page type must be cleaned and re-typed plainly, as a page and as a resource.
    for (const [type, v] of Object.entries(out.parameterised)) {
        assert(!/<script|onload/i.test(v.page) && !/<script|onload/i.test(v.resource), `${JSON.stringify(type)} kept a script`);
        assert(!/[;\s]/.test(v.pageType.trim()) && !/;/.test(v.resourceType), `${JSON.stringify(type)} kept a parameter: ${v.pageType} / ${v.resourceType}`);
        assert(/Content-Security-Policy/.test(v.page), `${JSON.stringify(type)} page has no policy`);
    }
    assert(out.brokenXhtml.type === 'text/html' && !/<script/i.test(out.brokenXhtml.data) && /one/.test(out.brokenXhtml.data), `broken xhtml: ${JSON.stringify(out.brokenXhtml)}`);
    assert(!/<\?|DOCTYPE|ENTITY|xsl/i.test(out.instructions.data.replace(/^<\?xml[^>]*\?>/, '')), `instructions or doctype remain: ${out.instructions.data}`);
    assert(!/noscript|xmp|refresh/i.test(out.noscript.data), `noscript/xmp/refresh remain: ${out.noscript.data}`);
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

console.log('Highlights, contents and settings');

// Selects the first words of the first paragraph after the chapter heading, the way a person's drag would.
const selectWords = frame => frame.evaluate(() => {
    const p = document.querySelector('#start ~ p');
    const node = p.firstChild;
    const range = document.createRange();
    range.setStart(node, 0);
    range.setEnd(node, Math.min(24, node.length));
    const selection = getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    return range.toString();
});
const events = (page, name) => page.evaluate(name => window.events.filter(e => e[0] === name), name);
const centreOf = e => [e[3] + e[5] / 2, e[4] + e[6] / 2];

await check('selecting text reports a CFI range, the text and where it is; collapsing it reports the end', () => withBook(desktop, {}, async ({ page }) => {
    await page.evaluate(() => window.br.goTo(2));
    const frame = await frameWith(page, '#start');
    const chosen = await selectWords(frame);
    const [, cfi, text, x, y, w, h] = await waitFor(async () => (await events(page, 'OnSelection')).at(-1), { label: 'a selection report' });
    assert(cfi.startsWith('epubcfi(') && cfi.includes(','), `not a CFI range: ${cfi}`);
    assert(text === chosen.replace(/\s+/g, ' ').trim(), `text differs: "${text}" vs "${chosen}"`);
    assert(w > 20 && h > 5 && x > 0 && y > 0 && x < 1280 && y < 800, `odd box ${[x, y, w, h]}`);
    assert((await page.evaluate(() => window.br.getSelection()))?.cfi === cfi, 'getSelection disagrees');
    await frame.evaluate(() => getSelection().removeAllRanges());
    await waitFor(async () => (await events(page, 'OnSelectionCleared')).length > 0, { label: 'the selection to clear' });
    assert((await page.evaluate(() => window.br.getSelection())) === null, 'selection still held');
}));

await check('a highlight is drawn on its words, taps on it report it (and do not turn the page), and it survives a text-size change', () => withBook(desktop, {}, async ({ page }) => {
    await page.evaluate(() => window.br.goTo(2));
    const frame = await frameWith(page, '#start');
    await selectWords(frame);
    const selection = await waitFor(async () => (await events(page, 'OnSelection')).at(-1), { label: 'a selection report' });
    const cfi = selection[1];
    await page.evaluate(cfi => { window.br.addHighlight({ id: 'h1', cfi, color: '#EAB308' }); window.br.clearSelection(); }, cfi);
    await sleep(400);
    const [cx, cy] = centreOf(selection);
    let relocations = await relocationCount(page);
    await page.mouse.click(cx, cy);
    const tapped = await waitFor(async () => (await events(page, 'OnHighlightTapped')).at(-1), { label: 'a highlight tap' });
    assert(tapped[1] === 'h1', `tap reported ${tapped[1]}`);
    await sleep(500);
    assert((await relocationCount(page)) === relocations, 'tapping a highlight turned the page');
    assert((await events(page, 'OnToggleChrome')).length === 0, 'tapping a highlight toggled the bars');

    // Bigger text moves everything; the highlight must still sit on the same words.
    await page.evaluate(() => window.br.setAppearance({ theme: 'light', fontSize: 28, lineHeight: 1.6 }));
    await sleep(400);
    await page.evaluate(cfi => window.br.goTo(cfi), cfi);
    await sleep(800);
    const where = await frame.evaluate(() => {
        const r = document.querySelector('#start ~ p').firstChild;
        const range = document.createRange();
        range.setStart(r, 0); range.setEnd(r, 24);
        const rect = range.getClientRects()[0];
        const box = window.frameElement.getBoundingClientRect();
        return [box.left + rect.left + rect.width / 2, box.top + rect.top + rect.height / 2];
    });
    const before = (await events(page, 'OnHighlightTapped')).length;
    await page.mouse.click(where[0], where[1]);
    await waitFor(async () => (await events(page, 'OnHighlightTapped')).length > before, { label: 'the highlight to answer after the text got bigger' });
}));

await check('recolouring and removing a highlight work; a position the book cannot place is skipped quietly', () => withBook(desktop, {}, async ({ page, consoleMessages }) => {
    await page.evaluate(() => window.br.goTo(2));
    const frame = await frameWith(page, '#start');
    await selectWords(frame);
    const selection = await waitFor(async () => (await events(page, 'OnSelection')).at(-1), { label: 'a selection report' });
    await page.evaluate(cfi => {
        window.br.setHighlights([{ id: 'good', cfi, color: '#60A5FA' }, { id: 'bad', cfi: 'epubcfi(/6/999!/4/2)', color: '#EAB308' }, { id: 'worse', cfi: 'not a cfi', color: '#EAB308' }]);
    }, selection[1]);
    await frame.evaluate(() => getSelection().removeAllRanges());
    await sleep(400);
    await page.evaluate(() => window.br.recolorHighlight('good', '#F472B6'));
    await sleep(200);
    const [cx, cy] = centreOf(selection);
    await page.mouse.click(cx, cy);
    await waitFor(async () => (await events(page, 'OnHighlightTapped')).at(-1)?.[1] === 'good', { label: 'the recoloured highlight' });
    await page.evaluate(() => window.br.removeHighlight('good'));
    await sleep(400);
    const before = (await events(page, 'OnHighlightTapped')).length;
    await page.mouse.click(cx, cy);
    await sleep(600);
    assert((await events(page, 'OnHighlightTapped')).length === before, 'a removed highlight still answers taps');
    assert(!consoleMessages.some(m => m.type === 'pageerror'), 'the page raised an error');
}));

await check('the contents list is flattened and an entry opens its chapter', () => withBook(desktop, {}, async ({ page }) => {
    const toc = await page.evaluate(() => window.br.getToc());
    assert(toc.length === 4 && toc.every(t => t.label && t.href && t.level === 0), `unexpected contents ${JSON.stringify(toc)}`);
    const count = await relocationCount(page);
    await page.evaluate(href => window.br.goToHref(href), toc[2].href);
    await frameWith(page, '#start');
    await nextRelocation(page, count);
}));

await check('settings are remembered per device, other stored keys survive, and flow/width/font apply', () => withBook(desktop, {}, async ({ page }) => {
    const saved = await page.evaluate(() => {
        localStorage.setItem('tuvima-reader-settings', JSON.stringify({ Theme: 'light', Margins: 48, FontFamily: 'Merriweather' }));
        window.br.saveSettings({ theme: 'sepia', fontSize: 22, lineHeight: 1.8, font: 'serif', pageWidth: 'wide', flow: 'scrolled' });
        return { stored: JSON.parse(localStorage.getItem('tuvima-reader-settings')), loaded: window.br.loadSettings() };
    });
    assert(saved.stored.Margins === 48 && saved.stored.FontFamily === 'Merriweather', 'the current reader\'s keys were lost');
    assert(JSON.stringify(sortedKeys(saved.loaded)) === JSON.stringify(sortedKeys({ theme: 'sepia', fontSize: 22, lineHeight: 1.8, font: 'serif', pageWidth: 'wide', flow: 'scrolled' })), `loaded ${JSON.stringify(saved.loaded)}`);
    await page.evaluate(() => window.br.setAppearance({ theme: 'dark', fontSize: 20, lineHeight: 1.6, font: 'sans', pageWidth: 'narrow', flow: 'scrolled' }));
    const attrs = await page.evaluate(() => {
        const r = document.querySelector('foliate-view').renderer;
        return { flow: r.getAttribute('flow'), width: r.getAttribute('max-inline-size') };
    });
    assert(attrs.flow === 'scrolled' && attrs.width === '560px', `renderer ${JSON.stringify(attrs)}`);
    await page.evaluate(() => window.br.goTo(2));
    const frame = await frameWith(page, '#start');
    const family = await frame.evaluate(() => getComputedStyle(document.querySelector('#start ~ p')).fontFamily);
    assert(/system-ui|Segoe|Roboto|sans-serif/.test(family), `font was ${family}`);
    await page.evaluate(() => window.br.setAppearance({ theme: 'light', fontSize: 20, lineHeight: 1.6, font: 'book', pageWidth: 'standard', flow: 'paginated' }));
    const back = await page.evaluate(() => document.querySelector('foliate-view').renderer.getAttribute('flow'));
    assert(back === 'paginated', 'flow did not return to pages');
    const garbage = await page.evaluate(() => {
        localStorage.setItem('tuvima-reader-settings', JSON.stringify({ ReaderFont: 'comic-sans', PageWidth: 'huge', Flow: 'sideways', FontSize: -3 }));
        return window.br.loadSettings();
    });
    assert(Object.keys(garbage).length === 0, `bad stored values were accepted: ${JSON.stringify(garbage)}`);
}));

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
