#!/usr/bin/env node
// Capture-DashboardScreens.mjs: screenshots of a running Dashboard at several widths.
//
// This script only drives a browser (Playwright, Chromium). It never starts or stops the apps.
// Follow .claude/rules/one-engine-at-a-time.md: claim the Engine slot, use a temporary data folder
// and a small sample library, and stop both apps when done.
//
// Start sequence that works on a Linux cloud machine (from the repo root, after
// `dotnet build src/MediaEngine.Api` and `dotnet build src/MediaEngine.Web`):
//
//   V=/tmp/visual-check                       # any empty temporary folder
//   mkdir -p $V/config $V/data $V/keys && cp -r config/. $V/config/
//   dotnet src/MediaEngine.Api/bin/Debug/net10.0/MediaEngine.Api.dll --generate-baseline-samples $V/samples-full
//   # copy a few files per kind into $V/samples/{books,comics,movies,tv,music,audiobooks}
//
//   # Known limit (trial of 2026-11-11): where metadata providers cannot be reached, files end as
//   # "unmatched" in Needs Review and Home, Read, Watch and Listen stay empty, so no detail page is
//   # captured. Populated pages need reachable providers or recorded provider answers
//   # (TUVIMA_PROVIDER_ANSWERS_MODE / TUVIMA_PROVIDER_ANSWERS_DIR). Films, TV and comics are also held
//   # until tmdb.json / comicvine.json in $V/config/providers have a key.
//
//   export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
//   export TUVIMA_CONFIG_DIR=$V/config TUVIMA_DATA_DIR=$V/data TUVIMA_DB_PATH=$V/data/library.db
//   export TUVIMA_DATA_PROTECTION_DIR=$V/keys TUVIMA_ENGINE_URL=http://localhost:61595
//   dotnet run --project src/MediaEngine.Api --no-build --no-launch-profile --urls http://localhost:61595 &
//   #   wait for http://localhost:61595/health/live = Healthy
//   dotnet run --project src/MediaEngine.Web --no-build --no-launch-profile --urls http://localhost:5116 &
//   #   wait for http://localhost:5116/health/ready = healthy
//
//   node tools/Capture-DashboardScreens.mjs --dashboard http://localhost:5116 --out .tmp/visual-check \
//        --setup --samples $V/samples
//
// Options:
//   --dashboard <url>   Dashboard address (default http://localhost:5116)
//   --out <folder>      Output folder for <page>-<width>.png (default .tmp/visual-check)
//   --setup             Run first-time setup if the Dashboard shows it (owner "name only" profile, no Vault,
//                       no metadata keys, one Read-only library per sample folder)
//   --samples <folder>  Sample root with books/ comics/ movies/ tv/ music/ audiobooks/ (needed with --setup);
//                       the path must be readable by the Engine
//   --name <text>       Owner name for setup (default "Shy")
//   --wait <seconds>    Longest wait for the first items to appear on Home (default 180)
//   --pages <list>      Comma list of page keys to capture (default: all)
//   --widths <list>     Comma list from 1024,1280,1440,phone (default: all)
//
// Playwright must already be installed (`require('playwright')`); nothing is downloaded.

import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const args = process.argv.slice(2);
const flag = (name) => args.includes(`--${name}`);
const option = (name, fallback) => {
    const index = args.indexOf(`--${name}`);
    return index >= 0 && index + 1 < args.length ? args[index + 1] : fallback;
};

const dashboard = option('dashboard', 'http://localhost:5116').replace(/\/+$/, '');
const outDir = path.resolve(option('out', '.tmp/visual-check'));
const samples = option('samples', null);
const ownerName = option('name', 'Shy');
const waitSeconds = Number(option('wait', '180'));

const viewports = {
    1024: { viewport: { width: 1024, height: 900 }, deviceScaleFactor: 1 },
    1280: { viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1 },
    1440: { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 },
    phone: { viewport: { width: 390, height: 844 }, deviceScaleFactor: 1, isMobile: true, hasTouch: true },
};

// "detail" is resolved at run time from the first item link found on the browse pages.
const pages = [
    ['home', '/'],
    ['read', '/read'],
    ['watch', '/watch'],
    ['listen', '/listen'],
    ['collections', '/collections'],
    ['detail', null],
    ['settings-libraries', '/settings/libraries'],
    ['settings-activity', '/settings/overview/activity'],
];

const sampleLibraries = [
    ['books', /^Read/, /^Books/],
    ['comics', /^Read/, /^Comics/],
    ['movies', /^Watch/, /^Movies/],
    ['tv', /^Watch/, /^TV/],
    ['music', /^Listen/, /^Music/],
    ['audiobooks', /^Listen/, /^Audiobooks/],
];

const wantedPages = option('pages', pages.map(([key]) => key).join(',')).split(',');
const wantedWidths = option('widths', Object.keys(viewports).join(',')).split(',');

async function open(page, route) {
    await page.goto(dashboard + route, { waitUntil: 'networkidle' });
    await page.waitForTimeout(2500); // Blazor Server renders after the circuit connects
}

async function clickIfPresent(page, name, timeout = 3000) {
    try {
        await page.getByRole('button', { name, exact: true }).first().click({ timeout });
        await page.waitForTimeout(1200);
        return true;
    } catch {
        return false; // the step is optional (the intro and the Vault offer are not always shown)
    }
}

async function click(scope, name) {
    await scope.getByRole('button', { name }).first().click({ timeout: 15000 });
    await scope.page().waitForTimeout(1200);
}

async function runSetup(page) {
    console.log('setup: running first-time setup');
    await clickIfPresent(page, 'Get started');

    // 1. Your Library
    await page.getByLabel('What should we call this Library?').first().fill('Trial Library');
    await click(page.locator('body'), /^Continue$/);

    // 2. Your profile ("Just my name" is the default choice and signs this browser in)
    await page.getByLabel('Your name').first().fill(ownerName);
    await click(page.locator('body'), /^Create profile$/);

    // 3. Tools (optional downloads carry on in the background)
    await click(page.locator('body'), /^Continue$/);

    // Optional Vault offer
    await clickIfPresent(page, 'Not now');

    // 4. Libraries: one Read-only library per sample folder
    for (const [folder, area, kind] of sampleLibraries) {
        const folderPath = path.join(path.resolve(samples), folder);
        if (!fs.existsSync(folderPath)) continue;
        await click(page.locator('body'), /^Add library$/);
        const dialog = page.locator('[role=dialog]');
        await click(dialog, area);
        await click(dialog, /^Next$/);
        await click(dialog, kind);
        await click(dialog, /^Next$/);
        await click(dialog, /^Type a path/);
        await dialog.locator('input[aria-label="Folder path"], input[placeholder="Folder path"]').first().fill(folderPath);
        await click(dialog, /^Go$/);
        await click(dialog, /^Read-only/);
        await click(dialog, /^Add library$/);
        await page.waitForTimeout(2000);
        await click(dialog, /^Done$/);
        console.log(`setup: added ${folder}`);
    }
    await click(page.locator('body'), /^Continue$/);

    // 5. Metadata: no keys
    await click(page.locator('body'), /^Skip for now$/);

    // 6. All set
    await page.waitForTimeout(3000);
    await click(page.locator('body'), /^Open my library$/);
    await page.waitForURL((url) => !url.pathname.startsWith('/setup'), { timeout: 30000 });
    await page.waitForTimeout(3000);
}

async function signInIfAsked(page) {
    // With a "name only" owner a new browser on this computer is let straight in or shown the profile picker.
    if (/\/(auth\/login|who)/.test(new URL(page.url()).pathname)) {
        await clickIfPresent(page, new RegExp(ownerName), 8000);
        await page.waitForTimeout(3000);
    }
    if (/\/(auth\/login|who|setup)/.test(new URL(page.url()).pathname)) {
        throw new Error(`Not signed in: the Dashboard stayed on ${page.url()}. Run with --setup, or sign in by hand first.`);
    }
}

async function waitForItems(page) {
    const deadline = Date.now() + waitSeconds * 1000;
    for (;;) {
        await open(page, '/');
        const count = await page.locator('a[href^="/details/"]').count();
        if (count > 0) {
            console.log(`home: ${count} item links`);
            return;
        }
        if (Date.now() > deadline) {
            console.log('home: no items appeared in time; capturing anyway');
            return;
        }
        await page.waitForTimeout(10000);
    }
}

async function findDetailRoute(page) {
    for (const route of ['/', '/read/books', '/watch/movies', '/listen/music', '/read', '/watch', '/listen']) {
        await open(page, route);
        const href = await page.locator('a[href^="/details/"]').first().getAttribute('href', { timeout: 2000 }).catch(() => null);
        if (href) return href;
    }
    return null;
}

const browser = await chromium.launch();
try {
    fs.mkdirSync(outDir, { recursive: true });
    const started = Date.now();

    // One desktop session does setup/sign-in; its cookies are reused for every width.
    const first = await browser.newContext(viewports[1280]);
    const page = await first.newPage();
    await open(page, '/');
    if (new URL(page.url()).pathname.startsWith('/setup')) {
        if (!flag('setup') || !samples) {
            throw new Error('The Dashboard is showing first-time setup. Pass --setup --samples <folder>.');
        }
        await runSetup(page);
    }
    await signInIfAsked(page);
    await waitForItems(page);
    const detailRoute = await findDetailRoute(page);
    if (!detailRoute) console.log('detail: no item link found; the detail page is skipped');
    const state = await first.storageState();
    await first.close();

    const written = [];
    for (const width of wantedWidths) {
        if (!viewports[width]) throw new Error(`Unknown width "${width}"`);
        const context = await browser.newContext({ ...viewports[width], storageState: state });
        const shot = await context.newPage();
        for (const [key, route] of pages) {
            if (!wantedPages.includes(key)) continue;
            const target = route ?? detailRoute;
            if (!target) continue;
            await open(shot, target);
            const file = path.join(outDir, `${key}-${width}.png`);
            await shot.screenshot({ path: file });
            written.push(file);
            console.log(`${file}  (${shot.url()})`);
        }
        await context.close();
    }
    console.log(`done: ${written.length} screenshots in ${Math.round((Date.now() - started) / 1000)}s`);
} finally {
    await browser.close();
}
