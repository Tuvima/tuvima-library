import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launcherModules, pictures, render } from '../../tools/Make-AndroidStoreImages.mjs';

// Google Play pictures and the launcher icon for the Android apps (spec native-apps WP12 follow-up).
// The sizes are written out here on purpose, apart from tools/Make-AndroidStoreImages.mjs, so a change to one is noticed against the other.

const root = fileURLToPath(new URL('../../', import.meta.url));
const read = (relative) => readFileSync(join(root, relative), 'utf8');
const pngInfo = (relative) => {
    const bytes = readFileSync(join(root, relative));
    assert.deepEqual([...bytes.subarray(0, 8)], [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], `${relative} is not a PNG`);
    return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20), size: bytes.length };
};

const expected = {
    'clients/android/store/hi-res-icon-512x512.png': [512, 512],
    'clients/android/store/feature-graphic-1024x500.png': [1024, 500],
};

test('the Play hi-res icon and feature graphic exist at the exact sizes Play asks for and under its 1 MB limit', () => {
    assert.deepEqual(pictures.map((picture) => picture.file).sort(), Object.keys(expected).sort());
    for (const [file, [width, height]] of Object.entries(expected)) {
        assert.ok(existsSync(join(root, file)), `${file} is missing: run node tools/Make-AndroidStoreImages.mjs`);
        const info = pngInfo(file);
        assert.deepEqual([info.width, info.height], [width, height], `${file} must be ${width} x ${height}`);
        assert.ok(info.size <= 1024 * 1024, `${file} is over Play's 1 MB limit`);
    }
});

test('the pictures on disk are what the script draws now', () => {
    for (const picture of pictures) {
        assert.deepEqual(readFileSync(join(root, picture.file)), render(picture), `${picture.file} is out of date: run node tools/Make-AndroidStoreImages.mjs`);
    }
});

test('both Android apps name an adaptive launcher icon with background, foreground and monochrome layers', () => {
    for (const module of launcherModules) {
        const manifest = read(`${module}/src/main/AndroidManifest.xml`);
        assert.match(manifest, /android:icon="@mipmap\/ic_launcher"/, `${module} manifest has no android:icon`);
        assert.match(manifest, /android:roundIcon="@mipmap\/ic_launcher_round"/, `${module} manifest has no android:roundIcon`);
        for (const name of ['ic_launcher', 'ic_launcher_round']) {
            const adaptive = read(`${module}/src/main/res/mipmap-anydpi-v26/${name}.xml`);
            assert.match(adaptive, /<background android:drawable="@color\/ic_launcher_background"/);
            assert.match(adaptive, /<foreground android:drawable="@drawable\/ic_launcher_foreground"/);
            assert.match(adaptive, /<monochrome android:drawable="@drawable\/ic_launcher_monochrome"/);
        }
        for (const file of ['drawable/ic_launcher_foreground.xml', 'drawable/ic_launcher_monochrome.xml', 'values/ic_launcher_background.xml']) {
            assert.ok(existsSync(join(root, module, 'src/main/res', file)), `${module} is missing res/${file}`);
        }
        assert.match(read(`${module}/src/main/res/values/ic_launcher_background.xml`), /#101115/);
    }
});

test('the TV app keeps its launcher banner', () => {
    assert.match(read('clients/android/tv/src/main/AndroidManifest.xml'), /android:banner="@drawable\/tv_banner"/);
    assert.ok(existsSync(join(root, 'clients/android/tv/src/main/res/drawable/tv_banner.xml')));
});
