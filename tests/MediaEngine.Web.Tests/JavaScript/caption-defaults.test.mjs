import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// Loads the caption selection functions out of app.js, as video-presentation.test.mjs does, to check how the profile's
// "Show subtitles" default (data-playback-caption-default) is ordered against the title's preferred subtitle, the file's
// default track and the viewer's own pick.
function captionRuntime() {
    const source = readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/app.js', import.meta.url), 'utf8');
    const inventory = source.slice(source.indexOf('    function readCaptionTrackChoices('), source.indexOf('    function currentCaptionBinding('));
    const selection = source.slice(source.indexOf('    function currentCaptionBinding('), source.indexOf('    function audioObserverFor('));
    const sandbox = {};
    vm.runInNewContext(`${inventory}\n${selection}\nglobalThis.api = { synchronizeCaptionSelection, selectCaptionTrack, readCaptionTrackChoices };`, sandbox);
    return sandbox.api;
}

function player({ managedPreferred = false } = {}) {
    // Browsers switch on the track marked default as soon as it exists.
    const english = { kind: 'subtitles', mode: 'showing', label: 'English', language: 'en' };
    const forced = { kind: 'subtitles', mode: 'disabled', label: 'English forced', language: 'en' };
    const managed = { kind: 'subtitles', mode: 'disabled', label: 'FR', language: 'fr' };
    const nodes = [
        { track: english, default: true, dataset: { playbackTrackKey: 'manifest:0' } },
        { track: forced, default: false, dataset: { playbackTrackKey: 'manifest:1' } },
    ];
    const tracks = [english, forced];
    const video = { dataset: { playbackAssetId: 'asset-a', playbackRequestVersion: '1' }, _tuvimaVideoBinding: { profile: 'profile-a', asset: 'asset-a', request: 1 } };
    video.textTracks = tracks;
    video.querySelectorAll = () => nodes;
    const addManaged = () => {
        nodes.push({ track: managed, default: false, dataset: { playbackTrackKey: 'managed:1', ...(managedPreferred ? { playbackPreferred: 'true' } : {}) } });
        tracks.push(managed);
    };
    return { video, english, forced, managed, addManaged };
}

const showing = (...tracks) => tracks.filter(track => track.mode === 'showing').length;

test('without a profile default the file\'s default track keeps showing', () => {
    const api = captionRuntime(), { video, english } = player();
    api.synchronizeCaptionSelection(video);
    assert.equal(english.mode, 'showing');
});

test('the profile default "off" beats the file\'s native default track', () => {
    const api = captionRuntime(), { video, english, forced } = player();
    video.dataset.playbackCaptionDefault = 'off';
    api.synchronizeCaptionSelection(video);
    assert.equal(showing(english, forced), 0);
});

test('the title\'s preferred subtitle beats the profile default, including "off"', () => {
    const api = captionRuntime(), { video, english, managed, addManaged } = player({ managedPreferred: true });
    addManaged();
    video.dataset.playbackCaptionDefault = 'off';
    api.synchronizeCaptionSelection(video);
    assert.equal(managed.mode, 'showing'); assert.equal(english.mode, 'disabled');
    video.dataset.playbackCaptionDefault = 'manifest:1';
    api.synchronizeCaptionSelection(video);
    assert.equal(managed.mode, 'showing');
});

test('a profile default track key beats the native default', () => {
    const api = captionRuntime(), { video, english, forced } = player();
    video.dataset.playbackCaptionDefault = 'manifest:1';
    api.synchronizeCaptionSelection(video);
    assert.equal(forced.mode, 'showing'); assert.equal(english.mode, 'disabled');
});

test('a profile default key whose track arrives late is picked up when it appears', () => {
    const api = captionRuntime(), { video, english, managed, addManaged } = player();
    video.dataset.playbackCaptionDefault = 'managed:1';
    api.synchronizeCaptionSelection(video);
    assert.equal(english.mode, 'showing'); assert.equal(managed.mode, 'disabled');
    addManaged();
    api.synchronizeCaptionSelection(video);
    assert.equal(managed.mode, 'showing'); assert.equal(english.mode, 'disabled');
});

test('a manual pick survives a later change of the profile default', () => {
    const api = captionRuntime(), { video, english, forced } = player();
    video.dataset.playbackCaptionDefault = 'off';
    api.synchronizeCaptionSelection(video);
    assert.equal(api.selectCaptionTrack(video, 'manifest:1', 'asset-a', 1, 'profile-a'), true);
    assert.equal(forced.mode, 'showing');
    video.dataset.playbackCaptionDefault = 'manifest:0';
    api.synchronizeCaptionSelection(video);
    assert.equal(forced.mode, 'showing'); assert.equal(english.mode, 'disabled');
    video.dataset.playbackCaptionDefault = 'off';
    api.synchronizeCaptionSelection(video);
    assert.equal(forced.mode, 'showing');
});

test('a manual Off survives a later profile default key', () => {
    const api = captionRuntime(), { video, english, forced } = player();
    api.synchronizeCaptionSelection(video);
    assert.equal(api.selectCaptionTrack(video, null, 'asset-a', 1, 'profile-a'), true);
    video.dataset.playbackCaptionDefault = 'manifest:1';
    api.synchronizeCaptionSelection(video);
    assert.equal(showing(english, forced), 0);
});

test('when the host withdraws its default (audio switched), the track it turned on gives way to the file default', () => {
    const api = captionRuntime(), { video, english, forced } = player();
    video.dataset.playbackCaptionDefault = 'manifest:1';
    api.synchronizeCaptionSelection(video);
    assert.equal(forced.mode, 'showing'); assert.equal(english.mode, 'disabled');
    delete video.dataset.playbackCaptionDefault;
    api.synchronizeCaptionSelection(video);
    assert.equal(english.mode, 'showing'); assert.equal(forced.mode, 'disabled');
});
