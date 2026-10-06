import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import zlib from 'node:zlib';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';

const defaultBaseline = fileURLToPath(new URL('../../tests/MediaEngine.Web.Tests/Fixtures/style-ownership-baseline.json', import.meta.url));
export function verifyBundleRatchet(baseline, assets) {
  const bundle = assets.find(asset => asset.name === 'MediaEngine.Web.styles.css');
  assert.ok(bundle, 'Missing scoped CSS bundle measurement');
  const enforced = baseline.finalized === true;
  if (!enforced) return { enforced, raw: bundle.raw };
  const ceiling = baseline.finalBundleCeilingBytes;
  assert.ok(Number.isSafeInteger(ceiling) && ceiling > 0,
    'Finalized style baseline must define a positive integer finalBundleCeilingBytes');
  assert.ok(bundle.raw <= ceiling,
    `Scoped CSS bundle grew to ${bundle.raw} bytes above its finalized ${ceiling}-byte ceiling`);
  return { enforced, raw: bundle.raw, ceiling };
}

// Run after Release publish. An optional local base URL checks actual endpoint delivery.
export async function verifyPublishedCss({ publish, baseUrl, output, baselinePath = defaultBaseline }) {
  publish = path.resolve(publish);
  const manifest = JSON.parse(await fs.readFile(path.join(publish, 'MediaEngine.Web.staticwebassets.endpoints.json'), 'utf8'));
  const names = ['MediaEngine.Web.styles.css', 'app.css', 'tuvima.tokens.css', 'native-fields.css', 'native-structure.css', 'native-utilities.css'];
  const assets = [];
  for (const name of names) {
    const bytes = await fs.readFile(path.join(publish, 'wwwroot', name));
    const source = bytes.toString('utf8');
    assert.ok(source.split('\n').length <= 2, `${name} is not a minified Release asset`);
    const gzip = await fs.readFile(path.join(publish, 'wwwroot', name + '.gz'));
    const brotli = await fs.readFile(path.join(publish, 'wwwroot', name + '.br'));
    assert.ok(zlib.gunzipSync(gzip).equals(bytes), `${name}: gzip was not derived from minified CSS`);
    assert.ok(zlib.brotliDecompressSync(brotli).equals(bytes), `${name}: brotli was not derived from minified CSS`);
    const endpoint = manifest.Endpoints.find(item => item.AssetFile === name && item.Route !== name && !item.Selectors.length);
    assert.ok(endpoint, `${name}: missing fingerprinted endpoint`);
    const properties = Object.fromEntries(endpoint.EndpointProperties.map(item => [item.Name, item.Value]));
    assert.equal(properties.integrity, 'sha256-' + crypto.createHash('sha256').update(bytes).digest('base64'));
    let delivered = false;
    if (baseUrl) {
      const response = await fetch(new URL(endpoint.Route, baseUrl));
      assert.equal(response.status, 200, `${endpoint.Route} does not resolve`);
      assert.ok(Buffer.from(await response.arrayBuffer()).equals(bytes), `${name}: endpoint serves different CSS`);
      delivered = true;
    }
    assets.push({ name, raw: bytes.length, gzip: gzip.length, brotli: brotli.length, route: endpoint.Route, delivered });
  }
  const dependencies = await fs.readFile(path.join(publish, 'MediaEngine.Web.deps.json'), 'utf8');
  assert.doesNotMatch(dependencies, /MudBlazor|NUglify/i, 'UI framework or build minifier leaked into runtime dependencies');
  assert.ok(!manifest.Endpoints.some(item => /MudBlazor/i.test(item.Route)), 'Retired framework assets remain published');
  const baseline = JSON.parse(await fs.readFile(baselinePath, 'utf8'));
  const scopedBundleRatchet = verifyBundleRatchet(baseline, assets);
  await fs.mkdir(path.dirname(output), { recursive: true });
  const report = { schemaVersion: 1, verified: true, assets,
    totals: Object.fromEntries(['raw', 'gzip', 'brotli'].map(metric => [metric, assets.reduce((sum, asset) => sum + asset[metric], 0)])),
    scopedBundleRatchet, endpointDeliveryChecked: !!baseUrl };
  await fs.writeFile(output, JSON.stringify(report, null, 2) + '\n');
  return report;
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  const report = await verifyPublishedCss({ publish: process.argv[2] ?? '.tmp/mud/publish',
    baseUrl: process.argv[3] || undefined, output: process.argv[4] ?? '.tmp/mud/p5/published-css.json',
    baselinePath: process.argv[5] ?? defaultBaseline });
  console.log(`Verified ${report.assets.length} minified CSS assets, compressed representations, fingerprints, and runtime dependency exclusions${report.endpointDeliveryChecked ? ', including local HTTP delivery' : ''}${report.scopedBundleRatchet.enforced ? ', including finalized scoped bundle ceiling' : ''}.`);
}
