// What this browser can actually play (playback-path spec D12). The Dashboard is not a paired app, so without this the Engine would
// assume a fixed "web" list. The browser's own media capability checks say which codecs it decodes (supported, smooth, and whether
// the decoder is power efficient, which is how a hardware decoder shows), and a video element that is never put on the page says which containers it opens, MKV
// included. The result has the shape of the Engine's capabilities record, version 2, so it can be sent as it is.

// One representative stream per codec (a typical 1080p file), plus the 10-bit and HDR forms.
const videoProbes = [
    { codec: 'h264', contentType: 'video/mp4; codecs="avc1.640028"', tenBit: 'video/mp4; codecs="avc1.6e0028"' },
    { codec: 'hevc', contentType: 'video/mp4; codecs="hvc1.1.6.L120.90"', tenBit: 'video/mp4; codecs="hvc1.2.4.L120.90"' },
    { codec: 'vp9', contentType: 'video/webm; codecs="vp09.00.40.08"', tenBit: 'video/webm; codecs="vp09.02.40.10"' },
    { codec: 'av1', contentType: 'video/mp4; codecs="av01.0.08M.08"', tenBit: 'video/mp4; codecs="av01.0.08M.10"' },
];

const audioProbes = [
    { codec: 'aac', contentType: 'audio/mp4; codecs="mp4a.40.2"' },
    { codec: 'mp3', contentType: 'audio/mpeg' },
    { codec: 'opus', contentType: 'audio/webm; codecs="opus"' },
    { codec: 'vorbis', contentType: 'audio/webm; codecs="vorbis"' },
    { codec: 'flac', contentType: 'audio/flac' },
    { codec: 'ac3', contentType: 'audio/mp4; codecs="ac-3"' },
    { codec: 'eac3', contentType: 'audio/mp4; codecs="ec-3"' },
];

// Containers asked of a video element, with a codec pair the container normally carries. MKV is listed only when the browser says so.
const videoContainerProbes = [
    { container: 'mp4', type: 'video/mp4; codecs="avc1.640028, mp4a.40.2"' },
    { container: 'm4v', type: 'video/mp4; codecs="avc1.640028, mp4a.40.2"' },
    { container: 'webm', type: 'video/webm; codecs="vp9, opus"' },
    { container: 'mkv', type: 'video/x-matroska; codecs="avc1.640028, mp4a.40.2"' },
];

const audioContainerProbes = [
    { container: 'mp3', type: 'audio/mpeg' },
    { container: 'm4a', type: 'audio/mp4; codecs="mp4a.40.2"' },
    { container: 'm4b', type: 'audio/mp4; codecs="mp4a.40.2"' },
    { container: 'aac', type: 'audio/aac' },
    { container: 'ogg', type: 'audio/ogg; codecs="vorbis"' },
    { container: 'wav', type: 'audio/wav' },
    { container: 'flac', type: 'audio/flac' },
];

// Documents the Engine lists with a browser's containers; they are opened by the readers, not by a video element.
const documentContainers = ['epub', 'pdf', 'cbz'];

const heights = [2160, 1440, 1080];
const sizes = { 2160: [3840, 2160], 1440: [2560, 1440], 1080: [1920, 1080] };

function videoConfig(contentType, height = 1080) {
    const [width, tall] = sizes[height] ?? sizes[1080];
    return { type: 'file', video: { contentType, width, height: tall, bitrate: height > 1080 ? 25_000_000 : 8_000_000, framerate: 30 } };
}

async function decode(mediaCapabilities, configuration) {
    try {
        const info = await mediaCapabilities.decodingInfo(configuration);
        return { supported: info?.supported === true, smooth: info?.smooth === true, efficient: info?.powerEfficient === true };
    } catch {
        // A browser that refuses a configuration (an unknown type string) is telling us it cannot play it.
        return { supported: false, smooth: false, efficient: false };
    }
}

/** True when a video element reports it can open this type: "probably" or "maybe" both count, "" does not. */
export function canOpen(element, type) {
    try {
        const answer = element.canPlayType(type);
        return answer === 'probably' || answer === 'maybe';
    } catch {
        return false;
    }
}

/**
 * Turns the raw answers into the Engine's capabilities record (version 2). Pure, so it can be tested without a browser.
 * @param {{ video: Record<string, {supported: boolean, smooth: boolean, efficient: boolean, tenBit: boolean, maxHeight: number}>,
 *           audio: Record<string, boolean>, surround: boolean, hdrDisplay: boolean,
 *           containers: string[], audioContainers: string[] }} measured
 */
export function buildReport(measured) {
    const videoCodecs = [];
    const hardware = [];
    const tenBit = [];
    let maxHeight = 0;
    for (const probe of videoProbes) {
        const answer = measured.video[probe.codec];
        // H.264 is kept even when "smooth" is not claimed: every Library browser plays it, and the Engine needs one codec to fall back to.
        if (!answer?.supported || (!answer.smooth && probe.codec !== 'h264')) {
            continue;
        }

        videoCodecs.push(probe.codec);
        if (answer.efficient) {
            hardware.push(probe.codec);
        }

        if (answer.tenBit) {
            tenBit.push(probe.codec);
        }

        maxHeight = Math.max(maxHeight, answer.maxHeight || 0);
    }

    const audioCodecs = audioProbes.filter(probe => measured.audio[probe.codec]).map(probe => probe.codec);
    if (measured.audioContainers.includes('wav')) {
        audioCodecs.push('pcm_s16le');
    }

    return {
        schema_version: 2,
        containers: [...measured.containers, ...measured.audioContainers, ...documentContainers],
        video_codecs: videoCodecs,
        audio_codecs: audioCodecs,
        subtitle_formats: ['vtt'],
        max_height: maxHeight > 0 ? maxHeight : null,
        max_audio_channels: measured.surround ? 6 : 2,
        supports_hdr: measured.hdrDisplay && tenBit.length > 0,
        supports_playback_speed: true,
        supports_offline_downloads: true,
        hardware_decode_video_codecs: hardware,
        ten_bit_video_codecs: tenBit,
    };
}

/**
 * Asks the browser what it can play. Returns null when the browser has no media capability checks (the Engine then keeps its fixed
 * web list), and never throws.
 * @param {{ navigator?: Navigator, document?: Document, matchMedia?: (query: string) => MediaQueryList }} [environment]
 */
export async function measure(environment = globalThis) {
    const mediaCapabilities = environment.navigator?.mediaCapabilities;
    if (typeof mediaCapabilities?.decodingInfo !== 'function' || !environment.document?.createElement) {
        return null;
    }

    const video = {};
    for (const probe of videoProbes) {
        const eight = await decode(mediaCapabilities, videoConfig(probe.contentType));
        const ten = eight.supported ? await decode(mediaCapabilities, videoConfig(probe.tenBit)) : { supported: false, smooth: false };
        let maxHeight = 0;
        if (eight.supported && eight.smooth) {
            for (const height of heights) {
                const answer = await decode(mediaCapabilities, videoConfig(probe.contentType, height));
                if (answer.supported && answer.smooth) {
                    maxHeight = height;
                    break;
                }
            }
        }

        video[probe.codec] = { ...eight, tenBit: ten.supported && ten.smooth, maxHeight: maxHeight || (eight.supported ? 1080 : 0) };
    }

    const audio = {};
    for (const probe of audioProbes) {
        audio[probe.codec] = (await decode(mediaCapabilities, { type: 'file', audio: { contentType: probe.contentType } })).supported;
    }

    const surround = (await decode(mediaCapabilities, {
        type: 'file',
        audio: { contentType: 'audio/mp4; codecs="mp4a.40.2"', channels: '5.1' },
    })).supported;

    // One element that is never put on the page answers for video and audio types alike; it plays nothing.
    const probeElement = environment.document.createElement('video');
    const containers = videoContainerProbes.filter(probe => canOpen(probeElement, probe.type)).map(probe => probe.container);
    const audioContainers = audioContainerProbes.filter(probe => canOpen(probeElement, probe.type)).map(probe => probe.container);
    let hdrDisplay = false;
    try {
        hdrDisplay = environment.matchMedia?.('(dynamic-range: high)')?.matches === true;
    } catch {
        // A browser that does not know the query has no HDR display to report.
    }

    return buildReport({ video, audio, surround, hdrDisplay, containers, audioContainers });
}
