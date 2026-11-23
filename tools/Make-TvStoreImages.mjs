// Draws the store artwork for the TV apps (Roku channel poster and splash, Samsung store icon, LG splash) from the Library
// mark in assets/images/tuvima-icon.svg and the logo in assets/images/tuvima-logo.svg.
// No rasteriser is installed on a normal machine, so this reads the SVG paths itself and fills them with the Node standard
// library only (no packages, nothing to licence-check). The two SVG files use only M, C, S, L, H, V and Z commands (relative or
// absolute) and one radial gradient; anything else stops the script instead of drawing something wrong.
// Run from the repo root: node tools/Make-TvStoreImages.mjs
// The sizes below are the sizes each store asks for; tests/build/tv-store-packages.test.mjs checks the files against them.
import { deflateSync } from 'node:zlib';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../', import.meta.url));
const background = [0x09, 0x08, 0x0d]; // the app's dark base, also the Roku splash_color

// kind 'mark' draws the icon alone; 'logo' draws the icon and the words from tuvima-logo.svg; 'splash' draws the logo on a large field.
export const images = [
    // Roku: the channel poster on the home screen (HD and FHD) and the splash screen shown while the channel starts.
    { file: 'clients/roku/images/channel-poster-hd.png', width: 336, height: 210, kind: 'mark' },
    { file: 'clients/roku/images/channel-poster-fhd.png', width: 540, height: 405, kind: 'mark' },
    { file: 'clients/roku/images/splash-hd.png', width: 1280, height: 720, kind: 'splash' },
    { file: 'clients/roku/images/splash-fhd.png', width: 1920, height: 1080, kind: 'splash' },
    // Samsung: the large store icon asked for in Seller Office (the 117 x 117 package icon is separate and already in the repo).
    { file: 'clients/tv-web/platforms/tizen/store/store-icon-512x423.png', width: 512, height: 423, kind: 'mark' },
    // LG: the splash shown while the app starts (appinfo.json bgImage).
    { file: 'clients/tv-web/platforms/webos/splash.png', width: 1920, height: 1080, kind: 'splash' },
];

// ---- PNG writer ---------------------------------------------------------------------------------------------------------
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
const encodePng = (width, height, rgb) => {
    const rows = [];
    for (let y = 0; y < height; y++) {
        const row = Buffer.alloc(1 + width * 3);
        rgb.copy(row, 1, y * width * 3, (y + 1) * width * 3);
        rows.push(row);
    }
    const header = Buffer.alloc(13);
    header.writeUInt32BE(width, 0);
    header.writeUInt32BE(height, 4);
    header[8] = 8; // bit depth
    header[9] = 2; // RGB, no transparency: the stores want a solid picture
    return Buffer.concat([
        Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        chunk('IHDR', header),
        chunk('IDAT', deflateSync(Buffer.concat(rows), { level: 9 })),
        chunk('IEND', Buffer.alloc(0)),
    ]);
};

// ---- SVG reading --------------------------------------------------------------------------------------------------------
const numbers = (text) => (text.match(/-?(?:\d+\.?\d*|\.\d+)(?:e-?\d+)?/gi) ?? []).map(Number);

// Turns a path's "d" into polygons (lists of [x, y]); curves are cut into short straight pieces.
export const flattenPath = (d) => {
    const polygons = [];
    let current = null;
    let x = 0, y = 0, startX = 0, startY = 0, lastC2 = null;
    const curve = (x1, y1, x2, y2, x3, y3) => {
        const steps = 24;
        for (let i = 1; i <= steps; i++) {
            const t = i / steps, u = 1 - t;
            current.push([
                u * u * u * x + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3,
                u * u * u * y + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3,
            ]);
        }
        lastC2 = [x2, y2];
        x = x3;
        y = y3;
    };
    for (const [, command, args] of d.matchAll(/([A-Za-z])([^A-Za-z]*)/g)) {
        const n = numbers(args);
        const relative = command === command.toLowerCase();
        const dx = relative ? x : 0, dy = relative ? y : 0;
        switch (command.toUpperCase()) {
            case 'M':
                // The first pair moves; any further pairs are implicit line-tos.
                x = dx + n[0];
                y = dy + n[1];
                startX = x;
                startY = y;
                current = [[x, y]];
                polygons.push(current);
                for (let i = 2; i + 1 < n.length; i += 2) {
                    x = (relative ? x : 0) + n[i];
                    y = (relative ? y : 0) + n[i + 1];
                    current.push([x, y]);
                }
                lastC2 = null;
                break;
            case 'L':
                for (let i = 0; i + 1 < n.length; i += 2) {
                    x = (relative ? x : 0) + n[i];
                    y = (relative ? y : 0) + n[i + 1];
                    current.push([x, y]);
                }
                lastC2 = null;
                break;
            case 'H':
                for (const value of n) {
                    x = (relative ? x : 0) + value;
                    current.push([x, y]);
                }
                lastC2 = null;
                break;
            case 'V':
                for (const value of n) {
                    y = (relative ? y : 0) + value;
                    current.push([x, y]);
                }
                lastC2 = null;
                break;
            case 'C':
                for (let i = 0; i + 5 < n.length; i += 6) {
                    const bx = relative ? x : 0, by = relative ? y : 0;
                    curve(bx + n[i], by + n[i + 1], bx + n[i + 2], by + n[i + 3], bx + n[i + 4], by + n[i + 5]);
                }
                break;
            case 'S':
                for (let i = 0; i + 3 < n.length; i += 4) {
                    const bx = relative ? x : 0, by = relative ? y : 0;
                    const [c1x, c1y] = lastC2 ? [2 * x - lastC2[0], 2 * y - lastC2[1]] : [x, y];
                    curve(c1x, c1y, bx + n[i], by + n[i + 1], bx + n[i + 2], by + n[i + 3]);
                }
                break;
            case 'Z':
                if (current) current.push([startX, startY]);
                x = startX;
                y = startY;
                lastC2 = null;
                break;
            default:
                throw new Error(`Path command "${command}" is not supported by Make-TvStoreImages.mjs.`);
        }
    }
    return polygons;
};

const readSvg = (relativePath) => {
    const text = readFileSync(join(repoRoot, relativePath), 'utf8');
    const viewBox = numbers(text.match(/viewBox="([^"]+)"/)[1]);
    const gradient = text.match(/<radialGradient[^>]*>/)[0];
    const attribute = (name) => Number(gradient.match(new RegExp(`\\b${name}="([^"]+)"`))[1]);
    const stops = [...text.matchAll(/<stop offset="([^"]+)" stop-color="#([0-9a-f]{6})"/gi)].map(([, offset, hex]) => ({
        offset: Number(offset),
        color: [0, 2, 4].map((i) => parseInt(hex.slice(i, i + 2), 16)),
    }));
    // Words are the paths inside the group with class "tl-text" (or the whole file when it has no such group); the rest is the mark.
    const textGroupStart = text.indexOf('class="tl-text"', text.indexOf('<g '));
    const paths = [...text.matchAll(/<path[^>]*\sd="([^"]+)"/g)].map((match) => ({
        polygons: flattenPath(match[1]),
        isWord: textGroupStart >= 0 && match.index > textGroupStart,
    }));
    return { viewBox, paths, gradient: { cx: attribute('cx'), cy: attribute('cy'), r: attribute('r'), stops } };
};

const gradientColor = ({ cx, cy, r, stops }, x, y) => {
    const t = Math.min(1, Math.hypot(x - cx, y - cy) / r);
    let previous = stops[0];
    for (const stop of stops) {
        if (t <= stop.offset) {
            const span = stop.offset - previous.offset;
            const f = span <= 0 ? 1 : (t - previous.offset) / span;
            return stop.color.map((c, i) => previous.color[i] + (c - previous.color[i]) * f);
        }
        previous = stop;
    }
    return stops[stops.length - 1].color;
};

// ---- Filling ------------------------------------------------------------------------------------------------------------
// Coverage (0..1) of every pixel by the polygons, using the non-zero rule like SVG: four sub-scanlines per row and exact
// horizontal coverage inside each.
const coverage = (polygons, width, height) => {
    const result = new Float32Array(width * height);
    const edges = [];
    for (const polygon of polygons) {
        for (let i = 0; i + 1 < polygon.length; i++) {
            const [x0, y0] = polygon[i];
            const [x1, y1] = polygon[i + 1];
            if (y0 !== y1) edges.push({ x0, y0, x1, y1, top: Math.min(y0, y1), bottom: Math.max(y0, y1), dir: y1 > y0 ? 1 : -1 });
        }
        // Fill rules close every sub-path.
        const first = polygon[0];
        const last = polygon[polygon.length - 1];
        if (first && last && (first[0] !== last[0] || first[1] !== last[1])) {
            const [x0, y0] = last;
            const [x1, y1] = first;
            if (y0 !== y1) edges.push({ x0, y0, x1, y1, top: Math.min(y0, y1), bottom: Math.max(y0, y1), dir: y1 > y0 ? 1 : -1 });
        }
    }
    const sub = 4;
    for (let row = 0; row < height; row++) {
        for (let k = 0; k < sub; k++) {
            const y = row + (k + 0.5) / sub;
            const crossings = [];
            for (const e of edges) {
                if (y >= e.top && y < e.bottom) {
                    crossings.push({ x: e.x0 + ((y - e.y0) / (e.y1 - e.y0)) * (e.x1 - e.x0), dir: e.dir });
                }
            }
            crossings.sort((a, b) => a.x - b.x);
            let winding = 0;
            for (let i = 0; i + 1 < crossings.length; i++) {
                winding += crossings[i].dir;
                if (winding === 0) continue;
                const a = Math.max(0, crossings[i].x);
                const b = Math.min(width, crossings[i + 1].x);
                if (b <= a) continue;
                for (let px = Math.floor(a); px <= Math.min(width - 1, Math.floor(b)); px++) {
                    const overlap = Math.min(b, px + 1) - Math.max(a, px);
                    if (overlap > 0) result[row * width + px] += overlap / sub;
                }
            }
        }
    }
    return result;
};

const fit = (svg, boxWidth, boxHeight) => {
    const [, , vw, vh] = svg.viewBox;
    const scale = Math.min(boxWidth / vw, boxHeight / vh);
    return { scale, left: (boxWidth - vw * scale) / 2, top: (boxHeight - vh * scale) / 2 };
};

const draw = (rgb, width, height, svg, placement, wordsToo) => {
    const { scale, left, top } = placement;
    const move = ([x, y]) => [left + x * scale, top + y * scale];
    for (const group of [false, true]) {
        if (group && !wordsToo) continue;
        const polygons = svg.paths.filter((p) => p.isWord === group).flatMap((p) => p.polygons.map((poly) => poly.map(move)));
        if (polygons.length === 0) continue;
        const mask = coverage(polygons, width, height);
        for (let py = 0; py < height; py++) {
            for (let px = 0; px < width; px++) {
                const a = Math.min(1, mask[py * width + px]);
                if (a <= 0) continue;
                // Words are white at 87 % like the dark theme's text; the mark carries its gold gradient.
                const [r, g, b] = group
                    ? [255, 255, 255].map((c) => c * 0.87 + 0 * 0.13)
                    : gradientColor(svg.gradient, ((px + 0.5) - left) / scale, ((py + 0.5) - top) / scale);
                const o = (py * width + px) * 3;
                rgb[o] = Math.round(rgb[o] * (1 - a) + r * a);
                rgb[o + 1] = Math.round(rgb[o + 1] * (1 - a) + g * a);
                rgb[o + 2] = Math.round(rgb[o + 2] * (1 - a) + b * a);
            }
        }
    }
};

export const render = ({ width, height, kind }) => {
    const rgb = Buffer.alloc(width * height * 3);
    for (let i = 0; i < width * height; i++) background.forEach((c, k) => (rgb[i * 3 + k] = c));
    if (kind === 'mark') {
        const icon = readSvg('assets/images/tuvima-icon.svg');
        // The mark fills 62 % of the shorter side, centred.
        const side = Math.min(width, height) * 0.62;
        const placement = fit(icon, side, side);
        draw(rgb, width, height, icon, { ...placement, left: placement.left + (width - side) / 2, top: placement.top + (height - side) / 2 }, false);
    } else {
        const logo = readSvg('assets/images/tuvima-logo.svg');
        // The logo is about four times wider than tall; it fills 46 % of the width, centred.
        const boxWidth = width * 0.46;
        const boxHeight = boxWidth * (logo.viewBox[3] / logo.viewBox[2]);
        const placement = fit(logo, boxWidth, boxHeight);
        draw(rgb, width, height, logo, { ...placement, left: placement.left + (width - boxWidth) / 2, top: placement.top + (height - boxHeight) / 2 }, true);
    }
    return encodePng(width, height, rgb);
};

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    for (const image of images) {
        const path = join(repoRoot, image.file);
        mkdirSync(dirname(path), { recursive: true });
        writeFileSync(path, render(image));
    }
    console.log(`Wrote ${images.length} store images.`);
}
