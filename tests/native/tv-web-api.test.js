// Behaviour checks for clients/tv-web/api.js: discovery, device-code pairing, token rotation and sign-out.
// Plain Node, no packages. api.js runs against a fake localStorage and a scripted fetch.
'use strict';

const assert = require('assert');
const path = require('path');

const store = new Map();
globalThis.window = globalThis;
globalThis.localStorage = {
  getItem: (k) => (store.has(k) ? store.get(k) : null),
  setItem: (k, v) => store.set(k, String(v)),
  removeItem: (k) => store.delete(k)
};
Object.defineProperty(globalThis, 'navigator', { value: { userAgent: 'Mozilla/5.0 (SMART-TV; Tizen 6.5) TV Safari' }, configurable: true });

const calls = [];
let script = [];

function reply(status, body, headers) {
  const text = body === undefined ? '' : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: (name) => (headers && headers[name]) || null },
    json: async () => JSON.parse(text),
    text: async () => text,
    blob: async () => ({})
  };
}

globalThis.fetch = async (url, init) => {
  const call = { url, method: (init && init.method) || 'GET', headers: (init && init.headers) || {}, body: init && init.body ? JSON.parse(init.body) : null };
  calls.push(call);
  const next = script.shift();
  if (!next) { throw new Error('Unexpected request: ' + call.method + ' ' + url); }
  if (typeof next === 'function') { return next(call); }
  if (next instanceof Error) { throw next; }
  return next;
};

require(path.join(__dirname, '..', '..', 'clients', 'tv-web', 'api.js'));
const api = window.TV.api;

const discovery = { supported_api_versions: ['1'], device_authorization_endpoint: '/api/v1/oauth/device_authorization', token_endpoint: '/api/v1/oauth/token' };
const tokenBody = (n) => ({ access_token: 'access-' + n, token_type: 'Bearer', expires_in: 600, refresh_token: 'refresh-' + n, scope: 'library.read', device_id: 'dev-1', profile_id: 'prof-1' });
const stored = () => JSON.parse(store.get('tuvima.tv.tokens'));

async function reset() {
  calls.length = 0;
  script = [];
}

const tests = [];
function test(name, fn) { tests.push([name, fn]); }

test('normalizeServer keeps only scheme and authority', () => {
  assert.strictEqual(api.normalizeServer('nas.local:5016'), 'https://nas.local:5016');
  assert.strictEqual(api.normalizeServer('nas.local', 'http'), 'http://nas.local');
  assert.strictEqual(api.normalizeServer('HTTP://Nas.Local:8080/some/path?x=1'), 'http://Nas.Local:8080');
  assert.strictEqual(api.normalizeServer('   '), '');
});

test('connect tries https then http for a bare address and remembers the one that answers', async () => {
  script = [new TypeError('fetch failed'), reply(200, discovery)];
  const result = await api.connect('nas.local');
  assert.deepStrictEqual(result.supported_api_versions, ['1']);
  assert.deepStrictEqual(calls.map((c) => c.url), ['https://nas.local/.well-known/tuvima', 'http://nas.local/.well-known/tuvima']);
  assert.strictEqual(api.server(), 'http://nas.local');
  assert.strictEqual(store.get('tuvima.tv.server'), 'http://nas.local');
});

test('connect refuses a server without API v1 and reports app access switched off', async () => {
  script = [reply(200, { supported_api_versions: ['2'] })];
  await assert.rejects(api.connect('http://nas.local'), (e) => e.code === 'unsupported');
  script = [reply(404)];
  await assert.rejects(api.connect('http://nas.local'), (e) => e.status === 404);
});

test('beginPairing posts the television client, scopes and HLS-only capabilities without a token', async () => {
  await reset();
  script = [reply(200, discovery), reply(200, { device_code: 'dc', user_code: 'TVMA-1234', verification_uri: 'http://nas.local/pair', expires_in: 600, interval: 5 })];
  await api.connect('http://nas.local');
  const session = await api.beginPairing();
  const post = calls[1];
  assert.strictEqual(session.user_code, 'TVMA-1234');
  assert.strictEqual(post.method, 'POST');
  assert.strictEqual(post.url, 'http://nas.local/api/v1/oauth/device_authorization');
  assert.strictEqual(post.headers.Authorization, undefined);
  assert.strictEqual(post.headers['Content-Type'], 'application/json');
  assert.strictEqual(post.body.client_id, 'tuvima-tv');
  assert.strictEqual(post.body.device_class, 'television');
  assert.strictEqual(post.body.device_name, 'Samsung TV');
  assert.ok(post.body.scope.split(' ').includes('playback.read'));
  assert.ok(!post.body.scope.includes('view.'), 'no photo scopes');
  assert.deepStrictEqual(post.body.capabilities.containers, []);
  assert.deepStrictEqual(post.body.capabilities.video_codecs, ['h264']);
  assert.deepStrictEqual(post.body.capabilities.audio_codecs, ['aac']);
});

test('pollPairing maps the device-code answers and stores tokens only in localStorage on success', async () => {
  await reset();
  const session = { device_code: 'dc' };
  script = [reply(400, { error: 'authorization_pending' })];
  assert.strictEqual((await api.pollPairing(session)).state, 'pending');
  script = [reply(400, { error: 'slow_down', interval: 10 })];
  const slow = await api.pollPairing(session);
  assert.strictEqual(slow.state, 'slow_down');
  assert.strictEqual(slow.interval, 10);
  script = [reply(400, { error: 'access_denied' })];
  assert.strictEqual((await api.pollPairing(session)).state, 'denied');
  script = [reply(400, { error: 'expired_token' })];
  assert.strictEqual((await api.pollPairing(session)).state, 'expired');
  script = [reply(429, { error: 'temporarily_unavailable', interval: 60 }, { 'Retry-After': '60' })];
  const throttled = await api.pollPairing(session);
  assert.strictEqual(throttled.state, 'slow_down');
  assert.strictEqual(throttled.interval, 60);
  script = [new TypeError('offline')];
  assert.strictEqual((await api.pollPairing(session)).state, 'error');

  script = [reply(200, tokenBody(1))];
  assert.strictEqual((await api.pollPairing(session)).state, 'authorized');
  const body = calls[calls.length - 1].body;
  assert.strictEqual(body.grant_type, 'urn:ietf:params:oauth:grant-type:device_code');
  assert.strictEqual(body.device_code, 'dc');
  assert.strictEqual(stored().access, 'access-1');
  assert.strictEqual(stored().refresh, 'refresh-1');
  assert.strictEqual(stored().deviceId, 'dev-1');
  assert.ok(api.isSignedIn());
});

test('an authenticated call sends the bearer token and a 401 triggers exactly one rotation for parallel calls', async () => {
  await reset();
  let refreshes = 0;
  script = [
    (c) => { assert.strictEqual(c.headers.Authorization, 'Bearer access-1'); return reply(401); },
    (c) => { assert.strictEqual(c.headers.Authorization, 'Bearer access-1'); return reply(401); },
    (c) => {
      refreshes++;
      assert.strictEqual(c.url, 'http://nas.local/api/v1/oauth/token');
      assert.strictEqual(c.body.grant_type, 'refresh_token');
      assert.strictEqual(c.body.refresh_token, 'refresh-1');
      assert.strictEqual(c.headers.Authorization, undefined);
      return reply(200, tokenBody(2));
    },
    (c) => { assert.strictEqual(c.headers.Authorization, 'Bearer access-2'); return reply(200, { key: 'home' }); },
    (c) => { assert.strictEqual(c.headers.Authorization, 'Bearer access-2'); return reply(200, { key: 'home' }); }
  ];
  const [a, b] = await Promise.all([api.home(), api.home()]);
  assert.strictEqual(a.key, 'home');
  assert.strictEqual(b.key, 'home');
  assert.strictEqual(refreshes, 1);
  assert.strictEqual(stored().refresh, 'refresh-2');
});

test('a token close to expiry is refreshed before the call, not after a 401', async () => {
  await reset();
  script = [reply(200, { ...tokenBody(3), expires_in: 5 })];
  await api.pollPairing({ device_code: 'dc3' });
  script = [
    (c) => { assert.strictEqual(c.body.grant_type, 'refresh_token'); assert.strictEqual(c.body.refresh_token, 'refresh-3'); return reply(200, tokenBody(6)); },
    (c) => { assert.strictEqual(c.headers.Authorization, 'Bearer access-6'); return reply(200, { items: [] }); }
  ];
  await api.recent();
  assert.strictEqual(stored().access, 'access-6');
  assert.ok(stored().expiresAt - Date.now() > 500000);
});

test('manifest asks for the tv-web profile, and tv-web-hevc once HEVC is switched on', async () => {
  await reset();
  script = [reply(200, { assetId: 'a' }), reply(200, { assetId: 'a' })];
  await api.manifest('aaaa');
  assert.ok(calls[0].url.includes('/api/v1/playback/aaaa/manifest?client=tv-web&'));
  api.settings.setHevc(true);
  await api.manifest('aaaa');
  assert.ok(calls[1].url.includes('client=tv-web-hevc'));
  assert.deepStrictEqual(api.capabilities().video_codecs, ['h264', 'hevc']);
  api.settings.setHevc(false);
});

test('the bearer token goes only to the paired server, never to another host', async () => {
  await reset();
  script = [reply(200, tokenBody(7))];
  await api.pollPairing({ device_code: 'dc7' });
  assert.ok(api.isServerUrl('/api/v1/stream/x'));
  assert.ok(api.isServerUrl('http://nas.local/api/v1/stream/x'));
  assert.ok(api.isServerUrl('HTTP://NAS.LOCAL/x'));
  assert.ok(!api.isServerUrl('http://nas.local.evil.example/x'));
  assert.ok(!api.isServerUrl('http://nas.local@evil.example/x'));
  assert.ok(!api.isServerUrl('https://nas.local/x'), 'a different scheme is a different origin');
  assert.ok(!api.isServerUrl('https://cdn.example/cover.jpg'));

  const seen = [];
  script = [
    (c) => { seen.push(c); return reply(200, 'WEBVTT'); },
    (c) => { seen.push(c); return reply(200, 'WEBVTT'); },
    (c) => { seen.push(c); return reply(200, 'WEBVTT'); }
  ];
  await api.fetchText('https://cdn.example/captions.vtt');
  await api.fetchText('http://nas.local/api/v1/stream/a/subtitles');
  await api.fetchText('/api/v1/stream/a/subtitles');
  assert.strictEqual(seen[0].headers.Authorization, undefined);
  assert.ok(seen[1].headers.Authorization.startsWith('Bearer '));
  assert.ok(seen[2].headers.Authorization.startsWith('Bearer '));
});

test('switchProfile: 428 means a PIN is needed, success replaces the tokens', async () => {
  await reset();
  script = [reply(428, { error: 'pin_required', error_description: 'PIN needed' })];
  await assert.rejects(api.switchProfile('p2'), (e) => e.status === 428 && e.code === 'pin_required');
  assert.deepStrictEqual(calls[0].body, { profile_id: 'p2' });
  script = [reply(200, { ...tokenBody(4), profile_id: 'p2' })];
  await api.switchProfile('p2', '1234');
  assert.deepStrictEqual(calls[1].body, { profile_id: 'p2', pin: '1234' });
  assert.strictEqual(stored().access, 'access-4');
  assert.strictEqual(stored().profileId, 'p2');
});

test('a refused refresh signs the TV out and announces it', async () => {
  await reset();
  let announced = 0;
  api.on('auth-lost', () => announced++);
  script = [reply(401), reply(400, { error: 'invalid_grant' })];
  await assert.rejects(api.home(), (e) => e.status === 401);
  assert.strictEqual(announced, 1);
  assert.strictEqual(store.get('tuvima.tv.tokens'), undefined);
  assert.ok(!api.isSignedIn());
});

test('signOut revokes the device and forgets the tokens but keeps the server address', async () => {
  await reset();
  script = [reply(200, tokenBody(5))];
  await api.pollPairing({ device_code: 'dc2' });
  script = [(c) => { assert.strictEqual(c.method, 'DELETE'); assert.strictEqual(c.url, 'http://nas.local/api/v1/devices/dev-1'); return reply(204); }];
  await api.signOut();
  assert.strictEqual(calls.length, 2);
  assert.ok(!api.isSignedIn());
  assert.strictEqual(store.get('tuvima.tv.tokens'), undefined);
  assert.strictEqual(api.server(), 'http://nas.local');
});

(async () => {
  let failed = 0;
  for (const [name, fn] of tests) {
    try {
      await fn();
      console.log('ok   - ' + name);
    } catch (error) {
      failed++;
      console.log('FAIL - ' + name + '\n' + (error && error.stack ? error.stack : error));
    }
  }
  if (failed) { console.log(failed + ' of ' + tests.length + ' failed'); process.exit(1); }
  console.log(tests.length + ' passed');
})();
