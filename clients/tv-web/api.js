/*
 * Tuvima TV web client - API v1 access.
 *
 * Server address, device-code pairing, rotating tokens, display/detail/playback calls and authenticated artwork.
 * Everything goes to the server address the person typed, through the Dashboard's app door (/api/v1).
 * Tokens live in localStorage only. Classic script (no modules) so a packaged app can load it from file://.
 */
(function (global) {
  'use strict';

  var TV = (global.TV = global.TV || {});

  var CLIENT_ID = 'tuvima-tv';
  var CLIENT_NAME = 'Tuvima TV';
  var CLIENT_VERSION = '0.1.0';
  var SCOPES = 'library.read artwork.read progress.read progress.write queue.read queue.write playback.read playback.write';
  var DEVICE_GRANT = 'urn:ietf:params:oauth:grant-type:device_code';
  var REQUEST_TIMEOUT_MS = 30000;
  var REFRESH_MARGIN_MS = 30000;

  var KEYS = {
    server: 'tuvima.tv.server',
    tokens: 'tuvima.tv.tokens',
    quality: 'tuvima.tv.quality',
    hevc: 'tuvima.tv.hevc',
    profile: 'tuvima.tv.profile'
  };

  /* ---- storage (localStorage only; a blocked store simply behaves as empty) ---- */

  function readStore(key) {
    try {
      return global.localStorage.getItem(key);
    } catch (e) {
      // Best effort: storage can be blocked in some TV browsers; the app then asks to pair each launch.
      return null;
    }
  }

  function writeStore(key, value) {
    try {
      if (value === null || value === undefined) {
        global.localStorage.removeItem(key);
      } else {
        global.localStorage.setItem(key, value);
      }
    } catch (e) {
      // Best effort, same reason as readStore.
    }
  }

  function readJson(key) {
    var raw = readStore(key);
    if (!raw) { return null; }
    try {
      return JSON.parse(raw);
    } catch (e) {
      // A damaged stored value is treated as absent.
      return null;
    }
  }

  /* ---- errors and events ---- */

  function ApiError(status, code, message, extra) {
    this.name = 'ApiError';
    this.status = status;
    this.code = code || 'request_failed';
    this.message = message || ('The server answered HTTP ' + status + '.');
    this.interval = extra && extra.interval;
    this.retryAfter = extra && extra.retryAfter;
  }
  ApiError.prototype = Object.create(Error.prototype);
  ApiError.prototype.constructor = ApiError;

  function isNetworkError(error) {
    return !!error && error.name !== 'ApiError';
  }

  var listeners = {};
  function on(name, fn) {
    (listeners[name] = listeners[name] || []).push(fn);
  }
  function emit(name, payload) {
    (listeners[name] || []).forEach(function (fn) {
      try { fn(payload); } catch (e) { if (global.console) { global.console.error(e); } }
    });
  }

  /* ---- server address ---- */

  var origin = readStore(KEYS.server) || '';

  // Accepts "host", "host:port", "http(s)://host[:port]"; keeps only scheme and authority.
  function normalizeServer(input, defaultScheme) {
    var text = String(input || '').trim();
    if (!text) { return ''; }
    if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(text)) {
      text = (defaultScheme || 'https') + '://' + text;
    }
    var match = /^(https?):\/\/([^\/?#]+)/i.exec(text);
    return match ? match[1].toLowerCase() + '://' + match[2] : '';
  }

  function absolute(path) {
    if (!path) { return ''; }
    if (/^https?:\/\//i.test(path)) { return path; }
    return origin + (path.charAt(0) === '/' ? path : '/' + path);
  }

  function withTimeout(init) {
    var out = init || {};
    if (typeof global.AbortController === 'function') {
      var controller = new global.AbortController();
      out.signal = controller.signal;
      global.setTimeout(function () { controller.abort(); }, REQUEST_TIMEOUT_MS);
    }
    return out;
  }

  async function readError(response) {
    var body = null;
    try { body = await response.json(); } catch (e) { body = null; /* not JSON: use the status text */ }
    var retry = parseInt(response.headers.get('Retry-After'), 10);
    return new ApiError(
      response.status,
      body && (body.error || body.code),
      body && (body.error_description || body.detail || body.title),
      { interval: body && body.interval, retryAfter: isNaN(retry) ? undefined : retry });
  }

  /** Looks for a Tuvima server at the address; tries https then http when no scheme was typed. */
  async function connect(input) {
    var typed = String(input || '').trim();
    var hasScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(typed);
    var candidates = hasScheme ? [normalizeServer(typed)] : [normalizeServer(typed, 'https'), normalizeServer(typed, 'http')];
    var lastError = null;
    for (var i = 0; i < candidates.length; i++) {
      var candidate = candidates[i];
      if (!candidate) { continue; }
      try {
        var response = await fetch(candidate + '/.well-known/tuvima', withTimeout({ headers: { Accept: 'application/json' } }));
        if (!response.ok) { throw await readError(response); }
        var discovery = await response.json();
        if (!discovery || (discovery.supported_api_versions || []).indexOf('1') < 0) {
          throw new ApiError(0, 'unsupported', 'This server does not offer API version 1.');
        }
        origin = candidate;
        writeStore(KEYS.server, origin);
        TV.discovery = discovery;
        return discovery;
      } catch (error) {
        lastError = error;
      }
    }
    throw lastError || new ApiError(0, 'invalid_address', 'Enter the address of your Tuvima server.');
  }

  /* ---- tokens ---- */

  var tokens = readJson(KEYS.tokens);

  function fromTokenResponse(json, previous) {
    return {
      access: json.access_token,
      refresh: json.refresh_token || (previous && previous.refresh) || '',
      expiresAt: Date.now() + (json.expires_in || 600) * 1000,
      deviceId: json.device_id || (previous && previous.deviceId) || '',
      profileId: json.profile_id || (previous && previous.profileId) || '',
      scope: json.scope || (previous && previous.scope) || ''
    };
  }

  function saveTokens(value) {
    tokens = value;
    writeStore(KEYS.tokens, value ? JSON.stringify(value) : null);
  }

  function isSignedIn() {
    return !!(tokens && tokens.access);
  }

  function accessToken() {
    return tokens ? tokens.access : '';
  }

  var refreshing = null;

  // One refresh at a time: refresh tokens rotate, so a second concurrent use would look like a replay and revoke the device.
  function refresh() {
    if (refreshing) { return refreshing; }
    var current = tokens;
    if (!current || !current.refresh) { return Promise.resolve(false); }
    refreshing = (async function () {
      try {
        var response = await fetch(absolute('/api/v1/oauth/token'), withTimeout({
          method: 'POST',
          headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
          body: JSON.stringify({ grant_type: 'refresh_token', client_id: CLIENT_ID, refresh_token: current.refresh })
        }));
        if (response.ok) {
          saveTokens(fromTokenResponse(await response.json(), current));
          return true;
        }
        var failure = await readError(response);
        if (response.status === 400 || response.status === 401 || response.status === 403) {
          // The device was revoked or the grant changed: the person has to pair again.
          saveTokens(null);
          emit('auth-lost', failure);
          return false;
        }
        throw failure;
      } finally {
        refreshing = null;
      }
    })();
    return refreshing;
  }

  /* ---- requests ---- */

  async function send(method, path, body, authenticated) {
    var headers = { Accept: 'application/json' };
    if (authenticated && tokens && tokens.access) { headers.Authorization = 'Bearer ' + tokens.access; }
    var init = { method: method, headers: headers };
    if (body !== undefined && body !== null) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    return fetch(absolute(path), withTimeout(init));
  }

  async function request(method, path, body, options) {
    var authenticated = !(options && options.auth === false);
    if (authenticated) {
      if (!isSignedIn()) { throw new ApiError(401, 'not_paired', 'This TV is not paired yet.'); }
      if (tokens.expiresAt - Date.now() < REFRESH_MARGIN_MS) { await refresh(); }
      if (!isSignedIn()) { throw new ApiError(401, 'not_paired', 'This TV is not paired yet.'); }
    }
    var response = await send(method, path, body, authenticated);
    if (response.status === 401 && authenticated) {
      if (await refresh() && isSignedIn()) {
        response = await send(method, path, body, authenticated);
      }
    }
    if (!response.ok) { throw await readError(response); }
    if (response.status === 204) { return null; }
    var text = await response.text();
    return text ? JSON.parse(text) : null;
  }

  function get(path) { return request('GET', path); }
  function enc(value) { return encodeURIComponent(value); }

  /** Text body with the bearer token (subtitle files). */
  async function fetchText(path) {
    if (tokens && tokens.expiresAt - Date.now() < REFRESH_MARGIN_MS) { await refresh(); }
    var response = await send('GET', path, null, true);
    if (response.status === 401 && await refresh()) { response = await send('GET', path, null, true); }
    if (!response.ok) { throw await readError(response); }
    return response.text();
  }

  /* ---- pairing ---- */

  function qualityValue() { return readStore(KEYS.quality) || 'auto'; }
  function hevcEnabled() { return readStore(KEYS.hevc) === '1'; }

  // The TV declares adaptive HLS only (H.264/AAC, optional HEVC): a browser video element cannot send the bearer token,
  // so the client plays everything through hls.js, which can. An empty container list is what forces HLS on the Engine.
  function capabilities() {
    var codecs = ['h264'];
    if (hevcEnabled()) { codecs.push('hevc'); }
    return {
      schema_version: 1,
      containers: [],
      video_codecs: codecs,
      audio_codecs: ['aac'],
      subtitle_formats: ['vtt'],
      protocols: ['https', 'hls'],
      max_width: 1920,
      max_height: 1080,
      max_audio_channels: 2,
      supports_hdr: false,
      supports_playback_speed: false,
      supports_offline_downloads: false
    };
  }

  function deviceName() {
    var ua = (global.navigator && global.navigator.userAgent) || '';
    if (/Tizen/i.test(ua)) { return 'Samsung TV'; }
    if (/Web0S|webOS/i.test(ua)) { return 'LG TV'; }
    return 'TV browser';
  }

  async function beginPairing() {
    var discovery = TV.discovery || {};
    var path = discovery.device_authorization_endpoint || '/api/v1/oauth/device_authorization';
    return request('POST', path, {
      client_id: CLIENT_ID,
      client_name: CLIENT_NAME,
      client_version: CLIENT_VERSION,
      device_name: deviceName(),
      device_class: 'television',
      scope: SCOPES,
      capabilities: capabilities()
    }, { auth: false });
  }

  /**
   * One poll of the token endpoint. Returns { state: 'authorized' | 'pending' | 'slow_down' | 'denied' | 'expired' | 'error', ... }.
   * The caller waits `interval` seconds between polls.
   */
  async function pollPairing(session) {
    var discovery = TV.discovery || {};
    var path = discovery.token_endpoint || '/api/v1/oauth/token';
    try {
      var json = await request('POST', path, {
        grant_type: DEVICE_GRANT,
        client_id: CLIENT_ID,
        device_code: session.device_code
      }, { auth: false });
      saveTokens(fromTokenResponse(json, null));
      return { state: 'authorized' };
    } catch (error) {
      if (!(error instanceof ApiError)) { return { state: 'error', error: error }; }
      if (error.code === 'authorization_pending') { return { state: 'pending', interval: error.interval }; }
      if (error.code === 'slow_down') { return { state: 'slow_down', interval: error.interval }; }
      if (error.code === 'access_denied') { return { state: 'denied', error: error }; }
      if (error.code === 'expired_token') { return { state: 'expired', error: error }; }
      // 429 from the app door's throttle: wait as long as the server asks.
      if (error.status === 429) { return { state: 'slow_down', interval: error.retryAfter || error.interval }; }
      return { state: 'error', error: error };
    }
  }

  /** Revokes this device on the server (best effort) and forgets the tokens. The server address stays. */
  async function signOut() {
    var deviceId = tokens && tokens.deviceId;
    if (deviceId && isSignedIn()) {
      try {
        await request('DELETE', '/api/v1/devices/' + enc(deviceId));
      } catch (e) {
        // Best effort: the tokens are dropped locally either way; an offline TV can be removed from Settings > Devices.
      }
    }
    saveTokens(null);
    writeStore(KEYS.profile, null);
  }

  function forgetServer() {
    saveTokens(null);
    origin = '';
    writeStore(KEYS.server, null);
    writeStore(KEYS.profile, null);
  }

  /* ---- people ---- */

  async function profiles() {
    var json = await get('/api/v1/devices/current/profiles');
    return (json && json.profiles) || [];
  }

  async function switchProfile(profileId, pin) {
    var body = { profile_id: profileId };
    if (pin) { body.pin = pin; }
    var json = await request('POST', '/api/v1/devices/current/switch-profile', body);
    saveTokens(fromTokenResponse(json, tokens));
    return json;
  }

  /** Tells the Engine this TV's codec list (used when the HEVC switch changes). */
  function updateCapabilities() {
    return request('PUT', '/api/v1/devices/current/capabilities', capabilities());
  }

  /* ---- display, details, playback ---- */

  var api = {
    home: function () { return get('/api/v1/display/home?includeCatalog=false'); },
    continueWatching: function (limit) { return get('/api/v1/display/continue?limit=' + (limit || 24) + '&includeCatalog=false'); },
    recent: function (limit) { return get('/api/v1/display/recent?type=all&limit=' + (limit || 18)); },
    browse: function (lane) { return get('/api/v1/display/browse?lane=' + enc(lane) + '&offset=0&limit=48&includeCatalog=false'); },
    browseSearch: function (lane, text) {
      return get('/api/v1/display/browse?lane=' + enc(lane) + '&search=' + enc(text) + '&offset=0&limit=48&includeCatalog=true');
    },
    search: function (text) { return get('/api/v1/display/search?q=' + enc(text) + '&limit=24'); },
    shelf: function (key, lane, cursor) {
      return get('/api/v1/display/shelves/' + enc(key) + '?limit=24' + (lane ? '&lane=' + enc(lane) : '') + (cursor ? '&cursor=' + enc(cursor) : ''));
    },
    group: function (id) { return get('/api/v1/display/groups/' + enc(id) + '?includeCatalog=false'); },
    details: function (type, id) { return get('/api/v1/details/' + enc(type) + '/' + enc(id)); },
    nextEpisodes: function (showId, afterAssetId) {
      return get('/api/v1/details/show/' + enc(showId) + '/next-episodes?after=' + enc(afterAssetId) + '&count=3');
    },
    collections: function (cursor) { return get('/api/v1/collections?limit=24' + (cursor ? '&cursor=' + enc(cursor) : '')); },
    collectionItems: function (id, cursor) {
      return get('/api/v1/collections/' + enc(id) + '/items?limit=48' + (cursor ? '&cursor=' + enc(cursor) : ''));
    },
    universe: function (qid) { return get('/api/v1/universe/' + enc(qid)); },
    manifest: function (assetId) {
      var client = hevcEnabled() ? 'tv-web-hevc' : 'tv-web';
      return get('/api/v1/playback/' + enc(assetId) + '/manifest?client=' + client + '&connectionPath=local');
    },
    playerState: function () { return get('/api/v1/player/state?client=tv-web'); },
    heartbeat: function (body) { return request('POST', '/api/v1/player/heartbeat', body); }
  };

  /* ---- artwork (needs the bearer token, so it is fetched and shown as a blob URL) ---- */

  var art = (function () {
    var cache = {};
    var order = [];
    var queue = [];
    var active = 0;
    var generation = 0;
    var MAX_CACHE = 160;
    var MAX_ACTIVE = 4;

    function remember(path, url) {
      cache[path] = url;
      order.push(path);
      while (order.length > MAX_CACHE) {
        var old = order.shift();
        if (cache[old]) { global.URL.revokeObjectURL(cache[old]); }
        delete cache[old];
      }
    }

    async function fetchBlob(path) {
      if (tokens && tokens.expiresAt - Date.now() < REFRESH_MARGIN_MS) { await refresh(); }
      var response = await send('GET', path, null, true);
      if (response.status === 401 && await refresh()) { response = await send('GET', path, null, true); }
      if (!response.ok) { throw new ApiError(response.status); }
      return global.URL.createObjectURL(await response.blob());
    }

    function pump() {
      while (active < MAX_ACTIVE && queue.length) {
        var job = queue.shift();
        if (job.generation !== generation) { continue; }
        active++;
        run(job);
      }
    }

    async function run(job) {
      try {
        var url = cache[job.path];
        if (!url) {
          url = await fetchBlob(job.path);
          remember(job.path, url);
        }
        if (job.generation === generation) { job.apply(url); }
      } catch (e) {
        // Missing artwork is not an error; the card keeps its text placeholder.
      } finally {
        active--;
        pump();
      }
    }

    return {
      /** Shows the artwork at `path` as the background image of `element` when it arrives. */
      load: function (element, path) {
        if (!element || !path) { return; }
        queue.push({
          path: path,
          generation: generation,
          apply: function (url) {
            element.style.backgroundImage = 'url("' + url + '")';
            element.classList.add('has-art');
          }
        });
        pump();
      },
      /** Drops queued work from the screen being left. Cached images stay for the next screen. */
      cancel: function () { generation++; queue = []; }
    };
  })();

  /* ---- profile choice memory (for the header) ---- */

  function rememberProfile(profile) {
    writeStore(KEYS.profile, profile ? JSON.stringify({ id: profile.profile_id, name: profile.display_name }) : null);
  }
  function rememberedProfile() { return readJson(KEYS.profile); }

  TV.api = {
    KEYS: KEYS,
    CLIENT_ID: CLIENT_ID,
    CLIENT_VERSION: CLIENT_VERSION,
    ApiError: ApiError,
    isNetworkError: isNetworkError,
    on: on,
    server: function () { return origin; },
    hasServer: function () { return !!origin; },
    normalizeServer: normalizeServer,
    absolute: absolute,
    connect: connect,
    isSignedIn: isSignedIn,
    accessToken: accessToken,
    deviceId: function () { return tokens ? tokens.deviceId : ''; },
    profileId: function () { return tokens ? tokens.profileId : ''; },
    refresh: refresh,
    beginPairing: beginPairing,
    pollPairing: pollPairing,
    signOut: signOut,
    forgetServer: forgetServer,
    profiles: profiles,
    switchProfile: switchProfile,
    updateCapabilities: updateCapabilities,
    capabilities: capabilities,
    fetchText: fetchText,
    art: art,
    rememberProfile: rememberProfile,
    rememberedProfile: rememberedProfile,
    settings: {
      quality: qualityValue,
      setQuality: function (value) { writeStore(KEYS.quality, value); },
      hevc: hevcEnabled,
      setHevc: function (on) { writeStore(KEYS.hevc, on ? '1' : '0'); }
    }
  };
  for (var name in api) {
    if (Object.prototype.hasOwnProperty.call(api, name)) { TV.api[name] = api[name]; }
  }
})(window);
