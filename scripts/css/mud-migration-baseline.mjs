import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

const root = process.cwd();
const assets = [
  ['scoped', 'src/MediaEngine.Web/obj/Release/net10.0/scopedcss/bundle/MediaEngine.Web.styles.css'],
  ['app', 'src/MediaEngine.Web/wwwroot/app.css'],
  ['tokens', 'src/MediaEngine.Web/wwwroot/tuvima.tokens.css'],
  ['maplibre', 'src/MediaEngine.Web/wwwroot/vendor/maplibre/maplibre-gl.css'],
  ['vendor-css', path.join(process.env.USERPROFILE, '.nuget/packages/mudblazor/9.0.0/staticwebassets/MudBlazor.min.css')],
  ['vendor-js', path.join(process.env.USERPROFILE, '.nuget/packages/mudblazor/9.0.0/staticwebassets/MudBlazor.min.js')],
];
const measured = assets.map(([name, file]) => {
  const data = fs.readFileSync(path.resolve(root, file));
  return { name, raw: data.length, gzip: zlib.gzipSync(data, { level: 9 }).length,
    brotli: zlib.brotliCompressSync(data).length };
});
const output = process.argv[2] ?? '.tmp/mud/p0/assets-before.json';
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, JSON.stringify({ schemaVersion: 1, commit: 'c765bc91', compression: 'gzip level 9; brotli default', assets: measured }, null, 2) + '\n');
console.log(JSON.stringify(measured));
