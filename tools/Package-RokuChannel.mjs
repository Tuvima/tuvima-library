// Builds the Roku channel package (a zip with the manifest at its root) from clients/roku, offline, with the Node standard library only.
// Run from the repo root: node tools/Package-RokuChannel.mjs [--out <file.zip>]
// The same package by hand (macOS, Linux or Git Bash): cd clients/roku && zip -r -X ../../library-roku.zip manifest LICENSE source components images
// What goes in: manifest, LICENSE (MPL 2.0 asks for it to travel with the code), source/, components/ and images/.
// What stays out: README.md, store/ (listing text and notes), dist/ and any file starting with a dot.
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createZip } from './lib/zip.mjs';

const repoRoot = fileURLToPath(new URL('../', import.meta.url));
const rokuRoot = join(repoRoot, 'clients/roku');

// Fields a store channel needs; each image field must also name a file inside clients/roku.
export const requiredManifestFields = [
    'title', 'major_version', 'minor_version', 'build_version', 'ui_resolutions',
    'mm_icon_focus_hd', 'mm_icon_focus_fhd', 'splash_screen_hd', 'splash_screen_fhd', 'splash_color',
];

export const readManifest = (text) => {
    const fields = {};
    for (const line of text.split(/\r?\n/)) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) continue;
        const at = trimmed.indexOf('=');
        if (at > 0) fields[trimmed.slice(0, at).trim()] = trimmed.slice(at + 1).trim();
    }
    return fields;
};

const walk = (directory, prefix) => {
    const found = [];
    for (const name of readdirSync(directory).sort()) {
        if (name.startsWith('.')) continue;
        const path = join(directory, name);
        if (statSync(path).isDirectory()) found.push(...walk(path, `${prefix}${name}/`));
        else found.push({ name: `${prefix}${name}`, path });
    }
    return found;
};

export const collectFiles = () => [
    { name: 'manifest', path: join(rokuRoot, 'manifest') },
    { name: 'LICENSE', path: join(rokuRoot, 'LICENSE') },
    ...['source', 'components', 'images'].flatMap((folder) => walk(join(rokuRoot, folder), `${folder}/`)),
];

export const build = () => {
    const fields = readManifest(readFileSync(join(rokuRoot, 'manifest'), 'utf8'));
    for (const field of requiredManifestFields) {
        if (!fields[field]) throw new Error(`The Roku manifest has no ${field}.`);
    }
    for (const [field, value] of Object.entries(fields)) {
        if (value.startsWith('pkg:/') && !existsSync(join(rokuRoot, value.slice(5)))) {
            throw new Error(`The Roku manifest ${field} names ${value}, which is not in clients/roku.`);
        }
    }
    const files = collectFiles();
    if (!files.some((file) => file.name === 'source/main.brs')) throw new Error('source/main.brs is missing.');
    return { fields, files, zip: createZip(files.map((file) => ({ name: file.name, data: readFileSync(file.path) }))) };
};

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
    const outIndex = process.argv.indexOf('--out');
    const out = resolve(outIndex > 0 ? process.argv[outIndex + 1] : join(rokuRoot, 'dist/library-roku.zip'));
    const { fields, files, zip } = build();
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, zip);
    console.log(`Wrote ${out}`);
    console.log(`  ${files.length} files, ${(zip.length / 1024).toFixed(0)} KB, version ${fields.major_version}.${fields.minor_version}.${fields.build_version}`);
    console.log('Install it on your Roku: turn on developer mode, open http://<roku-ip> in a browser, sign in, choose Upload and pick this zip.');
}
