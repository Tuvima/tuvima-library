// Small stand-in for the Dashboard: serves wwwroot and mimics /engine-book/{id}/file (byte ranges,
// private/no-cache) so the book reader can be checked in a real browser without the Engine.
//   import { startServer } from './serve.mjs'

import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const WWWROOT = path.resolve(here, '../../../src/MediaEngine.Web/wwwroot');

const TYPES = {
    '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.html': 'text/html',
    '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.ttf': 'font/ttf', '.woff2': 'font/woff2',
};

const PAGE = `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<link rel="stylesheet" href="/css/book-reader.css"><title>book reader check</title><style>html,body{margin:0}</style></head>
<body class="check-body"><div class="br-shell br-theme-light" id="shell" data-state="ready"><div class="br-stage" id="host"></div>
<header class="br-bar br-bar--top"><span class="br-icon-btn">&#8592;</span><div class="br-title"><div class="br-title-book" id="bar-title">Book</div><div class="br-title-author" id="bar-author"></div></div></header>
<footer class="br-bar br-bar--bottom"><span class="br-section" id="bar-section"></span><span class="br-percent" id="bar-percent"></span></footer></div></body></html>`;

/**
 * books: { [guid]: absolute file path }.  mode: 'range' (default), 'no-range' (ignores Range, sends 200),
 * or a number to answer every book request with that status (401, 404, 429, ...).
 */
export async function startServer({ books = {}, mode = 'range', port = 0 } = {}) {
    const log = [];
    const state = { mode };
    const server = http.createServer((req, res) => {
        const url = new URL(req.url, 'http://x');
        const entry = { method: req.method, path: url.pathname, range: req.headers.range ?? null, status: 0, bytes: 0 };
        log.push(entry);
        const finish = (status, headers = {}, body) => {
            entry.status = status;
            res.writeHead(status, headers);
            if (body === undefined) return res.end();
            entry.bytes = body.length ?? 0;
            res.end(body);
        };

        const m = /^\/engine-book\/([0-9a-f-]{36})\/file$/i.exec(url.pathname);
        if (m) {
            const file = books[m[1].toLowerCase()];
            if (typeof state.mode === 'number') return finish(state.mode, state.mode === 429 ? { 'Retry-After': '1' } : {});
            if (!file) return finish(404);
            const size = fs.statSync(file).size;
            const base = { 'Content-Type': 'application/epub+zip', 'Cache-Control': 'private, no-cache', 'X-Content-Type-Options': 'nosniff' };
            const range = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range ?? '');
            if (!range || state.mode === 'no-range') {
                entry.bytes = size;
                entry.status = 200;
                res.writeHead(200, { ...base, 'Content-Length': size });
                return fs.createReadStream(file).pipe(res);
            }
            let start; let end;
            if (range[1] === '') { start = Math.max(0, size - Number(range[2])); end = size - 1; }
            else { start = Number(range[1]); end = range[2] === '' ? size - 1 : Math.min(size - 1, Number(range[2])); }
            if (start >= size) return finish(416, { 'Content-Range': `bytes */${size}` });
            entry.status = 206;
            entry.bytes = end - start + 1;
            res.writeHead(206, { ...base, 'Accept-Ranges': 'bytes', 'Content-Range': `bytes ${start}-${end}/${size}`, 'Content-Length': end - start + 1 });
            return fs.createReadStream(file, { start, end }).pipe(res);
        }

        if (url.pathname === '/tracker/pixel.png') return finish(200, { 'Content-Type': 'image/png' }, Buffer.alloc(0));
        if (url.pathname === '/check.html') return finish(200, { 'Content-Type': 'text/html' }, PAGE);

        const target = path.join(WWWROOT, decodeURIComponent(url.pathname));
        if (!target.startsWith(WWWROOT) || !fs.existsSync(target) || fs.statSync(target).isDirectory()) return finish(404);
        const body = fs.readFileSync(target);
        return finish(200, { 'Content-Type': TYPES[path.extname(target)] ?? 'application/octet-stream' }, body);
    });
    await new Promise(resolve => server.listen(port, '127.0.0.1', resolve));
    const address = server.address();
    return {
        origin: `http://127.0.0.1:${address.port}`,
        log,
        setMode: value => { state.mode = value; },
        clearLog: () => { log.length = 0; },
        close: () => new Promise(resolve => server.close(resolve)),
    };
}
