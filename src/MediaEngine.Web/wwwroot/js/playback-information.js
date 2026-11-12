// Playback information (video-player follow-ups D13): the two facts only the browser knows. How many seconds of the playing picture are already
// buffered ahead of the playhead, and a short "Browser on System" name for this device. Nothing here is sent anywhere.

// Seconds buffered ahead of currentTime, from ranges shaped [{ start, end }]. A range that holds the playhead (within a tenth of a second, as
// browsers leave tiny gaps between segments) counts; a playhead outside every range has nothing ahead of it. Whole seconds, never negative.
export function bufferedAheadSeconds(ranges, currentTime) {
    if (!Array.isArray(ranges) || !Number.isFinite(currentTime)) return 0;
    const tolerance = 0.1;
    let ahead = 0;
    for (const range of ranges) {
        if (!range || !Number.isFinite(range.start) || !Number.isFinite(range.end)) continue;
        if (currentTime >= range.start - tolerance && currentTime <= range.end) {
            ahead = Math.max(ahead, range.end - currentTime);
        }
    }
    return Math.max(0, Math.floor(ahead));
}

function rangesOf(timeRanges) {
    const ranges = [];
    for (let index = 0; index < (timeRanges?.length ?? 0); index++) {
        ranges.push({ start: timeRanges.start(index), end: timeRanges.end(index) });
    }
    return ranges;
}

// The playing video or audio element, found by its id; null when it is not on the page, so the line is simply left out.
export function bufferAhead(elementId) {
    const element = elementId ? document.getElementById(elementId) : null;
    if (!element || !element.buffered) return null;
    return bufferedAheadSeconds(rangesOf(element.buffered), element.currentTime);
}

// Browser name from a user-agent string: the more specific names come first because Edge, Opera and Chrome on iOS all also say "Chrome" or "Safari".
export function browserFromUserAgent(userAgent) {
    const ua = userAgent ?? '';
    if (/\bEdg(e|A|iOS)?\//.test(ua)) return 'Edge';
    if (/\bOPR\//.test(ua) || /\bOpera\b/.test(ua)) return 'Opera';
    if (/\bFirefox\//.test(ua) || /\bFxiOS\//.test(ua)) return 'Firefox';
    if (/\bCriOS\//.test(ua) || /\bChrome\//.test(ua)) return 'Chrome';
    if (/\bSafari\//.test(ua) && /\bVersion\//.test(ua)) return 'Safari';
    return null;
}

// System name from a user-agent string: iPhone and iPad say "like Mac OS X" and Android says "Linux", so those are checked first.
export function systemFromUserAgent(userAgent) {
    const ua = userAgent ?? '';
    if (/\b(iPhone|iPad|iPod)\b/.test(ua)) return 'iOS';
    if (/\bAndroid\b/.test(ua)) return 'Android';
    if (/\bCrOS\b/.test(ua)) return 'ChromeOS';
    if (/\bWindows\b/.test(ua)) return 'Windows';
    if (/\b(Macintosh|Mac OS X)\b/.test(ua)) return 'macOS';
    if (/\bLinux\b/.test(ua) || /\bX11\b/.test(ua)) return 'Linux';
    return null;
}

function systemFromPlatform(platform) {
    const value = (platform ?? '').toLowerCase();
    if (!value) return null;
    if (value.startsWith('win')) return 'Windows';
    if (value === 'macos' || value.startsWith('mac')) return 'macOS';
    if (value === 'ios') return 'iOS';
    if (value === 'android') return 'Android';
    if (value === 'chrome os' || value === 'chromeos') return 'ChromeOS';
    if (value === 'linux') return 'Linux';
    return null;
}

function browserFromBrands(brands) {
    const names = (Array.isArray(brands) ? brands : []).map(entry => String(entry?.brand ?? ''));
    if (names.some(name => /Edge/i.test(name))) return 'Edge';
    if (names.some(name => /Opera/i.test(name))) return 'Opera';
    if (names.some(name => /Google Chrome/i.test(name))) return 'Chrome';
    // A bare "Chromium" brand is Brave, Vivaldi or a custom build; the user-agent text names those better, so it is left to that.
    return null;
}

// "Edge on Windows". The client hints are used when the browser offers them, the user-agent text otherwise; a part that is unknown is left out.
export function describeClient({ brands, platform, userAgent } = {}) {
    const browser = browserFromBrands(brands) ?? browserFromUserAgent(userAgent);
    const system = systemFromPlatform(platform) ?? systemFromUserAgent(userAgent);
    if (browser && system) return `${browser} on ${system}`;
    return browser ?? system ?? null;
}

export function clientName() {
    const hints = globalThis.navigator?.userAgentData;
    return describeClient({ brands: hints?.brands, platform: hints?.platform, userAgent: globalThis.navigator?.userAgent });
}
