#!/usr/bin/env node
// Check-PhoneStandards.mjs: the render half of the automatic phone check (Look v3.2 D12).
// Opens pages that render without media at 390 and 768 and fails when a page has a touch target under 44 px
// (48 on the bottom dock), sideways overflow, text under 12 px, or "on a computer" wording outside setup.
// The static half (hover-only reveals, text sizes in CSS, wording in source) runs in PhoneStandardsGuardrailTests.
//
// It only drives a browser (Playwright, already installed); it never starts or stops the apps. Follow
// .claude/rules/one-engine-at-a-time.md: claim the Engine slot, use a temporary data folder and a small library.
//
//   node tools/Check-PhoneStandards.mjs --dashboard http://localhost:5116 [--storage state.json] [--pages /settings,/settings/plugins] [--out .tmp/phone-check]
//
// --storage is a Playwright storage-state file from a signed-in browser (Capture-DashboardScreens.mjs can write one with --setup).
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const args = process.argv.slice(2);
const option = (name, fallback) => { const i = args.indexOf(`--${name}`); return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback; };
const dashboard = option('dashboard', 'http://localhost:5116').replace(/\/$/, '');
const storage = option('storage', null);
const outDir = option('out', '.tmp/phone-check');
const pages = (option('pages', '/,/search,/mylist,/settings,/settings/libraries,/settings/plugins,/settings/profiles,/settings/security,/settings/playback,/settings/network')).split(',');
const widths = [390, 768];

const audit = fs.readFileSync(new URL('./validate-touch-targets.js', import.meta.url), 'utf8');
const extra = () => {
    const small = [];
    const wording = /on a computer|computer or tablet|use a computer/i;
    for (const el of document.querySelectorAll('body *')) {
        if (!el.childNodes.length || ![...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim())) continue;
        const style = getComputedStyle(el), rect = el.getBoundingClientRect();
        if (rect.width === 0 || rect.height === 0 || style.visibility === 'hidden' || style.display === 'none') continue;
        if (parseFloat(style.fontSize) < 12) small.push(`${el.tagName.toLowerCase()} ${style.fontSize} "${el.textContent.trim().slice(0, 30)}"`);
    }
    const computerText = wording.test(document.body.innerText) && !location.pathname.startsWith('/setup');
    return { small: [...new Set(small)].slice(0, 30), computerText };
};

const browser = await chromium.launch();
let failed = false;
const report = [];
try {
    for (const width of widths) {
        const context = await browser.newContext({
            viewport: { width, height: width === 390 ? 844 : 1024 },
            isMobile: true, hasTouch: true, deviceScaleFactor: 1,
            ...(storage ? { storageState: storage } : {}),
        });
        const page = await context.newPage();
        for (const route of pages) {
            await page.goto(dashboard + route, { waitUntil: 'networkidle' }).catch(() => {});
            await page.waitForTimeout(1500);
            const touch = await page.evaluate(`(${audit})()`);
            const more = await page.evaluate(extra);
            const problems = [];
            if (touch.undersizedCount) problems.push(`${touch.undersizedCount} targets under 44: ${touch.undersized.slice(0, 8).join(' | ')}`);
            if (touch.widerThanViewportCount) problems.push(`sideways overflow: ${touch.widerThanViewport.slice(0, 5).join(' | ')}`);
            if (more.small.length) problems.push(`text under 12 px: ${more.small.slice(0, 5).join(' | ')}`);
            if (more.computerText) problems.push('"on a computer" wording');
            report.push({ width, route, ok: problems.length === 0, problems });
            console.log(`${problems.length ? 'FAIL' : 'ok  '} ${width} ${route}${problems.length ? '\n     ' + problems.join('\n     ') : ''}`);
            if (problems.length) failed = true;
        }
        await context.close();
    }
} finally {
    await browser.close();
}
fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, 'report.json'), JSON.stringify(report, null, 2));
process.exit(failed ? 1 : 0);
