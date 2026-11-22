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

  var HEARTBEAT_MS = 10000;
  var OVERLAY_HIDE_MS = 5000;
  var SEEK_STEP = 10;
  var PREPARE_RETRIES = 45;
  var PREPARE_WAIT_MS = 2000;
  var NEXT_COUNTDOWN = 10;
  var HLS_SCRIPT = 'vendor/hls.min.js';

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

  function formatTime(seconds) {
    if (!isFinite(seconds) || seconds < 0) { seconds = 0; }
    var total = Math.floor(seconds);
    var h = Math.floor(total / 3600);
    var m = Math.floor((total % 3600) / 60);
    var s = total % 60;
    var tail = (m < 10 && h > 0 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
    return h > 0 ? h + ':' + tail : tail;
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
   * options: { assetId, title, subtitle, restart, onClose(result), getNext() -> Promise<{assetId, title}|null> }
   */
  function open(options) {
    var assetId = options.assetId;
    var root = el('div', 'player');
    var video = el('video', 'player-video');
    video.setAttribute('playsinline', '');
    var status = el('div', 'player-status', 'Preparing playback...');
    var overlay = el('div', 'player-overlay');
    var heading = el('div', 'player-heading');
    heading.appendChild(el('div', 'player-title', options.title || ''));
    if (options.subtitle) { heading.appendChild(el('div', 'player-subtitle', options.subtitle)); }
    var seek = el('div', 'seek focusable');
    seek.setAttribute('tabindex', '-1');
    seek.setAttribute('data-key', 'seek');
    var seekFill = el('div', 'seek-fill');
    seek.appendChild(seekFill);
    var times = el('div', 'player-times');
    var timeNow = el('span', '', '0:00');
    var timeEnd = el('span', '', '');
    times.appendChild(timeNow);
    times.appendChild(timeEnd);
    var buttons = el('div', 'player-buttons');
    var playButton = button('playpause', 'Pause');
    var backButton = button('back10', '-10 s');
    var forwardButton = button('forward10', '+10 s');
    var audioButton = button('audiosubs', 'Audio & subtitles');
    // Music only: the Lyrics side panel (spec find-another-on-tv D6). Shown once the manifest says this is a song.
    var lyricsButton = button('lyrics', TV.findAnother.WORDS.lyricsHeading);
    lyricsButton.hidden = true;
    var closeButton = button('close', 'Close');
    [playButton, backButton, forwardButton, audioButton, lyricsButton, closeButton].forEach(function (b) { buttons.appendChild(b); });
    overlay.appendChild(heading);
    overlay.appendChild(seek);
    overlay.appendChild(times);
    overlay.appendChild(buttons);
    var upNext = el('div', 'player-next');
    upNext.hidden = true;
    // The Audio & subtitles side panel (right edge). It opens from the tool button below the timeline.
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
      panelOpen: false,
      panelMode: 'audio',
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
      ended: false
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

    /* ---- overlay ---- */

    function showOverlay() {
      overlay.classList.add('visible');
      global.clearTimeout(state.hideTimer);
      state.hideTimer = global.setTimeout(function () {
        if (!video.paused && !state.closed && !state.panelOpen && !state.finder) { overlay.classList.remove('visible'); }
      }, OVERLAY_HIDE_MS);
    }

    function overlayVisible() { return overlay.classList.contains('visible'); }

    function updateProgress() {
      var duration = video.duration;
      var position = video.currentTime || 0;
      state.lastPosition = position;
      seekFill.style.width = (isFinite(duration) && duration > 0 ? Math.min(100, position / duration * 100) : 0) + '%';
      timeNow.textContent = formatTime(position);
      timeEnd.textContent = isFinite(duration) ? formatTime(duration) : '';
      if (state.panelOpen && state.panelMode === 'lyrics') { paintLyricsActive(); }
    }

    function togglePlay() {
      if (video.paused) {
        var promise = video.play();
        if (promise && promise.catch) { promise.catch(function () { /* the pause/play events update the UI */ }); }
      } else {
        video.pause();
      }
    }

    function seekBy(seconds) {
      var target = (video.currentTime || 0) + seconds;
      var duration = isFinite(video.duration) ? video.duration : target;
      video.currentTime = Math.max(0, Math.min(duration, target));
      updateProgress();
    }

    seek.tvKey = function (direction) {
      if (direction === 'left') { seekBy(-SEEK_STEP); showOverlay(); return true; }
      if (direction === 'right') { seekBy(SEEK_STEP); showOverlay(); return true; }
      if (direction === 'enter') { togglePlay(); showOverlay(); return true; }
      return false;
    };

    playButton.addEventListener('click', function () { togglePlay(); showOverlay(); });
    backButton.addEventListener('click', function () { seekBy(-SEEK_STEP); showOverlay(); });
    forwardButton.addEventListener('click', function () { seekBy(SEEK_STEP); showOverlay(); });
    closeButton.addEventListener('click', function () { close(); });
    audioButton.addEventListener('click', function () { openPanel(); });
    lyricsButton.addEventListener('click', function () { openLyricsPanel(); });

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
          label: t.displayName || t.title || t.language || ('Track ' + (i + 1)),
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

    /* ---- Audio & subtitles side panel ---- */

    function audioTracks() { return (state.manifest && state.manifest.audioTracks) || []; }

    function trackName(t, i) { return t.displayName || t.language || ('Track ' + (i + 1)); }

    function panelRow(key, label, selected, onSelect) {
      var row = button(key, '');
      row.className = 'btn focusable panel-row';
      row.appendChild(el('span', 'panel-row-label', label));
      row.appendChild(el('span', 'panel-row-mark', selected ? '✓' : ''));
      row.addEventListener('click', onSelect);
      return row;
    }

    function renderPanel() {
      if (state.panelMode === 'lyrics') { renderLyrics(); return; }
      var remember = TV.nav.remember();
      panel.textContent = '';
      panel.appendChild(el('div', 'panel-title', 'Audio & subtitles'));
      var audios = audioTracks();
      if (audios.length > 1) {
        panel.appendChild(el('div', 'panel-heading', 'Audio'));
        audios.forEach(function (t, i) {
          panel.appendChild(panelRow('audio-' + i, trackName(t, i), i === state.audioIndex, function () { chooseAudio(i); }));
        });
        if (state.audioNote) { panel.appendChild(el('div', 'panel-note', state.audioNote)); }
      }
      panel.appendChild(el('div', 'panel-heading', 'Subtitles'));
      panel.appendChild(panelRow('subs-off', 'Off', state.captionIndex < 0, function () { pickCaption(-1); }));
      state.captions.forEach(function (c, i) {
        panel.appendChild(panelRow('subs-' + i, c.label, i === state.captionIndex, function () { pickCaption(i); }));
      });
      if (state.captionNote) { panel.appendChild(el('div', 'panel-note', state.captionNote)); }
      if (state.sourcesReady) {
        var find = panelRow('find-subtitles', TV.findAnother.WORDS.footerRow, false, function () { openFinder(); });
        find.classList.add('panel-footer');
        panel.appendChild(find);
      }
      if (remember && panel.querySelector('[data-key="' + remember + '"]')) { TV.nav.restore(panel, remember); }
    }

    function pickCaption(index) {
      chooseCaption(index).then(function () { if (state.panelOpen) { renderPanel(); } });
      renderPanel();
    }

    function chooseAudio(index) {
      var hls = state.hls;
      state.audioNote = '';
      if (hls && hls.audioTracks && hls.audioTracks.length > index) {
        hls.audioTrack = index;
        state.audioIndex = index;
      } else {
        state.audioNote = 'This audio track cannot be switched here.';
      }
      renderPanel();
    }

    function openPanel() {
      if (state.panelOpen || state.closed) { return; }
      state.panelOpen = true;
      state.panelMode = 'audio';
      panel.classList.remove('lyrics-panel');
      state.sourcesReady = false;
      panel.hidden = false;
      showOverlay();
      TV.nav.setScope(panel);
      renderPanel();
      TV.nav.restore(panel, state.captionIndex < 0 ? 'subs-off' : 'subs-' + state.captionIndex);
      // The footer row shows only when the Engine says a subtitle source is ready for this item (and this profile may use it).
      TV.api.textTrackSources(assetId, 'subtitles').then(function (answer) {
        return TV.findAnother.sourceReady(answer);
      }, function () { return false; }).then(function (ready) {
        if (state.closed || !state.panelOpen || state.sourcesReady === ready) { return; }
        state.sourcesReady = ready;
        renderPanel();
      });
    }

    function closePanel() {
      if (!state.panelOpen) { return; }
      var wasLyrics = state.panelMode === 'lyrics';
      state.panelOpen = false;
      state.lyricsGeneration++;
      panel.hidden = true;
      panel.textContent = '';
      panel.classList.remove('lyrics-panel');
      state.lyricsScroller = null;
      state.lyricsLines = [];
      TV.nav.setScope(root);
      TV.nav.focus(wasLyrics ? lyricsButton : audioButton);
      showOverlay();
    }

    /* ---- Lyrics side panel (music) ---- */

    function lyricsWords() { return TV.findAnother; }

    // Reads the song's lyrics track (the Engine's preferred one) and turns it into lines. A generation number drops a read that
    // was overtaken by the panel closing or by a newer choice.
    async function loadLyrics(note) {
      var mine = ++state.lyricsGeneration;
      state.lyrics = { status: 'loading', parsed: null, note: note || '' };
      state.lyricsIndex = -2;
      if (state.panelOpen && state.panelMode === 'lyrics') { renderLyrics(); }
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
      if (state.panelOpen && state.panelMode === 'lyrics') { renderLyrics(); }
    }

    function renderLyrics() {
      var words = lyricsWords();
      var remember = TV.nav.remember();
      panel.textContent = '';
      panel.classList.add('lyrics-panel');
      var head = el('div', 'lyrics-head');
      head.appendChild(el('div', 'panel-title', words.WORDS.lyricsHeading));
      if (state.lyrics.status === 'ready') { head.appendChild(el('span', 'fa-chip', words.lyricsChip(state.lyrics.parsed))); }
      panel.appendChild(head);
      var scroller = el('div', 'lyrics-lines');
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
      panel.appendChild(scroller);
      if (state.lyricsSourcesReady) {
        var find = panelRow('find-lyrics', words.WORDS.findLyricsRow, false, function () { openFinder(); });
        find.classList.add('panel-footer');
        panel.appendChild(find);
      }
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

    function openLyricsPanel() {
      if (state.panelOpen || state.closed) { return; }
      state.panelOpen = true;
      state.panelMode = 'lyrics';
      state.lyricsSourcesReady = false;
      panel.hidden = false;
      showOverlay();
      TV.nav.setScope(panel);
      loadLyrics();
      // The footer row shows only when the Engine says a lyrics source is ready for this song (and this profile may use it).
      TV.api.textTrackSources(assetId, 'lyrics').then(function (answer) {
        return TV.findAnother.sourceReady(answer);
      }, function () { return false; }).then(function (ready) {
        if (state.closed || !state.panelOpen || state.panelMode !== 'lyrics' || state.lyricsSourcesReady === ready) { return; }
        state.lyricsSourcesReady = ready;
        renderLyrics();
        if (ready && !state.finder) { TV.nav.restore(panel, 'find-lyrics'); }
      });
    }

    function openFinder() {
      if (state.finder || state.closed) { return; }
      var lyrics = state.panelMode === 'lyrics';
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
          TV.nav.setScope(panel);
          TV.nav.restore(panel, lyrics ? 'find-lyrics' : 'find-subtitles');
        },
        onApplied: function () {
          state.finder = null;
          if (state.closed) { return; }
          if (lyrics) {
            // The panel stays open and shows the new lyrics once they are read; the song carries on at the same place.
            if (wasPlaying) { resumePlay(); }
            TV.nav.setScope(panel);
            loadLyrics(TV.findAnother.WORDS.applyingLyrics).then(function () { if (state.panelOpen && state.panelMode === 'lyrics') { TV.nav.restore(panel, 'find-lyrics'); } });
            return;
          }
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
      TV.nav.setScope(root);
      TV.nav.focus(seek);
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
      overlay.classList.remove('visible');
      global.clearInterval(state.beat);
    }

    /* ---- up next ---- */

    function showUpNext(next) {
      state.next = next;
      var remaining = NEXT_COUNTDOWN;
      upNext.hidden = false;
      var label = el('div', 'player-next-title');
      var play = button('next', 'Play next');
      var stop = button('stop', 'Not now');
      upNext.textContent = '';
      upNext.appendChild(label);
      upNext.appendChild(play);
      upNext.appendChild(stop);
      function paint() { label.textContent = 'Up next: ' + next.title + ' (' + remaining + ')'; }
      paint();
      play.addEventListener('click', function () { playNext(); });
      stop.addEventListener('click', function () { close(); });
      TV.nav.setScope(upNext);
      TV.nav.focus(play);
      state.nextTimer = global.setInterval(function () {
        remaining--;
        if (remaining <= 0) { playNext(); } else { paint(); }
      }, 1000);
    }

    function playNext() {
      var next = state.next;
      global.clearInterval(state.nextTimer);
      teardown();
      if (options.onNext && next) { options.onNext(next); } else if (options.onClose) { options.onClose({ ended: true }); }
    }

    /* ---- keys ---- */

    function onAction(action) {
      if (state.closed) { return false; }
      if (state.panelOpen) {
        if (action === 'back') { closePanel(); return true; }
        showOverlay();
        return false; // the rows use the normal focus movement inside the panel
      }
      if (!upNext.hidden) {
        if (action === 'back') { close(); return true; }
        return false; // the buttons use the normal focus movement
      }
      if (action === 'back') { close(); return true; }
      if (action === 'playpause' || action === 'play' || action === 'pause') {
        if (action === 'pause') { video.pause(); } else if (action === 'play') { video.play(); } else { togglePlay(); }
        showOverlay();
        return true;
      }
      if (action === 'stop') { close(); return true; }
      if (action === 'forward') { seekBy(SEEK_STEP * 3); showOverlay(); return true; }
      if (action === 'rewind') { seekBy(-SEEK_STEP * 3); showOverlay(); return true; }
      if (!overlayVisible()) {
        showOverlay();
        TV.nav.setScope(root);
        TV.nav.focus(seek);
        if (action === 'left') { seekBy(-SEEK_STEP); }
        if (action === 'right') { seekBy(SEEK_STEP); }
        if (action === 'enter') { togglePlay(); }
        return true;
      }
      showOverlay();
      return false; // overlay is up: normal focus movement and Enter on the focused control
    }

    TV.nav.setKeyHandler(onAction);

    /* ---- video events ---- */

    video.addEventListener('timeupdate', updateProgress);
    video.addEventListener('playing', function () { state.started = true; showStatus(''); playButton.textContent = 'Pause'; });
    video.addEventListener('pause', function () { playButton.textContent = 'Play'; showOverlay(); if (!state.closed) { beat(false, false); } });
    video.addEventListener('play', function () { playButton.textContent = 'Pause'; });
    video.addEventListener('waiting', function () { showStatus('Buffering...'); });
    video.addEventListener('seeked', function () { beat(false, false); });
    video.addEventListener('error', function () {
      if (!state.hls) { fail('Playback stopped: the video could not be played.'); }
    });
    video.addEventListener('ended', function () {
      state.ended = true;
      beat(true, true);
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

    TV.nav.setScope(root);
    TV.nav.focus(seek);

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
        lyricsButton.hidden = !TV.findAnother.isMusic(manifest && manifest.mediaType);
        var ready =manifest && manifest.recommendedDelivery === 'hls' && manifest.hlsUrl &&
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

  TV.player = { open: open, formatTime: formatTime, toVtt: toVtt, authorize: authorize };
})(window);
