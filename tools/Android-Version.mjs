// Reads clients/android/version.properties, the one place the Android version is set (spec native-apps WP12), with the Node standard library only.
// The Gradle files compute versionCode with the same formula; tests/build/android-release.test.mjs keeps the two in step.
// Run from the repo root:
//   node tools/Android-Version.mjs                          prints versionName, versionBuild, versionCode and the release tag
//   node tools/Android-Version.mjs --check-tag <tag>        exits 1 unless <tag> is the release tag for the file's version
//   node tools/Android-Version.mjs --github-output <file>   also appends name, code and tag lines for a workflow step
import { appendFileSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../', import.meta.url));

export const readProperties = (text) => {
    const fields = {};
    for (const line of text.split(/\r?\n/)) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) continue;
        const at = trimmed.indexOf('=');
        if (at < 1) throw new Error(`version.properties has a line without a name and a value: ${trimmed}`);
        fields[trimmed.slice(0, at).trim()] = trimmed.slice(at + 1).trim();
    }
    return fields;
};

// versionCode = major * 1000000 + minor * 10000 + patch * 100 + versionBuild (the same formula as clients/android/*/build.gradle.kts).
export const parseVersion = (text) => {
    const fields = readProperties(text);
    const versionName = fields.versionName;
    const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(versionName ?? '');
    if (!match) throw new Error(`versionName must be major.minor.patch, got '${versionName}'.`);
    const [major, minor, patch] = match.slice(1).map(Number);
    const versionBuild = Number(fields.versionBuild ?? '0');
    if (!Number.isInteger(versionBuild) || versionBuild < 0 || versionBuild > 99) throw new Error('versionBuild must be a whole number from 0 to 99.');
    if (minor > 99 || patch > 99) throw new Error('minor and patch must each be 0 to 99.');
    const versionCode = major * 1_000_000 + minor * 10_000 + patch * 100 + versionBuild;
    if (versionCode > 2_100_000_000) throw new Error('versionCode is above Google Play\'s limit of 2100000000.');
    return { versionName, versionBuild, versionCode, tag: releaseTag(versionName, versionBuild) };
};

export const releaseTag = (versionName, versionBuild) => (versionBuild > 0 ? `android-v${versionName}-build${versionBuild}` : `android-v${versionName}`);

export const readVersion = () => parseVersion(readFileSync(join(repoRoot, 'clients/android/version.properties'), 'utf8'));

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
    const version = readVersion();
    const args = process.argv.slice(2);
    const option = (name) => (args.includes(name) ? args[args.indexOf(name) + 1] : undefined);
    const tag = option('--check-tag');
    if (tag !== undefined && tag !== version.tag) {
        console.error(`The tag '${tag}' does not match clients/android/version.properties, which needs the tag '${version.tag}'. Change the file or the tag.`);
        process.exit(1);
    }
    const output = option('--github-output');
    if (output) appendFileSync(output, `version_name=${version.versionName}\nversion_code=${version.versionCode}\ntag=${version.tag}\n`);
    console.log(`versionName ${version.versionName}, versionBuild ${version.versionBuild}, versionCode ${version.versionCode}, tag ${version.tag}`);
}
