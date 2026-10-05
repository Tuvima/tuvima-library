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
    const names = ['currentCaptionBinding', 'readCaptionTrackChoices', 'selectCaptionTrack'];
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


test('shared chrome anchors the native cue bottom twelve pixels above the seek rail', async () => {
    const chrome = fs.readFileSync(path.join(repositoryRoot, 'src/MediaEngine.Web/wwwroot/js/playback-chrome.js'),'utf8');
    const { cueLine } = await import(`data:text/javascript;base64,${Buffer.from(chrome).toString('base64')}`);
    const video={top:65,height:720}; const rail=650;
    assert.equal(cueLine(rail,video)/100*video.height+video.top,rail-12);
    assert.equal(cueLine(-10,video),0); assert.equal(cueLine(10000,video),95);
    assert.ok(chrome.includes("cue.lineAlign = 'end'"));
    assert.ok(!source.includes('updateAutomaticCaptionPlacement'));
});
