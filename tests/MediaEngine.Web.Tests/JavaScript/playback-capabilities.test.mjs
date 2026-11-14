import assert from 'node:assert/strict';
import test from 'node:test';

import { buildReport, canOpen, measure } from '../../../src/MediaEngine.Web/wwwroot/js/playback-capabilities.js';

// A browser stand-in. `decodes` maps a codec marker found in the content type to what decodingInfo answers for it; `opens` lists the
// container types a video element says it can open.
function browser({ decodes = {}, opens = [], audioOpens = [], hdr = false, withMediaCapabilities = true } = {}) {
    const mediaCapabilities = {
        async decodingInfo(configuration) {
            const contentType = configuration.video?.contentType ?? configuration.audio?.contentType ?? '';
            const key = Object.keys(decodes).find(marker => contentType.includes(marker));
            const answer = key ? decodes[key] : { supported: false, smooth: false, powerEfficient: false };
            // A 4K picture is only smooth when the answer says so for 4K.
            const height = configuration.video?.height ?? 0;
            return height > 1080 ? { ...answer, smooth: answer.smooth && answer.smooth4k === true } : answer;
        },
    };
    const element = types => ({ canPlayType: type => (types.some(known => type.startsWith(known)) ? 'maybe' : '') });
    return {
        navigator: withMediaCapabilities ? { mediaCapabilities } : {},
        document: { createElement: () => element([...opens, ...audioOpens]) },
        matchMedia: () => ({ matches: hdr }),
    };
}

const hardwareH264 = { supported: true, smooth: true, powerEfficient: true };
const softwareH264 = { supported: true, smooth: true, powerEfficient: false };

test('a browser without media capability checks reports nothing, so the Engine keeps its fixed web list', async () => {
    assert.equal(await measure(browser({ withMediaCapabilities: false })), null);
    assert.equal(await measure({}), null);
});

test('an MKV is listed only when the browser says it can open one', async () => {
    const decodes = { avc1: hardwareH264, 'mp4a.40.2': hardwareH264 };

    const withMkv = await measure(browser({ decodes, opens: ['video/mp4', 'video/x-matroska'] }));
    const withoutMkv = await measure(browser({ decodes, opens: ['video/mp4'] }));

    assert.ok(withMkv.containers.includes('mkv'));
    assert.ok(withMkv.containers.includes('mp4'));
    assert.ok(!withoutMkv.containers.includes('mkv'));
});

test('hardware decoding shows as power efficient, and software-only codecs are not flagged', async () => {
    const report = await measure(browser({
        decodes: { avc1: hardwareH264, hvc1: { supported: true, smooth: true, powerEfficient: true }, 'vp09': softwareH264 },
        opens: ['video/mp4'],
    }));

    assert.deepEqual(report.video_codecs, ['h264', 'hevc', 'vp9']);
    assert.deepEqual(report.hardware_decode_video_codecs, ['h264', 'hevc']);
    assert.equal(report.schema_version, 2);
});

test('a codec the browser does not decode is left out, so the Engine converts it', async () => {
    const report = await measure(browser({ decodes: { avc1: hardwareH264 }, opens: ['video/mp4'] }));

    assert.deepEqual(report.video_codecs, ['h264']);
    assert.ok(!report.video_codecs.includes('hevc'));
});

test('a codec that decodes but is not smooth is left out, except H.264 which every browser plays', async () => {
    const stutter = { supported: true, smooth: false, powerEfficient: false };
    const report = await measure(browser({ decodes: { avc1: stutter, hvc1: stutter }, opens: ['video/mp4'] }));

    assert.deepEqual(report.video_codecs, ['h264']);
});

test('ten-bit support is reported per codec', async () => {
    const report = await measure(browser({
        // Main 10 HEVC ("hvc1.2.4") and High 10 H.264 ("avc1.6e") are the ten-bit probes.
        decodes: { 'avc1.640028': hardwareH264, 'hvc1.1.6': hardwareH264, 'hvc1.2.4': hardwareH264 },
        opens: ['video/mp4'],
    }));

    assert.deepEqual(report.ten_bit_video_codecs, ['hevc']);
});

test('the tallest smooth picture sets the size limit, and HDR needs an HDR screen and a ten-bit codec', async () => {
    const fourK = { ...hardwareH264, smooth4k: true };
    const decodes = { avc1: fourK, 'hvc1.1.6': fourK, 'hvc1.2.4': fourK };

    const hdr = await measure(browser({ decodes, opens: ['video/mp4'], hdr: true }));
    const sdr = await measure(browser({ decodes, opens: ['video/mp4'], hdr: false }));
    const only1080 = await measure(browser({ decodes: { avc1: hardwareH264 }, opens: ['video/mp4'] }));

    assert.equal(hdr.max_height, 2160);
    assert.equal(hdr.supports_hdr, true);
    assert.equal(sdr.supports_hdr, false);
    assert.equal(only1080.max_height, 1080);
});

test('audio containers and documents ride along with the video containers', async () => {
    const report = await measure(browser({
        decodes: { avc1: hardwareH264, 'mp4a.40.2': hardwareH264, 'audio/mpeg': hardwareH264 },
        opens: ['video/mp4'],
        audioOpens: ['audio/mpeg', 'audio/wav'],
    }));

    assert.ok(report.containers.includes('mp3'));
    assert.ok(report.containers.includes('wav'));
    assert.ok(report.containers.includes('epub'));
    assert.ok(report.audio_codecs.includes('aac'));
    assert.ok(report.audio_codecs.includes('pcm_s16le'));
});

test('a decodingInfo that throws for a configuration counts as not supported, never as a crash', async () => {
    const environment = browser({ opens: ['video/mp4'] });
    environment.navigator.mediaCapabilities.decodingInfo = async () => { throw new TypeError('unknown type'); };

    const report = await measure(environment);

    assert.deepEqual(report.video_codecs, []);
});

test('canOpen counts "probably" and "maybe" and nothing else', () => {
    assert.equal(canOpen({ canPlayType: () => 'probably' }, 'x'), true);
    assert.equal(canOpen({ canPlayType: () => 'maybe' }, 'x'), true);
    assert.equal(canOpen({ canPlayType: () => '' }, 'x'), false);
    assert.equal(canOpen({ canPlayType: () => { throw new Error('no'); } }, 'x'), false);
});

test('buildReport carries nothing but codecs, containers, sizes, HDR and audio channels', () => {
    const report = buildReport({
        video: { h264: { supported: true, smooth: true, efficient: false, tenBit: false, maxHeight: 1080 } },
        audio: { aac: true },
        surround: true,
        hdrDisplay: false,
        containers: ['mp4'],
        audioContainers: [],
    });

    assert.deepEqual(Object.keys(report).sort(), [
        'audio_codecs', 'containers', 'hardware_decode_video_codecs', 'max_audio_channels', 'max_height', 'schema_version',
        'subtitle_formats', 'supports_hdr', 'supports_offline_downloads', 'supports_playback_speed', 'ten_bit_video_codecs', 'video_codecs',
    ]);
    assert.equal(report.max_audio_channels, 6);
});
