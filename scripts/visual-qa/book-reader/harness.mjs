// Shared helpers for the book reader browser checks (Playwright + the stand-in Dashboard server).
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);

export function loadPlaywright() {
    const candidates = [process.env.PLAYWRIGHT_MODULE, 'playwright', '/opt/node22/lib/node_modules/playwright'].filter(Boolean);
    for (const candidate of candidates) {
        try { return require(candidate); } catch { /* try the next place */ }
    }
    throw new Error('Playwright was not found. Install it (npm i -g playwright) or set PLAYWRIGHT_MODULE to its folder.');
}

export const GUIDS = {
    regression: '00000000-0000-4000-8000-000000000001',
    big5: '00000000-0000-4000-8000-000000000005',
    big50: '00000000-0000-4000-8000-000000000050',
    big200: '00000000-0000-4000-8000-000000000200',
    notABook: '00000000-0000-4000-8000-0000000000aa',
};

export const bookUrl = id => `/engine-book/${id}/file`;

export function ensureDir(dir) {
    fs.mkdirSync(dir, { recursive: true });
    return dir;
}

export const fixturesDir = () => path.resolve(process.env.BOOK_READER_FIXTURES ?? '.tmp/book-reader-fixtures');

/** Opens the check page, loads the adapter and wires a fake .NET side that records what the script reports. */
export async function openCheckPage(context, origin) {
    const page = await context.newPage();
    const consoleMessages = [];
    page.on('console', message => consoleMessages.push({ type: message.type(), text: message.text() }));
    page.on('pageerror', error => consoleMessages.push({ type: 'pageerror', text: String(error) }));
    await page.goto(`${origin}/check.html`);
    await page.evaluate(async () => {
        window.br = await import('/js/book-reader.js');
        window.events = [];
        window.dotNet = { invokeMethodAsync: (method, ...args) => { window.events.push([method, ...args]); return Promise.resolve(); } };
    });
    return { page, consoleMessages };
}

export const openBook = (page, url, options = {}) => page.evaluate(
    ({ url, options }) => window.br.open(document.getElementById('host'), url, options, window.dotNet),
    { url, options });

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export async function waitFor(fn, { timeout = 8000, interval = 50, label = 'condition' } = {}) {
    const end = Date.now() + timeout;
    let last;
    while (Date.now() < end) {
        last = await fn();
        if (last) return last;
        await sleep(interval);
    }
    throw new Error(`Timed out waiting for ${label}`);
}

/** The visible book frame (a blob: iframe inside the library's closed shadow root). */
export const bookFrames = page => page.frames().filter(frame => frame.url().startsWith('blob:'));

export async function frameWith(page, selector, timeout = 8000) {
    return waitFor(async () => {
        for (const frame of bookFrames(page)) {
            try { if (await frame.$(selector)) return frame; } catch { /* frame navigating */ }
        }
        return null;
    }, { timeout, label: `frame with ${selector}` });
}

export const lastRelocation = page => page.evaluate(() => window.events.filter(e => e[0] === 'OnRelocated').at(-1) ?? null);
export const relocationCount = page => page.evaluate(() => window.events.filter(e => e[0] === 'OnRelocated').length);

/** Waits for the next debounced relocation after `count` earlier ones and returns it. */
export async function nextRelocation(page, count, timeout = 8000) {
    await waitFor(async () => (await relocationCount(page)) > count, { label: 'a page change', timeout });
    return lastRelocation(page);
}
