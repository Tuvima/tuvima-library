// Drives one upload in headless Chromium and prints a JSON line of measurements.
import { chromium } from '/opt/node-tools/node_modules/playwright/index.mjs';
import { execSync } from 'node:child_process';
const a = Object.fromEntries(process.argv.slice(2).map(x => x.replace(/^--/, '').split('=')));
const page_ = a.page ?? 'http://127.0.0.1:5200/';
const engine = a.engine ?? 'http://127.0.0.1:5201';
const relay = a.relayUrl ?? 'http://127.0.0.1:5200';
const post = (u) => fetch(u, { method: 'POST' });
await post(`${engine}/clean`); await post(`${engine}/metrics/reset`);
if (a.base === '/relay') await post(`${relay}/metrics/reset`);
if (a.dropAt) await post(`${engine}/fault?at=${a.dropAt}`);
const browser = await chromium.launch();
const page = await browser.newPage();
await page.goto(page_);
await page.setInputFiles('#file', a.file);
await page.selectOption('#mode', a.mode);
await page.fill('#chunk', a.chunk ?? '64');
await page.fill('#base', a.base ?? '');
if (a.plainAttempts) await page.evaluate(n => window.__plainAttempts = n, a.plainAttempts);
let peakBrowserMb = 0, peakStoreBytes = 0;
const rss = () => { try { return Number(execSync("ps -eo rss,comm | awk '/chrom|headless/ {s+=$1} END {print s}'").toString()) / 1024; } catch { return 0; } };
const timer = setInterval(async () => {
  peakBrowserMb = Math.max(peakBrowserMb, rss());
  try { const m = await (await fetch(`${engine}/metrics`)).json(); peakStoreBytes = Math.max(peakStoreBytes, m.storeBytes); } catch {}
}, 500);
await page.click('#go');
if (a.cancelAt) {
  await page.waitForFunction(f => window.__progress >= f, Number(a.cancelAt), { timeout: 0, polling: 200 });
  await page.click('#cancel');
}
await page.waitForFunction(() => window.__result, null, { timeout: 0, polling: 500 });
const result = await page.evaluate(() => window.__result);
clearInterval(timer);
await new Promise(r => setTimeout(r, 1500));
const em = await (await fetch(`${engine}/metrics`)).json();
const rm = a.base === '/relay' ? await (await fetch(`${relay}/metrics`)).json() : null;
const elog = await (await fetch(`${engine}/log`)).json();
const rlog = a.base === '/relay' ? await (await fetch(`${relay}/log`)).json() : [];
console.log(JSON.stringify({ file: a.file, mode: a.mode, base: a.base ?? '', chunk: a.chunk, dropAt: a.dropAt, cancelAt: a.cancelAt, result,
  enginePeakMb: Math.round(em.peakWorkingSetMb), relayPeakMb: rm && Math.round(rm.peakWorkingSetMb), browserPeakMb: Math.round(peakBrowserMb),
  peakStoreGb: +(peakStoreBytes / 2 ** 30).toFixed(2), storeAfterGb: +(em.storeBytes / 2 ** 30).toFixed(2), engineLog: elog.slice(-3), relayLog: rlog.slice(-3) }));
await browser.close();
