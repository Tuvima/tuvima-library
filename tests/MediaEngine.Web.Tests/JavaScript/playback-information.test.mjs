import test from 'node:test';
import assert from 'node:assert/strict';

const { bufferedAheadSeconds, bufferAhead, browserFromUserAgent, systemFromUserAgent, describeClient, clientName } =
    await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-information.js', import.meta.url).href);

const edgeOnWindows = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0';
const chromeOnMac = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36';
const firefoxOnLinux = 'Mozilla/5.0 (X11; Linux x86_64; rv:127.0) Gecko/20100101 Firefox/127.0';
const safariOnMac = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15';
const safariOnIphone = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1';
const chromeOnAndroid = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36';
const chromeOnChromeOs = 'Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36';

test('buffer ahead counts whole seconds from the playhead to the end of the range that holds it', () => {
    assert.equal(bufferedAheadSeconds([{ start: 0, end: 60 }], 12.4), 47);
    assert.equal(bufferedAheadSeconds([{ start: 0, end: 10 }, { start: 30, end: 90 }], 31), 59);
});

test('buffer ahead is zero when the playhead sits outside every range or the input is unusable', () => {
    assert.equal(bufferedAheadSeconds([{ start: 30, end: 90 }], 5), 0);
    assert.equal(bufferedAheadSeconds([], 5), 0);
    assert.equal(bufferedAheadSeconds(null, 5), 0);
    assert.equal(bufferedAheadSeconds([{ start: 0, end: 10 }], Number.NaN), 0);
    assert.equal(bufferedAheadSeconds([{ start: 0, end: 10 }], 10), 0);
});

test('buffer ahead forgives the tiny gap a browser leaves between segments', () => {
    assert.equal(bufferedAheadSeconds([{ start: 5.05, end: 20 }], 5), 15);
});

test('buffer ahead reads the element by id and returns null when it is not on the page', () => {
    const element = { currentTime: 4, buffered: { length: 1, start: () => 0, end: () => 24.9 } };
    globalThis.document = { getElementById: id => (id === 'tuvima-video-engine' ? element : null) };
    assert.equal(bufferAhead('tuvima-video-engine'), 20);
    assert.equal(bufferAhead('missing'), null);
    assert.equal(bufferAhead(''), null);
});

test('user-agent text names the browser, with Edge and Opera ahead of Chrome', () => {
    assert.equal(browserFromUserAgent(edgeOnWindows), 'Edge');
    assert.equal(browserFromUserAgent(chromeOnMac), 'Chrome');
    assert.equal(browserFromUserAgent(firefoxOnLinux), 'Firefox');
    assert.equal(browserFromUserAgent(safariOnMac), 'Safari');
    assert.equal(browserFromUserAgent(chromeOnAndroid), 'Chrome');
    assert.equal(browserFromUserAgent('Mozilla/5.0 Chrome/126 Safari/537.36 OPR/110.0'), 'Opera');
    assert.equal(browserFromUserAgent('SomethingElse/1.0'), null);
    assert.equal(browserFromUserAgent(undefined), null);
});

test('user-agent text names the system, with iOS and Android ahead of macOS and Linux', () => {
    assert.equal(systemFromUserAgent(edgeOnWindows), 'Windows');
    assert.equal(systemFromUserAgent(chromeOnMac), 'macOS');
    assert.equal(systemFromUserAgent(firefoxOnLinux), 'Linux');
    assert.equal(systemFromUserAgent(safariOnIphone), 'iOS');
    assert.equal(systemFromUserAgent(chromeOnAndroid), 'Android');
    assert.equal(systemFromUserAgent(chromeOnChromeOs), 'ChromeOS');
    assert.equal(systemFromUserAgent('SomethingElse/1.0'), null);
});

test('the client reads "Browser on System" from the user agent', () => {
    assert.equal(describeClient({ userAgent: edgeOnWindows }), 'Edge on Windows');
    assert.equal(describeClient({ userAgent: firefoxOnLinux }), 'Firefox on Linux');
    assert.equal(describeClient({ userAgent: safariOnIphone }), 'Safari on iOS');
});

test('client hints are used when the browser offers them', () => {
    const brands = [{ brand: 'Not/A)Brand', version: '8' }, { brand: 'Chromium', version: '126' }, { brand: 'Microsoft Edge', version: '126' }];
    assert.equal(describeClient({ brands, platform: 'Windows', userAgent: '' }), 'Edge on Windows');
    assert.equal(describeClient({ brands: [{ brand: 'Google Chrome', version: '126' }], platform: 'macOS', userAgent: '' }), 'Chrome on macOS');
    assert.equal(describeClient({ brands: [{ brand: 'Google Chrome', version: '126' }], platform: 'Chrome OS', userAgent: '' }), 'Chrome on ChromeOS');
});

test('a bare Chromium brand falls back to the user-agent text for the browser name', () => {
    const brands = [{ brand: 'Chromium', version: '126' }];
    assert.equal(describeClient({ brands, platform: 'Linux', userAgent: chromeOnMac }), 'Chrome on Linux');
});

test('an unknown part is left out rather than guessed', () => {
    assert.equal(describeClient({ userAgent: 'Mozilla/5.0 Firefox/127.0' }), 'Firefox');
    assert.equal(describeClient({ userAgent: 'Mozilla/5.0 (Windows NT 10.0)' }), 'Windows');
    assert.equal(describeClient({ userAgent: 'curl/8' }), null);
    assert.equal(describeClient(), null);
});

test('clientName reads the navigator of the page', () => {
    Object.defineProperty(globalThis, 'navigator', { value: { userAgent: firefoxOnLinux }, configurable: true });
    assert.equal(clientName(), 'Firefox on Linux');
    Object.defineProperty(globalThis, 'navigator', {
        value: { userAgent: '', userAgentData: { brands: [{ brand: 'Microsoft Edge', version: '126' }], platform: 'Windows' } },
        configurable: true,
    });
    assert.equal(clientName(), 'Edge on Windows');
});
