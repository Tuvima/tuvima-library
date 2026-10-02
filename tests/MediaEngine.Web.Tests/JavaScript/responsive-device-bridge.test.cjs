const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const repositoryRoot = path.resolve(__dirname, '../../..');
const appJs = fs.readFileSync(path.join(repositoryRoot, 'src/MediaEngine.Web/wwwroot/app.js'), 'utf8').replace(/\r\n/g, '\n');
const mainLayout = fs.readFileSync(path.join(repositoryRoot, 'src/MediaEngine.Web/Shared/MainLayout.razor'), 'utf8').replace(/\r\n/g, '\n');
const blockStart = appJs.indexOf('window.tuvimaResponsive = (function () {');
const blockEnd = appJs.indexOf('\n/**\n * Returns responsive presentation density only.', blockStart);
assert.ok(blockStart >= 0 && blockEnd > blockStart, 'responsive bridge block is present');
const responsiveBridgeSource = appJs.slice(blockStart, blockEnd);
const popupSyncStart = appJs.indexOf('window.tuvimaPopupStateSync = (function () {');
const popupSyncEnd = appJs.indexOf('\n\nwindow.listenPlayback = (function () {', popupSyncStart);
assert.ok(popupSyncStart >= 0 && popupSyncEnd > popupSyncStart, 'popup snapshot handshake block is present');
const popupSyncSource = appJs.slice(popupSyncStart, popupSyncEnd);
const dockToolsStart = appJs.indexOf('window.playbackTools = window.playbackTools || {');
const dockToolsEnd = appJs.indexOf('\n};\n\n// Scroll the existing episode rail', dockToolsStart) + 3;
assert.ok(dockToolsStart >= 0 && dockToolsEnd > dockToolsStart, 'playback dock observer block is present');
const dockToolsSource = appJs.slice(dockToolsStart, dockToolsEnd);

function createBrowser(initialWidth = 1024) {
    const windowListeners = new Map();
    const mediaListeners = new Map();
    const resizeObservers = [];
    const documentElement = { clientWidth: initialWidth };
    const mediaQuery = {
        addEventListener(type, handler) { mediaListeners.set(type, handler); },
        removeEventListener(type, handler) { if (mediaListeners.get(type) === handler) mediaListeners.delete(type); },
        fire(type = 'change') { mediaListeners.get(type)?.(); },
    };
    const visualViewportListeners = new Map();
    const visualViewport = {
        addEventListener(type, handler) { visualViewportListeners.set(type, handler); },
        removeEventListener(type, handler) { if (visualViewportListeners.get(type) === handler) visualViewportListeners.delete(type); },
        fire(type = 'resize') { visualViewportListeners.get(type)?.(); },
    };
    class MockResizeObserver {
        constructor(callback) { this.callback = callback; resizeObservers.push(this); }
        observe() { this.observing = true; }
        disconnect() { this.observing = false; }
        fire() { if (this.observing) this.callback(); }
    }
    const window = {
        innerWidth: initialWidth,
        visualViewport,
        ResizeObserver: MockResizeObserver,
        getComputedStyle: () => ({ getPropertyValue: () => '840px' }),
        addEventListener(type, handler) { windowListeners.set(type, handler); },
        removeEventListener(type, handler) { if (windowListeners.get(type) === handler) windowListeners.delete(type); },
        fire(type) { windowListeners.get(type)?.(); },
        matchMedia() { return mediaQuery; },
        clearTimeout,
        setTimeout,
    };
    const document = { documentElement };
    Object.defineProperty(mediaQuery, 'matches', { get: () => window.innerWidth <= 840 });
    const context = { window, document, Promise, Number, Math, setTimeout, clearTimeout };
    vm.runInNewContext(responsiveBridgeSource, context, { filename: 'app.js:responsive-bridge' });
    return { window, documentElement, mediaQuery, resizeObservers, visualViewportListeners };
}

function createDockTools() {
    const styleValues = new Map();
    const resizeObservers = [];
    class MockHTMLElement {
        constructor(height) { this.height = height; }
        getBoundingClientRect() { return { height: this.height }; }
    }
    class MockResizeObserver {
        constructor(callback) { this.callback = callback; resizeObservers.push(this); }
        observe(element) { this.element = element; this.observing = true; this.callback(); }
        disconnect() { this.observing = false; }
        fire() { if (this.observing) this.callback(); }
    }
    const document = { documentElement: { style: { setProperty: (key, value) => styleValues.set(key, value) } } };
    const context = { window: { playbackTools: null }, document, HTMLElement: MockHTMLElement, ResizeObserver: MockResizeObserver };
    vm.runInNewContext(dockToolsSource, context, { filename: 'app.js:playback-dock' });
    return { tools: context.window.playbackTools, styleValues, resizeObservers, MockHTMLElement };
}

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

test('viewport events update the .NET device class and unregister removes listeners', async () => {
    const browser = createBrowser();
    const calls = [];
    const dotNetRef = { invokeMethodAsync: (_method, value) => { calls.push(value); return Promise.resolve(); } };
    browser.window.tuvimaResponsive.registerDeviceClassObserver(dotNetRef);
    await delay(0);
    assert.deepEqual(calls, ['web']);

    browser.documentElement.clientWidth = 390;
    browser.window.innerWidth = 390;
    browser.window.visualViewport.fire();
    await delay(220);
    assert.equal(calls.at(-1), 'mobile');

    browser.documentElement.clientWidth = 1024;
    browser.window.innerWidth = 1024;
    browser.mediaQuery.fire();
    await delay(220);
    assert.equal(calls.at(-1), 'web');

    browser.window.tuvimaResponsive.unregisterDeviceClassObserver();
    assert.equal(browser.visualViewportListeners.size, 0);
    assert.equal(browser.resizeObservers.every(observer => !observer.observing), true);
});

test('device density matches CSS media-query classification when the document has a scrollbar', async () => {
    const browser = createBrowser(842);
    browser.documentElement.clientWidth = 832;
    const calls = [];
    browser.window.tuvimaResponsive.registerDeviceClassObserver({
        invokeMethodAsync: (_method, value) => { calls.push(value); return Promise.resolve(); },
    });
    await delay(0);

    assert.deepEqual(calls, ['web']);
    browser.window.tuvimaResponsive.unregisterDeviceClassObserver();
});

test('failed circuit invocation leaves the class retryable on the next viewport event', async () => {
    const browser = createBrowser();
    const calls = [];
    let failMobileOnce = true;
    const dotNetRef = {
        invokeMethodAsync: (_method, value) => {
            calls.push(value);
            if (value === 'mobile' && failMobileOnce) {
                failMobileOnce = false;
                return Promise.reject(new Error('circuit unavailable'));
            }
            return Promise.resolve();
        },
    };
    browser.window.tuvimaResponsive.registerDeviceClassObserver(dotNetRef);
    await delay(0);
    browser.documentElement.clientWidth = 390;
    browser.window.innerWidth = 390;
    browser.window.fire('resize');
    await delay(220);
    browser.window.fire('orientationchange');
    await delay(220);
    assert.equal(calls.filter(value => value === 'mobile').length, 2);
});

test('a reverse resize is delivered while the preceding class update is still pending', async () => {
    const browser = createBrowser();
    const calls = [];
    let finishMobile;
    const dotNetRef = {
        invokeMethodAsync: (_method, value) => {
            calls.push(value);
            if (value === 'mobile') return new Promise(resolve => { finishMobile = resolve; });
            return Promise.resolve();
        },
    };
    browser.window.tuvimaResponsive.registerDeviceClassObserver(dotNetRef);
    await delay(0);

    browser.documentElement.clientWidth = 390;
    browser.window.innerWidth = 390;
    browser.window.visualViewport.fire();
    await delay(220);
    browser.documentElement.clientWidth = 1024;
    browser.window.innerWidth = 1024;
    browser.window.fire('resize');
    await delay(220);
    assert.deepEqual(calls, ['web', 'mobile', 'web']);

    finishMobile();
    await delay(0);
    browser.window.tuvimaResponsive.unregisterDeviceClassObserver();
});

test('late completion from an unregistered circuit cannot suppress the new circuit notification', async () => {
    const browser = createBrowser();
    let finishOld;
    const oldCalls = [];
    const oldRef = { invokeMethodAsync: (_method, value) => {
        oldCalls.push(value);
        return new Promise(resolve => { finishOld = resolve; });
    } };
    browser.window.tuvimaResponsive.registerDeviceClassObserver(oldRef);

    const currentCalls = [];
    const currentRef = { invokeMethodAsync: (_method, value) => { currentCalls.push(value); return Promise.resolve(); } };
    browser.window.tuvimaResponsive.registerDeviceClassObserver(currentRef);
    await delay(0);
    finishOld();
    await delay(0);
    browser.documentElement.clientWidth = 390;
    browser.window.innerWidth = 390;
    browser.window.fire('resize');
    await delay(220);
    assert.deepEqual(currentCalls, ['web', 'mobile']);
});

test('disposing an older layout owner cannot unregister its replacement observer', async () => {
    const browser = createBrowser();
    const oldCalls = [];
    const newCalls = [];
    browser.window.tuvimaResponsive.registerDeviceClassObserver({
        invokeMethodAsync: (_method, value) => { oldCalls.push(value); return Promise.resolve(); },
    }, 'old-layout');
    browser.window.tuvimaResponsive.registerDeviceClassObserver({
        invokeMethodAsync: (_method, value) => { newCalls.push(value); return Promise.resolve(); },
    }, 'new-layout');
    await delay(0);

    browser.window.tuvimaResponsive.unregisterDeviceClassObserver('old-layout');
    browser.window.innerWidth = 390;
    browser.window.fire('resize');
    await delay(220);

    assert.deepEqual(oldCalls, ['web']);
    assert.deepEqual(newCalls, ['web', 'mobile']);
});

test('MainLayout owns and registers the viewport observer before awaiting device settings', () => {
    const register = mainLayout.indexOf('registerDeviceClassObserver", startupToken, _dotNetRef, _deviceObserverOwner');
    const initialize = mainLayout.indexOf('var deviceInitialization = DeviceContext.InitialiseAsync(detectedDevice);');
    const awaitInitialize = mainLayout.indexOf('await deviceInitialization;');
    const unregister = mainLayout.indexOf('unregisterDeviceClassObserver", _deviceObserverOwner');
    const observeDock = mainLayout.indexOf('playbackTools.observeDock", startupToken, _audioDockRef, _deviceObserverOwner');
    const disconnectDock = mainLayout.indexOf('playbackTools.disconnectDock", _deviceObserverOwner');
    const authorityWait = mainLayout.indexOf('await _authorityRefreshTask;');

    assert.ok(initialize >= 0 && initialize < register && register < observeDock && observeDock < awaitInitialize);
    assert.ok(unregister >= 0 && disconnectDock > unregister && authorityWait > disconnectDock);
    assert.match(mainLayout, /private readonly string _deviceObserverOwner = Guid\.NewGuid\(\)\.ToString\("N"\);/);
});

test('disposing an older layout cannot clear the replacement audio dock observer or height', () => {
    const { tools, styleValues, resizeObservers, MockHTMLElement } = createDockTools();
    const oldElement = new MockHTMLElement(72);
    const newElement = new MockHTMLElement(108);
    tools.observeDock(oldElement, 'old-layout');
    tools.observeDock(newElement, 'new-layout');
    assert.equal(styleValues.get('--tl-audio-dock-height'), '108px');

    assert.equal(tools.disconnectDock('old-layout'), false);
    assert.equal(styleValues.get('--tl-audio-dock-height'), '108px');
    newElement.height = 116;
    resizeObservers[1].fire();
    assert.equal(styleValues.get('--tl-audio-dock-height'), '116px');
    assert.equal(resizeObservers[0].observing, false);
    assert.equal(tools.disconnectDock('new-layout'), true);
    assert.equal(styleValues.get('--tl-audio-dock-height'), '0px');
});

test('a popup opened after publication prefers the same-origin opener snapshot over its stale cache', () => {
    const parentState = '{"CurrentItem":{"Title":"Latest opener item"}}';
    const popupWindow = {
        location: { origin: 'https://tuvima.test' },
        opener: {
            closed: false,
            location: { origin: 'https://tuvima.test' },
            listenPlayback: { getStoredState: () => parentState },
        },
        localStorage: { getItem: () => '{"CurrentItem":{"Title":"stale popup cache"}}' },
    };
    vm.runInNewContext(popupSyncSource, { window: popupWindow, JSON, Object });

    assert.equal(popupWindow.tuvimaPopupStateSync.getLatestState('state-key'), parentState);
});

test('a cleared opener snapshot does not resurrect stale popup storage', () => {
    const popupWindow = {
        location: { origin: 'https://tuvima.test' },
        opener: {
            closed: false,
            location: { origin: 'https://tuvima.test' },
            listenPlayback: { getStoredState: () => null },
        },
        localStorage: { getItem: () => '{"CurrentItem":{"Title":"stale popup cache"}}' },
    };
    vm.runInNewContext(popupSyncSource, { window: popupWindow, JSON, Object });

    assert.equal(popupWindow.tuvimaPopupStateSync.getLatestState('state-key'), null);
});

test('a popup opened before publication requests and then reads the fresh opener snapshot', () => {
    let parentState = null;
    let parentPublished = false;
    const popupNotifications = [];
    const popupWindow = {
        location: { origin: 'https://tuvima.test' },
        opener: {
            closed: false,
            location: { origin: 'https://tuvima.test' },
            listenPlayback: { getStoredState: () => parentPublished ? parentState : null },
        },
        localStorage: { getItem: () => null },
    };
    vm.runInNewContext(popupSyncSource, { window: popupWindow, JSON, Object });
    const popupSync = popupWindow.tuvimaPopupStateSync;

    popupSync.requestLatestState(json => {
        const request = JSON.parse(json);
        assert.equal(request.action, 'request-state');
        parentState = '{"CurrentItem":{"Title":"Published after popup registration"}}';
        parentPublished = true;
        popupNotifications.push(parentState);
    });

    assert.deepEqual(popupNotifications, [parentState]);
    assert.equal(popupSync.getLatestState('state-key'), parentState);
});
