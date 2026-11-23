import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build, readManifest, requiredManifestFields } from '../../tools/Package-RokuChannel.mjs';
import { images } from '../../tools/Make-TvStoreImages.mjs';
import { readZip } from '../../tools/lib/zip.mjs';

// Store paperwork for the TV apps (spec native-apps WP13). None of the three stores can be reached from CI, so this checks what can go
// wrong silently before a person uploads: a manifest field or file missing, a picture at the wrong size, a package that holds the wrong files.
// The sizes below are written out here on purpose, apart from tools/Make-TvStoreImages.mjs, so a change to one is noticed against the other.

const root = fileURLToPath(new URL('../../', import.meta.url));
const read = (relative) => readFileSync(join(root, relative), 'utf8');
const pngInfo = (relative) => {
    const bytes = readFileSync(join(root, relative));
    assert.deepEqual([...bytes.subarray(0, 8)], [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], `${relative} is not a PNG`);
    return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20), colorType: bytes[25] };
};

const rokuPictures = {
    mm_icon_focus_hd: [336, 210],
    mm_icon_focus_fhd: [540, 405],
    splash_screen_hd: [1280, 720],
    splash_screen_fhd: [1920, 1080],
};

test('the Roku manifest has every field a store channel needs and names pictures that exist', () => {
    const fields = readManifest(read('clients/roku/manifest'));
    for (const field of requiredManifestFields) assert.ok(fields[field], `manifest has no ${field}`);
    assert.equal(fields.title, 'Library');
    assert.match(fields.splash_color, /^#[0-9A-Fa-f]{6}$/);
    assert.match(fields.ui_resolutions, /\bhd\b/);
    assert.match(fields.ui_resolutions, /\bfhd\b/);
    for (const [field, [width, height]] of Object.entries(rokuPictures)) {
        assert.match(fields[field] ?? '', /^pkg:\/images\/.+\.png$/, `${field} must point into images/`);
        const relative = `clients/roku/${fields[field].slice(5)}`;
        assert.ok(existsSync(join(root, relative)), `${field} names ${fields[field]}, which is missing`);
        const info = pngInfo(relative);
        assert.deepEqual([info.width, info.height], [width, height], `${relative} must be ${width} x ${height}`);
    }
});

test('the Roku version fields are whole numbers', () => {
    const fields = readManifest(read('clients/roku/manifest'));
    for (const part of ['major_version', 'minor_version', 'build_version']) assert.match(fields[part], /^\d+$/, `${part} must be a whole number`);
});

test('the Roku package holds the channel files and nothing else', () => {
    const { zip, files } = build();
    const names = readZip(zip).map((entry) => entry.name);
    assert.equal(names.length, files.length);
    assert.equal(names[0], 'manifest', 'the manifest must be at the root of the zip');
    for (const required of ['LICENSE', 'source/main.brs', 'components/MainScene.xml', 'components/MainScene.brs', 'images/channel-poster-fhd.png', 'images/splash-fhd.png']) {
        assert.ok(names.includes(required), `${required} is not in the package`);
    }
    for (const name of names) {
        assert.ok(/^(manifest|LICENSE|(source|components|images)\/[^/]+)$/.test(name), `${name} should not be in the channel package`);
        assert.ok(!name.includes('\\'), `${name} must use forward slashes`);
    }
    for (const entry of readZip(zip)) {
        assert.deepEqual(entry.data, readFileSync(join(root, 'clients/roku', entry.name)), `${entry.name} differs from the file on disk`);
    }
});

test('the picture list builds exactly the files the three stores need, at the sizes they ask for', () => {
    const expected = {
        'clients/roku/images/channel-poster-hd.png': [336, 210],
        'clients/roku/images/channel-poster-fhd.png': [540, 405],
        'clients/roku/images/splash-hd.png': [1280, 720],
        'clients/roku/images/splash-fhd.png': [1920, 1080],
        'clients/tv-web/platforms/tizen/store/store-icon-512x423.png': [512, 423],
        'clients/tv-web/platforms/webos/splash.png': [1920, 1080],
    };
    assert.deepEqual(images.map((image) => image.file).sort(), Object.keys(expected).sort());
    for (const [file, [width, height]] of Object.entries(expected)) {
        const info = pngInfo(file);
        assert.deepEqual([info.width, info.height], [width, height], `${file} must be ${width} x ${height}`);
        assert.equal(info.colorType, 2, `${file} should be plain RGB, with no see-through areas`);
    }
});

test('the Tizen package describes the app in plain words and has its icons at the right size', () => {
    const config = read('clients/tv-web/platforms/tizen/config.xml');
    assert.match(config, /<name>Library<\/name>/);
    assert.match(config, /<description>[^<]*needs a Library server on your network[^<]*<\/description>/i);
    assert.match(config, /<author\b[^>]*>[^<]+<\/author>/);
    assert.match(config, /<license\b[^>]*>MPL-2\.0<\/license>/);
    assert.match(config, /<tizen:profile name="tv"\/>/);
    assert.match(config, /<icon src="icon\.png"\/>/);
    assert.match(config, /tizen:application id="[A-Za-z0-9]{10}\.[A-Za-z0-9]+" package="[A-Za-z0-9]{10}"/, 'a Tizen package id is ten letters or digits');
    for (const privilege of ['internet', 'tv.inputdevice']) assert.ok(config.includes(`http://tizen.org/privilege/${privilege}`), `config.xml lacks the ${privilege} privilege`);
    assert.match(config, /<widget[^>]*\sversion="\d+\.\d+\.\d+"/);
    const icon = pngInfo('clients/tv-web/platforms/tizen/icon.png');
    assert.deepEqual([icon.width, icon.height], [117, 117]);
});

test('the webOS package names every picture it uses and has the sizes LG asks for', () => {
    const info = JSON.parse(read('clients/tv-web/platforms/webos/appinfo.json'));
    for (const field of ['id', 'version', 'vendor', 'type', 'main', 'title', 'appDescription', 'icon', 'largeIcon', 'bgImage', 'resolution']) {
        assert.ok(info[field], `appinfo.json has no ${field}`);
    }
    assert.equal(info.title, 'Library');
    assert.match(info.version, /^\d+\.\d+\.\d+$/);
    assert.match(info.appDescription, /needs a Library server on your network/i);
    const sizes = { icon: [80, 80], largeIcon: [130, 130], bgImage: [1920, 1080] };
    for (const [field, [width, height]] of Object.entries(sizes)) {
        const size = pngInfo(`clients/tv-web/platforms/webos/${info[field]}`);
        assert.deepEqual([size.width, size.height], [width, height], `${field} (${info[field]}) must be ${width} x ${height}`);
    }
    const script = read('clients/tv-web/platforms/webos/package.ps1');
    assert.ok(script.includes(`'${info.bgImage}'`), 'package.ps1 must copy the splash picture into the package');
});

test('the listing text says the app needs a server, is free, and names the app Library', () => {
    const listing = read('engineering/validation/native-tv-store-listing-2026-11.md');
    const oneLine = listing.match(/\*\*One line \(80 characters or fewer\):\*\*\s*\n(.+)\n/)?.[1];
    assert.ok(oneLine, 'the one-line description is missing');
    assert.ok(oneLine.length <= 80, `the one-line description is ${oneLine.length} characters`);
    assert.match(oneLine, /Library server/);
    assert.match(listing, /needs a Library server on your network/i);
    assert.match(listing, /\| Price \| Free\./);
    assert.match(listing, /\| App name \| Library \|/);
    assert.ok(!/\bTuvima Library\b/.test(listing), 'the app is called Library, not Tuvima Library');
});

test('the checklist names every step a person does by hand and keeps the Roku sign-in question open', () => {
    const checklist = read('engineering/validation/native-tv-store-submission-2026-11.md');
    for (const heading of ['## A. Roku', '## B. Samsung', '## C. LG', '## Decisions you owe']) assert.ok(checklist.includes(heading), `${heading} is missing`);
    assert.match(checklist, /### A5\. Roku's sign-in question \(open, not guessed\)/);
    const rokuReadme = read('clients/roku/README.md');
    assert.match(rokuReadme, /### Open question for Roku: sign-in/);
});
