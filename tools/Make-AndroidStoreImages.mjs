// Draws the Google Play pictures for the Android apps and writes the launcher icon drawables, from the Library mark in
// assets/images/tuvima-icon.svg and the logo in assets/images/tuvima-logo.svg.
// Like tools/Make-TvStoreImages.mjs it uses the Node standard library only: the SVG reader, the PNG writer and the filler are
// reused from there. The tagline has no font to draw with, so it is set in a small stroke alphabet written below (only the
// letters the tagline needs; a different tagline needs its letters added).
// Run from the repo root: node tools/Make-AndroidStoreImages.mjs
// tests/build/android-store-pictures.test.mjs checks the files against the sizes Play asks for.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { coverage, draw, encodePng, fit, readSvg } from './Make-TvStoreImages.mjs';

const repoRoot = fileURLToPath(new URL('../', import.meta.url));
const background = [0x10, 0x11, 0x15]; // #101115, the same colour as the launcher icon's background layer
const accent = [0x8e, 0x6c, 0xff]; // #8E6CFF, the apps' accent (Theme.Tuvima colorAccent)
export const tagline = 'One library. Every story.';

export const pictures = [
    { file: 'clients/android/store/hi-res-icon-512x512.png', width: 512, height: 512, kind: 'icon' },
    { file: 'clients/android/store/feature-graphic-1024x500.png', width: 1024, height: 500, kind: 'feature' },
];

// The launcher icon of both apps (phone and TV share the same artwork).
export const launcherModules = ['clients/android/mobile', 'clients/android/tv'];

// ---- Stroke alphabet ----------------------------------------------------------------------------------------------------
// Units: the baseline is y = 0, the x-height 5, capitals and tall letters 8, y points up. A stroke is a list of points drawn
// as a line with round ends; a one-point stroke is a dot.
const arc = (cx, cy, rx, ry, from, to) => {
    const steps = Math.max(8, Math.ceil(Math.abs(to - from) / 10));
    return Array.from({ length: steps + 1 }, (_, i) => {
        const t = ((from + ((to - from) * i) / steps) * Math.PI) / 180;
        return [cx + rx * Math.cos(t), cy + ry * Math.sin(t)];
    });
};
const glyphs = {
    O: { width: 6, strokes: [arc(3, 4, 3, 4, 0, 360)] },
    E: { width: 3.5, strokes: [[[3.5, 8], [0, 8], [0, 0], [3.5, 0]], [[0, 4], [3, 4]]] },
    n: { width: 4, strokes: [[[0, 5], [0, 0]], [...arc(2, 3, 2, 2, 180, 0), [4, 0]]] },
    e: { width: 4, strokes: [[[0, 2.5], [4, 2.5], ...arc(2, 2.5, 2, 2.5, 0, 320)]] },
    o: { width: 4, strokes: [arc(2, 2.5, 2, 2.5, 0, 360)] },
    l: { width: 0, strokes: [[[0, 0], [0, 8]]] },
    i: { width: 0, strokes: [[[0, 0], [0, 5]], [[0, 7.3]]] },
    b: { width: 4.4, strokes: [[[0, 0], [0, 8]], arc(2.2, 2.5, 2.2, 2.5, 0, 360)] },
    r: { width: 3.3, strokes: [[[0, 5], [0, 0]], [...arc(2.5, 2.5, 2.5, 2.5, 180, 90), [3.3, 4.9]]] },
    a: { width: 4, strokes: [arc(2, 2.5, 2, 2.5, 0, 360), [[4, 5], [4, 0]]] },
    y: { width: 4, strokes: [[[0, 5], [2.25, 0]], [[4, 5], [1.2, -3]]] },
    v: { width: 4, strokes: [[[0, 5], [2, 0], [4, 5]]] },
    s: { width: 4, strokes: [[...arc(2, 3.75, 2, 1.25, 30, 270), ...arc(2, 1.25, 2, 1.25, 90, -150)]] },
    t: { width: 3.2, strokes: [[[1.2, 7], [1.2, 1], ...arc(2.2, 1, 1, 1, 180, 270), [3.1, 0.2]], [[0, 5], [3.2, 5]]] },
    '.': { width: 0, strokes: [[[0, 0.3]]] },
    ' ': { width: 2, strokes: [] },
};
const gap = 2.7;
const capHeight = 8;

// Polygons for the text, left edge at x, baseline at y, `unit` pixels per alphabet unit. Every polygon is wound the same way so
// overlapping pieces add up under the non-zero rule instead of cancelling.
const textPolygons = (text, x, baseline, unit, strokeUnits) => {
    const half = (strokeUnits * unit) / 2;
    const polygons = [];
    const circle = (cx, cy) => polygons.push(Array.from({ length: 17 }, (_, i) => [cx + half * Math.cos((i / 16) * 2 * Math.PI), cy + half * Math.sin((i / 16) * 2 * Math.PI)]));
    let cursor = x;
    for (const character of text) {
        const glyph = glyphs[character];
        if (!glyph) throw new Error(`The stroke alphabet in Make-AndroidStoreImages.mjs has no "${character}".`);
        for (const stroke of glyph.strokes) {
            const points = stroke.map(([gx, gy]) => [cursor + gx * unit, baseline - gy * unit]);
            for (const [px, py] of points) circle(px, py);
            for (let i = 0; i + 1 < points.length; i++) {
                const [x0, y0] = points[i];
                const [x1, y1] = points[i + 1];
                const length = Math.hypot(x1 - x0, y1 - y0);
                if (length === 0) continue;
                const nx = (-(y1 - y0) / length) * half;
                const ny = ((x1 - x0) / length) * half;
                const quad = [[x0 + nx, y0 + ny], [x1 + nx, y1 + ny], [x1 - nx, y1 - ny], [x0 - nx, y0 - ny], [x0 + nx, y0 + ny]];
                const area = quad.slice(0, 4).reduce((sum, [ax, ay], k) => sum + (ax * quad[(k + 1) % 4][1] - quad[(k + 1) % 4][0] * ay), 0);
                polygons.push(area < 0 ? quad.reverse() : quad);
            }
        }
        cursor += (glyph.width + (character === ' ' ? 0 : gap)) * unit;
    }
    // The circles above are wound one way and the quads are normalised to match it.
    return polygons.map((polygon) => {
        const area = polygon.reduce((sum, [ax, ay], k) => sum + (ax * polygon[(k + 1) % polygon.length][1] - polygon[(k + 1) % polygon.length][0] * ay), 0);
        return area < 0 ? [...polygon].reverse() : polygon;
    });
};
const textWidth = (text, unit) => [...text].reduce((sum, character) => sum + (glyphs[character].width + (character === ' ' ? 0 : gap)) * unit, 0) - gap * unit;

// ---- Painting -----------------------------------------------------------------------------------------------------------
const paintMask = (rgb, width, height, mask, color, opacity = 1) => {
    for (let i = 0; i < width * height; i++) {
        const a = Math.min(1, mask[i]) * opacity;
        if (a <= 0) continue;
        for (let k = 0; k < 3; k++) rgb[i * 3 + k] = Math.round(rgb[i * 3 + k] * (1 - a) + color[k] * a);
    }
};

// The dark base with a faint purple glow behind the mark.
const field = (width, height, glowX, glowY, glowRadius) => {
    const rgb = Buffer.alloc(width * height * 3);
    for (let py = 0; py < height; py++) {
        for (let px = 0; px < width; px++) {
            const t = Math.max(0, 1 - Math.hypot(px + 0.5 - glowX, py + 0.5 - glowY) / glowRadius);
            const strength = 0.2 * t * t;
            for (let k = 0; k < 3; k++) rgb[(py * width + px) * 3 + k] = Math.round(background[k] * (1 - strength) + accent[k] * strength);
        }
    }
    return rgb;
};

export const render = ({ width, height, kind }) => {
    if (kind === 'icon') {
        // Play's hi-res icon is a plain square; Play rounds the corners itself, so the mark stays inside the middle 60 %.
        const rgb = field(width, height, width / 2, height / 2, width * 0.62);
        const mark = readSvg('assets/images/tuvima-icon.svg');
        const side = width * 0.6;
        const placement = fit(mark, side, side);
        draw(rgb, width, height, mark, { ...placement, left: placement.left + (width - side) / 2, top: placement.top + (height - side) / 2 }, false);
        return encodePng(width, height, rgb);
    }
    const logo = readSvg('assets/images/tuvima-logo.svg');
    const logoWidth = width * 0.5;
    const logoHeight = logoWidth * (logo.viewBox[3] / logo.viewBox[2]);
    const unit = 3.6;
    const barHeight = 4;
    const space = 38;
    const stack = logoHeight + space + barHeight + space + capHeight * unit;
    const top = (height - stack) / 2;
    const rgb = field(width, height, width / 2, top + logoHeight / 2, width * 0.55);
    const placement = fit(logo, logoWidth, logoHeight);
    draw(rgb, width, height, logo, { ...placement, left: placement.left + (width - logoWidth) / 2, top: placement.top + top }, true);
    // A short purple bar, then the tagline in the dark theme's text colour.
    const barTop = top + logoHeight + space;
    const bar = [[[width / 2 - 36, barTop], [width / 2 + 36, barTop], [width / 2 + 36, barTop + barHeight], [width / 2 - 36, barTop + barHeight], [width / 2 - 36, barTop]]];
    paintMask(rgb, width, height, coverage(bar, width, height), accent);
    const baseline = barTop + barHeight + space + capHeight * unit;
    const words = textPolygons(tagline, (width - textWidth(tagline, unit)) / 2, baseline, unit, 0.85);
    paintMask(rgb, width, height, coverage(words, width, height), [255, 255, 255], 0.87);
    return encodePng(width, height, rgb);
};

// ---- Launcher icon drawables --------------------------------------------------------------------------------------------
// The adaptive icon is 108 dp with the middle 66 dp always visible, so the mark is drawn 54 dp wide in the middle.
export const launcherDrawables = () => {
    const text = readFileSync(join(repoRoot, 'assets/images/tuvima-icon.svg'), 'utf8');
    const [, , vw, vh] = readSvg('assets/images/tuvima-icon.svg').viewBox;
    const pathData = text.match(/<path[^>]*\sd="([^"]+)"/)[1];
    const gradient = readSvg('assets/images/tuvima-icon.svg').gradient;
    const scale = 54 / vw;
    const left = (108 - 54) / 2;
    const topOffset = (108 - vh * scale) / 2;
    const number = (value) => String(Math.round(value * 10000) / 10000);
    const group = (inner) => `    <group
        android:scaleX="${number(scale)}"
        android:scaleY="${number(scale)}"
        android:translateX="${number(left)}"
        android:translateY="${number(topOffset)}">
${inner}
    </group>`;
    const header = `<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by tools/Make-AndroidStoreImages.mjs from assets/images/tuvima-icon.svg; change the script, not this file. -->
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    xmlns:aapt="http://schemas.android.com/aapt"
    android:width="108dp"
    android:height="108dp"
    android:viewportWidth="108"
    android:viewportHeight="108">
`;
    const argb = (color) => `#FF${color.map((c) => Math.round(c).toString(16).padStart(2, '0')).join('').toUpperCase()}`;
    const stops = gradient.stops.map((stop) => `                    <item android:offset="${stop.offset}" android:color="${argb(stop.color)}" />`).join('\n');
    const foreground = `${header}${group(`        <path android:pathData="${pathData}">
            <aapt:attr name="android:fillColor">
                <gradient
                    android:type="radial"
                    android:centerX="${gradient.cx}"
                    android:centerY="${gradient.cy}"
                    android:gradientRadius="${gradient.r}">
${stops}
                </gradient>
            </aapt:attr>
        </path>`)}
</vector>
`;
    const monochrome = `${header}${group(`        <path
            android:fillColor="#FF000000"
            android:pathData="${pathData}" />`)}
</vector>
`;
    return { foreground, monochrome };
};

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    for (const picture of pictures) {
        const path = join(repoRoot, picture.file);
        mkdirSync(dirname(path), { recursive: true });
        writeFileSync(path, render(picture));
    }
    const { foreground, monochrome } = launcherDrawables();
    for (const module of launcherModules) {
        const drawable = join(repoRoot, module, 'src/main/res/drawable');
        mkdirSync(drawable, { recursive: true });
        writeFileSync(join(drawable, 'ic_launcher_foreground.xml'), foreground);
        writeFileSync(join(drawable, 'ic_launcher_monochrome.xml'), monochrome);
    }
    console.log(`Wrote ${pictures.length} store pictures and the launcher drawables for ${launcherModules.length} apps.`);
}
