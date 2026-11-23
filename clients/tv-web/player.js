/*
 * Tuvima TV web client - video player.
 *
 * Plays the adaptive HLS package the playback manifest points to. The Dashboard's app door needs the bearer token on
 * every stream request and a browser video element cannot send one, so playback goes through hls.js (which can), loaded
 * on first use from vendor/hls.min.js. Where hls.js is not supported the player falls back to the platform's native HLS
 * (which cannot authenticate, so it only works when the server allows it).
 * Resume comes from the manifest (and the player state); progress is reported with the player heartbeat.
 */
(function (global) {
  'use strict';

  var TV = (global.TV = global.TV || {});
  var el = TV.el;
  var L = TV.playerLogic;
  var W = L.WORDS;

  var HEARTBEAT_MS = 10000;
  var PREPARE_RETRIES = 45;
  var PREPARE_WAIT_MS = 2000;
  var HLS_SCRIPT = 'vendor/hls.min.js';
  var SVG_NS = 'http://www.w3.org/2000/svg';

  // Transport glyphs (24-unit view box). Skip back and skip forward share the replay arrow, mirrored; the seconds sit inside.
  var ICONS = {
    'play': 'M8 5v14l11-7z',
    'pause': 'M6 5h4v14H6zM14 5h4v14h-4z',
    'previous': 'M6 6h2v12H6zM9.5 12L18 18V6z',
    'next': 'M16 6h2v12h-2zM6 18l8.5-6L6 6z',
    'skip-back': 'M12 5V1L7 6l5 5V7c3.31 0 6 2.69 6 6s-2.69 6-6 6-6-2.69-6-6H4c0 4.42 3.58 8 8 8s8-3.58 8-8-3.58-8-8-8z',
    'skip-forward': 'M12 5V1l5 5-5 5V7c-3.31 0-6 2.69-6 6s2.69 6 6 6 6-2.69 6-6h2c0 4.42-3.58 8-8 8s-8-3.58-8-8 3.58-8 8-8z'
  };

  function icon(name) {
    var svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('class', 'tp-icon');
    svg.setAttribute('aria-hidden', 'true');
    var path = document.createElementNS(SVG_NS, 'path');
    path.setAttribute('d', ICONS[name]);
    path.setAttribute('fill', 'currentColor');
    svg.appendChild(path);
    return svg;
  }

  var hlsLoading = null;

  function loadHlsLibrary() {
    if (global.Hls) { return Promise.resolve(global.Hls); }
    if (hlsLoading) { return hlsLoading; }
    hlsLoading = new Promise(function (resolve) {
      var script = document.createElement('script');
      script.src = HLS_SCRIPT;
      script.onload = function () { resolve(global.Hls || null); };
      script.onerror = function () { resolve(null); };
      document.head.appendChild(script);
    });
    return hlsLoading;
  }

  // hls.js request hook: the bearer token goes only to the paired server, never to another host.
  function authorize(xhr, url) {
    var token = TV.api.accessToken();
    if (token && TV.api.isServerUrl(url)) { xhr.setRequestHeader('Authorization', 'Bearer ' + token); }
  }

  // The server hands out WebVTT; some sources are SRT. Browsers only show WebVTT, so SRT is converted.
  function toVtt(text) {
    var body = String(text || '').replace(/^﻿/, '');
    if (/^WEBVTT/.test(body)) { return body; }
    return 'WEBVTT\n\n' + body.replace(/\r+/g, '').replace(/(\d{2}:\d{2}:\d{2}),(\d{3})/g, '$1.$2');
  }

  function wait(ms) {
    return new Promise(function (resolve) { global.setTimeout(resolve, ms); });
  }

  /**
   * Opens the player over the current screen.
   * options: { assetId, workId, title, subtitle, info, kind, showId, restart, onClose(result),
   *   getNext() -> Promise<{assetId, title, ...}|null>, getEpisodes() -> Promise<detail of the show>,
   *   onSwitch(item) / onNext(item): start another episode }.
   * info: { season, episode, number, title, year, description } for the line under the title and the paused details.
   */
  function open(options) {
    var assetId = options.assetId;
    var workId = options.workId || '';
    var root = el('div', 'player');
    var video = el('video', 'player-video');
    video.setAttribute('playsinline', '');
    var status = el('div', 'player-status', 'Preparing playback...');
    var overlay = el('div', 'player-overlay');

    // Bottom left: the title, the one line, and (while paused) a short description.
    var details = el('div', 'player-details');
    var titleNode = el('div', 'player-title', options.title || '');
    var lineNode = el('div', 'player-subtitle', '');
    var descNode = el('div', 'player-desc', '');
    details.appendChild(titleNode);
    details.appendChild(lineNode);
    details.appendChild(descNode);

    // The timeline with the time played at the left and the time left at the right.
    var timeline = el('div', 'player-timeline');
    var timeNow = el('span', 'player-time', '0:00');
    var seek = el('div', 'seek focusable');
    seek.setAttribute('tabindex', '-1');
    seek.setAttribute('data-key', 'seek');
    var seekFill = el('div', 'seek-fill');
    seek.appendChild(seekFill);
    var timeEnd = el('span', 'player-time right', '');
    timeline.appendChild(timeNow);
    timeline.appendChild(seek);
    timeline.appendChild(timeEnd);

    var transportRow = el('div', 'player-transport');
    var toolsRow = el('div', 'player-tools');
    overlay.appendChild(details);
    overlay.appendChild(timeline);
    overlay.appendChild(transportRow);
    overlay.appendChild(toolsRow);

    // The end card (above the controls, at the right) and the side panel (700 px, right edge).
    var upNext = el('div', 'player-next');
    upNext.hidden = true;
    var panel = el('div', 'side-panel');
    panel.hidden = true;
    root.appendChild(video);
    root.appendChild(status);
    root.appendChild(overlay);
    root.appendChild(upNext);
    root.appendChild(panel);
    document.body.appendChild(root);

    var state = {
      closed: false,
      hls: null,
      manifest: null,
      beat: null,
      hideTimer: null,
      inFlight: false,
      blobs: [],
      captions: [],
      captionIndex: -1,
      captionNote: '',
      audioIndex: 0,
      audioNote: '',
      panel: L.panelsInitial(),
      finder: null,
      sourcesReady: false,
      lyricsSourcesReady: false,
      lyrics: { status: 'loading', parsed: null, note: '' }, // status: loading, ready, none or failed
      lyricsGeneration: 0,
      lyricsIndex: -2,
      lyricsLines: [],
      lyricsScroller: null,
      next: null,
      nextTimer: null,
      mediaErrors: 0,
      networkErrors: 0,
      lastPosition: 0,
      started: false,
      ended: false,
      kind: L.kindOf({ kind: options.kind, showId: options.showId }),
      prefs: null,
      skip: L.skipSeconds(null, TV.api.settings.playback()),
      autoplay: L.autoplayOn(null, TV.api.settings.playback()),
      speed: 1,
      appearance: L.appearanceOf(null, TV.api.settings.playback()),
      seasons: null,
      seasonIndex: 0,
      episodesFailed: false,
      nextEpisode: null,
      transport: {},
      tools: {},
      toolSig: '',
      bm: { list: [], loaded: false, tab: 'mine', ticks: true, editId: '', confirmId: '', error: false, add: null },
      tickDuration: -1,
      episodesPromise: null,
      episodeCurrentSub: null
    };

    function button(key, label) {
      var b = el('button', 'btn focusable', label);
      b.type = 'button';
      b.setAttribute('data-key', key);
      return b;
    }

    function showStatus(text) {
      status.textContent = text || '';
      status.hidden = !text;
    }

    function panelIsOpen() { return state.panel.open !== null; }

    /* ---- what is playing: kind, line and neighbours ---- */

    function currentItem() {
      var all = L.flattenEpisodes(state.seasons);
      var id = String(assetId).toLowerCase();
      for (var i = 0; i < all.length; i++) { if (all[i].assetId.toLowerCase() === id) { return all[i]; } }
      return null;
    }

    function neighbourItems() {
      var n = L.neighbours(state.seasons, assetId);
      return { previous: n.previous, next: n.next || state.nextEpisode };
    }

    function lengthSeconds() {
      if (isFinite(video.duration) && video.duration > 0) { return video.duration; }
      return (state.manifest && state.manifest.durationSeconds) || 0;
    }

    function paintDetails() {
      var info = options.info || {};
      var item = currentItem();
      var line = L.detailLine(state.kind, {
        season: info.season || (item && item.season),
        episode: info.episode || (item && item.number),
        number: info.number || (item && item.number),
        title: info.title || (item && item.title),
        year: info.year,
        seconds: lengthSeconds()
      });
      lineNode.textContent = line || options.subtitle || '';
      lineNode.hidden = !lineNode.textContent;
      descNode.textContent = (state.kind === 'episode' || state.kind === 'lesson') ? (info.description || (item && item.description) || '') : '';
    }

    function paintPausedClass() {
      root.classList.toggle('is-paused', video.paused && state.started && !state.ended);
    }

    /* ---- overlay ---- */

    function showOverlay() {
      overlay.classList.add('visible');
      global.clearTimeout(state.hideTimer);
      state.hideTimer = global.setTimeout(function () {
        if (state.closed) { return; }
        if (L.overlayShouldHide({ paused: video.paused, panelOpen: panelIsOpen(), finderOpen: !!state.finder, endCard: !upNext.hidden })) {
          overlay.classList.remove('visible');
        }
      }, L.OVERLAY_HIDE_MS);
    }

    function hideOverlay() {
      global.clearTimeout(state.hideTimer);
      overlay.classList.remove('visible');
    }

    function overlayVisible() { return overlay.classList.contains('visible'); }

    function focusPlay() {
      TV.nav.setScope(root);
      TV.nav.focus(state.transport['play-pause'] || seek);
    }

    function updateProgress() {
      var duration = video.duration;
      var position = video.currentTime || 0;
      state.lastPosition = position;
      seekFill.style.width = (isFinite(duration) && duration > 0 ? Math.min(100, position / duration * 100) : 0) + '%';
      timeNow.textContent = L.formatTime(position);
      timeEnd.textContent = L.timeLeft(position, isFinite(duration) ? duration : 0);
      if (state.tickDuration !== lengthSeconds()) { paintTicks(); }
      if (state.panel.open === 'lyrics') { paintLyricsActive(); }
      if (state.panel.open === 'bookmarks' && state.bm.add) {
        var label = L.bookmarkAtLabel(position);
        if (state.bm.add.textContent !== label) { state.bm.add.textContent = label; }
      }
      if (state.panel.open === 'episodes' && state.panel.view === 'root' && state.episodeCurrentSub) { paintEpisodeCurrent(); }
    }

    function togglePlay() {
      if (video.paused) { resumePlay(); } else { video.pause(); }
    }

    function seekBy(seconds) {
      var target = (video.currentTime || 0) + seconds;
      var duration = isFinite(video.duration) ? video.duration : target;
      video.currentTime = Math.max(0, Math.min(duration, target));
      updateProgress();
    }

    // Skip back and skip forward (the buttons, the Left and Right keys on the timeline, and Rewind and Fast-forward) use the
    // profile's seconds, 10 and 30 by default.
    function skipBack() { seekBy(-state.skip.back); }
    function skipForward() { seekBy(state.skip.forward); }

    seek.tvKey = function (direction) {
      if (direction === 'enter') { togglePlay(); showOverlay(); return true; }
      return false;
    };

    /* ---- transport row ---- */

    function buildTransport() {
      transportRow.textContent = '';
      state.transport = {};
      L.transportFor(state.kind, state.skip, {}).forEach(function (spec) {
        var b = button(spec.key, '');
        b.className = 'btn focusable tp-btn tp-' + spec.key;
        b.appendChild(spec.key === 'play-pause' ? icon('play') : icon(spec.key));
        if (spec.seconds !== undefined) { b.appendChild(el('span', 'tp-secs', String(spec.seconds))); }
        b.addEventListener('click', function () { onTransport(spec.key); });
        state.transport[spec.key] = b;
        transportRow.appendChild(b);
      });
      paintTransport();
    }

    function paintTransport() {
      var n = neighbourItems();
      var playing = !video.paused;
      L.transportFor(state.kind, state.skip, { playing: playing, canPrevious: !!n.previous, canNext: !!n.next }).forEach(function (spec) {
        var b = state.transport[spec.key];
        if (!b) { return; }
        b.setAttribute('aria-label', spec.label);
        b.setAttribute('title', spec.label);
        b.classList.toggle('is-dim', !!spec.dimmed);
        if (spec.dimmed) { b.setAttribute('data-disabled', ''); } else { b.removeAttribute('data-disabled'); }
        if (spec.key === 'play-pause') {
          var want = playing ? 'pause' : 'play';
          if (b.getAttribute('data-icon') !== want) {
            b.setAttribute('data-icon', want);
            b.replaceChild(icon(want), b.firstChild);
          }
        }
        if (spec.seconds !== undefined && b.lastChild) { b.lastChild.textContent = String(spec.seconds); }
      });
    }

    function onTransport(key) {
      var n = neighbourItems();
      if (key === 'play-pause') { togglePlay(); }
      else if (key === 'skip-back') { skipBack(); }
      else if (key === 'skip-forward') { skipForward(); }
      else if (key === 'previous' && n.previous) { switchTo(n.previous); return; }
      else if (key === 'next' && n.next) { switchTo(n.next); return; }
      showOverlay();
    }

    // Starts another episode: the current one reports where it stopped, then the app opens the player again.
    function switchTo(item) {
      if (state.closed) { return; }
      var handler = options.onSwitch || options.onNext;
      if (!handler) { return; }
      global.clearInterval(state.nextTimer);
      if (!state.ended) { beat(true, false); }
      teardown();
      handler(item);
    }

    /* ---- tool row ---- */

    function toolCaps() {
      return {
        hasEpisodes: !!options.getEpisodes && !state.episodesFailed && !(state.seasons && !state.seasons.length),
        canBookmark: !!workId
      };
    }

    function buildTools() {
      var specs = L.toolsFor(state.kind, toolCaps());
      var sig = state.kind + '|' + specs.map(function (t) { return t.key + ':' + t.label; }).join(',');
      if (sig === state.toolSig) { return; }
      state.toolSig = sig;
      var keep = TV.nav.remember();
      toolsRow.textContent = '';
      state.tools = {};
      specs.forEach(function (spec) {
        var b = button('tool-' + spec.key, spec.label);
        b.className = 'btn focusable tool-btn';
        b.addEventListener('click', function () { toggleTool(spec.key); });
        state.tools[spec.key] = b;
        toolsRow.appendChild(b);
      });
      toolsRow.hidden = !specs.length;
      paintTools();
      if (keep && keep.indexOf('tool-') === 0 && toolsRow.querySelector('[data-key="' + keep + '"]')) { TV.nav.restore(toolsRow, keep); }
    }

    // The tool that opened the panel stays highlighted.
    function paintTools() {
      Object.keys(state.tools).forEach(function (key) {
        state.tools[key].classList.toggle('sel', L.PANEL_OF_TOOL[key] === state.panel.open);
      });
    }

    function toggleTool(key) {
      var name = L.PANEL_OF_TOOL[key];
      if (state.panel.open === name) { closePanel(); } else { openSidePanel(name); }
    }

    /* ---- captions ---- */

    function setCaptionMode(index) {
      state.captions.forEach(function (c, i) {
        if (c.track) { c.track.mode = i === index ? 'showing' : 'disabled'; }
      });
    }

    async function loadCaption(entry) {
      if (entry.loaded) { return; }
      var text = await TV.api.fetchText(entry.url);
      var url = global.URL.createObjectURL(new global.Blob([toVtt(text)], { type: 'text/vtt' }));
      state.blobs.push(url);
      var track = document.createElement('track');
      track.kind = 'subtitles';
      track.label = entry.label;
      if (entry.language) { track.srclang = entry.language; }
      track.src = url;
      video.appendChild(track);
      entry.element = track;
      entry.track = track.track;
      entry.loaded = true;
    }

    async function chooseCaption(index) {
      state.captionIndex = index;
      state.captionNote = '';
      if (index >= 0) {
        try {
          await loadCaption(state.captions[index]);
        } catch (e) {
          state.captionIndex = -1;
          state.captionNote = 'That track could not be loaded.';
          setCaptionMode(-1);
          return;
        }
      }
      setCaptionMode(index);
    }

    function removeCaptionTracks() {
      state.captions.forEach(function (c) {
        if (c.element && c.element.parentNode) { c.element.parentNode.removeChild(c.element); }
      });
      state.captions = [];
      state.captionIndex = -1;
    }

    // `seen` is set after a "Find another" choice: the addresses the list had before, so the track that is new (or the one
    // the Engine now marks preferred) is turned on. At the start it is empty and the file's default track is used.
    function prepareCaptions(manifest, seen) {
      state.captions = (manifest.subtitleTracks || []).filter(function (t) { return !!t.deliveryUrl; }).map(function (t, i) {
        return {
          label: L.subtitleLabel(t, i),
          language: t.language || '',
          url: t.deliveryUrl,
          isDefault: !!(t.isDefault || t.isForced),
          loaded: false,
          track: null,
          element: null
        };
      });
      var pick = -1;
      if (seen) {
        var fresh = [];
        state.captions.forEach(function (c, i) { if (!seen[c.url]) { fresh.push(i); } });
        fresh.forEach(function (i) { if (pick < 0 && state.captions[i].isDefault) { pick = i; } });
        if (pick < 0 && fresh.length) { pick = fresh[0]; }
      }
      state.captions.forEach(function (c, i) { if (pick < 0 && c.isDefault) { pick = i; } });
      if (pick >= 0) { return chooseCaption(pick); }
      return Promise.resolve();
    }

    function applyAppearance() {
      ['sub-large', 'sub-contrast', 'sub-bg-none', 'sub-bg-shadow', 'sub-bg-box'].forEach(function (c) { root.classList.remove(c); });
      L.appearanceClasses(state.appearance).forEach(function (c) { root.classList.add(c); });
    }

    /* ---- side panels: frame and rows ---- */

    // A panel is a fixed header (name and Close, with a back arrow in a nested view), a scrolling middle and a fixed footer.
    function panelFrame(title, opts) {
      var o = opts || {};
      panel.textContent = '';
      panel.className = 'side-panel' + (o.extraClass ? ' ' + o.extraClass : '');
      var head = el('div', 'panel-head');
      if (o.back) {
        var back = button('panel-back', '‹');
        back.className = 'btn focusable panel-back';
        back.setAttribute('aria-label', W.back);
        back.addEventListener('click', function () { leaveView(); });
        head.appendChild(back);
      }
      head.appendChild(el('div', 'panel-title', title));
      if (o.chip) { head.appendChild(el('span', 'fa-chip', o.chip)); }
      var closeButton = button('panel-close', W.close);
      closeButton.className = 'btn focusable panel-close';
      closeButton.addEventListener('click', function () { closePanel(); });
      head.appendChild(closeButton);
      var body = el('div', 'panel-body' + (o.bodyClass ? ' ' + o.bodyClass : ''));
      var foot = el('div', 'panel-foot');
      panel.appendChild(head);
      panel.appendChild(body);
      panel.appendChild(foot);
      return { head: head, body: body, foot: foot };
    }

    function frameDone(frame) { frame.foot.hidden = !frame.foot.childNodes.length; }

    // One focusable row. opts: { sub, value, chip, selected, mark, still, disabled, onSelect, className }
    function panelRow(key, label, opts) {
      var o = opts || {};
      var row = button(key, '');
      row.className = 'btn focusable panel-row' + (o.className ? ' ' + o.className : '') + (o.selected ? ' is-selected' : '');
      if (o.still !== undefined) {
        var still = el('span', 'panel-still');
        if (o.still) { TV.api.art.load(still, o.still); }
        row.appendChild(still);
      }
      var main = el('span', 'panel-row-main');
      main.appendChild(el('span', 'panel-row-label', label));
      if (o.sub) { main.appendChild(el('span', 'panel-row-sub', o.sub)); }
      row.appendChild(main);
      if (o.chip) { row.appendChild(el('span', 'row-chip', o.chip)); }
      if (o.value) { row.appendChild(el('span', 'panel-row-value', o.value)); }
      if (o.mark !== false && o.selected !== undefined) { row.appendChild(el('span', 'panel-row-mark', o.selected ? '✓' : '')); }
      if (o.disabled) { row.setAttribute('data-disabled', ''); row.classList.add('is-dim'); }
      if (o.onSelect) { row.addEventListener('click', o.onSelect); }
      return row;
    }

    function panelHeading(parent, label) { parent.appendChild(el('div', 'panel-heading', label)); }

    function switchRow(key, label, on, onSelect) {
      var row = button(key, '');
      row.className = 'btn focusable panel-row switch-row';
      row.setAttribute('role', 'switch');
      row.setAttribute('aria-checked', on ? 'true' : 'false');
      row.appendChild(el('span', 'panel-row-label', label));
      row.appendChild(el('span', 'sw' + (on ? ' on' : '')));
      row.addEventListener('click', onSelect);
      return row;
    }

    function focusKeyInPanel() {
      var cur = TV.nav.current();
      return cur && panel.contains(cur) ? cur.getAttribute('data-key') : null;
    }

    // Draws the open panel again. Focus stays on the same row when it was in the panel; `focusKey` puts it somewhere on purpose.
    function renderPanel(focusKey) {
      var keep = focusKey || focusKeyInPanel();
      var name = state.panel.open;
      if (name === 'episodes') { renderEpisodes(); }
      else if (name === 'audio') { renderAudio(); }
      else if (name === 'bookmarks') { renderBookmarks(); }
      else if (name === 'settings') { renderSettings(); }
      else if (name === 'lyrics') { renderLyrics(); }
      if (keep && panel.querySelector('[data-key="' + keep + '"]')) { TV.nav.restore(panel, keep); }
      else if (focusKey !== undefined || keep) { TV.nav.first(panel); }
    }

    function setPanelView(view, focusKey) {
      state.panel = L.enterView(state.panel, view);
      if (view !== 'edit') { state.bm.editId = ''; }
      state.bm.error = false;
      renderPanel(focusKey || (view === 'root' ? panelRootFocus() : ''));
    }

    // The row that opened a nested view, so Back (or the back arrow) returns to it.
    var VIEW_ORIGIN = {
      seasons: 'season',
      appearance: 'subtitle-appearance',
      speed: 'set-speed',
      'skip-back': 'set-skip-back',
      'skip-forward': 'set-skip-forward',
      info: 'set-info'
    };

    function leaveView() {
      var view = state.panel.view;
      var key = view === 'edit' ? 'bm:' + state.bm.editId : VIEW_ORIGIN[view];
      setPanelView('root', key || panelRootFocus());
    }

    // Where focus goes when a panel opens: the current row (Episodes), the selected row (Audio & subtitles), the Bookmark at
    // button (Bookmarks) or the first row (Playback settings).
    function panelRootFocus() {
      var name = state.panel.open;
      if (name === 'episodes') { return 'ep:' + assetId; }
      if (name === 'audio') {
        var rows = L.audioRows(audioTracks(), state.audioIndex, switchableAudio());
        return rows.length ? 'audio-' + state.audioIndex : (state.captionIndex < 0 ? 'subs-off' : 'subs-' + state.captionIndex);
      }
      if (name === 'bookmarks') { return 'bm-add'; }
      if (name === 'settings') { return 'set-speed'; }
      return '';
    }

    function openSidePanel(name) {
      if (state.closed || !name || !upNext.hidden) { return; }
      if (state.panel.open === name) { return; }
      if (state.panel.open) { leavePanel(); }
      state.panel = L.openPanel(state.panel, name);
      root.classList.add('panel-open');
      panel.hidden = false;
      showOverlay();
      TV.nav.setScope(root);
      paintTools();
      if (name === 'lyrics') { openLyrics(); return; }
      if (name === 'bookmarks') { loadBookmarks(); }
      if (name === 'episodes') {
        loadEpisodes().then(function () {
          if (state.panel.open !== 'episodes') { return; }
          var at = focusKeyInPanel();
          renderPanel(!at || at === 'panel-close' ? panelRootFocus() : undefined);
        });
      }
      if (name === 'audio') { checkSubtitleSources(); }
      renderPanel(panelRootFocus() || '');
    }

    // Closing a panel never changes playback.
    function closePanel() {
      if (!panelIsOpen()) { return; }
      var key = state.panel.open;
      leavePanel();
      state.panel = L.closePanel();
      root.classList.remove('panel-open');
      panel.hidden = true;
      panel.textContent = '';
      panel.className = 'side-panel';
      paintTools();
      TV.nav.setScope(root);
      var tool = null;
      Object.keys(L.PANEL_OF_TOOL).forEach(function (t) { if (L.PANEL_OF_TOOL[t] === key) { tool = state.tools[t]; } });
      TV.nav.focus(tool || state.transport['play-pause']);
      showOverlay();
      paintPausedClass();
    }

    // Drops what only the panel being left was using.
    function leavePanel() {
      state.lyricsGeneration++;
      state.lyricsScroller = null;
      state.lyricsLines = [];
      state.bm.add = null;
      state.episodeCurrentSub = null;
      state.bm.editId = '';
      state.bm.confirmId = '';
    }

    /* ---- Episodes and Lessons panel ---- */

    function loadEpisodes() {
      if (!options.getEpisodes) { return Promise.resolve(); }
      if (state.episodesPromise) { return state.episodesPromise; }
      state.episodesPromise = (async function () {
        try {
          state.seasons = L.episodeSeasons(await options.getEpisodes());
          state.seasonIndex = L.seasonIndexOf(state.seasons, assetId);
        } catch (e) {
          // No list means no Episodes tool; playback itself is not affected.
          state.episodesFailed = true;
          state.seasons = null;
        }
        if (state.closed) { return; }
        buildTools();
        paintDetails();
        paintTransport();
      })();
      return state.episodesPromise;
    }

    function rowContext() {
      var n = neighbourItems();
      return {
        assetId: assetId,
        playing: !video.paused,
        position: video.currentTime || 0,
        duration: lengthSeconds(),
        nextAssetId: n.next ? n.next.assetId : ''
      };
    }

    function paintEpisodeCurrent() {
      var row = panel.querySelector('[data-key="ep:' + assetId + '"] .panel-row-sub');
      var item = currentItem();
      if (!row || !item) { return; }
      var text = L.episodeRow(item, rowContext()).sub;
      if (row.textContent !== text) { row.textContent = text; }
    }

    function renderEpisodes() {
      var kind = state.kind;
      var seasons = state.seasons || [];
      var view = state.panel.view;
      var name = kind === 'lesson' ? W.lessons : W.episodes;
      if (view === 'seasons') {
        var pick = panelFrame(W.season, { back: true });
        seasons.forEach(function (s, i) {
          pick.body.appendChild(panelRow('season-' + i, s.title, {
            selected: i === state.seasonIndex,
            onSelect: function () { state.seasonIndex = i; setPanelView('root', 'ep:' + (s.items[0] ? s.items[0].assetId : '')); }
          }));
        });
        frameDone(pick);
        return;
      }
      var frame = panelFrame(name);
      var season = seasons[state.seasonIndex] || seasons[0];
      var bar = el('div', 'panel-bar');
      if (kind === 'episode' && season) {
        var dropdown = button('season', season.title + ' ▾');
        dropdown.className = 'btn focusable panel-dropdown';
        dropdown.addEventListener('click', function () { setPanelView('seasons', 'season-' + state.seasonIndex); });
        bar.appendChild(dropdown);
      }
      var total = season ? season.items.length : 0;
      bar.appendChild(el('span', 'panel-count', season ? L.countLabel(total, kind) : ''));
      frame.body.appendChild(bar);
      var ctx = rowContext();
      state.episodeCurrentSub = null;
      (season ? season.items : []).forEach(function (item) {
        var r = L.episodeRow(item, ctx);
        var row = panelRow(r.key, r.title, {
          sub: r.sub,
          chip: r.upNext ? W.upNext : '',
          still: r.stillUrl,
          className: 'episode-row' + (r.current ? ' is-current' : ''),
          onSelect: function () { onEpisode(item); }
        });
        if (r.current) { state.episodeCurrentSub = true; }
        frame.body.appendChild(row);
      });
      // Autoplay next episode: a device choice kept on this TV (the profile's preference is read-only for an app).
      frame.foot.appendChild(switchRow('autoplay', W.autoplay, state.autoplay, function () {
        state.autoplay = !state.autoplay;
        TV.api.settings.setPlayback({ autoplay: state.autoplay });
        renderPanel();
      }));
      frameDone(frame);
    }

    // Selecting a row plays or resumes it.
    function onEpisode(item) {
      if (item.assetId.toLowerCase() === String(assetId).toLowerCase()) {
        if (video.paused) { resumePlay(); }
        showOverlay();
        return;
      }
      switchTo(item);
    }

    /* ---- Audio & subtitles panel ---- */

    function audioTracks() { return (state.manifest && state.manifest.audioTracks) || []; }

    function switchableAudio() {
      var hls = state.hls;
      return hls && hls.audioTracks ? hls.audioTracks.length : null;
    }

    function renderAudio() {
      if (state.panel.view === 'appearance') { renderAppearance(); return; }
      var frame = panelFrame(W.audioSubtitles);
      var audios = L.audioRows(audioTracks(), state.audioIndex, switchableAudio());
      if (audios.length) {
        panelHeading(frame.body, W.audio);
        audios.forEach(function (r) {
          frame.body.appendChild(panelRow('audio-' + r.index, r.name, {
            sub: r.sub,
            selected: r.selected,
            disabled: !r.selectable,
            onSelect: function () { chooseAudio(r.index); }
          }));
        });
        if (state.audioNote) { frame.body.appendChild(el('div', 'panel-note', state.audioNote)); }
      }
      panelHeading(frame.body, W.subtitles);
      frame.body.appendChild(panelRow('subs-off', W.off, { selected: state.captionIndex < 0, onSelect: function () { pickCaption(-1); } }));
      state.captions.forEach(function (c, i) {
        frame.body.appendChild(panelRow('subs-' + i, c.label, { selected: i === state.captionIndex, onSelect: function () { pickCaption(i); } }));
      });
      if (state.captionNote) { frame.body.appendChild(el('div', 'panel-note', state.captionNote)); }
      frame.foot.appendChild(panelRow('subtitle-appearance', W.subtitleAppearance, {
        value: '›',
        onSelect: function () { setPanelView('appearance', 'style-' + state.appearance.style); }
      }));
      if (state.sourcesReady) {
        frame.foot.appendChild(panelRow('find-subtitles', TV.findAnother.WORDS.footerRow, { value: '›', onSelect: function () { openFinder(); } }));
      }
      frameDone(frame);
    }

    // Subtitle appearance: Style and Background, saved on this TV as soon as one is chosen.
    function renderAppearance() {
      var frame = panelFrame(W.subtitleAppearance, { back: true });
      panelHeading(frame.body, W.style);
      L.APPEARANCE_STYLES.forEach(function (name) {
        frame.body.appendChild(panelRow('style-' + name, name, {
          selected: state.appearance.style === name,
          onSelect: function () { chooseAppearance({ subtitleStyle: name }); }
        }));
      });
      panelHeading(frame.body, W.background);
      L.APPEARANCE_BACKGROUNDS.forEach(function (name) {
        frame.body.appendChild(panelRow('background-' + name, name, {
          selected: state.appearance.background === name,
          onSelect: function () { chooseAppearance({ subtitleBackground: name }); }
        }));
      });
      frame.foot.appendChild(panelRow('appearance-reset', W.resetToDefault, {
        onSelect: function () {
          TV.api.settings.setPlayback({ subtitleStyle: null, subtitleBackground: null });
          state.appearance = L.appearanceOf(state.prefs, TV.api.settings.playback());
          applyAppearance();
          renderPanel();
        }
      }));
      frame.foot.appendChild(el('div', 'panel-note', W.changesSaved));
      frameDone(frame);
    }

    function chooseAppearance(values) {
      TV.api.settings.setPlayback(values);
      state.appearance = L.appearanceOf(state.prefs, TV.api.settings.playback());
      applyAppearance();
      renderPanel();
    }

    function checkSubtitleSources() {
      state.sourcesReady = false;
      // The footer row shows only when the Engine says a subtitle source is ready for this item (and this profile may use it).
      TV.api.textTrackSources(assetId, 'subtitles').then(function (answer) {
        return TV.findAnother.sourceReady(answer);
      }, function () { return false; }).then(function (ready) {
        if (state.closed || state.panel.open !== 'audio' || state.sourcesReady === ready) { return; }
        state.sourcesReady = ready;
        renderPanel();
      });
    }

    function pickCaption(index) {
      chooseCaption(index).then(function () { if (state.panel.open === 'audio') { renderPanel(); } });
      renderPanel();
    }

    function chooseAudio(index) {
      var hls = state.hls;
      state.audioNote = '';
      if (hls && hls.audioTracks && hls.audioTracks.length > index) {
        hls.audioTrack = index;
        state.audioIndex = index;
      } else {
        state.audioNote = W.notSwitchable;
      }
      renderPanel();
    }

    /* ---- Bookmarks panel ---- */

    async function loadBookmarks() {
      if (!workId) { return; }
      try {
        var answer = await TV.api.bookmarks(workId);
        state.bm.list = Object.prototype.toString.call(answer) === '[object Array]' ? answer : [];
      } catch (e) {
        // A list that cannot be read shows as empty; the Engine decides what this profile may see.
        state.bm.list = [];
      }
      state.bm.loaded = true;
      if (state.closed) { return; }
      paintTicks();
      if (state.panel.open === 'bookmarks') { renderPanel(); }
    }

    function currentBookmarkTab(tabs) {
      if (!tabs.showChoice) { return 'mine'; }
      return state.bm.tab === 'global' ? 'global' : 'mine';
    }

    function renderBookmarks() {
      if (state.panel.view === 'edit') { renderBookmarkEdit(); return; }
      var frame = panelFrame(W.bookmarks);
      var tabs = L.bookmarkTabs(state.bm.list);
      var tab = currentBookmarkTab(tabs);
      if (tabs.showChoice) {
        var seg = el('div', 'seg');
        tabs.tabs.forEach(function (t) {
          var b = button('tab-' + t.key, t.label);
          b.className = 'btn focusable seg-btn' + (t.key === tab ? ' on' : '');
          b.addEventListener('click', function () { state.bm.tab = t.key; state.bm.confirmId = ''; renderPanel('tab-' + t.key); });
          seg.appendChild(b);
        });
        frame.body.appendChild(seg);
      }
      // "Bookmark at {0}": saves at the time it was pressed, at once, with no note.
      var add = button('bm-add', L.bookmarkAtLabel(video.currentTime || 0));
      add.className = 'btn focusable bm-add';
      add.addEventListener('click', function () { addBookmark(); });
      state.bm.add = add;
      frame.body.appendChild(add);
      if (state.bm.error) { frame.body.appendChild(el('div', 'panel-note', W.couldNotSave)); }
      var rows = L.bookmarkRows(tab === 'global' ? tabs.global : tabs.mine, tab);
      if (!rows.length) {
        if (state.bm.loaded) { frame.body.appendChild(el('div', 'panel-note', L.bookmarkEmpty(tab))); }
      } else {
        rows.forEach(function (r) { frame.body.appendChild(bookmarkRow(r)); });
        frame.body.appendChild(el('div', 'panel-note', W.selectBookmark));
      }
      frame.foot.appendChild(switchRow('bm-ticks', W.showOnTimeline, state.bm.ticks, function () {
        state.bm.ticks = !state.bm.ticks;
        paintTicks();
        renderPanel();
      }));
      frameDone(frame);
    }

    // A row: the time, the name or note and a frame from that moment. Select jumps there; Right reaches Edit and Delete on the
    // person's own bookmarks (Delete asks once: Delete or Keep).
    function bookmarkRow(r) {
      var line = el('div', 'bm-row');
      line.appendChild(panelRow(r.key, r.time, {
        sub: r.sub,
        still: r.frameUrl,
        className: 'bm-main',
        onSelect: function () { video.currentTime = Math.max(0, r.seconds); updateProgress(); showOverlay(); }
      }));
      if (r.canChange) {
        if (state.bm.confirmId === r.id) {
          var del = button('bm-delete-' + r.id, W.remove);
          del.className = 'btn focusable bm-btn danger';
          del.addEventListener('click', function () { deleteBookmark(r.id); });
          var keep = button('bm-keep-' + r.id, W.keep);
          keep.className = 'btn focusable bm-btn';
          keep.addEventListener('click', function () { state.bm.confirmId = ''; renderPanel(r.key); });
          line.appendChild(del);
          line.appendChild(keep);
        } else {
          var edit = button('bm-edit-' + r.id, W.edit);
          edit.className = 'btn focusable bm-btn';
          edit.addEventListener('click', function () { state.bm.editId = r.id; setPanelView('edit', 'bm-name'); });
          var remove = button('bm-remove-' + r.id, W.remove);
          remove.className = 'btn focusable bm-btn';
          remove.addEventListener('click', function () { state.bm.confirmId = r.id; renderPanel('bm-keep-' + r.id); });
          line.appendChild(edit);
          line.appendChild(remove);
        }
      }
      return line;
    }

    function editedBookmark() {
      var found = null;
      state.bm.list.forEach(function (b) { if (String(b.id) === state.bm.editId) { found = b; } });
      return found;
    }

    // Rename with the TV's own on-screen keyboard where it has one. There is no note field and no search box on a television.
    function renderBookmarkEdit() {
      var item = editedBookmark();
      var frame = panelFrame(W.edit, { back: true });
      if (!item) { frame.body.appendChild(el('div', 'panel-note', W.noMine)); frameDone(frame); return; }
      panelHeading(frame.body, W.name);
      var input = el('input', 'input focusable');
      input.type = 'text';
      input.maxLength = 120;
      input.value = item.label || '';
      input.setAttribute('data-key', 'bm-name');
      input.setAttribute('aria-label', W.name);
      input.tvKey = function (direction) {
        if (direction === 'enter') { TV.nav.focus(panel.querySelector('[data-key="bm-save"]')); return true; }
        return false;
      };
      frame.body.appendChild(input);
      if (state.bm.error) { frame.body.appendChild(el('div', 'panel-note', W.couldNotSave)); }
      var row = el('div', 'button-row');
      var save = button('bm-save', W.save);
      save.className = 'btn focusable primary';
      save.addEventListener('click', function () { saveBookmarkName(item, input.value); });
      var cancel = button('bm-cancel', W.cancel);
      cancel.className = 'btn focusable';
      cancel.addEventListener('click', function () { setPanelView('root', 'bm:' + item.id); });
      row.appendChild(save);
      row.appendChild(cancel);
      frame.body.appendChild(row);
      frameDone(frame);
    }

    async function addBookmark() {
      if (!workId) { return; }
      state.bm.error = false;
      try {
        var made = await TV.api.createBookmark(workId, L.bookmarkRequest(assetId, video.currentTime || 0, lengthSeconds()));
        if (made && made.id) { state.bm.list = state.bm.list.concat([made]); } else { await loadBookmarks(); }
      } catch (e) {
        state.bm.error = true;
      }
      if (state.closed) { return; }
      paintTicks();
      if (state.panel.open === 'bookmarks') { renderPanel('bm-add'); }
    }

    async function saveBookmarkName(item, value) {
      state.bm.error = false;
      try {
        var changed = await TV.api.updateBookmark(item.id, { label: String(value || '').trim(), note: item.note || '' });
        state.bm.list = state.bm.list.map(function (b) { return String(b.id) === String(item.id) ? (changed || b) : b; });
      } catch (e) {
        state.bm.error = true;
        if (!state.closed && state.panel.open === 'bookmarks') { renderPanel('bm-save'); }
        return;
      }
      if (state.closed) { return; }
      state.bm.editId = '';
      setPanelView('root', 'bm:' + item.id);
    }

    async function deleteBookmark(id) {
      state.bm.error = false;
      try {
        await TV.api.deleteBookmark(id);
        state.bm.list = state.bm.list.filter(function (b) { return String(b.id) !== String(id); });
      } catch (e) {
        state.bm.error = true;
      }
      state.bm.confirmId = '';
      if (state.closed) { return; }
      paintTicks();
      if (state.panel.open === 'bookmarks') { renderPanel('bm-add'); }
    }

    // "Show on timeline": a tick on the timeline for each bookmark.
    function paintTicks() {
      state.tickDuration = lengthSeconds();
      Array.prototype.slice.call(seek.querySelectorAll('.seek-tick')).forEach(function (t) { seek.removeChild(t); });
      if (!state.bm.ticks) { return; }
      L.bookmarkTicks(state.bm.list, state.tickDuration).forEach(function (percent) {
        var tick = el('div', 'seek-tick');
        tick.style.left = percent + '%';
        seek.appendChild(tick);
      });
    }

    /* ---- Playback settings panel ---- */

    function setSpeed(value) {
      state.speed = L.clampSpeed(value);
      video.playbackRate = state.speed;
      TV.api.settings.setPlayback({ speed: state.speed });
    }

    function renderSettings() {
      var view = state.panel.view;
      if (view === 'speed') { renderSpeed(); return; }
      if (view === 'skip-back' || view === 'skip-forward') { renderSkipChoices(view === 'skip-back' ? 'back' : 'forward'); return; }
      if (view === 'info') { renderInformation(); return; }
      var frame = panelFrame(W.playbackSettings);
      frame.body.appendChild(panelRow('set-speed', W.playbackSpeed, { value: L.speedLabel(state.speed) + ' ›', onSelect: function () { setPanelView('speed', 'speed-' + state.speed); } }));
      frame.body.appendChild(panelRow('set-skip-back', W.skipBack, { value: state.skip.back + ' s ›', onSelect: function () { setPanelView('skip-back', 'skip-back-' + state.skip.back); } }));
      frame.body.appendChild(panelRow('set-skip-forward', W.skipForward, { value: state.skip.forward + ' s ›', onSelect: function () { setPanelView('skip-forward', 'skip-forward-' + state.skip.forward); } }));
      frame.body.appendChild(panelRow('set-info', W.playbackInformation, { value: '›', onSelect: function () { setPanelView('info', 'info-back'); } }));
      frame.foot.appendChild(panelRow('set-reset', W.resetPreferences, { onSelect: function () { resetPreferences(); } }));
      frameDone(frame);
    }

    // On a television the speed opens the presets, and Left and Right take the fine steps (0.05) instead of a slider.
    function renderSpeed() {
      var frame = panelFrame(W.playbackSpeed, { back: true });
      frame.body.appendChild(el('div', 'panel-note', W.currentSpeed + ' · ' + L.speedLabel(state.speed)));
      L.SPEED_PRESETS.forEach(function (preset) {
        var row = panelRow('speed-' + preset, L.speedLabel(preset), {
          selected: Math.abs(state.speed - preset) < 0.0001,
          onSelect: function () { setSpeed(preset); renderPanel('speed-' + preset); }
        });
        row.tvKey = function (direction) {
          if (direction !== 'left' && direction !== 'right') { return false; }
          setSpeed(L.stepSpeed(state.speed, direction === 'left' ? -1 : 1));
          renderPanel(L.isPreset(state.speed) ? 'speed-' + state.speed : 'speed-' + preset);
          return true;
        };
        frame.body.appendChild(row);
      });
      frame.foot.appendChild(panelRow('speed-reset', W.resetSpeed, { onSelect: function () { setSpeed(1); renderPanel('speed-1'); } }));
      frameDone(frame);
    }

    function renderSkipChoices(which) {
      var frame = panelFrame(which === 'back' ? W.skipBack : W.skipForward, { back: true });
      L.SKIP_CHOICES[which].forEach(function (seconds) {
        frame.body.appendChild(panelRow('skip-' + which + '-' + seconds, seconds + ' s', {
          selected: state.skip[which] === seconds,
          onSelect: function () {
            TV.api.settings.setPlayback(which === 'back' ? { skipBack: seconds } : { skipForward: seconds });
            state.skip = L.skipSeconds(state.prefs, TV.api.settings.playback());
            paintTransport();
            renderPanel('skip-' + which + '-' + seconds);
          }
        }));
      });
      frameDone(frame);
    }

    function renderInformation() {
      var frame = panelFrame(W.playbackInformation, { back: true });
      L.playbackInfoLines(state.manifest).forEach(function (line) {
        var row = el('div', 'info-row');
        row.appendChild(el('span', 'info-label', line.label));
        row.appendChild(el('span', 'info-value', line.value));
        frame.body.appendChild(row);
      });
      frameDone(frame);
    }

    // The profile's own preferences are read-only for an app, so this clears what was chosen on this TV and the profile's
    // values (read from the Engine) apply again.
    function resetPreferences() {
      TV.api.settings.clearPlayback();
      refreshPreferences();
      renderPanel('set-reset');
    }

    function refreshPreferences() {
      var device = TV.api.settings.playback();
      state.skip = L.skipSeconds(state.prefs, device);
      state.autoplay = L.autoplayOn(state.prefs, device);
      state.appearance = L.appearanceOf(state.prefs, device);
      state.speed = L.clampSpeed(device.speed || (state.prefs && state.prefs.defaultPlaybackSpeed) || 1);
      video.playbackRate = state.speed;
      applyAppearance();
      paintTransport();
    }

    /* ---- Lyrics side panel (music) ---- */

    // Reads the song's lyrics track (the Engine's preferred one) and turns it into lines. A generation number drops a read that
    // was overtaken by the panel closing or by a newer choice.
    async function loadLyrics(note) {
      var mine = ++state.lyricsGeneration;
      state.lyrics = { status: 'loading', parsed: null, note: note || '' };
      state.lyricsIndex = -2;
      if (state.panel.open === 'lyrics') { renderLyrics(); }
      var next;
      try {
        var pick = TV.findAnother.pickLyricsTrack(await TV.api.textTracks(assetId));
        if (!pick) {
          next = { status: 'none', parsed: null };
        } else {
          var parsed = TV.findAnother.parseLyrics(await TV.api.textTrackText(assetId, pick.id));
          next = parsed.lines.length ? { status: 'ready', parsed: parsed } : { status: 'none', parsed: null };
        }
      } catch (e) {
        next = { status: 'failed', parsed: null };
      }
      if (mine !== state.lyricsGeneration || state.closed) { return; }
      state.lyrics = next;
      if (state.panel.open === 'lyrics') { renderLyrics(); }
    }

    function renderLyrics() {
      var words = TV.findAnother;
      var remember = focusKeyInPanel();
      var frame = panelFrame(words.WORDS.lyricsHeading, {
        extraClass: 'lyrics-panel',
        bodyClass: 'lyrics-lines',
        chip: state.lyrics.status === 'ready' ? words.lyricsChip(state.lyrics.parsed) : ''
      });
      var scroller = frame.body;
      state.lyricsScroller = scroller;
      state.lyricsLines = [];
      state.lyricsIndex = -2;
      if (state.lyrics.status === 'ready') {
        state.lyrics.parsed.lines.forEach(function (line) {
          var node = el('div', 'lyrics-line', line.text || '♪');
          scroller.appendChild(node);
          state.lyricsLines.push(node);
        });
      } else {
        var note = state.lyrics.status === 'loading' ? words.WORDS.lookingLyrics
          : state.lyrics.status === 'failed' ? words.WORDS.lyricsNotLoaded : words.WORDS.noLyrics;
        scroller.appendChild(el('div', 'panel-note', (state.lyrics.status === 'loading' && state.lyrics.note) || note));
      }
      if (state.lyricsSourcesReady) {
        frame.foot.appendChild(panelRow('find-lyrics', words.WORDS.findLyricsRow, { value: '›', onSelect: function () { openFinder(); } }));
      }
      frameDone(frame);
      paintLyricsActive();
      if (remember && panel.querySelector('[data-key="' + remember + '"]')) { TV.nav.restore(panel, remember); }
    }

    // The current synced line is highlighted and kept in the middle of the list as the song plays.
    function paintLyricsActive() {
      if (state.lyrics.status !== 'ready' || !state.lyricsScroller) { return; }
      var index = TV.findAnother.activeLyricIndex(state.lyrics.parsed.lines, video.currentTime || 0);
      if (index === state.lyricsIndex) { return; }
      state.lyricsIndex = index;
      state.lyricsLines.forEach(function (node, i) { node.classList.toggle('is-active', i === index); });
      var current = state.lyricsLines[index];
      if (current) {
        var scroller = state.lyricsScroller;
        scroller.scrollTop = current.offsetTop - scroller.clientHeight / 2 + current.clientHeight / 2;
      }
    }

    function openLyrics() {
      state.lyricsSourcesReady = false;
      loadLyrics();
      TV.nav.first(panel);
      // The footer row shows only when the Engine says a lyrics source is ready for this song (and this profile may use it).
      TV.api.textTrackSources(assetId, 'lyrics').then(function (answer) {
        return TV.findAnother.sourceReady(answer);
      }, function () { return false; }).then(function (ready) {
        if (state.closed || state.panel.open !== 'lyrics' || state.lyricsSourcesReady === ready) { return; }
        state.lyricsSourcesReady = ready;
        renderLyrics();
        if (ready && !state.finder) { TV.nav.restore(panel, 'find-lyrics'); }
      });
    }

    /* ---- Find another ---- */

    function openFinder() {
      if (state.finder || state.closed) { return; }
      var lyrics = state.panel.open === 'lyrics';
      var wasPlaying = !video.paused;
      if (wasPlaying) { video.pause(); }
      state.finder = TV.findAnother.open({
        assetId: assetId,
        title: options.title || '',
        kind: lyrics ? 'lyrics' : 'subtitles',
        restoreKeys: onAction,
        onClosed: function () {
          state.finder = null;
          if (state.closed) { return; }
          if (wasPlaying) { resumePlay(); }
          TV.nav.setScope(root);
          TV.nav.restore(panel, lyrics ? 'find-lyrics' : 'find-subtitles');
        },
        onApplied: function () {
          state.finder = null;
          if (state.closed) { return; }
          if (lyrics) {
            // The panel stays open and shows the new lyrics once they are read; the song carries on at the same place.
            if (wasPlaying) { resumePlay(); }
            TV.nav.setScope(root);
            loadLyrics(TV.findAnother.WORDS.applyingLyrics).then(function () { if (state.panel.open === 'lyrics') { TV.nav.restore(panel, 'find-lyrics'); } });
            return;
          }
          // Use this closes the window and the panel and returns to the video at the same place with the new subtitles on.
          closePanel();
          applyChosenSubtitles(wasPlaying);
        }
      });
    }

    function resumePlay() {
      var promise = video.play();
      if (promise && promise.catch) { promise.catch(function () { /* the pause/play events update the UI */ }); }
    }

    // After "Use this": ask the Engine for the track list again, turn the new preferred track on and carry on at the same place.
    async function applyChosenSubtitles(wasPlaying) {
      var position = video.currentTime || state.lastPosition || 0;
      showStatus(TV.findAnother.WORDS.applying);
      try {
        var seen = {};
        state.captions.forEach(function (c) { seen[c.url] = true; });
        var manifest = await TV.api.manifest(assetId);
        if (state.closed) { return; }
        removeCaptionTracks();
        if (state.manifest) { state.manifest.subtitleTracks = manifest.subtitleTracks; }
        await prepareCaptions(manifest, seen);
      } catch (e) {
        state.captionNote = 'The new subtitles could not be loaded.';
      }
      if (state.closed) { return; }
      showStatus('');
      if (Math.abs((video.currentTime || 0) - position) > 1) { video.currentTime = position; }
      if (wasPlaying) { resumePlay(); }
      focusPlay();
      showOverlay();
    }

    /* ---- progress reporting ---- */

    function beat(final, ended) {
      // Nothing is reported until the video has really played: a failed start must not overwrite the saved position.
      if (!assetId || !state.started || (state.inFlight && !final)) { return Promise.resolve(); }
      var duration = isFinite(video.duration) ? video.duration : undefined;
      var position = video.currentTime || state.lastPosition || 0;
      state.inFlight = true;
      return TV.api.heartbeat({
        assetId: assetId,
        isPlaying: !final && !video.paused && !video.ended,
        hasPlaybackEnded: !!ended,
        positionSeconds: position,
        durationSeconds: duration,
        client: 'tv-web'
      }).catch(function () {
        // Progress is best effort; the next beat carries the newer position.
      }).then(function () { state.inFlight = false; });
    }

    function startBeating() {
      global.clearInterval(state.beat);
      state.beat = global.setInterval(function () { if (!video.paused) { beat(false, false); } }, HEARTBEAT_MS);
    }

    /* ---- engine ---- */

    function capLevel(hls) {
      var cap = parseInt(TV.api.settings.quality(), 10);
      if (!isFinite(cap)) { hls.autoLevelCapping = -1; return; }
      var best = 0;
      (hls.levels || []).forEach(function (level, index) { if (level.height && level.height <= cap) { best = index; } });
      hls.autoLevelCapping = best;
    }

    async function startPlayback(url, startAt) {
      var Hls = await loadHlsLibrary();
      if (state.closed) { return; }
      if (Hls && Hls.isSupported()) {
        var hls = new Hls({
          startPosition: startAt > 0 ? startAt : -1,
          xhrSetup: authorize
        });
        state.hls = hls;
        hls.on(Hls.Events.MANIFEST_PARSED, function () {
          capLevel(hls);
          var promise = video.play();
          if (promise && promise.catch) { promise.catch(function () { showOverlay(); }); }
        });
        hls.on(Hls.Events.ERROR, function (event, data) {
          if (!data || !data.fatal) { return; }
          if (data.type === Hls.ErrorTypes.NETWORK_ERROR && state.networkErrors++ < 3) { hls.startLoad(); return; }
          if (data.type === Hls.ErrorTypes.MEDIA_ERROR && state.mediaErrors++ < 3) { hls.recoverMediaError(); return; }
          fail('Playback stopped: the stream could not be read.');
        });
        hls.loadSource(url);
        hls.attachMedia(video);
        return;
      }
      if (video.canPlayType('application/vnd.apple.mpegurl')) {
        video.src = url;
        if (startAt > 0) {
          video.addEventListener('loadedmetadata', function once() {
            video.removeEventListener('loadedmetadata', once);
            video.currentTime = startAt;
          });
        }
        var started = video.play();
        if (started && started.catch) { started.catch(function () { showOverlay(); }); }
        return;
      }
      fail('This TV cannot play adaptive streams.');
    }

    function fail(message) {
      showStatus(message + ' Press Back to return.');
      hideOverlay();
      global.clearInterval(state.beat);
    }

    /* ---- end card ---- */

    // As an episode ends a card shows above the controls: "Up next", the countdown while autoplay is on, the next item and the
    // buttons Cancel and Play now. With autoplay off the card stays and has no countdown.
    function showUpNext(next) {
      state.next = next;
      if (panelIsOpen()) { closePanel(); }
      var remaining = state.autoplay ? L.END_CARD_SECONDS : null;
      var view = L.endCardView(next, state.kind, remaining);
      upNext.hidden = false;
      upNext.textContent = '';
      upNext.appendChild(el('div', 'player-next-heading', view.heading));
      var countdown = el('div', 'player-next-countdown', view.countdown);
      countdown.hidden = !view.countdown;
      upNext.appendChild(countdown);
      var item = el('div', 'player-next-item');
      var still = el('span', 'panel-still');
      if (next.stillUrl) { TV.api.art.load(still, next.stillUrl); }
      item.appendChild(still);
      var words = el('div', 'player-next-words');
      words.appendChild(el('div', 'player-next-title', view.title));
      words.appendChild(el('div', 'player-next-kind', view.kind));
      item.appendChild(words);
      upNext.appendChild(item);
      var buttons = el('div', 'player-next-buttons');
      var cancel = button('end-cancel', view.cancel);
      var play = button('end-play', view.playNow);
      play.classList.add('primary');
      buttons.appendChild(cancel);
      buttons.appendChild(play);
      upNext.appendChild(buttons);
      play.addEventListener('click', function () { switchTo(next); });
      cancel.addEventListener('click', function () { cancelEndCard(); });
      showOverlay();
      TV.nav.setScope(upNext);
      TV.nav.focus(play);
      if (remaining !== null) {
        state.nextTimer = global.setInterval(function () {
          remaining--;
          if (remaining <= 0) { switchTo(next); return; }
          countdown.textContent = L.endCardView(next, state.kind, remaining).countdown;
        }, 1000);
      }
    }

    function cancelEndCard() {
      global.clearInterval(state.nextTimer);
      upNext.hidden = true;
      upNext.textContent = '';
      focusPlay();
      showOverlay();
    }

    /* ---- keys ---- */

    function focusZone() {
      var cur = TV.nav.current();
      if (cur === seek) { return 'timeline'; }
      return cur && panel.contains(cur) ? 'panel' : 'other';
    }

    // The contract's key map (tv-keys), decided by TV.playerLogic.keyEffect and done here.
    function onAction(action) {
      if (state.closed) { return false; }
      if (state.finder) { return false; }
      var result = L.keyEffect({
        overlayVisible: overlayVisible(),
        panelOpen: panelIsOpen(),
        panelView: state.panel.view,
        endCard: !upNext.hidden,
        zone: focusZone()
      }, action);
      var effect = result.effect;
      if (effect === 'cancel-end-card') { cancelEndCard(); }
      else if (effect === 'close-player') { close(); }
      else if (effect === 'panel-root') { leaveView(); }
      else if (effect === 'close-panel') { closePanel(); }
      else if (effect === 'hide-overlay') { hideOverlay(); }
      else if (effect === 'skip-back') { skipBack(); showOverlay(); }
      else if (effect === 'skip-forward') { skipForward(); showOverlay(); }
      else if (effect === 'toggle-play') { togglePlay(); showOverlay(); }
      else if (effect === 'play') { resumePlay(); showOverlay(); }
      else if (effect === 'pause') { video.pause(); showOverlay(); }
      else if (effect === 'show-overlay') { showOverlay(); focusPlay(); }
      else if (effect === 'move') { if (upNext.hidden) { showOverlay(); } }
      return result.consume;
    }

    TV.nav.setKeyHandler(onAction);

    /* ---- video events ---- */

    video.addEventListener('timeupdate', updateProgress);
    video.addEventListener('loadedmetadata', function () { paintDetails(); updateProgress(); });
    video.addEventListener('durationchange', function () { paintDetails(); updateProgress(); });
    video.addEventListener('playing', function () {
      state.started = true;
      showStatus('');
      if (Math.abs(video.playbackRate - state.speed) > 0.001) { video.playbackRate = state.speed; }
      paintTransport();
      paintPausedClass();
      if (overlayVisible()) { showOverlay(); }
    });
    video.addEventListener('pause', function () {
      paintTransport();
      paintPausedClass();
      showOverlay();
      if (!state.closed) { beat(false, false); }
    });
    video.addEventListener('play', function () { paintTransport(); paintPausedClass(); });
    video.addEventListener('waiting', function () { showStatus('Buffering...'); });
    video.addEventListener('seeked', function () { beat(false, false); });
    video.addEventListener('error', function () {
      if (!state.hls) { fail('Playback stopped: the video could not be played.'); }
    });
    video.addEventListener('ended', function () {
      state.ended = true;
      beat(true, true);
      paintPausedClass();
      if (options.getNext) {
        options.getNext().then(function (next) {
          if (state.closed) { return; }
          if (next) { showUpNext(next); } else { close(); }
        }, function () { close(); });
      } else {
        close();
      }
    });

    /* ---- lifecycle ---- */

    var backHandler = TV.nav.pushBack(function () { close(); return true; });

    function teardown() {
      if (state.closed) { return; }
      state.closed = true;
      global.clearInterval(state.beat);
      global.clearInterval(state.nextTimer);
      global.clearTimeout(state.hideTimer);
      if (state.finder) { state.finder.close(); state.finder = null; }
      TV.nav.setKeyHandler(null);
      TV.nav.popBack(backHandler);
      TV.nav.setScope(null);
      if (state.hls) { state.hls.destroy(); state.hls = null; }
      video.pause();
      video.removeAttribute('src');
      video.load();
      state.blobs.forEach(function (url) { global.URL.revokeObjectURL(url); });
      if (root.parentNode) { root.parentNode.removeChild(root); }
    }

    function close() {
      if (state.closed) { return; }
      var position = video.currentTime || state.lastPosition || 0;
      var ended = state.ended;
      if (!ended) { beat(true, false); }
      teardown();
      if (options.onClose) { options.onClose({ position: position, ended: ended }); }
    }

    applyAppearance();
    buildTransport();
    buildTools();
    paintDetails();
    showOverlay();
    focusPlay(); // focus starts on the transport row, on Play

    // The profile's skip seconds, Autoplay and subtitle look come from the Engine; until they arrive the defaults (10 and 30) apply.
    TV.api.playbackPreferences().then(function (prefs) {
      if (state.closed) { return; }
      state.prefs = prefs;
      refreshPreferences();
    }, function () {
      // No preferences answer is not an error: the contract's defaults stay.
    });

    (async function begin() {
      try {
        var manifest = null;
        for (var attempt = 0; attempt < PREPARE_RETRIES && !state.closed; attempt++) {
          manifest = await TV.api.manifest(assetId);
          if (manifest.hlsStatus !== 'preparing') { break; }
          showStatus('Preparing adaptive playback...');
          await wait(PREPARE_WAIT_MS);
        }
        if (state.closed) { return; }
        state.manifest = manifest;
        var kind = L.kindOf({ kind: options.kind, mediaType: manifest && manifest.mediaType, showId: options.showId });
        if (kind !== state.kind) { state.kind = kind; buildTransport(); }
        buildTools();
        paintDetails();
        var ready = manifest && manifest.recommendedDelivery === 'hls' && manifest.hlsUrl &&
          (manifest.hlsStatus === 'ready' || manifest.hlsStatus === 'streaming');
        if (!ready) {
          var reason = 'This title cannot be played on this TV.';
          if (manifest && manifest.recommendedDelivery === 'reader') { reason = 'This title is for reading, not playing.'; }
          else if (manifest && manifest.hlsStatus === 'unavailable') { reason = 'The server cannot prepare adaptive playback right now.'; }
          else if (manifest && manifest.hlsStatus === 'preparing') { reason = 'The server is still preparing this title. Try again in a moment.'; }
          fail(reason);
          return;
        }

        var startAt = 0;
        if (!options.restart) {
          var resume = manifest.resume || {};
          var completed = (resume.progressPct || 0) >= 95;
          startAt = !completed && resume.positionSeconds > 10 ? resume.positionSeconds : 0;
          if (!startAt && !completed) { startAt = await resumeFromPlayerState(assetId); }
        }

        prepareCaptions(manifest, null);
        showStatus('');
        updateProgress();
        showOverlay();
        startBeating();
        // The episode list (Previous, Next and the Episodes panel), the next episode and the person's bookmarks on the timeline
        // are read beside the start; none of them holds playback up.
        if (options.getEpisodes) { loadEpisodes(); }
        if (options.getNext && state.kind === 'episode') {
          options.getNext().then(function (next) {
            if (state.closed) { return; }
            state.nextEpisode = next;
            paintTransport();
          }, function () {
            // No next episode is known; Next stays dimmed and the end card closes the player.
          });
        }
        if (workId) { loadBookmarks(); }
        await startPlayback(TV.api.absolute(manifest.hlsUrl), startAt);
      } catch (error) {
        if (state.closed) { return; }
        if (error && error.status === 403) { fail('This profile cannot play this title.'); }
        else if (error && error.status === 404) { fail('This title is no longer available.'); }
        else { fail('Playback could not start. Check the connection to the server.'); }
      }
    })();

    return { close: close };
  }

  // The player state remembers where this profile stopped; used when the manifest has no resume point.
  async function resumeFromPlayerState(assetId) {
    try {
      var playerState = await TV.api.playerState();
      var item = playerState && playerState.currentItem;
      if (item && String(item.assetId || '').toLowerCase() === String(assetId).toLowerCase() &&
          playerState.positionSeconds > 10 && playerState.progressPct < 95) {
        return playerState.positionSeconds;
      }
    } catch (e) {
      // No saved state is not an error: playback starts from the beginning.
    }
    return 0;
  }

  TV.player = { open: open, formatTime: L.formatTime, toVtt: toVtt, authorize: authorize };
})(window);
