const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const repositoryRoot = path.resolve(__dirname, '../../..');
const source = fs.readFileSync(path.join(repositoryRoot, 'src/MediaEngine.Web/wwwroot/app.js'), 'utf8').replace(/\r\n/g, '\n');

function extractFunction(name) {
    const start = source.indexOf(`function ${name}(`);
    assert.ok(start >= 0, `${name} is defined`);
    const open = source.indexOf('{', start);
    let depth = 0;
    for (let index = open; index < source.length; index++) {
        if (source[index] === '{') depth++;
        if (source[index] === '}') depth--;
        if (depth === 0) return source.slice(start, index + 1);
    }
    throw new Error(`Could not find end of ${name}`);
}

function loadHelpers(nativeDefaultLine = 'auto') {
    const sandbox = {
        document: { pictureInPictureElement: null },
        window: {
            innerWidth: 390,
            VTTCue: class { constructor() { this.line = nativeDefaultLine; this.lineAlign = 'start'; this.snapToLines = true; } }
        },
        getComputedStyle: element => ({ display: element.display || 'block' }),
        module: { exports: {} }
    };
    const names = ['getNativeDefaultCuePlacement', 'readCaptionTrackChoices', 'estimateCaptionCueHeight', 'selectCaptionTrack', 'updateAutomaticCaptionPlacement', 'restoreAutomaticCaptionPlacement'];
    const functions = names.map(extractFunction).join('\n');
    vm.runInNewContext(`var nativeDefaultCuePlacementLoaded = false; var nativeDefaultCuePlacement = null;\n${functions}\nmodule.exports = { ${names.join(', ')} };`, sandbox);
    return sandbox.module.exports;
}

test('native caption inventory reports active track and excludes metadata tracks', () => {
    const { readCaptionTrackChoices } = loadHelpers();
    const english = { kind: 'subtitles', label: 'ENG', language: 'eng', mode: 'showing' };
    const commentary = { kind: 'subtitles', label: 'ENG', language: 'eng', mode: 'disabled' };
    const metadata = { kind: 'metadata', label: 'Chapters', language: '', mode: 'showing' };
    const element = {
        textTracks: [english, commentary, metadata],
        _tuvimaHls: null,
        querySelectorAll: () => [
            { track: english, dataset: { playbackTrackKey: 'manifest:0' } },
            { track: commentary, dataset: { playbackTrackKey: 'managed:book' } }
        ]
    };

    assert.deepEqual(JSON.parse(JSON.stringify(readCaptionTrackChoices(element).map(({ key, label, selected }) => ({ key, label, selected })))), [
        { key: 'manifest:0', label: 'ENG', selected: true },
        { key: 'managed:book', label: 'ENG', selected: false }
    ]);
});

test('caption choices can turn native captions off and select an actual track', () => {
    const { selectCaptionTrack } = loadHelpers();
    const first = { kind: 'subtitles', label: 'ENG', language: 'eng', mode: 'showing' };
    const second = { kind: 'subtitles', label: 'SPA', language: 'spa', mode: 'disabled' };
    const nodes = [
        { track: first, dataset: { playbackTrackKey: 'manifest:0' } },
        { track: second, dataset: { playbackTrackKey: 'browser:1' } }
    ];
    const element = { textTracks: [first, second], _tuvimaHls: null, querySelectorAll: () => nodes };

    assert.equal(selectCaptionTrack(element, null), true);
    assert.equal(first.mode, 'disabled');
    assert.equal(second.mode, 'disabled');
    assert.equal(selectCaptionTrack(element, 'browser:1'), true);
    assert.equal(first.mode, 'disabled');
    assert.equal(second.mode, 'showing');
    assert.equal(selectCaptionTrack(element, 'provider:0'), false);
});

test('HLS tracks merge with matching native rows and selection controls the real HLS track', () => {
    const { readCaptionTrackChoices, selectCaptionTrack } = loadHelpers();
    const native = { kind: 'subtitles', label: 'English', language: 'en', mode: 'disabled' };
    const hls = { subtitleTrack: 0, subtitleDisplay: true, subtitleTracks: [{ name: 'English', lang: 'en' }, { name: 'Français', lang: 'fr' }] };
    const element = { textTracks: [native], _tuvimaHls: hls, querySelectorAll: () => [{ track: native, dataset: { playbackTrackKey: 'manifest:0' } }] };

    const choices = readCaptionTrackChoices(element);
    assert.deepEqual(JSON.parse(JSON.stringify(choices.map(choice => choice.key))), ['manifest:0', 'hls:1']);
    assert.equal(choices[0].selected, true);
    assert.equal(selectCaptionTrack(element, 'hls:1'), true);
    assert.equal(hls.subtitleTrack, 1);
    assert.equal(hls.subtitleDisplay, true);
    assert.equal(selectCaptionTrack(element, null), true);
    assert.equal(hls.subtitleTrack, -1);
    assert.equal(hls.subtitleDisplay, false);
});

test('same-label native and HLS tracks retain distinct source indices', () => {
    const { readCaptionTrackChoices, selectCaptionTrack } = loadHelpers();
    const first = { kind: 'subtitles', label: 'ENG', language: 'en', mode: 'disabled' };
    const second = { kind: 'subtitles', label: 'ENG', language: 'en', mode: 'disabled' };
    const hls = { subtitleTrack: 1, subtitleDisplay: true, subtitleTracks: [{ name: 'ENG', lang: 'en' }, { name: 'ENG', lang: 'en' }] };
    const element = {
        textTracks: [first, second], _tuvimaHls: hls,
        querySelectorAll: () => [
            { track: first, dataset: { playbackTrackKey: 'manifest:0' } },
            { track: second, dataset: { playbackTrackKey: 'manifest:1' } }
        ]
    };

    const choices = readCaptionTrackChoices(element);
    assert.deepEqual(JSON.parse(JSON.stringify(choices.map(choice => [choice.key, choice.hlsIndex ?? null, choice.selected]))), [
        ['manifest:0', 0, false],
        ['manifest:1', 1, true]
    ]);
    assert.equal(selectCaptionTrack(element, 'manifest:0'), true);
    assert.equal(hls.subtitleTrack, 0);
    assert.equal(selectCaptionTrack(element, 'manifest:1'), true);
    assert.equal(hls.subtitleTrack, 1);
});

test('automatic captions reserve space above visible controls and restore when controls disappear', () => {
    const { updateAutomaticCaptionPlacement, estimateCaptionCueHeight, restoreAutomaticCaptionPlacement } = loadHelpers();
    const automatic = { line: 'auto', lineAlign: 'start', snapToLines: true, text: 'One short automatic caption' };
    const authored = { line: 22, lineAlign: 'center', snapToLines: true };
    const track = { kind: 'subtitles', activeCues: [automatic, authored] };
    const controls = { display: 'block', getBoundingClientRect: () => ({ top: 700 }) };
    const host = { classList: { contains: name => name === 'is-expanded' }, querySelector: () => controls };
    const element = {
        tagName: 'VIDEO', textTracks: [track], closest: () => host,
        getBoundingClientRect: () => ({ top: 0, height: 800 })
    };
    const observer = { automaticCues: new Map() };

    updateAutomaticCaptionPlacement(element, observer);
    assert.equal(automatic.line, Math.floor((700 - 28 - estimateCaptionCueHeight(automatic, 800)) / 800 * 100));
    assert.equal(automatic.lineAlign, 'start');
    assert.equal(automatic.snapToLines, false);
    assert.equal(authored.line, 22);

    host.classList.contains = () => false;
    updateAutomaticCaptionPlacement(element, observer);
    assert.equal(automatic.line, 'auto');
    assert.equal(automatic.lineAlign, 'start');
    assert.equal(automatic.snapToLines, true);
    assert.equal(authored.line, 22);

    updateAutomaticCaptionPlacement(element, observer);
    restoreAutomaticCaptionPlacement(observer);
    assert.equal(automatic.line, 'auto');
    assert.equal(observer.automaticCues.size, 0);
});

test('native default -1 multiline cue is moved above controls while authored numeric cues stay put', () => {
    const { updateAutomaticCaptionPlacement, estimateCaptionCueHeight } = loadHelpers(-1);
    const automatic = { line: -1, lineAlign: 'start', snapToLines: true, size: 100, text: 'First line\nSecond line' };
    const authored = { line: 22, lineAlign: 'center', snapToLines: true, size: 100, text: 'Authored placement' };
    const track = { kind: 'subtitles', activeCues: [automatic, authored] };
    const controlsTop = 635.497;
    const videoHeight = 843.636;
    const controls = { display: 'block', getBoundingClientRect: () => ({ top: controlsTop }) };
    const host = { classList: { contains: name => name === 'is-expanded' }, querySelector: () => controls };
    const element = {
        tagName: 'VIDEO', textTracks: [track], closest: () => host,
        getBoundingClientRect: () => ({ top: 0, width: 390, height: videoHeight })
    };
    const observer = { automaticCues: new Map() };

    updateAutomaticCaptionPlacement(element, observer);

    const reservedBottom = automatic.line / 100 * videoHeight + estimateCaptionCueHeight(automatic, 390);
    assert.ok(reservedBottom <= controlsTop - 28);
    assert.equal(automatic.lineAlign, 'start');
    assert.equal(automatic.snapToLines, false);
    assert.equal(authored.line, 22);
    assert.equal(authored.lineAlign, 'center');
});

test('numeric -1 is preserved when the browser default is automatic', () => {
    const { updateAutomaticCaptionPlacement } = loadHelpers('auto');
    const explicit = { line: -1, lineAlign: 'start', snapToLines: true, text: 'Authored negative line' };
    const track = { kind: 'subtitles', activeCues: [explicit] };
    const controls = { display: 'block', getBoundingClientRect: () => ({ top: 635 }) };
    const host = { classList: { contains: name => name === 'is-expanded' }, querySelector: () => controls };
    const element = { tagName: 'VIDEO', textTracks: [track], closest: () => host, getBoundingClientRect: () => ({ top: 0, width: 390, height: 844 }) };
    const observer = { automaticCues: new Map() };

    updateAutomaticCaptionPlacement(element, observer);

    assert.equal(explicit.line, -1);
    assert.equal(observer.automaticCues.size, 0);
});

test('long unbroken caption text reserves every estimated wrapped line', () => {
    const { estimateCaptionCueHeight } = loadHelpers();
    const cue = { size: 100, text: 'x'.repeat(120) };

    assert.equal(estimateCaptionCueHeight(cue, 390), 3 * 16 * 1.35);
});
