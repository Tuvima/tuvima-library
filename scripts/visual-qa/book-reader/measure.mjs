// Size and speed measurements for the book reader (BR2 gate): how long a book takes to open, how many bytes
// of it are fetched, and what 30 page turns cost, on an emulated phone (small screen, 4x slower CPU,
// throttled network). Run epub-fixtures.mjs first.
//
//   node scripts/visual-qa/book-reader/measure.mjs [--network fast4g|slow4g] [--cpu 4] [--books 5,50,200,fixed]
//
// Prints a table and a JSON blob. The browser here is Chromium; see the notes in the plan about WebKit/Android WebView.

import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { startServer } from './serve.mjs';
import { GUIDS, bookUrl, fixturesDir, frameWith, loadPlaywright, nextRelocation, openBook, openCheckPage, relocationCount, sleep, waitFor } from './harness.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, arg, i, all) => (arg.startsWith('--') ? [...pairs, [arg.slice(2), all[i + 1]]] : pairs), []));
const NETWORKS = {
    fast4g: { label: 'Fast 4G (9 Mbps, 170 ms)', latency: 170, down: 9_000_000 / 8, up: 1_500_000 / 8 },
    slow4g: { label: 'Slow 4G (1.6 Mbps, 150 ms)', latency: 150, down: 1_600_000 / 8, up: 750_000 / 8 },
};
const network = NETWORKS[args.network ?? 'fast4g'];
const cpu = Number(args.cpu ?? 4);
const wanted = (args.books ?? '5,50,200,fixed').split(',');
const dir = fixturesDir();

const books = {
    5: { guid: GUIDS.big5, file: 'big-5mb.epub', name: '5 MB (10 chapters)', jump: 8 },
    50: { guid: GUIDS.big50, file: 'big-50mb.epub', name: '50 MB (98 chapters)', jump: 88 },
    200: { guid: GUIDS.big200, file: 'big-200mb.epub', name: '200 MB (394 chapters)', jump: 354 },
    fixed: { guid: '00000000-0000-4000-8000-0000000000f1', file: 'fixed-layout.epub', name: 'Fixed layout, 40 large pages (~25 MB)', jump: 38 },
};

const rssMB = () => {
    try {
        const out = execFileSync('ps', ['-eo', 'rss,args'], { encoding: 'utf8' });
        let kb = 0;
        for (const line of out.split('\n')) if (/chrom/i.test(line) && /--type=renderer/.test(line)) kb += Number(line.trim().split(/\s+/)[0]) || 0;
        return Math.round(kb / 1024);
    } catch { return null; }
};

const server = await startServer({ books: Object.fromEntries(wanted.map(k => [books[k].guid, path.join(dir, books[k].file)])) });
const { chromium } = loadPlaywright();
const browser = await chromium.launch();
const rows = [];

for (const key of wanted) {
    const book = books[key];
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, hasTouch: true, isMobile: true });
    const { page } = await openCheckPage(context, server.origin);
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: network.latency, downloadThroughput: network.down, uploadThroughput: network.up });
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: cpu });
    await cdp.send('Performance.enable');
    const heap = async () => Math.round((await cdp.send('Performance.getMetrics')).metrics.find(m => m.name === 'JSHeapUsedSize').value / 1048576);

    server.clearLog();
    const rssBefore = rssMB();
    const started = Date.now();
    const result = await openBook(page, bookUrl(book.guid), { theme: 'light', fontSize: 18, lineHeight: 1.6 });
    const openMs = Date.now() - started;
    if (!result.ok) { console.log(`${book.name}: open failed (${result.code}: ${result.message})`); await context.close(); continue; }
    await waitFor(() => page.evaluate(() => window.br.getLocation()?.cfi), { label: 'first page', timeout: 60000 });
    const firstPageMs = Date.now() - started;
    const afterOpen = { requests: result.stats.requests, mb: result.stats.bytes / 1048576 };

    // 30 page turns (long chapters: this walks across several chapters, each with a ~500 KB picture).
    const turns = [];
    const turnKey = result.layout === 'fixed' ? 'ArrowRight' : 'ArrowRight';
    for (let i = 0; i < 30; i++) {
        const count = await relocationCount(page);
        const t0 = Date.now();
        await page.keyboard.press(turnKey);
        await nextRelocation(page, count, 60000);
        turns.push(Date.now() - t0 - 250); // the adapter reports 250 ms after the page settles
    }
    const afterTurns = await page.evaluate(() => window.br.getStats());
    await sleep(400);
    const heapMB = await heap();
    const rssAfter = rssMB();

    // Jump deep into the book (table of contents / slider use): how much more is fetched?
    const beforeJump = (await page.evaluate(() => window.br.getStats())).bytes;
    const j0 = Date.now();
    const jumpCount = await relocationCount(page);
    await page.evaluate(i => window.br.goTo(i), book.jump);
    await nextRelocation(page, jumpCount, 60000);
    const jumpMs = Date.now() - j0 - 250;
    const afterJump = await page.evaluate(() => window.br.getStats());

    const fileMB = result.stats.fileSize / 1048576;
    const wholeFileSeconds = (result.stats.fileSize / network.down) + network.latency / 1000;
    turns.sort((a, b) => a - b);
    rows.push({
        book: book.name,
        fileMB: +fileMB.toFixed(1),
        openSeconds: +(openMs / 1000).toFixed(2),
        firstPageSeconds: +(firstPageMs / 1000).toFixed(2),
        openRequests: afterOpen.requests,
        openFetchedMB: +afterOpen.mb.toFixed(2),
        wholeFileDownloadSeconds: +wholeFileSeconds.toFixed(1),
        turnMedianMs: turns[15], turnP95Ms: turns[28],
        after30TurnsRequests: afterTurns.requests, after30TurnsFetchedMB: +(afterTurns.bytes / 1048576).toFixed(2),
        jumpSeconds: +(jumpMs / 1000).toFixed(2), jumpFetchedMB: +((afterJump.bytes - beforeJump) / 1048576).toFixed(2),
        fetchedPercentOfFile: +((afterJump.bytes / result.stats.fileSize) * 100).toFixed(1),
        jsHeapMB: await heap(), jsHeapAfterTurnsMB: heapMB,
        rendererRssDeltaMB: rssBefore !== null && rssAfter !== null ? rssAfter - rssBefore : null,
        entries: result.stats.entries,
        serverRequests: server.log.filter(e => e.path.startsWith('/engine-book')).length,
    });
    console.log(JSON.stringify(rows.at(-1)));
    await context.close();
}

console.log(`\nProfile: 390x844 @2x, ${cpu}x CPU slowdown, ${network.label}, Chromium ${browser.version()}`);
console.table(rows);
await browser.close();
await server.close();
