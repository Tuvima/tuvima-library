/*
 * Tuvima TV web client - screens and routing.
 *
 * Screens: connect, pair, home, watch/read/listen lanes, collections, search, details, shelf (see all),
 * group, collection items, universe, profiles, settings, and the player (player.js).
 * Everything is drawn into #screen; the top bar (#topnav) shows on the main screens.
 */
(function (global) {
  'use strict';

  var TV = global.TV;
  var api = TV.api;
  var el = TV.el;
  var nav = TV.nav;

  var screenRoot = document.getElementById('screen');
  var topNav = document.getElementById('topnav');
  var dialogRoot = document.getElementById('dialog');

  var MAIN_TABS = [
    { key: 'home', label: 'Home', screen: 'home' },
    { key: 'watch', label: 'Watch', screen: 'lane', params: { lane: 'watch' } },
    { key: 'read', label: 'Read', screen: 'lane', params: { lane: 'read' } },
    { key: 'listen', label: 'Listen', screen: 'lane', params: { lane: 'listen' } },
    { key: 'collections', label: 'Collections', screen: 'collections' },
    { key: 'search', label: 'Search', screen: 'search' },
    { key: 'profiles', label: 'Profiles', screen: 'profiles' },
    { key: 'settings', label: 'Settings', screen: 'settings' }
  ];

  var QUALITIES = [
    { value: 'auto', label: 'Auto' },
    { value: '1080', label: 'Up to 1080p' },
    { value: '720', label: 'Up to 720p' },
    { value: '480', label: 'Up to 480p' }
  ];

  var GROUP_LABELS = { show: 'Show', series: 'Series', collection: 'Collection', list: 'List', universe: 'Universe' };

  /* ------------------------------------------------------------------ helpers */

  function text(value) { return value === undefined || value === null ? '' : String(value); }

  function initials(name) {
    var words = text(name).trim().split(/\s+/).filter(Boolean);
    if (!words.length) { return '?'; }
    var letters = words.length === 1 ? words[0].slice(0, 2) : words[0].charAt(0) + words[words.length - 1].charAt(0);
    return letters.toUpperCase();
  }

  function first() {
    for (var i = 0; i < arguments.length; i++) {
      if (arguments[i]) { return arguments[i]; }
    }
    return '';
  }

  // Artwork path for a card, by the tile's shape. The Engine sends several sizes; a TV tile needs the medium one.
  function pickArt(artwork, shape) {
    var a = artwork || {};
    if (shape === 'landscape') {
      return first(a.backgroundMediumUrl, a.backgroundUrl, a.bannerMediumUrl, a.bannerUrl, a.coverMediumUrl, a.coverUrl);
    }
    if (shape === 'square') {
      return first(a.squareMediumUrl, a.squareUrl, a.coverMediumUrl, a.coverUrl, a.squareSmallUrl);
    }
    return first(a.coverMediumUrl, a.coverUrl, a.coverLargeUrl, a.coverSmallUrl, a.squareMediumUrl, a.squareUrl);
  }

  function shapeOf(card, fallback) {
    var s = text(card && card.preferredShape).toLowerCase();
    if (s.indexOf('land') === 0 || s.indexOf('wide') === 0 || s === 'background') { return 'landscape'; }
    if (s.indexOf('sq') === 0) { return 'square'; }
    if (s.indexOf('port') === 0 || s === 'cover' || s === 'poster') { return 'portrait'; }
    return fallback || 'portrait';
  }

  function assetOf(card) {
    if (card && card.assetId) { return card.assetId; }
    var actions = (card && card.actions) || [];
    for (var i = 0; i < actions.length; i++) {
      if (actions[i].assetId) { return actions[i].assetId; }
    }
    if (card && card.progress && card.progress.resumeAction && card.progress.resumeAction.assetId) {
      return card.progress.resumeAction.assetId;
    }
    if (card && card.episodeContext && card.episodeContext.episodeAssetId) { return card.episodeContext.episodeAssetId; }
    return '';
  }

  // Maps a media type word from the Engine to the details route name (same mapping as the other native clients).
  function detailType(mediaType) {
    var v = text(mediaType).toLowerCase();
    if (v.indexOf('movie') >= 0) { return 'movie'; }
    if (v.indexOf('episode') >= 0) { return 'tvEpisode'; }
    if (v.indexOf('tv') >= 0) { return 'tvShow'; }
    if (v.indexOf('audiobook') >= 0) { return 'audiobook'; }
    if (v.indexOf('book') >= 0) { return 'book'; }
    if (v.indexOf('comic') >= 0) { return 'comicIssue'; }
    if (v.indexOf('music') >= 0 || v.indexOf('album') >= 0) { return 'musicAlbum'; }
    return 'work';
  }

  function laneOfType(type) {
    var v = text(type).toLowerCase();
    if (v === 'movie' || v === 'tvshow' || v === 'tvseason' || v === 'tvepisode' || v === 'movieseries') { return 'watch'; }
    if (v === 'audiobook' || v === 'musicalbum') { return 'listen'; }
    if (v === 'book' || v === 'comicissue' || v === 'bookseries' || v === 'comicseries') { return 'read'; }
    return 'watch';
  }

  function canPlay(type) {
    var lane = laneOfType(type);
    return lane === 'watch' || lane === 'listen';
  }

  function errorMessage(error) {
    if (!error) { return 'Something went wrong.'; }
    if (api.isNetworkError(error)) { return 'Cannot reach the server. Check that this TV is on the same network and try again.'; }
    if (error.status === 401) { return 'This TV needs to be paired again.'; }
    if (error.status === 403) { return 'This profile is not allowed to see that.'; }
    if (error.status === 404) { return 'That is no longer available.'; }
    if (error.status === 429) { return 'The server asked this TV to slow down. Try again in a moment.'; }
    return error.message || 'The server could not answer.';
  }

  /* ------------------------------------------------------------------ router */

  var history = [];
  var currentScreen = null; // { name, params, focusKey }
  var generation = 0;
  var screens = {};
  var toastTimer = null;

  function isMain(name) {
    return name === 'home' || name === 'lane' || name === 'collections' || name === 'search' || name === 'profiles' || name === 'settings';
  }

  function activeTab(name, params) {
    if (name === 'lane') { return params && params.lane; }
    return name;
  }

  function paintNav(name, params) {
    topNav.textContent = '';
    topNav.hidden = !isMain(name);
    if (topNav.hidden) { return; }
    var tab = activeTab(name, params);
    var brand = el('img', 'brand');
    brand.src = 'assets/tuvima-icon.svg';
    brand.alt = '';
    topNav.appendChild(brand);
    MAIN_TABS.forEach(function (item) {
      var b = el('button', 'btn navitem focusable' + (item.key === tab ? ' current' : ''), item.label);
      b.type = 'button';
      b.setAttribute('data-key', 'nav-' + item.key);
      b.addEventListener('click', function () { go(item.screen, item.params || {}, { root: true }); });
      topNav.appendChild(b);
    });
    var who = api.rememberedProfile();
    if (who && who.name) { topNav.appendChild(el('div', 'who', who.name)); }
  }

  function go(name, params, options) {
    var opts = options || {};
    if (currentScreen && !opts.replace) {
      currentScreen.focusKey = nav.remember();
      if (opts.root) { history = []; } else { history.push(currentScreen); }
    } else if (opts.root) {
      history = [];
    }
    show({ name: name, params: params || {}, focusKey: null });
  }

  function goBack() {
    if (!history.length) { return false; }
    show(history.pop());
    return true;
  }

  function show(target) {
    currentScreen = target;
    var mine = ++generation;
    api.art.cancel();
    nav.setScope(null);
    screenRoot.textContent = '';
    screenRoot.scrollTop = 0;
    paintNav(target.name, target.params);
    var ctx = {
      root: screenRoot,
      isCurrent: function () { return mine === generation; },
      focus: function () {
        if (mine === generation) { nav.restore(screenRoot, target.focusKey); }
      }
    };
    var screen = screens[target.name];
    var result;
    try {
      result = screen(target.params, ctx);
    } catch (error) {
      showError(ctx, error, function () { show(target); });
      return;
    }
    if (result && result.catch) {
      result.catch(function (error) { if (ctx.isCurrent()) { showError(ctx, error, function () { show(target); }); } });
    }
  }

  function refreshCurrent() {
    if (currentScreen) { currentScreen.focusKey = nav.remember(); show(currentScreen); }
  }

  function showError(ctx, error, retry) {
    if (!ctx.isCurrent()) { return; }
    if (error && error.status === 401 && !api.isSignedIn()) { go('pair', {}, { root: true, replace: true }); return; }
    var box = el('div', 'panel center');
    box.appendChild(el('h2', '', 'That did not work'));
    box.appendChild(el('p', 'muted', errorMessage(error)));
    var row = el('div', 'button-row');
    var again = el('button', 'btn primary focusable', 'Try again');
    again.type = 'button';
    again.setAttribute('data-key', 'retry');
    again.addEventListener('click', retry);
    row.appendChild(again);
    if (history.length) {
      var back = el('button', 'btn focusable', 'Go back');
      back.type = 'button';
      back.setAttribute('data-key', 'goback');
      back.addEventListener('click', goBack);
      row.appendChild(back);
    }
    box.appendChild(row);
    ctx.root.textContent = '';
    ctx.root.appendChild(box);
    nav.first(ctx.root);
  }

  function loading(ctx, message) {
    ctx.root.textContent = '';
    ctx.root.appendChild(el('div', 'loading', message || 'Loading...'));
  }

  function toast(message) {
    var node = document.getElementById('toast');
    node.textContent = message;
    node.hidden = false;
    global.clearTimeout(toastTimer);
    toastTimer = global.setTimeout(function () { node.hidden = true; }, 4000);
  }

  /* ------------------------------------------------------------------ building blocks */

  function button(label, key, onClick, className) {
    var b = el('button', 'btn focusable' + (className ? ' ' + className : ''), label);
    b.type = 'button';
    b.setAttribute('data-key', key);
    b.addEventListener('click', onClick);
    return b;
  }

  function tile(spec) {
    var shape = spec.shape || 'portrait';
    var t = el('button', 'tile focusable shape-' + shape);
    t.type = 'button';
    t.setAttribute('data-key', spec.key);
    var artBox = el('div', 'tile-art');
    if (!spec.art) { artBox.appendChild(el('span', 'tile-initial', initials(spec.title))); }
    t.appendChild(artBox);
    if (spec.badge) { t.appendChild(el('span', 'tile-badge', spec.badge)); }
    if (spec.progress > 0 && spec.progress < 100) {
      var bar = el('div', 'tile-progress');
      var fill = el('div', 'tile-progress-fill');
      fill.style.width = Math.max(2, Math.min(100, spec.progress)) + '%';
      bar.appendChild(fill);
      t.appendChild(bar);
    }
    t.appendChild(el('div', 'tile-title', spec.title));
    if (spec.subtitle) { t.appendChild(el('div', 'tile-sub', spec.subtitle)); }
    t.addEventListener('click', spec.onOpen);
    if (spec.art) { api.art.load(artBox, spec.art); }
    return t;
  }

  function row(title, tiles) {
    var section = el('section', 'row');
    section.appendChild(el('h2', 'row-title', title));
    var track = el('div', 'row-track');
    tiles.forEach(function (t) { track.appendChild(t); });
    section.appendChild(track);
    return section;
  }

  function moreTile(key, label, onOpen) {
    var t = el('button', 'tile focusable more-tile shape-portrait');
    t.type = 'button';
    t.setAttribute('data-key', key);
    t.appendChild(el('div', 'more-label', label));
    t.addEventListener('click', onOpen);
    return t;
  }

  function cardTile(card, ctx, options) {
    var opts = options || {};
    var shape = opts.shape || shapeOf(card, 'portrait');
    var progress = card.progress && card.progress.percent ? card.progress.percent : 0;
    var subtitleParts = [];
    if (card.subtitle) { subtitleParts.push(card.subtitle); }
    else if (card.facts && card.facts.length) { subtitleParts.push(card.facts.slice(0, 2).join(' - ')); }
    return tile({
      key: 'card:' + card.id + (opts.keySuffix || ''),
      title: card.title,
      subtitle: subtitleParts.join(''),
      art: pickArt(card.artwork, shape),
      shape: shape,
      progress: progress,
      onOpen: function () { openCard(card, opts); }
    });
  }

  function shelvesToRows(page, ctx, opts) {
    var rows = [];
    ((page && page.shelves) || []).forEach(function (shelf) {
      var items = shelf.items || [];
      if (!items.length) { return; }
      var tiles = items.map(function (card) { return cardTile(card, ctx, opts); });
      if (shelf.seeAllRoute && shelf.key) {
        tiles.push(moreTile('more:' + shelf.key, 'See all', function () {
          go('shelf', { key: shelf.key, title: shelf.title, lane: opts && opts.lane });
        }));
      }
      rows.push(row(shelf.title || '', tiles));
    });
    return rows;
  }

  /* ------------------------------------------------------------------ opening things */

  function openCard(card, opts) {
    var subject = text(card.subject);
    var lane = (opts && opts.lane) || undefined;
    var assetId = assetOf(card);
    var ep = card.episodeContext;

    // A card from Continue plays straight away when it knows its file.
    if (opts && opts.continuePlay && assetId && card.mediaType && canPlay(detailType(card.mediaType)) &&
        text(card.continuationState) !== 'Completed') {
      var showId = ep && ep.showWorkId;
      play({ assetId: assetId, title: ep ? ep.showTitle : card.title, subtitle: ep ? ep.episodeTitle : card.subtitle, showId: showId });
      return;
    }

    if (card.flags && card.flags.isCollection && card.collectionId && subject !== 'TvShow') {
      go('group', { id: card.collectionId, title: card.title, lane: lane });
      return;
    }

    var type;
    var id = card.id;
    if (subject === 'TvShow') { type = 'tvShow'; id = (ep && ep.showWorkId) || card.id; }
    else if (subject === 'TvEpisode') { type = 'tvEpisode'; id = (ep && ep.episodeWorkId) || card.workId || card.id; }
    else if (subject === 'Album') { type = 'musicAlbum'; id = card.workId || card.id; }
    else { type = detailType(card.mediaType); id = card.workId || card.id; }

    go('details', {
      type: type,
      id: id,
      title: card.title,
      assetId: assetId,
      showId: ep && ep.showWorkId,
      lane: lane
    });
  }

  function openDetailRoute(route, fallbackId, fallbackType, title) {
    var match = /\/details\/([A-Za-z]+)\/([0-9a-fA-F-]{36})/.exec(text(route));
    go('details', {
      type: match ? match[1] : (fallbackType || 'work'),
      id: match ? match[2] : fallbackId,
      title: title
    });
  }

  function play(options) {
    var returnKey = nav.remember();
    TV.player.open({
      assetId: options.assetId,
      title: options.title,
      subtitle: options.subtitle,
      restart: options.restart,
      getNext: options.showId ? function () { return nextEpisode(options.showId, options.assetId); } : null,
      onNext: function (next) {
        play({ assetId: next.assetId, title: options.title, subtitle: next.title, showId: options.showId });
      },
      onClose: function () {
        // The saved position changed; redraw the screen under the player so Continue and progress are current.
        if (currentScreen) { currentScreen.focusKey = returnKey; show(currentScreen); }
      }
    });
  }

  async function nextEpisode(showId, assetId) {
    var result = await api.nextEpisodes(showId, assetId);
    var episodes = (result && result.episodes) || [];
    if (!episodes.length) { return null; }
    var e = episodes[0];
    var label = (e.season_number ? 'S' + e.season_number : '') + (e.episode_number ? 'E' + e.episode_number + ' ' : '') + text(e.title);
    return { assetId: e.asset_id, title: label.trim() };
  }

  // The details route works on works, but a manifest needs the media file. The browse search lists cards with their file.
  async function resolveAsset(workId, title, lane) {
    try {
      var page = await api.browseSearch(lane, title);
      var cards = (page.catalog || []).slice();
      (page.shelves || []).forEach(function (shelf) { cards = cards.concat(shelf.items || []); });
      for (var i = 0; i < cards.length; i++) {
        var c = cards[i];
        if ((c.id === workId || c.workId === workId) && assetOf(c)) { return assetOf(c); }
      }
    } catch (e) {
      // The caller shows "Not playable from here" when no file is found.
    }
    return '';
  }

  /* ------------------------------------------------------------------ dialogs */

  function openDialog(build) {
    return new Promise(function (resolve) {
      dialogRoot.textContent = '';
      dialogRoot.hidden = false;
      var box = el('div', 'dialog-box');
      dialogRoot.appendChild(box);
      var previousFocus = nav.current();
      var previousScope = nav.scope();
      var backHandler = null;
      function done(value) {
        nav.popBack(backHandler);
        dialogRoot.hidden = true;
        dialogRoot.textContent = '';
        nav.setScope(previousScope);
        if (previousFocus && document.body.contains(previousFocus)) { nav.focus(previousFocus); }
        resolve(value);
      }
      backHandler = nav.pushBack(function () { done(null); return true; });
      build(box, done);
      nav.setScope(box);
      nav.first(box);
    });
  }

  function confirmDialog(message, yesLabel) {
    return openDialog(function (box, done) {
      box.appendChild(el('h2', '', message));
      var row = el('div', 'button-row');
      row.appendChild(button('Cancel', 'dialog-cancel', function () { done(false); }));
      row.appendChild(button(yesLabel || 'OK', 'dialog-ok', function () { done(true); }, 'primary'));
      box.appendChild(row);
    }).then(function (value) { return value === true; });
  }

  function pinDialog(name, message) {
    return openDialog(function (box, done) {
      var pin = '';
      box.appendChild(el('h2', '', 'PIN for ' + name));
      var note = el('p', 'muted', message || 'Enter the PIN with the number buttons.');
      box.appendChild(note);
      var dots = el('div', 'pin-dots', '');
      box.appendChild(dots);
      function paint() { dots.textContent = pin.length ? new Array(pin.length + 1).join('● ') : '–'; }
      paint();
      var pad = el('div', 'keypad');
      ['1', '2', '3', '4', '5', '6', '7', '8', '9', 'Clear', '0', 'Del'].forEach(function (k) {
        pad.appendChild(button(k, 'pin-' + k, function () {
          if (k === 'Clear') { pin = ''; } else if (k === 'Del') { pin = pin.slice(0, -1); } else if (pin.length < 8) { pin += k; }
          paint();
        }, 'key'));
      });
      box.appendChild(pad);
      var row = el('div', 'button-row');
      row.appendChild(button('Cancel', 'pin-cancel', function () { done(null); }));
      row.appendChild(button('OK', 'pin-ok', function () { if (pin) { done(pin); } }, 'primary'));
      box.appendChild(row);
    });
  }

  /* ------------------------------------------------------------------ screens: connect and pair */

  screens.connect = async function (params, ctx) {
    var root = ctx.root;
    root.textContent = '';
    var panel = el('div', 'panel center');
    var logo = el('img', 'logo');
    logo.src = 'assets/tuvima-logo-dark.svg';
    logo.alt = 'Tuvima Library';
    panel.appendChild(logo);
    panel.appendChild(el('h1', '', 'Connect to your library'));
    panel.appendChild(el('p', 'muted', 'Enter the address of your Tuvima server, as shown in a web browser on your home network.'));
    var input = el('input', 'input focusable');
    input.type = 'text';
    input.setAttribute('data-key', 'server-input');
    input.setAttribute('autocomplete', 'off');
    input.setAttribute('autocapitalize', 'none');
    input.setAttribute('spellcheck', 'false');
    input.placeholder = 'http://nas.local';
    input.value = api.server() || (/^https?:$/.test(global.location.protocol) ? global.location.origin : '');
    panel.appendChild(input);
    var message = el('p', 'message', params && params.message ? params.message : '');
    panel.appendChild(message);
    var submit = button('Connect', 'connect', async function () {
      var address = input.value.trim();
      if (!address) { message.textContent = 'Enter the address first.'; return; }
      message.textContent = 'Looking for your server...';
      try {
        await api.connect(address);
        if (!ctx.isCurrent()) { return; }
        go(api.isSignedIn() ? 'home' : 'pair', {}, { root: true, replace: true });
      } catch (error) {
        if (!ctx.isCurrent()) { return; }
        message.textContent = error && error.status === 404
          ? 'That address answered, but app access is switched off on the server (Settings, Network, Apps).'
          : 'Could not find a Tuvima server there. ' + errorMessage(error);
      }
    }, 'primary');
    // Enter on an untouched box opens the TV's on-screen keyboard; once the address was typed, Enter connects.
    var edited = false;
    input.addEventListener('input', function () { edited = true; });
    input.tvKey = function (direction) {
      if (direction === 'enter' && edited && input.value.trim()) { edited = false; submit.click(); return true; }
      return false;
    };
    panel.appendChild(submit);
    root.appendChild(panel);
    nav.first(root);
  };

  screens.pair = async function (params, ctx) {
    var root = ctx.root;
    loading(ctx, 'Getting a pairing code...');
    var session;
    try {
      if (!TV.discovery) { await api.connect(api.server()); }
      session = await api.beginPairing();
    } catch (error) {
      if (!ctx.isCurrent()) { return; }
      var failure = el('div', 'panel center');
      failure.appendChild(el('h2', '', 'Could not start pairing'));
      failure.appendChild(el('p', 'muted', errorMessage(error)));
      var failRow = el('div', 'button-row');
      failRow.appendChild(button('Try again', 'retry', function () { show(currentScreen); }, 'primary'));
      failRow.appendChild(button('Change server', 'change-server', function () { api.forgetServer(); go('connect', {}, { root: true, replace: true }); }));
      failure.appendChild(failRow);
      root.textContent = '';
      root.appendChild(failure);
      nav.first(root);
      return;
    }
    if (!ctx.isCurrent()) { return; }

    root.textContent = '';
    var panel = el('div', 'panel center');
    panel.appendChild(el('h1', '', 'Pair this TV'));
    panel.appendChild(el('p', 'muted', 'On your phone or computer, open this page and enter the code:'));
    panel.appendChild(el('div', 'url', session.verification_uri));
    panel.appendChild(el('div', 'code', session.user_code));
    var message = el('p', 'message', params && params.message ? params.message : 'Waiting for you to approve this TV...');
    panel.appendChild(message);
    var rowButtons = el('div', 'button-row');
    rowButtons.appendChild(button('New code', 'new-code', function () { show(currentScreen); }));
    rowButtons.appendChild(button('Change server', 'change-server', function () {
      api.forgetServer();
      go('connect', {}, { root: true, replace: true });
    }));
    panel.appendChild(rowButtons);
    root.appendChild(panel);
    nav.first(root);

    var interval = Math.max(2, session.interval || 5);
    var deadline = Date.now() + (session.expires_in || 600) * 1000;
    while (ctx.isCurrent()) {
      await new Promise(function (resolve) { global.setTimeout(resolve, interval * 1000); });
      if (!ctx.isCurrent()) { return; }
      if (Date.now() > deadline) {
        go('pair', { message: 'That code expired. Here is a new one.' }, { root: true, replace: true });
        return;
      }
      var result = await api.pollPairing(session);
      if (!ctx.isCurrent()) { return; }
      if (result.state === 'authorized') {
        await rememberActiveProfile();
        go('home', {}, { root: true, replace: true });
        return;
      }
      if (result.state === 'slow_down') { interval = Math.max(interval + 5, result.interval || 0); }
      else if (result.state === 'pending' && result.interval) { interval = Math.max(interval, result.interval); }
      else if (result.state === 'denied') {
        go('pair', { message: 'That request was denied. Here is a new code if you want to try again.' }, { root: true, replace: true });
        return;
      } else if (result.state === 'expired') {
        go('pair', { message: 'That code expired. Here is a new one.' }, { root: true, replace: true });
        return;
      } else if (result.state === 'error') {
        message.textContent = errorMessage(result.error) + ' Still trying...';
      }
    }
  };

  async function rememberActiveProfile() {
    try {
      var list = await api.profiles();
      var active = list.filter(function (p) { return p.is_active; })[0];
      api.rememberProfile(active || null);
    } catch (e) {
      // The name in the top bar is a convenience; pairing already worked.
    }
  }

  /* ------------------------------------------------------------------ screens: browse */

  screens.home = async function (params, ctx) {
    loading(ctx);
    var results = await Promise.all([
      api.home().catch(function (e) { return { error: e }; }),
      api.continueWatching().catch(function () { return null; }),
      api.recent().catch(function () { return null; })
    ]);
    if (!ctx.isCurrent()) { return; }
    if (results[0] && results[0].error) { throw results[0].error; }
    var home = results[0];
    var rows = [];

    // Continue always leads the page, whatever else the server sends.
    rows = rows.concat(shelvesToRows(results[1], ctx, { continuePlay: true, keySuffix: ':c' }));
    var homeShelves = Object.assign({}, home, {
      shelves: ((home && home.shelves) || []).filter(function (s) { return text(s.key).toLowerCase().indexOf('continue') < 0; })
    });
    rows = rows.concat(shelvesToRows(homeShelves, ctx, {}));

    var recent = results[2];
    var recentCards = ((recent && recent.items) || []).map(function (i) { return i.catalogue; }).filter(Boolean);
    if (recentCards.length) {
      rows.push(row('Recently added', recentCards.map(function (c) { return cardTile(c, ctx, { keySuffix: ':r' }); })));
    }

    ctx.root.textContent = '';
    if (!rows.length) {
      ctx.root.appendChild(emptyPanel('Your library is empty', 'Add folders in Tuvima on your computer and they will appear here.'));
    } else {
      rows.forEach(function (r) { ctx.root.appendChild(r); });
    }
    ctx.focus();
  };

  function emptyPanel(title, body) {
    var panel = el('div', 'panel center');
    panel.appendChild(el('h2', '', title));
    panel.appendChild(el('p', 'muted', body));
    return panel;
  }

  screens.lane = async function (params, ctx) {
    var lane = params.lane;
    loading(ctx);
    var page = await api.browse(lane);
    if (!ctx.isCurrent()) { return; }
    var rows = shelvesToRows(page, ctx, { lane: lane });
    ctx.root.textContent = '';
    if (lane === 'read') {
      ctx.root.appendChild(el('p', 'note', 'Reading is available in the Tuvima apps for phones, tablets and the web. This TV shows your books and comics so you can browse them.'));
    }
    if (!rows.length) {
      ctx.root.appendChild(emptyPanel('Nothing here yet', 'Items you add to this part of your library will appear here.'));
    } else {
      rows.forEach(function (r) { ctx.root.appendChild(r); });
    }
    ctx.focus();
  };

  screens.shelf = async function (params, ctx) {
    var cursor = null;
    var grid = el('div', 'grid');
    var more = null;
    loading(ctx);

    async function loadPage() {
      var page = await api.shelf(params.key, params.lane, cursor);
      if (!ctx.isCurrent()) { return; }
      var firstNew = null;
      ((page.shelf && page.shelf.items) || []).forEach(function (card) {
        var t = cardTile(card, ctx, { lane: params.lane });
        if (!firstNew) { firstNew = t; }
        grid.appendChild(t);
      });
      if (more && more.parentNode) { more.parentNode.removeChild(more); }
      cursor = page.nextCursor || null;
      if (page.hasMore && cursor) {
        more = moreTile('more-page', 'Load more', function () { loadPage().catch(function (e) { toast(errorMessage(e)); }); });
        grid.appendChild(more);
      }
      return firstNew;
    }

    var firstTile = await loadPage();
    if (!ctx.isCurrent()) { return; }
    ctx.root.textContent = '';
    ctx.root.appendChild(el('h1', 'page-title', params.title || 'All'));
    ctx.root.appendChild(grid);
    ctx.focus();
    if (firstTile) { nav.focus(firstTile); }
  };

  screens.group = async function (params, ctx) {
    loading(ctx);
    var page = await api.group(params.id);
    if (!ctx.isCurrent()) { return; }
    ctx.root.textContent = '';
    ctx.root.appendChild(el('h1', 'page-title', page.title || params.title || ''));
    if (page.subtitle) { ctx.root.appendChild(el('p', 'muted', page.subtitle)); }
    var rows = shelvesToRows(page, ctx, { lane: params.lane });
    if (!rows.length) { ctx.root.appendChild(emptyPanel('Nothing here', 'This group has no items you can open.')); }
    rows.forEach(function (r) { ctx.root.appendChild(r); });
    ctx.focus();
  };

  /* ------------------------------------------------------------------ screens: collections */

  screens.collections = async function (params, ctx) {
    var cursor = null;
    var grid = el('div', 'grid');
    var more = null;
    loading(ctx);

    function entryTile(entry) {
      var kind = GROUP_LABELS[text(entry.group_type).toLowerCase()] || 'Collection';
      var cover = entry.artwork_items && entry.artwork_items[0] ? entry.artwork_items[0].cover_url : '';
      return tile({
        key: 'collection:' + entry.id,
        title: entry.name,
        subtitle: kind + ' - ' + (entry.item_count || 0) + (entry.item_count === 1 ? ' item' : ' items'),
        art: cover,
        shape: 'portrait',
        onOpen: function () {
          if (text(entry.group_type).toLowerCase() === 'universe' && entry.universe_qid) {
            go('universe', { qid: entry.universe_qid, title: entry.name });
          } else {
            go('collectionItems', { id: entry.id, title: entry.name });
          }
        }
      });
    }

    async function loadPage() {
      var page = await api.collections(cursor);
      if (!ctx.isCurrent()) { return null; }
      var firstNew = null;
      (page.items || []).forEach(function (entry) {
        var t = entryTile(entry);
        if (!firstNew) { firstNew = t; }
        grid.appendChild(t);
      });
      if (more && more.parentNode) { more.parentNode.removeChild(more); }
      cursor = page.next_cursor || null;
      if (page.has_more && cursor) {
        more = moreTile('more-page', 'Load more', function () { loadPage().catch(function (e) { toast(errorMessage(e)); }); });
        grid.appendChild(more);
      }
      return { first: firstNew, count: (page.items || []).length };
    }

    var result = await loadPage();
    if (!result || !ctx.isCurrent()) { return; }
    ctx.root.textContent = '';
    ctx.root.appendChild(el('h1', 'page-title', 'Collections'));
    if (!result.count) {
      ctx.root.appendChild(emptyPanel('No collections yet', 'Series, shows and Universes appear here once your library has them.'));
    } else {
      ctx.root.appendChild(grid);
    }
    ctx.focus();
  };

  screens.collectionItems = async function (params, ctx) {
    var cursor = null;
    var grid = el('div', 'grid');
    var more = null;
    loading(ctx);

    async function loadPage() {
      var page = await api.collectionItems(params.id, cursor);
      if (!ctx.isCurrent()) { return null; }
      var firstNew = null;
      (page.items || []).forEach(function (item) {
        var t = tile({
          key: 'item:' + item.id,
          title: item.title,
          subtitle: item.creator || '',
          art: item.cover_url,
          shape: text(item.media_type).toLowerCase().indexOf('music') >= 0 ? 'square' : 'portrait',
          onOpen: function () { openDetailRoute(item.detail_route, item.work_id, detailType(item.media_type), item.title); }
        });
        if (!firstNew) { firstNew = t; }
        grid.appendChild(t);
      });
      if (more && more.parentNode) { more.parentNode.removeChild(more); }
      cursor = page.next_cursor || null;
      if (page.has_more && cursor) {
        more = moreTile('more-page', 'Load more', function () { loadPage().catch(function (e) { toast(errorMessage(e)); }); });
        grid.appendChild(more);
      }
      return firstNew;
    }

    await loadPage();
    if (!ctx.isCurrent()) { return; }
    ctx.root.textContent = '';
    ctx.root.appendChild(el('h1', 'page-title', params.title || 'Collection'));
    ctx.root.appendChild(grid);
    ctx.focus();
  };

  screens.universe = async function (params, ctx) {
    loading(ctx);
    var data = await api.universe(params.qid);
    if (!ctx.isCurrent()) { return; }
    ctx.root.textContent = '';
    ctx.root.appendChild(el('h1', 'page-title', data.name || params.title || 'Universe'));
    if (data.description) { ctx.root.appendChild(el('p', 'muted', data.description)); }
    var any = false;
    (data.shelves || []).forEach(function (entry) {
      var shelf = entry.shelf || {};
      var items = shelf.items || [];
      if (!items.length) { return; }
      any = true;
      var kind = GROUP_LABELS[text(entry.group_type).toLowerCase()] || '';
      ctx.root.appendChild(row((shelf.title || '') + (kind ? ' (' + kind + ')' : ''),
        items.map(function (card) { return cardTile(card, ctx, { keySuffix: ':' + entry.collection_id }); })));
    });
    if (!any) { ctx.root.appendChild(emptyPanel('Nothing to show', 'No items in this Universe are available to this profile.')); }
    ctx.focus();
  };

  /* ------------------------------------------------------------------ screens: search */

  screens.search = async function (params, ctx) {
    var root = ctx.root;
    root.textContent = '';
    var bar = el('div', 'search-bar');
    var input = el('input', 'input focusable');
    input.type = 'search';
    input.setAttribute('data-key', 'search-input');
    input.setAttribute('autocomplete', 'off');
    input.placeholder = 'Search your library';
    input.value = (params && params.q) || '';
    var results = el('div', 'search-results');
    var status = el('p', 'message', '');

    async function run() {
      var q = input.value.trim();
      if (!q) { status.textContent = 'Type something to search for.'; return; }
      status.textContent = 'Searching...';
      results.textContent = '';
      try {
        var response = await api.search(q);
        if (!ctx.isCurrent()) { return; }
        currentScreen.params = { q: q };
        var shown = 0;
        (response.sections || []).forEach(function (section) {
          var items = section.results || [];
          if (!items.length) { return; }
          shown += items.length;
          results.appendChild(row(section.title, items.map(function (r) {
            return tile({
              key: 'result:' + r.id,
              title: r.title,
              subtitle: first(r.subtitle, r.year, r.creator),
              art: r.artworkUrl,
              shape: text(r.mediaType).toLowerCase().indexOf('music') >= 0 ? 'square' : 'portrait',
              onOpen: function () {
                openDetailRoute(r.detailRoute, r.id, r.mediaType ? detailType(r.mediaType) : detailType(r.entityType), r.title);
              }
            });
          })));
        });
        status.textContent = shown ? '' : 'Nothing matched "' + q + '".';
        if (shown) { var firstResult = results.querySelector('.tile'); if (firstResult) { nav.focus(firstResult); } }
      } catch (error) {
        if (ctx.isCurrent()) { status.textContent = errorMessage(error); }
      }
    }

    // Enter on an untouched box opens the TV's on-screen keyboard; once something was typed, Enter searches.
    var edited = false;
    input.addEventListener('input', function () { edited = true; });
    input.tvKey = function (direction) {
      if (direction === 'enter' && edited && input.value.trim()) { edited = false; run(); return true; }
      return false;
    };
    bar.appendChild(input);
    bar.appendChild(button('Search', 'search-go', run, 'primary'));
    bar.appendChild(button('Clear', 'search-clear', function () {
      input.value = '';
      results.textContent = '';
      status.textContent = '';
      nav.focus(input);
    }));
    root.appendChild(bar);
    root.appendChild(status);
    root.appendChild(results);
    if (input.value) { await run(); } else { nav.focus(input); }
  };

  /* ------------------------------------------------------------------ screens: details */

  function detailFacts(vm) {
    var f = vm.facts || {};
    var parts = [];
    if (f.year) { parts.push(f.year); }
    if (f.contentRating) { parts.push(f.contentRating); }
    if (f.runtime || f.duration) { parts.push(f.runtime || f.duration); }
    if (f.seasonCount) { parts.push(f.seasonCount + (String(f.seasonCount) === '1' ? ' season' : ' seasons')); }
    if (f.network) { parts.push(f.network); }
    return parts.join(' - ');
  }

  function episodeLabel(item) {
    var ctxEp = item.episodeContext || {};
    var prefix = ctxEp.seasonNumber ? 'S' + ctxEp.seasonNumber : '';
    if (ctxEp.episodeNumber) { prefix += 'E' + ctxEp.episodeNumber; }
    return (prefix ? prefix + ' ' : '') + text(item.title);
  }

  // The episode "Play" should start: the one in progress, else the first not yet finished, else the first.
  function pickEpisode(groups) {
    var all = [];
    (groups || []).forEach(function (g) { (g.items || []).forEach(function (i) { if (i.episodeContext && i.episodeContext.episodeAssetId) { all.push(i); } }); });
    var i;
    for (i = 0; i < all.length; i++) { if (all[i].episodeContext.state === 'InProgress') { return all[i]; } }
    for (i = 0; i < all.length; i++) { if (all[i].episodeContext.state !== 'Completed') { return all[i]; } }
    return all[0] || null;
  }

  screens.details = async function (params, ctx) {
    loading(ctx);
    var vm = await api.details(params.type, params.id);
    if (!ctx.isCurrent()) { return; }
    var type = text(params.type);
    var isShow = type.toLowerCase() === 'tvshow';
    var lane = laneOfType(type);
    var playable = canPlay(type);
    var art = vm.artwork || {};
    var hero = art.heroArtwork || {};

    var wrap = el('div', 'details');
    var backdrop = el('div', 'details-backdrop');
    wrap.appendChild(backdrop);
    var backdropPath = first(art.backdropUrl, hero.largeUrl, hero.url, art.bannerUrl);
    if (backdropPath) { api.art.load(backdrop, backdropPath); }

    var body = el('div', 'details-body');
    var posterBox = el('div', 'details-poster');
    var posterPath = first(art.posterUrl, art.coverUrl, art.portraitUrl);
    if (posterPath) { api.art.load(posterBox, posterPath); } else { posterBox.appendChild(el('span', 'tile-initial', initials(vm.title))); }
    body.appendChild(posterBox);

    var info = el('div', 'details-info');
    info.appendChild(el('h1', 'details-title', vm.title || params.title || ''));
    if (vm.subtitle) { info.appendChild(el('div', 'details-subtitle', vm.subtitle)); }
    var facts = detailFacts(vm);
    if (facts) { info.appendChild(el('div', 'details-facts', facts)); }
    if (vm.facts && vm.facts.genres && vm.facts.genres.length) { info.appendChild(el('div', 'details-genres', vm.facts.genres.slice(0, 5).join(' - '))); }
    if (vm.tagline) { info.appendChild(el('div', 'details-tagline', vm.tagline)); }
    if (vm.description) { info.appendChild(el('p', 'details-description', vm.description)); }
    var cast = (vm.previewContributors || []).map(function (c) { return c.displayName; }).filter(Boolean).slice(0, 6);
    if (cast.length) { info.appendChild(el('div', 'details-cast', 'With ' + cast.join(', '))); }
    if (vm.progress && vm.progress.percent > 0 && vm.progress.label) { info.appendChild(el('div', 'details-progress', vm.progress.label)); }

    var actions = el('div', 'button-row');
    info.appendChild(actions);
    body.appendChild(info);
    wrap.appendChild(body);

    // Episodes of a show: one row per season, each episode plays directly.
    var seasonRows = [];
    var chosen = null;
    if (isShow) {
      var groups = (vm.sequencePlacement && vm.sequencePlacement.groups) || [];
      chosen = pickEpisode(groups);
      groups.forEach(function (g) {
        var items = (g.items || []).filter(function (i) { return i.isOwned !== false; });
        if (!items.length) { return; }
        seasonRows.push(row(g.title || 'Episodes', items.map(function (item) {
          var e = item.episodeContext || {};
          return tile({
            key: 'ep:' + item.id,
            title: episodeLabel(item),
            subtitle: item.duration || '',
            art: item.episodeStillUrl || item.artworkUrl,
            shape: 'landscape',
            progress: item.progressPercent || 0,
            onOpen: function () {
              if (!e.episodeAssetId) { toast('This episode is not available to play.'); return; }
              play({ assetId: e.episodeAssetId, title: vm.title, subtitle: episodeLabel(item), showId: params.id });
            }
          });
        })));
      });
    }

    var assetId = params.assetId || '';
    var showId = params.showId || (isShow ? params.id : '');

    if (playable) {
      if (isShow) {
        if (chosen) {
          var e = chosen.episodeContext;
          var resuming = e.state === 'InProgress';
          actions.appendChild(button((resuming ? 'Resume ' : 'Play ') + episodeLabel(chosen), 'play', function () {
            play({ assetId: e.episodeAssetId, title: vm.title, subtitle: episodeLabel(chosen), showId: params.id });
          }, 'primary'));
        }
      } else {
        var playButton = button(lane === 'listen' ? 'Listen' : 'Play', 'play', async function () {
          if (!assetId) {
            playButton.textContent = 'Finding the file...';
            assetId = await resolveAsset(params.id, vm.title || params.title, lane);
            playButton.textContent = vm.progress && vm.progress.percent > 0 ? 'Resume' : (lane === 'listen' ? 'Listen' : 'Play');
          }
          if (!assetId) { toast('This title cannot be played from here yet. Start it from Home or Watch.'); return; }
          play({ assetId: assetId, title: vm.title, subtitle: vm.subtitle, showId: showId });
        }, 'primary');
        if (vm.progress && vm.progress.percent > 0) { playButton.textContent = 'Resume'; }
        actions.appendChild(playButton);
        if (vm.progress && vm.progress.percent > 0) {
          actions.appendChild(button('Start over', 'restart', async function () {
            if (!assetId) { assetId = await resolveAsset(params.id, vm.title || params.title, lane); }
            if (!assetId) { toast('This title cannot be played from here yet.'); return; }
            play({ assetId: assetId, title: vm.title, subtitle: vm.subtitle, restart: true, showId: showId });
          }));
        }
      }
    } else {
      info.appendChild(el('p', 'note', 'This is a book or comic. Open it in the Tuvima apps for phones, tablets and the web to read it.'));
    }
    actions.appendChild(button('Back', 'back', function () { goBack(); }));

    ctx.root.textContent = '';
    ctx.root.appendChild(wrap);
    seasonRows.forEach(function (r) { ctx.root.appendChild(r); });
    ctx.focus();
    if (!currentScreen.focusKey) { nav.first(actions); }
  };

  /* ------------------------------------------------------------------ screens: profiles and settings */

  screens.profiles = async function (params, ctx) {
    loading(ctx);
    var list = await api.profiles();
    if (!ctx.isCurrent()) { return; }
    ctx.root.textContent = '';
    ctx.root.appendChild(el('h1', 'page-title', 'Who is watching?'));
    var message = el('p', 'message', params && params.message ? params.message : '');
    ctx.root.appendChild(message);
    var grid = el('div', 'profile-grid');

    list.forEach(function (p) {
      var b = el('button', 'profile focusable' + (p.is_active ? ' current' : ''));
      b.type = 'button';
      b.setAttribute('data-key', 'profile:' + p.profile_id);
      var avatar = el('div', 'avatar');
      if (p.avatar_url) { api.art.load(avatar, p.avatar_url); } else { avatar.appendChild(el('span', 'avatar-initials', initials(p.display_name))); }
      b.appendChild(avatar);
      b.appendChild(el('div', 'profile-name', p.display_name));
      var tags = [];
      if (p.is_active) { tags.push('In use'); }
      if (p.is_kids) { tags.push('Kids'); }
      if (p.requires_pin) { tags.push('PIN'); }
      if (tags.length) { b.appendChild(el('div', 'profile-tags', tags.join(' - '))); }
      b.addEventListener('click', function () { chooseProfile(p, message, ctx); });
      grid.appendChild(b);
    });
    ctx.root.appendChild(grid);
    ctx.focus();
    if (!currentScreen.focusKey) {
      var active = grid.querySelector('.profile.current') || grid.querySelector('.profile');
      if (active) { nav.focus(active); }
    }
  };

  async function chooseProfile(profile, message, ctx) {
    if (profile.is_active) { go('home', {}, { root: true }); return; }
    var pin = null;
    var prompt = null;
    while (true) {
      if (profile.requires_pin || prompt) {
        pin = await pinDialog(profile.display_name, prompt);
        if (!pin) { return; }
      }
      try {
        await api.switchProfile(profile.profile_id, pin);
        api.rememberProfile(profile);
        go('home', {}, { root: true });
        return;
      } catch (error) {
        if (error.status === 428) { prompt = 'That PIN was not accepted. Try again.'; continue; }
        if (error.status === 409) { message.textContent = profile.display_name + ' is already using this TV.'; return; }
        if (error.status === 403) { message.textContent = 'This TV is not allowed to use that profile.'; return; }
        if (error.status === 429) {
          message.textContent = error.code === 'pin_locked'
            ? 'Too many wrong PINs. Try again later.'
            : 'Too many switches. Try again in a minute.';
          return;
        }
        if (error.status === 401) { await api.signOut(); go('pair', { message: 'Pair this TV again to continue.' }, { root: true }); return; }
        message.textContent = errorMessage(error);
        return;
      }
    }
  }

  screens.settings = async function (params, ctx) {
    var root = ctx.root;
    root.textContent = '';
    root.appendChild(el('h1', 'page-title', 'Settings'));
    var list = el('div', 'settings');
    var message = el('p', 'message', '');

    function setting(key, label, valueText, onActivate, value) {
      var b = el('button', 'setting focusable');
      b.type = 'button';
      b.setAttribute('data-key', key);
      b.appendChild(el('span', 'setting-label', label));
      var v = el('span', 'setting-value', valueText);
      b.appendChild(v);
      b.addEventListener('click', function () { onActivate(v, b); });
      list.appendChild(b);
      return b;
    }

    function qualityLabel() {
      var current = api.settings.quality();
      var match = QUALITIES.filter(function (q) { return q.value === current; })[0];
      return match ? match.label : 'Auto';
    }

    setting('quality', 'Video quality', qualityLabel(), function (v) {
      var index = 0;
      QUALITIES.forEach(function (q, i) { if (q.value === api.settings.quality()) { index = i; } });
      api.settings.setQuality(QUALITIES[(index + 1) % QUALITIES.length].value);
      v.textContent = qualityLabel();
    });

    setting('hevc', 'HEVC (H.265) video', api.settings.hevc() ? 'On' : 'Off', async function (v) {
      var on = !api.settings.hevc();
      api.settings.setHevc(on);
      v.textContent = on ? 'On' : 'Off';
      try {
        await api.updateCapabilities();
        message.textContent = 'Saved. Applies the next time you start something.';
      } catch (error) {
        message.textContent = 'Saved on this TV; the server could not be told: ' + errorMessage(error);
      }
    });

    var who = api.rememberedProfile();
    setting('profile', 'Profile', who && who.name ? who.name : 'Switch', function () { go('profiles', {}, { root: true }); });
    setting('server', 'Server', api.server().replace(/^https?:\/\//, ''), async function () {
      if (await confirmDialog('Connect to a different server? This TV will be signed out.', 'Change server')) {
        await api.signOut();
        api.forgetServer();
        go('connect', {}, { root: true });
      }
    });
    setting('signout', 'Sign out of this TV', 'Remove', async function () {
      if (await confirmDialog('Remove this TV from your library? You can pair it again at any time.', 'Sign out')) {
        await api.signOut();
        go('pair', {}, { root: true });
      }
    });
    root.appendChild(list);
    root.appendChild(message);
    root.appendChild(el('p', 'muted', 'Tuvima TV ' + api.CLIENT_VERSION));
    ctx.focus();
  };

  /* ------------------------------------------------------------------ start */

  screens.start = async function (params, ctx) {
    loading(ctx, 'Starting...');
    if (!api.hasServer()) { go('connect', {}, { root: true, replace: true }); return; }
    try {
      await api.connect(api.server());
    } catch (error) {
      if (!ctx.isCurrent()) { return; }
      if (api.isSignedIn() && api.isNetworkError(error)) {
        // Offline start with a pairing on file: try the library anyway; the screen shows its own error.
        go('home', {}, { root: true, replace: true });
        return;
      }
      go('connect', { message: 'Could not reach the server. ' + errorMessage(error) }, { root: true, replace: true });
      return;
    }
    go(api.isSignedIn() ? 'home' : 'pair', {}, { root: true, replace: true });
  };

  nav.pushBack(function () {
    if (goBack()) { return true; }
    // From the first screen: Back goes to the top bar first; from the top bar it leaves the app through the
    // platform (Tizen, webOS), or is left to the browser when there is no platform to call.
    if (currentScreen && isMain(currentScreen.name)) {
      var focused = nav.current();
      if (focused && !topNav.contains(focused)) {
        var tab = topNav.querySelector('.navitem.current') || topNav.querySelector('.navitem');
        if (tab) { nav.focus(tab); return true; }
      }
    }
    return nav.exitApp();
  });

  api.on('auth-lost', function () {
    go('pair', { message: 'This TV was signed out. Pair it again to continue.' }, { root: true });
  });

  global.TV.app = { go: go, back: goBack, refresh: refreshCurrent, toast: toast, screens: screens };

  show({ name: 'start', params: {}, focusKey: null });
})(window);
