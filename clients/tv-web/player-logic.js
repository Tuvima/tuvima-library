/*
 * Tuvima TV web client - player logic.
 *
 * Plain functions with no DOM, so Node can test them: the transport and tool rows, the lines of text the player shows
 * (the one line under the title, the end card, bookmark and episode rows), the key map of the television layout, the
 * overlay-hiding rule and the side-panel state. Every fixed word comes from the Televisions section and the Panels table of
 * clients/contracts/v1/player-controls.md; the layout is the approved mock-up in
 * engineering/design/mockups/tv-player/tv-player-panels.html. Classic script so a packaged app can load it from file://.
 */
(function (global) {
  'use strict';

  var SEP = ' · ';     // middle dot
  var MINUS = '-';

  var OVERLAY_HIDE_MS = 5000;      // the overlay hides after 5 seconds with no input
  var END_CARD_SECONDS = 10;       // the "Starts in {0} seconds" countdown
  var DEFAULT_SKIP = { back: 10, forward: 30 };
  var MAX_SKIP = 600;

  // The profile's choices for video (the contract's skip-defaults table); used by the Skip back and Skip forward views.
  var SKIP_CHOICES = { back: [5, 10, 15, 30], forward: [10, 30, 60, 90] };

  // Speed presets (the contract's speed-presets table) and the fine steps Left and Right take on a television.
  var SPEED_PRESETS = [0.75, 1, 1.25, 1.5, 2];
  var SPEED_MIN = 0.5;
  var SPEED_MAX = 3;
  var SPEED_STEP = 0.05;

  var APPEARANCE_STYLES = ['Default', 'Large text', 'High contrast'];
  var APPEARANCE_BACKGROUNDS = ['None', 'Shadow', 'Box'];

  var WORDS = {
    episodes: 'Episodes',
    lessons: 'Lessons',
    audioSubtitles: 'Audio & subtitles',
    bookmarks: 'Bookmarks',
    playbackSettings: 'Playback settings',
    lyrics: 'Lyrics',
    close: 'Close',
    back: 'Back',
    play: 'Play',
    pause: 'Pause',
    season: 'Season',
    autoplay: 'Autoplay next episode',
    upNext: 'Up next',
    playing: 'Playing',
    paused: 'Paused',
    audio: 'Audio',
    subtitles: 'Subtitles',
    off: 'Off',
    notSwitchable: 'Not switchable in this browser',
    subtitleAppearance: 'Subtitle appearance',
    style: 'Style',
    background: 'Background',
    resetToDefault: 'Reset to default',
    changesSaved: 'Changes saved automatically',
    bookmarkAt: 'Bookmark at {0}',
    mine: 'Mine',
    global: 'Global',
    showOnTimeline: 'Show on timeline',
    edit: 'Edit',
    remove: 'Delete',
    keep: 'Keep',
    name: 'Name',
    save: 'Save',
    cancel: 'Cancel',
    couldNotSave: 'Couldn’t save · Try again',
    noGlobal: 'No Global bookmarks on this title yet',
    noMine: 'No bookmarks of yours yet',
    selectBookmark: 'Select a bookmark to jump to that moment.',
    playbackSpeed: 'Playback speed',
    currentSpeed: 'Current speed',
    resetSpeed: 'Reset to 1×',
    skipBack: 'Skip back',
    skipForward: 'Skip forward',
    playbackInformation: 'Playback information',
    resetPreferences: 'Reset playback preferences',
    endHeading: 'Up next',
    endCountdown: 'Starts in {0} seconds',
    nextEpisode: 'Next episode',
    nextLesson: 'Next lesson',
    playNow: 'Play now'
  };

  function text(value) { return value === undefined || value === null ? '' : String(value); }

  function number(value) {
    var n = Number(value);
    return isFinite(n) ? n : 0;
  }

  function list(value) { return Object.prototype.toString.call(value) === '[object Array]' ? value : []; }

  // Replaces {0} and {1} in a contract string.
  function fill(template, a, b) {
    return text(template).replace('{0}', text(a)).replace('{1}', text(b));
  }

  /* ---- time and length ---- */

  function formatTime(seconds) {
    var value = number(seconds);
    if (value < 0) { value = 0; }
    var total = Math.floor(value);
    var h = Math.floor(total / 3600);
    var m = Math.floor((total % 3600) / 60);
    var s = total % 60;
    var tail = (m < 10 && h > 0 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
    return h > 0 ? h + ':' + tail : tail;
  }

  // The time left at the right of the timeline, "-14:59". Nothing while the length is not known.
  function timeLeft(position, duration) {
    var d = number(duration);
    if (!(d > 0)) { return ''; }
    return MINUS + formatTime(Math.max(0, d - number(position)));
  }

  // A length under an hour reads "22 min", from an hour "1h 52m"; nothing when it is not known.
  function lengthText(seconds) {
    var s = number(seconds);
    if (!(s > 0)) { return ''; }
    var minutes = Math.max(1, Math.floor(s / 60));
    if (minutes < 60) { return minutes + ' min'; }
    return Math.floor(minutes / 60) + 'h ' + (minutes % 60) + 'm';
  }

  /* ---- what is playing ---- */

  /** 'episode', 'lesson' (folder video), 'film', 'music' or 'audiobook'. An explicit kind from the app wins. */
  function kindOf(info) {
    var given = text(info && info.kind).toLowerCase();
    if (given === 'episode' || given === 'lesson' || given === 'film' || given === 'music' || given === 'audiobook') { return given; }
    var type = text(info && info.mediaType).toLowerCase();
    if (type.indexOf('audiobook') >= 0) { return 'audiobook'; }
    if (type.indexOf('music') >= 0 || type.indexOf('song') >= 0 || type.indexOf('track') >= 0) { return 'music'; }
    if (type === 'videos' || type === 'video') { return 'lesson'; }
    if (text(info && info.showId) || type === 'tv' || type.indexOf('episode') >= 0) { return 'episode'; }
    return 'film';
  }

  // "S1 E1 · The First Wish · 22 min" (episode), "3 · Ball handling · 12 min" (folder video), "2019 · 1h 52m" (film).
  function detailLine(kind, info) {
    var i = info || {};
    var parts = [];
    var length = lengthText(i.seconds);
    if (kind === 'episode') {
      var season = number(i.season) > 0 ? 'S' + Math.floor(number(i.season)) : '';
      var episode = number(i.episode) > 0 ? 'E' + Math.floor(number(i.episode)) : '';
      var code = (season && episode) ? season + ' ' + episode : (season || episode);
      if (code) { parts.push(code); }
      if (text(i.title)) { parts.push(text(i.title)); }
      if (length) { parts.push(length); }
    } else if (kind === 'lesson') {
      if (number(i.number) > 0) { parts.push(String(Math.floor(number(i.number)))); }
      if (text(i.title)) { parts.push(text(i.title)); }
      if (length) { parts.push(length); }
    } else {
      if (number(i.year) > 0) { parts.push(String(Math.floor(number(i.year)))); }
      if (length) { parts.push(length); }
    }
    return parts.join(SEP);
  }

  /* ---- the profile's playback preferences ---- */

  function wholeSeconds(value, fallback) {
    var n = Math.floor(number(value));
    return n > 0 && n <= MAX_SKIP ? n : fallback;
  }

  /**
   * The seconds the skip buttons and the Left and Right keys use: a device choice made in Playback settings, else the
   * profile's saved pair from GET /api/v1/devices/current/playback-preferences, else 10 back and 30 forward.
   */
  function skipSeconds(prefs, device) {
    var p = prefs || {};
    var d = device || {};
    return {
      back: wholeSeconds(d.skipBack, wholeSeconds(p.skipBackSeconds, DEFAULT_SKIP.back)),
      forward: wholeSeconds(d.skipForward, wholeSeconds(p.skipForwardSeconds, DEFAULT_SKIP.forward))
    };
  }

  /** Autoplay next episode: a device choice, else the profile's preference, else on. */
  function autoplayOn(prefs, device) {
    if (device && typeof device.autoplay === 'boolean') { return device.autoplay; }
    if (prefs && typeof prefs.autoplayNextEpisode === 'boolean') { return prefs.autoplayNextEpisode; }
    return true;
  }

  /* ---- speed ---- */

  function clampSpeed(value) {
    var v = number(value);
    if (!(v > 0)) { return 1; }
    v = Math.min(SPEED_MAX, Math.max(SPEED_MIN, v));
    return Math.round(v * 100) / 100;
  }

  function stepSpeed(value, direction) {
    return clampSpeed(clampSpeed(value) + (direction < 0 ? -SPEED_STEP : SPEED_STEP));
  }

  function speedLabel(value) { return clampSpeed(value) + '×'; }

  function isPreset(value) {
    var v = clampSpeed(value);
    return SPEED_PRESETS.some(function (p) { return Math.abs(p - v) < 0.0001; });
  }

  /* ---- transport and tools ---- */

  /**
   * The transport row. Five buttons for episodes, films and lessons (the contract's transport-tv and transport-film
   * tables); a song has no skip buttons. A button that cannot act is dimmed in place, never left out.
   * ctx: { playing, canPrevious, canNext }
   */
  function transportFor(kind, skip, ctx) {
    var c = ctx || {};
    var s = skip || DEFAULT_SKIP;
    var playLabel = c.playing ? WORDS.pause : WORDS.play;
    if (kind === 'music') {
      return [
        { key: 'previous', label: 'Previous track', dimmed: !c.canPrevious },
        { key: 'play-pause', label: playLabel, dimmed: false },
        { key: 'next', label: 'Next track', dimmed: !c.canNext }
      ];
    }
    var tv = kind === 'episode';
    return [
      { key: 'previous', label: tv ? 'Previous episode' : 'Previous', dimmed: !c.canPrevious },
      { key: 'skip-back', label: 'Skip back ' + s.back + ' s', dimmed: false, seconds: s.back },
      { key: 'play-pause', label: playLabel, dimmed: false },
      { key: 'skip-forward', label: 'Skip forward ' + s.forward + ' s', dimmed: false, seconds: s.forward },
      { key: 'next', label: tv ? 'Next episode' : 'Next', dimmed: !c.canNext }
    ];
  }

  /**
   * The tool row, in the Dashboard's order without Volume, Fullscreen, Close and Picture in picture (a television owns
   * them). A tool the media or the app does not support is absent. caps: { hasEpisodes, canBookmark }
   */
  function toolsFor(kind, caps) {
    var c = caps || {};
    var tools = [];
    if (kind === 'music') { return [{ key: 'lyrics', label: WORDS.lyrics }]; }
    if (kind === 'audiobook') { return tools; }
    if (c.hasEpisodes && (kind === 'episode' || kind === 'lesson')) {
      tools.push({ key: 'episodes', label: kind === 'lesson' ? WORDS.lessons : WORDS.episodes });
    }
    tools.push({ key: 'audio-subtitles', label: WORDS.audioSubtitles });
    if (c.canBookmark) { tools.push({ key: 'bookmarks', label: WORDS.bookmarks }); }
    tools.push({ key: 'playback-settings', label: WORDS.playbackSettings });
    return tools;
  }

  /** The panel a tool opens. */
  var PANEL_OF_TOOL = {
    episodes: 'episodes',
    'audio-subtitles': 'audio',
    bookmarks: 'bookmarks',
    'playback-settings': 'settings',
    lyrics: 'lyrics'
  };

  /* ---- side panels ---- */

  function panelsInitial() { return { open: null, view: 'root' }; }

  // Only one panel is open at a time: opening another closes the first.
  function openPanel(state, name) { return { open: name, view: 'root' }; }

  function closePanel() { return panelsInitial(); }

  function enterView(state, view) { return state && state.open ? { open: state.open, view: view } : panelsInitial(); }

  /* ---- keys ---- */

  /**
   * What a key does while the player is up (the contract's tv-keys table). state: { overlayVisible, panelOpen,
   * panelView, endCard, zone } where zone is 'timeline', 'panel' or 'other' (the focused control).
   * Returns { effect, consume }. 'move' is the normal focus movement and Select on the focused control; every other
   * effect is done by the player and consumes the key.
   */
  function keyEffect(state, action) {
    var s = state || {};
    if (s.endCard) {
      if (action === 'back') { return { effect: 'cancel-end-card', consume: true }; }
      if (action === 'stop') { return { effect: 'close-player', consume: true }; }
      return { effect: 'move', consume: false };
    }
    if (action === 'back') {
      if (s.panelOpen) { return { effect: s.panelView && s.panelView !== 'root' ? 'panel-root' : 'close-panel', consume: true }; }
      return { effect: s.overlayVisible ? 'hide-overlay' : 'close-player', consume: true };
    }
    if (action === 'stop') { return { effect: 'close-player', consume: true }; }
    if (action === 'rewind') { return { effect: 'skip-back', consume: true }; }
    if (action === 'forward') { return { effect: 'skip-forward', consume: true }; }
    if (action === 'playpause') { return { effect: 'toggle-play', consume: true }; }
    if (action === 'play') { return { effect: 'play', consume: true }; }
    if (action === 'pause') { return { effect: 'pause', consume: true }; }
    if (!s.overlayVisible) { return { effect: 'show-overlay', consume: true }; }
    if ((action === 'left' || action === 'right') && s.zone === 'timeline') {
      return { effect: action === 'left' ? 'skip-back' : 'skip-forward', consume: true };
    }
    return { effect: 'move', consume: false };
  }

  /** The overlay hides after 5 seconds, but never while a panel, the Find window or the end card is open, nor while paused. */
  function overlayShouldHide(state) {
    var s = state || {};
    return !s.paused && !s.panelOpen && !s.finderOpen && !s.endCard;
  }

  /* ---- end card ---- */

  /** The end card's rows. remaining is null while autoplay is off: the card stays and has no countdown. */
  function endCardView(next, kind, remaining) {
    return {
      heading: WORDS.endHeading,
      countdown: remaining === null || remaining === undefined ? '' : fill(WORDS.endCountdown, remaining),
      title: text(next && next.title),
      kind: kind === 'lesson' ? WORDS.nextLesson : WORDS.nextEpisode,
      cancel: WORDS.cancel,
      playNow: WORDS.playNow
    };
  }

  /* ---- episodes and lessons ---- */

  // One Season (group) of a show's details, owned episodes only, with what each row shows.
  function episodeSeasons(detail) {
    var groups = (detail && detail.sequencePlacement && detail.sequencePlacement.groups) || [];
    var seasons = [];
    list(groups).forEach(function (g, index) {
      var items = [];
      list(g.items).forEach(function (item) {
        var e = item.episodeContext || {};
        if (item.isOwned === false || !e.episodeAssetId) { return; }
        items.push({
          assetId: text(e.episodeAssetId),
          workId: text(e.episodeWorkId),
          season: e.seasonNumber || null,
          number: e.episodeNumber || null,
          title: text(item.title),
          description: text(item.description),
          duration: text(item.duration),
          positionSeconds: item.positionSeconds === undefined ? e.positionSeconds : item.positionSeconds,
          durationSeconds: item.durationSeconds === undefined ? e.durationSeconds : item.durationSeconds,
          progressPercent: number(item.progressPercent),
          state: text(e.state),
          stillUrl: text(item.episodeStillUrl || item.artworkUrl)
        });
      });
      if (!items.length) { return; }
      var number1 = items[0].season;
      seasons.push({
        key: text(g.key) || String(index),
        title: text(g.title) || (number1 ? WORDS.season + ' ' + number1 : WORDS.season + ' ' + (seasons.length + 1)),
        items: items
      });
    });
    return seasons;
  }

  function flattenEpisodes(seasons) {
    var all = [];
    list(seasons).forEach(function (s) { list(s.items).forEach(function (i) { all.push(i); }); });
    return all;
  }

  /** The episode before and after the one in play, across Seasons; null where there is none. */
  function neighbours(seasons, assetId) {
    var all = flattenEpisodes(seasons);
    var id = text(assetId).toLowerCase();
    for (var i = 0; i < all.length; i++) {
      if (all[i].assetId.toLowerCase() === id) {
        return { previous: i > 0 ? all[i - 1] : null, next: i < all.length - 1 ? all[i + 1] : null };
      }
    }
    return { previous: null, next: null };
  }

  function seasonIndexOf(seasons, assetId) {
    var id = text(assetId).toLowerCase();
    var found = 0;
    list(seasons).forEach(function (s, index) {
      s.items.forEach(function (i) { if (i.assetId.toLowerCase() === id) { found = index; } });
    });
    return found;
  }

  /** "20 episodes" for the Episodes panel, "12 lessons" for Lessons (a single one reads in the singular). */
  function countLabel(count, kind) {
    var noun = kind === 'lesson' ? 'lesson' : 'episode';
    return count + ' ' + noun + (count === 1 ? '' : 's');
  }

  /**
   * One row of the Episodes or Lessons panel. The current row reads "Playing · 2:23 of 22:41" (or "Paused"); another that
   * was left part-way reads "Paused · 4:10 of 22:00"; the next one carries the "Up next" chip.
   * ctx: { assetId, playing, position, duration, nextAssetId }
   */
  function episodeRow(item, ctx) {
    var c = ctx || {};
    var current = text(item.assetId).toLowerCase() === text(c.assetId).toLowerCase();
    var title = (item.number ? item.number + SEP : '') + item.title;
    var sub;
    if (current) {
      sub = (c.playing ? WORDS.playing : WORDS.paused) + SEP + formatTime(c.position) + ' of ' + formatTime(c.duration);
    } else if (item.state === 'InProgress' && number(item.positionSeconds) > 0 && number(item.durationSeconds) > 0) {
      sub = WORDS.paused + SEP + formatTime(item.positionSeconds) + ' of ' + formatTime(item.durationSeconds);
    } else {
      sub = [item.duration, item.description].filter(Boolean).join(SEP);
    }
    return {
      assetId: item.assetId,
      key: 'ep:' + item.assetId,
      title: title,
      sub: sub,
      current: current,
      upNext: !current && !!c.nextAssetId && text(c.nextAssetId).toLowerCase() === text(item.assetId).toLowerCase(),
      stillUrl: item.stillUrl
    };
  }

  /* ---- bookmarks ---- */

  function bookmarkAtLabel(position) { return fill(WORDS.bookmarkAt, formatTime(position)); }

  /**
   * Mine | Global. The Engine decides who sees what: Mine is isYours and Global is inEveryone, as in the Dashboard; the choice shows only when a Global set exists.
   * Mine is first and the default.
   */
  function bookmarkTabs(items) {
    var all = list(items);
    var mine = all.filter(function (b) { return b.isYours; });
    var global = all.filter(function (b) { return b.inEveryone; });
    return {
      showChoice: global.length > 0,
      mine: mine,
      global: global,
      tabs: [
        { key: 'mine', label: WORDS.mine + SEP + mine.length },
        { key: 'global', label: WORDS.global + SEP + global.length }
      ]
    };
  }

  /** The rows of one tab, in time order: the time, then the name or note (the maker's name on Global). */
  function bookmarkRows(items, tab) {
    return list(items).slice().sort(function (a, b) { return number(a.positionSeconds) - number(b.positionSeconds); }).map(function (b) {
      var time = formatTime(b.positionSeconds);
      var words = text(b.label) || text(b.note) || bookmarkAtLabel(b.positionSeconds);
      var sub = tab === 'global' && text(b.ownerName) ? words + SEP + text(b.ownerName) : words;
      return {
        id: text(b.id),
        key: 'bm:' + text(b.id),
        seconds: number(b.positionSeconds),
        time: time,
        sub: sub,
        label: text(b.label),
        note: text(b.note),
        frameUrl: text(b.frameUrl),
        canChange: tab !== 'global' && !!b.isYours
      };
    });
  }

  function bookmarkEmpty(tab) { return tab === 'global' ? WORDS.noGlobal : WORDS.noMine; }

  /** The body of "Bookmark at {0}": saved at the moment it was pressed, at once, with no note. */
  function bookmarkRequest(assetId, position, duration) {
    var body = { assetId: text(assetId), positionSeconds: Math.round(number(position) * 10) / 10, isGlobal: false };
    if (number(duration) > 0) { body.durationSeconds = Math.round(number(duration) * 10) / 10; }
    return body;
  }

  /** Where each bookmark sits on the timeline, as a percentage; a bookmark past the end or with no length is left out. */
  function bookmarkTicks(items, duration) {
    var d = number(duration);
    if (!(d > 0)) { return []; }
    return list(items).map(function (b) { return number(b.positionSeconds) / d * 100; })
      .filter(function (p) { return p >= 0 && p <= 100; });
  }

  /* ---- audio and subtitles ---- */

  var CODEC_NAMES = {
    eac3: 'Dolby Digital Plus', ac3: 'Dolby Digital', truehd: 'Dolby TrueHD', dts: 'DTS', aac: 'AAC', mp3: 'MP3',
    flac: 'FLAC', opus: 'Opus', vorbis: 'Vorbis'
  };

  function channelsLabel(channels) {
    var n = Math.floor(number(channels));
    if (n === 1) { return 'Mono'; }
    if (n === 2) { return 'Stereo'; }
    if (n === 6) { return '5.1'; }
    if (n === 8) { return '7.1'; }
    return n > 0 ? n + ' channels' : '';
  }

  // "Dolby Digital · 5.1": the track's facts under its language.
  function audioFacts(track) {
    var codec = text(track && track.codec).toLowerCase();
    return [CODEC_NAMES[codec] || '', channelsLabel(track && track.channels)].filter(Boolean).join(SEP);
  }

  function isExtraAudio(track) {
    return /commentary|audio description|described/i.test(text(track && (track.displayName || track.title)));
  }

  /**
   * The audio rows in the panel's order: the main tracks, then commentary and audio-description tracks. `index` is the track's
   * place in the manifest (hls.js numbers them the same way). switchable: how many tracks the player can switch between
   * (null when it cannot switch any).
   */
  function audioRows(tracks, selected, switchable) {
    var rows = list(tracks).map(function (t, i) {
      var name = text(t.displayName) || text(t.language) || ('Track ' + (i + 1));
      var can = switchable !== null && switchable !== undefined && i < switchable;
      if (i === selected) { can = true; }
      return {
        index: i,
        name: name,
        sub: can ? audioFacts(t) : WORDS.notSwitchable,
        selected: i === selected,
        selectable: can,
        extra: isExtraAudio(t)
      };
    });
    return rows.filter(function (r) { return !r.extra; }).concat(rows.filter(function (r) { return r.extra; }));
  }

  /** "English", "English · SDH" or "English · Forced". */
  function subtitleLabel(track, index) {
    var base = text(track.displayName) || text(track.title) || text(track.language) || ('Track ' + (index + 1));
    if (track.isHearingImpaired && !/sdh|hearing/i.test(base)) { base += SEP + 'SDH'; }
    if (track.isForced && !/forced/i.test(base)) { base += SEP + 'Forced'; }
    return base;
  }

  /** The two Subtitle appearance choices: the device's saved pair, else what the profile's size and background imply. */
  function appearanceOf(prefs, device) {
    var p = prefs || {};
    var d = device || {};
    var size = text(p.subtitleSize).toLowerCase();
    var style = APPEARANCE_STYLES.indexOf(d.subtitleStyle) >= 0 ? d.subtitleStyle
      : (size === 'large' || size === 'extralarge' ? 'Large text' : 'Default');
    var background = APPEARANCE_BACKGROUNDS.indexOf(d.subtitleBackground) >= 0 ? d.subtitleBackground
      : (p.subtitleBackground === false ? 'None' : 'Box');
    return { style: style, background: background };
  }

  function appearanceClasses(appearance) {
    var a = appearance || {};
    var classes = [];
    if (a.style === 'Large text') { classes.push('sub-large'); }
    if (a.style === 'High contrast') { classes.push('sub-contrast'); }
    classes.push('sub-bg-' + text(a.background || 'Box').toLowerCase());
    return classes;
  }

  /* ---- playback information ---- */

  function playbackInfoLines(manifest) {
    var m = manifest || {};
    var t = m.technical || {};
    var lines = [];
    var video = [t.height ? t.height + 'p' : '', text(t.videoCodec).toUpperCase()].filter(Boolean).join(SEP);
    if (video) { lines.push({ label: 'Video', value: video }); }
    var audio = [text(t.audioCodec).toUpperCase(), channelsLabel(t.channels)].filter(Boolean).join(SEP);
    if (audio) { lines.push({ label: WORDS.audio, value: audio }); }
    var rung = text(m.playbackPath && m.playbackPath.rung);
    lines.push({ label: 'Delivery', value: !rung || rung === 'direct' ? 'Direct' : 'Converted' });
    lines.push({ label: 'Connection', value: text(m.connection && m.connection.path) === 'remote' ? 'Remote' : 'Local' });
    if (t.fileName) { lines.push({ label: 'File', value: text(t.fileName) }); }
    return lines;
  }

  var logic = {
    SEP: SEP,
    OVERLAY_HIDE_MS: OVERLAY_HIDE_MS,
    END_CARD_SECONDS: END_CARD_SECONDS,
    DEFAULT_SKIP: DEFAULT_SKIP,
    SKIP_CHOICES: SKIP_CHOICES,
    SPEED_PRESETS: SPEED_PRESETS,
    APPEARANCE_STYLES: APPEARANCE_STYLES,
    APPEARANCE_BACKGROUNDS: APPEARANCE_BACKGROUNDS,
    PANEL_OF_TOOL: PANEL_OF_TOOL,
    WORDS: WORDS,
    fill: fill,
    formatTime: formatTime,
    timeLeft: timeLeft,
    lengthText: lengthText,
    kindOf: kindOf,
    detailLine: detailLine,
    skipSeconds: skipSeconds,
    autoplayOn: autoplayOn,
    clampSpeed: clampSpeed,
    stepSpeed: stepSpeed,
    speedLabel: speedLabel,
    isPreset: isPreset,
    transportFor: transportFor,
    toolsFor: toolsFor,
    panelsInitial: panelsInitial,
    openPanel: openPanel,
    closePanel: closePanel,
    enterView: enterView,
    keyEffect: keyEffect,
    overlayShouldHide: overlayShouldHide,
    endCardView: endCardView,
    episodeSeasons: episodeSeasons,
    flattenEpisodes: flattenEpisodes,
    neighbours: neighbours,
    seasonIndexOf: seasonIndexOf,
    countLabel: countLabel,
    episodeRow: episodeRow,
    bookmarkAtLabel: bookmarkAtLabel,
    bookmarkTabs: bookmarkTabs,
    bookmarkRows: bookmarkRows,
    bookmarkEmpty: bookmarkEmpty,
    bookmarkRequest: bookmarkRequest,
    bookmarkTicks: bookmarkTicks,
    audioFacts: audioFacts,
    audioRows: audioRows,
    subtitleLabel: subtitleLabel,
    appearanceOf: appearanceOf,
    appearanceClasses: appearanceClasses,
    playbackInfoLines: playbackInfoLines
  };

  if (typeof module !== 'undefined' && module.exports) { module.exports = logic; }
  if (global) {
    var TV = (global.TV = global.TV || {});
    TV.playerLogic = logic;
  }
})(typeof window !== 'undefined' ? window : (typeof globalThis !== 'undefined' ? globalThis : this));
