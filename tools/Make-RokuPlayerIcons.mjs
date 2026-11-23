// Draws the plain white shapes the Roku player's transport row uses (clients/roku/images/*.png).
// A Roku Poster can only show a picture, so the round button, Play, Pause, Previous, Next, the two skip arrows and the check mark are
// rasterised here with the Node standard library only (no packages). The player tints them with blendColor.
// Run from the repo root: node tools/Make-RokuPlayerIcons.mjs
import { deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const size = 128;
const samples = 4;
const outDir = fileURLToPath(new URL('../clients/roku/images/', import.meta.url));

const crcTable = new Int32Array(256).map((_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c;
});
const crc32 = (buffer) => {
    let c = -1;
    for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
    return (c ^ -1) >>> 0;
};
const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const out = Buffer.alloc(body.length + 8);
    out.writeUInt32BE(data.length, 0);
    body.copy(out, 4);
    out.writeUInt32BE(crc32(body), body.length + 4);
    return out;
};

// inside(x, y) says whether a point (0..128) is part of the shape; each pixel is averaged over samples x samples points.
const png = (inside) => {
    const rows = [];
    for (let y = 0; y < size; y++) {
        const row = Buffer.alloc(1 + size * 4);
        for (let x = 0; x < size; x++) {
            let hit = 0;
            for (let sy = 0; sy < samples; sy++) {
                for (let sx = 0; sx < samples; sx++) {
                    if (inside(x + (sx + 0.5) / samples, y + (sy + 0.5) / samples)) hit++;
                }
            }
            const o = 1 + x * 4;
            row[o] = row[o + 1] = row[o + 2] = 255;
            row[o + 3] = Math.round((hit / (samples * samples)) * 255);
        }
        rows.push(row);
    }
    const header = Buffer.alloc(13);
    header.writeUInt32BE(size, 0);
    header.writeUInt32BE(size, 4);
    header[8] = 8; // bit depth
    header[9] = 6; // RGBA
    return Buffer.concat([
        Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        chunk('IHDR', header),
        chunk('IDAT', deflateSync(Buffer.concat(rows))),
        chunk('IEND', Buffer.alloc(0)),
    ]);
};

const triangle = (ax, ay, bx, by, cx, cy) => (x, y) => {
    const d = (px, py, qx, qy) => (x - qx) * (py - qy) - (px - qx) * (y - qy);
    const d1 = d(ax, ay, bx, by);
    const d2 = d(bx, by, cx, cy);
    const d3 = d(cx, cy, ax, ay);
    return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
};
const box = (x0, y0, x1, y1) => (x, y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;
// A line with round ends, `width` thick, from (ax, ay) to (bx, by).
const stroke = (ax, ay, bx, by, width) => (x, y) => {
    const vx = bx - ax;
    const vy = by - ay;
    const t = Math.max(0, Math.min(1, ((x - ax) * vx + (y - ay) * vy) / (vx * vx + vy * vy)));
    return Math.hypot(x - (ax + t * vx), y - (ay + t * vy)) <= width / 2;
};
const any = (...shapes) => (x, y) => shapes.some((s) => s(x, y));

// A ring with a gap at the top-left and an arrow head: "skip back". The forward arrow is the same picture mirrored.
const arrow = (mirror) => {
    const cx = 64;
    const cy = 68;
    const outer = 46;
    const inner = 36;
    const head = triangle(34, 12, 34, 44, 12, 28);
    return (px, py) => {
        const x = mirror ? size - px : px;
        const dx = x - cx;
        const dy = py - cy;
        const r = Math.hypot(dx, dy);
        const angle = (Math.atan2(dy, dx) * 180) / Math.PI; // 0 = right, -90 = up
        const inGap = angle > -150 && angle < -95;
        return (r <= outer && r >= inner && !inGap) || head(x, py);
    };
};

const shapes = {
    circle: (x, y) => Math.hypot(x - 64, y - 64) <= 62,
    play: triangle(40, 22, 40, 106, 108, 64),
    pause: any(box(34, 24, 54, 104), box(74, 24, 94, 104)),
    previous: any(box(26, 24, 40, 104), triangle(106, 24, 106, 104, 46, 64)),
    next: any(box(88, 24, 102, 104), triangle(22, 24, 22, 104, 82, 64)),
    check: any(stroke(24, 68, 50, 94, 16), stroke(50, 94, 106, 36, 16)),
    'skip-back': arrow(false),
    'skip-forward': arrow(true),
};

mkdirSync(outDir, { recursive: true });
for (const [name, inside] of Object.entries(shapes)) {
    writeFileSync(join(outDir, `${name}.png`), png(inside));
}
console.log(`Wrote ${Object.keys(shapes).length} icons to ${outDir}`);
