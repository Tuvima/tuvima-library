// Behaviour checks for clients/tv-web/nav.js (remote key map) and player.js helpers (time text, SRT to WebVTT).
// Plain Node, no packages; the two files only touch the document inside functions, so a tiny stub is enough.
'use strict';

const assert = require('assert');
const path = require('path');

globalThis.window = globalThis;
const store = new Map();
globalThis.localStorage = {
  getItem: (k) => (store.has(k) ? store.get(k) : null),
  setItem: (k, v) => store.set(k, String(v)),
  removeItem: (k) => store.delete(k)
};
globalThis.document = { addEventListener() {}, createElement() { return {}; } };

const root = path.join(__dirname, '..', '..', 'clients', 'tv-web');
require(path.join(root, 'api.js'));
require(path.join(root, 'nav.js'));
require(path.join(root, 'player.js'));
const { nav, player } = window.TV;

const press = (keyCode, key) => nav.actionFor({ keyCode, key });

assert.strictEqual(press(37), 'left');
assert.strictEqual(press(38), 'up');
assert.strictEqual(press(39), 'right');
assert.strictEqual(press(40), 'down');
assert.strictEqual(press(13), 'enter');
// Back: Escape and Backspace in browsers, Tizen Return, webOS Back.
assert.strictEqual(press(27), 'back');
assert.strictEqual(press(8), 'back');
assert.strictEqual(press(10009), 'back');
assert.strictEqual(press(461), 'back');
// Media keys on both platforms.
assert.strictEqual(press(415), 'play');
assert.strictEqual(press(19), 'pause');
assert.strictEqual(press(413), 'stop');
assert.strictEqual(press(10252), 'playpause');
assert.strictEqual(press(417), 'forward');
assert.strictEqual(press(412), 'rewind');
// The key name wins over the code, and unknown keys are ignored.
assert.strictEqual(press(0, 'ArrowDown'), 'down');
assert.strictEqual(press(0, 'GoBack'), 'back');
assert.strictEqual(press(65, 'a'), null);

assert.strictEqual(player.formatTime(0), '0:00');
assert.strictEqual(player.formatTime(75.9), '1:15');
assert.strictEqual(player.formatTime(3725), '1:02:05');
assert.strictEqual(player.formatTime(NaN), '0:00');

assert.strictEqual(player.toVtt('WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHi'), 'WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHi');
assert.strictEqual(
  player.toVtt('1\r\n00:00:01,500 --> 00:00:02,250\r\nHello\r\n'),
  'WEBVTT\n\n1\n00:00:01.500 --> 00:00:02.250\nHello\n');

// hls.js requests carry the token only when they go to the paired server.
function fakeXhr() { const headers = {}; return { headers, setRequestHeader: (k, v) => { headers[k] = v; } }; }
(async () => {
  store.clear();
  global.fetch = async () => ({
    ok: true, status: 200, headers: { get: () => null },
    json: async () => ({ supported_api_versions: ['1'] })
  });
  await window.TV.api.connect('http://nas.local');
  // No token yet: nothing is attached, even for the server.
  let xhr = fakeXhr();
  player.authorize(xhr, 'http://nas.local/api/v1/stream/hls/g/p/master.m3u8');
  assert.strictEqual(xhr.headers.Authorization, undefined);

  global.fetch = async () => ({
    ok: true, status: 200, headers: { get: () => null },
    text: async () => JSON.stringify({ access_token: 'tok', refresh_token: 'r', expires_in: 600, device_id: 'd', profile_id: 'p' })
  });
  await window.TV.api.pollPairing({ device_code: 'x' });
  xhr = fakeXhr();
  player.authorize(xhr, 'http://nas.local/api/v1/stream/hls/g/p/seg1.ts');
  assert.strictEqual(xhr.headers.Authorization, 'Bearer tok');
  xhr = fakeXhr();
  player.authorize(xhr, 'https://cdn.example/seg1.ts');
  assert.strictEqual(xhr.headers.Authorization, undefined);

  // Leaving the app: nothing on a desktop browser, the platform call on Tizen and webOS.
  assert.strictEqual(nav.exitApp(), false);
  let exited = 0;
  window.tizen = { application: { getCurrentApplication: () => ({ exit: () => exited++ }) } };
  assert.strictEqual(nav.exitApp(), true);
  assert.strictEqual(exited, 1);
  delete window.tizen;
  let backs = 0;
  window.webOS = { platformBack: () => backs++ };
  assert.strictEqual(nav.exitApp(), true);
  assert.strictEqual(backs, 1);
  delete window.webOS;
  let closed = 0;
  window.webOSSystem = {};
  const realClose = window.close;
  window.close = () => closed++;
  assert.strictEqual(nav.exitApp(), true);
  assert.strictEqual(closed, 1);
  delete window.webOSSystem;
  window.close = realClose;
  window.tizen = { application: { getCurrentApplication: () => { throw new Error('refused'); } } };
  assert.strictEqual(nav.exitApp(), false, 'a platform that refuses leaves the key to the platform');
  delete window.tizen;

  console.log('tv-web input and player helper checks passed');
})().catch((e) => { console.error(e); process.exit(1); });
