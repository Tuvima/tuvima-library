import fs from 'node:fs/promises';
import path from 'node:path';
import zlib from 'node:zlib';
import assert from 'node:assert/strict';

// Compare two verified static trees using the same compression settings. Include
// every non-framework CSS/JS module, including lazy-loaded unchanged vendor assets.
const [baselineRoot, publishRoot, output = '.tmp/mud/p5/asset-cost.json'] = process.argv.slice(2);
assert.ok(baselineRoot && publishRoot, 'Usage: node measure-dashboard-assets.mjs BASELINE_WWWROOT PUBLISH_WWWROOT [OUTPUT]');
async function measure(root) {
  const assets = [];
  async function visit(directory) {
    for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
      const file = path.join(directory, entry.name);
      if (entry.isDirectory()) { if (entry.name !== '_framework') await visit(file); continue; }
      if (!/\.(css|m?js)$/.test(entry.name)) continue;
      const bytes = await fs.readFile(file);
      assets.push({ name: path.relative(root, file).replaceAll('\\', '/'), raw: bytes.length,
        gzip: zlib.gzipSync(bytes, { level: 9 }).length, brotli: zlib.brotliCompressSync(bytes).length });
    }
  }
  await visit(path.resolve(root));
  assets.sort((a,b) => a.name.localeCompare(b.name));
  const totals = Object.fromEntries(['raw','gzip','brotli'].map(metric => [metric, assets.reduce((sum, asset) => sum + asset[metric], 0)]));
  const cssTotals = Object.fromEntries(['raw','gzip','brotli'].map(metric => [metric, assets.filter(asset => asset.name.endsWith('.css')).reduce((sum, asset) => sum + asset[metric], 0)]));
  return { assets, totals, cssTotals };
}
const before = await measure(baselineRoot), after = await measure(publishRoot);
assert.ok(before.assets.some(asset => /MudBlazor.min.css/.test(asset.name)), 'Baseline must include original vendor CSS');
assert.ok(before.assets.some(asset => /MudBlazor.min.js/.test(asset.name)), 'Baseline must include original vendor JavaScript');
assert.ok(!after.assets.some(asset => /MudBlazor/.test(asset.name)), 'Retired vendor files remain delivered');
assert.ok(after.totals.gzip < before.totals.gzip, 'Complete CSS/JS gzip cost did not decrease');
assert.ok(after.cssTotals.gzip < before.cssTotals.gzip, 'CSS-only gzip cost did not decrease');
await fs.mkdir(path.dirname(output), { recursive: true });
await fs.writeFile(output, JSON.stringify({ schemaVersion: 1, compression: 'gzip level9; brotli default, identical before/after settings',
  framework: 'unchanged .NET framework files excluded equally', before, after,
  gzipSaved: before.totals.gzip - after.totals.gzip, cssGzipSaved: before.cssTotals.gzip - after.cssTotals.gzip }, null, 2) + '\n');
console.log(JSON.stringify({ before: before.totals, after: after.totals, cssBefore: before.cssTotals, cssAfter: after.cssTotals }));
