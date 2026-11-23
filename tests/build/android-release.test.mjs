import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseVersion, readVersion, releaseTag } from '../../tools/Android-Version.mjs';

// Android packaging (spec native-apps WP12). A signed build needs secrets and an emulator, neither of which a test has, so this checks
// what can go wrong silently before a person tags a release: the release workflow missing or leaking a secret, the version file not
// parsing, the Gradle files drifting from the one version source, or a key file sneaking into the repository.

const root = fileURLToPath(new URL('../../', import.meta.url));
const read = (relative) => readFileSync(join(root, relative), 'utf8');
const workflowPath = '.github/workflows/android-release.yml';
const apps = ['mobile', 'tv'];

test('the Android release workflow exists, runs on android tags and uploads nothing to Google Play', () => {
    assert.ok(existsSync(join(root, workflowPath)), `${workflowPath} is missing`);
    const workflow = read(workflowPath);
    assert.match(workflow, /tags:\s*\n\s*- "android-v\*"/);
    assert.match(workflow, /:mobile:assembleRelease/);
    assert.match(workflow, /:mobile:bundleRelease/);
    assert.match(workflow, /:tv:assembleRelease/);
    assert.match(workflow, /:tv:bundleRelease/);
    assert.match(workflow, /gh release create/);
    assert.match(workflow, /TUVIMA_ANDROID_SIGNING_REQUIRED: "true"/);
    for (const forbidden of [/play\.google/i, /r0adkll/i, /upload-google-play/i, /androidpublisher/i, /fastlane/i, /supply/i]) {
        assert.doesNotMatch(workflow.replace(/^\s*#.*$/gm, ''), forbidden, `the workflow must not upload to Google Play (${forbidden})`);
    }
});

test('the release workflow reads secrets only through step env blocks and never prints or types one', () => {
    const lines = read(workflowPath).split(/\r?\n/);
    const allowed = ['ANDROID_KEYSTORE_BASE64', 'ANDROID_KEYSTORE_PASSWORD', 'ANDROID_KEY_ALIAS', 'ANDROID_KEY_PASSWORD'];
    const used = new Set();
    for (const [index, line] of lines.entries()) {
        if (/^\s*#/.test(line)) continue;
        if (!/secrets\./.test(line)) continue;
        const match = /^\s+([A-Z][A-Z0-9_]*): \$\{\{ secrets\.([A-Z0-9_]+) \}\}\s*$/.exec(line);
        assert.ok(match, `line ${index + 1} uses a secret outside a plain "NAME: \${{ secrets.NAME }}" env line: ${line.trim()}`);
        assert.ok(allowed.includes(match[2]), `line ${index + 1} names an unknown secret ${match[2]}`);
        used.add(match[2]);
    }
    assert.deepEqual([...used].sort(), [...allowed].sort(), 'every signing secret should be used and no others');

    // Nothing may echo a secret-carrying variable, switch on shell tracing, or put a literal password in the file.
    const text = lines.filter((line) => !/^\s*#/.test(line)).join('\n');
    assert.doesNotMatch(text, /echo[^\n]*(PASSWORD|KEYSTORE_BASE64|KEY_ALIAS)/, 'a secret-carrying variable is echoed');
    assert.doesNotMatch(text, /set -[a-z]*x|set -o xtrace/, 'shell tracing would print secrets');
    assert.doesNotMatch(text, /-----BEGIN/, 'a key is pasted into the workflow');
    assert.doesNotMatch(text, /(storePassword|keyPassword|STORE_PASSWORD|KEY_PASSWORD)\s*[:=]\s*(?!\$\{\{ secrets\.)["']?[A-Za-z0-9]/, 'a password is written into the workflow');
});

test('the release workflow refuses a tag that is not on main or does not match the version file', () => {
    const workflow = read(workflowPath);
    assert.match(workflow, /merge-base --is-ancestor/);
    assert.match(workflow, /tools\/Android-Version\.mjs --check-tag/);
});

test('the version file parses and gives a monotonic versionCode and the matching release tag', () => {
    const version = readVersion();
    assert.match(version.versionName, /^\d+\.\d+\.\d+$/);
    assert.ok(version.versionCode >= 1);
    assert.equal(version.tag, releaseTag(version.versionName, version.versionBuild));

    const code = (name, build = 0) => parseVersion(`versionName=${name}\nversionBuild=${build}\n`).versionCode;
    assert.equal(code('0.1.0'), 10000);
    assert.equal(code('0.1.1'), 10100);
    assert.equal(code('1.0.0'), 1000000);
    const order = [code('0.1.0'), code('0.1.0', 1), code('0.1.1'), code('0.2.0'), code('0.99.99', 99), code('1.0.0'), code('2.3.4')];
    assert.deepEqual(order, [...order].sort((a, b) => a - b), 'a higher version must have a higher versionCode');
    assert.equal(parseVersion('versionName=0.1.0\nversionBuild=2\n').tag, 'android-v0.1.0-build2');
    assert.equal(parseVersion('versionName=0.1.0\n').tag, 'android-v0.1.0');
    assert.throws(() => parseVersion('versionName=0.1\n'));
    assert.throws(() => parseVersion('versionName=0.100.0\n'));
    assert.throws(() => parseVersion('versionName=0.1.0\nversionBuild=100\n'));
});

test('both Android apps take their version from version.properties and sign only from environment variables', () => {
    for (const app of apps) {
        const gradle = read(`clients/android/${app}/build.gradle.kts`);
        assert.match(gradle, /versionCode = appVersionCode/, `${app} must not type its own versionCode`);
        assert.match(gradle, /versionName = appVersionName/, `${app} must not type its own versionName`);
        assert.match(gradle, /"version\.properties"/);
        // The same formula as tools/Android-Version.mjs.
        assert.match(gradle, /appVersionParts\[0\] \* 1_000_000 \+ appVersionParts\[1\] \* 10_000 \+ appVersionParts\[2\] \* 100 \+ appVersionBuild/);
        for (const variable of ['TUVIMA_ANDROID_KEYSTORE_FILE', 'TUVIMA_ANDROID_KEYSTORE_PASSWORD', 'TUVIMA_ANDROID_KEY_ALIAS', 'TUVIMA_ANDROID_KEY_PASSWORD']) {
            assert.match(gradle, new RegExp(`environmentVariable\\("${variable}"\\)`), `${app} does not read ${variable}`);
        }
        // Signing values are only ever the variables read above, never a literal.
        assert.match(gradle, /storePassword = signingKeystorePassword\b/);
        assert.match(gradle, /keyPassword = signingKeyPassword\b/);
        assert.doesNotMatch(gradle, /(storePassword|keyPassword|keyAlias)\s*=\s*"/, `${app} has a literal signing value`);
        // Without secrets the release build stays unsigned instead of failing, so pull requests keep passing.
        assert.match(gradle, /if \(releaseSigningAvailable\) signingConfig = /);
    }
});

test('the pull-request Android build still builds both apps without any secret', () => {
    const workflow = read('.github/workflows/native-clients.yml');
    assert.match(workflow, /:tv:assembleDebug :mobile:assembleDebug/);
    assert.match(workflow, /:tv:assembleRelease :mobile:assembleRelease/);
    assert.doesNotMatch(workflow, /secrets\.ANDROID/);
});

test('no signing key file or password is committed under clients', () => {
    const gitignore = read('.gitignore');
    for (const pattern of ['*.jks', '*.keystore', '*.p12']) assert.ok(gitignore.split(/\r?\n/).includes(pattern), `.gitignore does not list ${pattern}`);
    for (const app of apps) {
        const gradle = read(`clients/android/${app}/build.gradle.kts`);
        assert.doesNotMatch(gradle, /\.(jks|keystore|p12)"/, `${app} names a key file`);
    }
    assert.doesNotMatch(read('clients/android/version.properties'), /password|secret|key/i);
});

test('the store text and the by-hand checklist exist beside the other native-app paperwork', () => {
    for (const file of ['engineering/validation/native-android-play-listing-2026-11.md', 'engineering/validation/native-android-play-submission-2026-11.md']) {
        assert.ok(existsSync(join(root, file)), `${file} is missing`);
    }
    const listing = read('engineering/validation/native-android-play-listing-2026-11.md');
    assert.match(listing, /needs a Library server/i);
    assert.match(listing, /\$4\.99/);
});
