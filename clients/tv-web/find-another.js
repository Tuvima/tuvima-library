/*
 * Tuvima TV web client - Find another (subtitles, and lyrics once a Lyrics panel exists).
 *
 * Two halves in one classic script. The top half is plain logic with no DOM, so Node can test it: the one-second rest rule
 * (a row is read only after focus has rested on it for a second; scrolling quickly reads nothing), the read-once cache, the
 * remote's key map, and the words each row and state shows. The bottom half draws the window from the Engine's answers and
 * only runs in a browser. Spec: engineering/specs/find-another-on-tv.md (section 4); the rows are the Dashboard's
 * (FindAnotherResultMapper), so the wording here matches it.
 */
(function (global) {
  'use strict';

  var DWELL_MS = 1000;               // D10: previews read after one second at rest
  var SAME_RELEASE_CONFIDENCE = 0.9; // the Dashboard's "Made for your file" line (SubtitleFindView)
  var AMBER_SECONDS = 15;            // lyrics: amber up to 15 seconds off, red beyond
  var AMBER_MINUTES = 15;            // subtitles: amber up to 15 minutes off, red beyond
  var FULL_LINES = 20;               // "about 20 lines" when a row is opened in full (the Engine may send fewer)

  // The common languages offered on the Language button when the profile gives only its own (the preferences answer
  // holds one language name). The profile's language is always put first.
  var COMMON_LANGUAGES = [
    'English', 'Spanish', 'French', 'German', 'Italian', 'Portuguese', 'Dutch', 'Swedish',
    'Russian', 'Japanese', 'Korean', 'Chinese', 'Arabic', 'Hindi'
  ];

  var WORDS = {
    findSubtitles: 'Find subtitles',
    findLyrics: 'Find lyrics',
    language: 'Language',
    useThis: 'Use this',
    cancel: 'Cancel',
    bestMatch: 'Best match',
    inUse: 'In use',
    hearingImpaired: 'Hearing impaired',
    madeForYourFile: 'Made for your file',
    differentRelease: 'A different release, may be out of step',
    fitsTheFilm: 'Fits the film',
    fitsTheSong: 'Fits the song',
    synced: 'Synced',
    wordsOnly: 'Words only, not timed',
    reading: 'Reading…',
    couldNotRead: 'Could not read this one',
    notRead: 'Select this result to read its first lines.',
    looking: 'Looking for subtitles…',
    lookingLyrics: 'Looking for lyrics…',
    applying: 'Updating subtitles…',
    untitled: 'Untitled result',
    footerRow: 'Find subtitles…'
  };

  function text(value) { return value === undefined || value === null ? '' : String(value); }
  function isNumber(value) { return typeof value === 'number' && isFinite(value); }
  function plural(n, one, many) { return n + ' ' + (n === 1 ? one : many); }
  function wholeAtLeastOne(value) { return Math.max(1, Math.round(value)); }

  /** "3:42", or "1:05:09" when long. */
  function clock(seconds) {
    var total = Math.max(0, Math.round(isNumber(seconds) ? seconds : 0));
    var h = Math.floor(total / 3600);
    var m = Math.floor((total % 3600) / 60);
    var s = total % 60;
    var tail = (h > 0 && m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
    return h > 0 ? h + ':' + tail : tail;
  }

  /* ---- row wording ---- */

  // "Made for your file" / "A different release, may be out of step" for subtitles; "Synced" / "Words only, not timed" for lyrics.
  function checkOf(item, kind) {
    if (kind === 'lyrics') {
      return item.is_timed ? { text: WORDS.synced, tone: 'ok' } : { text: WORDS.wordsOnly, tone: 'plain' };
    }
    return (item.confidence || 0) >= SAME_RELEASE_CONFIDENCE
      ? { text: WORDS.madeForYourFile, tone: 'ok' }
      : { text: WORDS.differentRelease, tone: 'warn' };
  }

  // The timing check, present only once the file has been read (it needs the file's last line): "Fits the film" or how far off.
  function fitOf(item, kind) {
    if (!item.length_check || !isNumber(item.length_difference_seconds)) { return null; }
    var diff = item.length_difference_seconds;
    if (kind === 'lyrics') {
      if (item.length_check === 'Fits') { return { text: WORDS.fitsTheSong, tone: 'ok' }; }
      var secs = wholeAtLeastOne(Math.abs(diff));
      return {
        text: plural(secs, 'second', 'seconds') + (diff > 0 ? ' longer' : ' shorter'),
        tone: Math.abs(diff) <= AMBER_SECONDS ? 'warn' : 'bad'
      };
    }
    if (item.length_check === 'Fits') { return { text: WORDS.fitsTheFilm, tone: 'ok' }; }
    var minutes = Math.abs(diff) / 60;
    return {
      text: plural(wholeAtLeastOne(minutes), 'minute', 'minutes') + (diff < 0 ? ' short' : ' long'),
      tone: minutes <= AMBER_MINUTES ? 'warn' : 'bad'
    };
  }

  /**
   * What one list row shows. `item` is the list result with anything read since laid over it. `isBest` marks the first row
   * (the Engine sends the best match first); the "In use" chip comes from the result's own flag.
   */
  function rowView(item, kind, isBest) {
    var name = kind === 'lyrics' ? (text(item.title) || text(item.release_name)) : text(item.release_name);
    var meta = [];
    if (kind === 'lyrics') {
      if (item.artist) { meta.push(text(item.artist)); }
      if (item.album) { meta.push(text(item.album)); }
      if (isNumber(item.length_seconds) && item.length_seconds > 0) { meta.push(clock(item.length_seconds)); }
    }
    var chips = [];
    if (isBest) { chips.push(WORDS.bestMatch); }
    if (item.is_current) { chips.push(WORDS.inUse); }
    return {
      name: name || WORDS.untitled,
      hearing: kind !== 'lyrics' && item.is_hearing_impaired ? WORDS.hearingImpaired : '',
      meta: meta.join(' · '),
      check: checkOf(item, kind),
      fit: fitOf(item, kind),
      chips: chips
    };
  }

  /** One pane line: subtitles show the time each appears, lyrics just the words. */
  function lineText(line, kind) {
    var words = text(line && line.text);
    if (kind !== 'lyrics' && line && isNumber(line.at_seconds)) { return clock(line.at_seconds) + '   ' + words; }
    return words;
  }

  /** How many lines the pane shows: a few until a row is opened, about 20 once it is. Lyrics show six. */
  function paneLimit(kind, full) {
    if (kind === 'lyrics') { return 6; }
    return full ? FULL_LINES : 3;
  }

  /* ---- states ---- */

  var FALLBACK_SENTENCES = {
    NoSourceOn: 'No subtitle source is switched on.',
    SourceKeyRequired: 'The subtitle source did not accept its key.',
    IdentityUnverified: 'This title could not be matched for an online search.',
    LimitReached: 'The subtitle source has reached its limit for now. Try again later.',
    Unavailable: 'The subtitle source could not be reached.',
    Blocked: 'This library never looks anything up online.',
    NotAllowed: 'Your profile cannot search for subtitles.',
    LanguageUnknown: 'That language is not supported for subtitle search.',
    NoneFound: 'No subtitles were found. Try another language.',
    AssetMissing: 'This file is not available.',
    Unsupported: 'Subtitles cannot be searched for this title.'
  };

  /** The centered sentence for an empty or problem answer: the Engine's own words when it sent some, else the same plain sentence the Dashboard uses. */
  function statusSentence(status, message) {
    var own = text(message).trim();
    if (own) { return own; }
    if (FALLBACK_SENTENCES[status]) { return FALLBACK_SENTENCES[status]; }
    return 'The search could not be completed.';
  }

  /** Whether the footer row of the Audio & subtitles panel shows: only when the Engine says a source is ready. */
  function sourceReady(response) {
    return !!response && response.status === 'Ready';
  }

  /** The words that go with a failed "Use this". */
  function chooseFailureSentence(response) {
    var own = text(response && response.message).trim();
    return own || 'Could not use this one.';
  }

  /* ---- profile languages ---- */

  /** The Language list: the profile's own language first, then the common ones, each once. */
  function languageList(profileLanguage, current) {
    var seen = {};
    var out = [];
    [profileLanguage, current].concat(COMMON_LANGUAGES).forEach(function (name) {
      var clean = text(name).trim();
      var key = clean.toLowerCase();
      if (clean && !seen[key]) { seen[key] = true; out.push(clean); }
    });
    return out;
  }

  /* ---- the one-second rest rule ---- */

  /**
   * options: { delay, setTimeout, clearTimeout, onRest(key) }. rest(key) starts the clock for that key (and drops any other);
   * onRest fires only if nothing else came first. Timers are injectable so a test needs no real waiting.
   */
  function createDwell(options) {
    var delay = isNumber(options.delay) ? options.delay : DWELL_MS;
    var set = options.setTimeout || function (fn, ms) { return global.setTimeout(fn, ms); };
    var clear = options.clearTimeout || function (id) { global.clearTimeout(id); };
    var timer = null;
    var pendingKey = null;
    function cancel() {
      if (timer !== null) { clear(timer); }
      timer = null;
      pendingKey = null;
    }
    return {
      rest: function (key) {
        cancel();
        pendingKey = key;
        timer = set(function () {
          var fired = pendingKey;
          timer = null;
          pendingKey = null;
          if (options.onRest) { options.onRest(fired); }
        }, delay);
      },
      cancel: cancel,
      pending: function () { return pendingKey; }
    };
  }

  /**
   * Reads each row at most once per window. options: { read(key) -> Promise<item>, onChange(key), delay, setTimeout, clearTimeout }.
   * State per key: 'reading', 'read' or 'failed' (a failed row is not asked again: the source's daily allowance is limited,
   * and the row can still be used). The best match is seeded as read when the list already carries its lines.
   */
  function createReader(options) {
    var entries = {};
    var closed = false;
    var reads = 0;
    function notify(key) { if (!closed && options.onChange) { options.onChange(key); } }
    function start(key) {
      if (closed || entries[key]) { return Promise.resolve(); }
      entries[key] = { state: 'reading', item: null };
      reads++;
      notify(key);
      return Promise.resolve().then(function () { return options.read(key); }).then(function (item) {
        entries[key] = { state: item ? 'read' : 'failed', item: item || null };
        notify(key);
      }, function () {
        entries[key] = { state: 'failed', item: null };
        notify(key);
      });
    }
    var dwell = createDwell({
      delay: options.delay, setTimeout: options.setTimeout, clearTimeout: options.clearTimeout,
      onRest: function (key) { start(key); }
    });
    return {
      /** A row whose words arrived with the list. */
      seed: function (key, item) { if (!entries[key]) { entries[key] = { state: 'read', item: item }; } },
      /** Focus moved onto this row: read it once focus has rested for the delay. Already-known rows read nothing. */
      focus: function (key) {
        if (entries[key]) { dwell.cancel(); return; }
        dwell.rest(key);
      },
      /** The person asked for this row (Select), or it is the best match: read it now. */
      readNow: function (key) { dwell.cancel(); return start(key); },
      state: function (key) { return entries[key] ? entries[key].state : 'unread'; },
      item: function (key) { return entries[key] ? entries[key].item : null; },
      readCount: function () { return reads; },
      pending: function () { return dwell.pending(); },
      cancel: function () { dwell.cancel(); },
      close: function () { closed = true; dwell.cancel(); }
    };
  }

  /* ---- the key map ---- */

  /**
   * state: { zone, count, index, languageCount, languageIndex, full }
   * zones: 'language' (the header button), 'list', 'pane', 'use', 'cancel', 'languages' (the short list of languages).
   * Returns { state, effect, delta }: effect is one of 'select' (open the row in full), 'apply', 'close', 'open-languages',
   * 'choose-language', 'scroll-pane' (with delta), or null. Every key is used while the window is open, so none reaches the video.
   */
  function step(state, action) {
    var s = {
      zone: state.zone, count: state.count, index: state.index,
      languageCount: state.languageCount || 0, languageIndex: state.languageIndex || 0, full: !!state.full
    };
    var effect = null;
    var delta = 0;
    var hasList = s.count > 0;

    if (action === 'back') {
      if (s.zone === 'languages') { s.zone = 'language'; }
      else if (s.full) { s.full = false; s.zone = hasList ? 'list' : 'cancel'; }
      else { effect = 'close'; }
      return { state: s, effect: effect, delta: 0 };
    }

    if (s.zone === 'languages') {
      if (action === 'up' && s.languageIndex > 0) { s.languageIndex--; }
      else if (action === 'down' && s.languageIndex < s.languageCount - 1) { s.languageIndex++; }
      else if (action === 'enter') { s.zone = 'language'; effect = 'choose-language'; }
      return { state: s, effect: effect, delta: 0 };
    }

    if (s.zone === 'language') {
      if (action === 'enter') { s.zone = 'languages'; effect = 'open-languages'; }
      else if (action === 'down') { s.zone = hasList ? 'list' : 'cancel'; }
      return { state: s, effect: effect, delta: 0 };
    }

    if (s.zone === 'list') {
      if (action === 'up') { if (s.index > 0) { s.index--; } else { s.zone = 'language'; } }
      else if (action === 'down') { if (s.index < s.count - 1) { s.index++; } }
      else if (action === 'right') { s.zone = 'pane'; }
      else if (action === 'enter') { s.zone = 'use'; s.full = true; effect = 'select'; }
      return { state: s, effect: effect, delta: 0 };
    }

    if (s.zone === 'pane') {
      if (action === 'left') { s.zone = 'list'; s.full = false; }
      else if (action === 'right' || action === 'enter') { s.zone = 'use'; }
      else if (action === 'up') { effect = 'scroll-pane'; delta = -1; }
      else if (action === 'down') { effect = 'scroll-pane'; delta = 1; }
      return { state: s, effect: effect, delta: delta };
    }

    if (s.zone === 'use') {
      if (action === 'enter') { effect = 'apply'; }
      else if (action === 'left') { s.zone = 'pane'; }
      else if (action === 'right') { s.zone = 'cancel'; }
      else if (action === 'up') { s.zone = 'list'; s.full = false; }
      return { state: s, effect: effect, delta: 0 };
    }

    // 'cancel'
    if (action === 'enter') { effect = 'close'; }
    else if (action === 'left') { s.zone = hasList ? 'use' : 'cancel'; }
    else if (action === 'up') { s.zone = hasList ? 'list' : 'language'; }
    return { state: s, effect: effect, delta: 0 };
  }

  var logic = {
    DWELL_MS: DWELL_MS,
    SAME_RELEASE_CONFIDENCE: SAME_RELEASE_CONFIDENCE,
    FULL_LINES: FULL_LINES,
    COMMON_LANGUAGES: COMMON_LANGUAGES,
    WORDS: WORDS,
    clock: clock,
    checkOf: checkOf,
    fitOf: fitOf,
    rowView: rowView,
    lineText: lineText,
    paneLimit: paneLimit,
    statusSentence: statusSentence,
    sourceReady: sourceReady,
    chooseFailureSentence: chooseFailureSentence,
    languageList: languageList,
    createDwell: createDwell,
    createReader: createReader,
    step: step
  };

  if (typeof module !== 'undefined' && module.exports) { module.exports = logic; }
  if (!global || !global.document) { return; }

  /* ================= the window (browser only) ================= */

  var TV = (global.TV = global.TV || {});

  function itemKey(item) { return text(item.provider) + ':' + text(item.source_id); }

  /**
   * Opens the window over the player.
   * options: { assetId, title, kind ('subtitles'), language (the profile's), restoreKeys(), onApplied(), onClosed() }.
   * Resolves nothing; calls onApplied() after "Use this" worked (the player reloads its tracks), onClosed() otherwise.
   */
  function open(options) {
    var el = TV.el;
    var kind = options.kind === 'lyrics' ? 'lyrics' : 'subtitles';
    var assetId = options.assetId;
    var languages = [];
    var language = text(options.language) || 'English';
    var items = [];
    var generation = 0;
    var closed = false;
    var applying = false;
    var message = '';
    var loading = true;
    var keyState = { zone: 'list', count: 0, index: 0, languageCount: 0, languageIndex: 0, full: false };

    var win = el('div', 'fa-window');
    var header = el('div', 'fa-header');
    var heading = el('div', 'fa-heading');
    heading.appendChild(el('div', 'fa-title', options.title || ''));
    heading.appendChild(el('div', 'fa-kind', kind === 'lyrics' ? WORDS.findLyrics : WORDS.findSubtitles));
    var languageButton = el('button', 'btn focusable fa-language');
    languageButton.type = 'button';
    languageButton.setAttribute('data-key', 'fa-language');
    header.appendChild(heading);
    header.appendChild(languageButton);

    var body = el('div', 'fa-body');
    var list = el('div', 'fa-list');
    var pane = el('div', 'fa-pane focusable');
    pane.setAttribute('tabindex', '-1');
    pane.setAttribute('data-key', 'fa-pane');
    var paneTitle = el('div', 'fa-pane-title');
    var paneBody = el('div', 'fa-pane-body');
    pane.appendChild(paneTitle);
    pane.appendChild(paneBody);
    body.appendChild(list);
    body.appendChild(pane);

    var notice = el('div', 'fa-notice');
    var footer = el('div', 'fa-footer');
    var useButton = el('button', 'btn primary focusable', WORDS.useThis);
    useButton.type = 'button';
    useButton.setAttribute('data-key', 'fa-use');
    var cancelButton = el('button', 'btn focusable', WORDS.cancel);
    cancelButton.type = 'button';
    cancelButton.setAttribute('data-key', 'fa-cancel');
    footer.appendChild(useButton);
    footer.appendChild(cancelButton);

    var languageBox = el('div', 'fa-languages');
    languageBox.hidden = true;

    win.appendChild(header);
    win.appendChild(body);
    win.appendChild(notice);
    win.appendChild(footer);
    win.appendChild(languageBox);
    document.body.appendChild(win);

    var rows = [];

    var reader = createReader({
      read: function (key) {
        var picked = items.filter(function (item) { return itemKey(item) === key; })[0];
        if (!picked) { return Promise.reject(new Error('gone')); }
        return TV.api.findPreview(assetId, kind, picked.provider, picked.source_id, language).then(function (answer) {
          return answer && answer.status === 'Found' && answer.item ? answer.item : null;
        });
      },
      onChange: function (key) { refreshRow(key); if (selectedKey() === key) { paintPane(); } }
    });

    function selectedItem() { return items[keyState.index] || null; }
    function selectedKey() { var item = selectedItem(); return item ? itemKey(item) : null; }

    // The list result with whatever has been read laid over it.
    function merged(item) {
      var read = reader.item(itemKey(item));
      if (!read) { return item; }
      var out = {};
      Object.keys(item).forEach(function (k) { out[k] = item[k]; });
      Object.keys(read).forEach(function (k) { if (read[k] !== null && read[k] !== undefined) { out[k] = read[k]; } });
      return out;
    }

    function tone(node, t) { if (t && t !== 'plain') { node.className += ' fa-' + t; } return node; }

    function fillRow(row, item, index) {
      var view = rowView(merged(item), kind, index === 0);
      row.textContent = '';
      var main = el('div', 'fa-row-main');
      main.appendChild(el('div', 'fa-row-name', view.name));
      var sub = [];
      if (view.hearing) { sub.push(view.hearing); }
      if (view.meta) { sub.push(view.meta); }
      if (sub.length) { main.appendChild(el('div', 'fa-row-sub', sub.join(' · '))); }
      main.appendChild(tone(el('div', 'fa-row-check', view.check.text), view.check.tone));
      if (view.fit) { main.appendChild(tone(el('div', 'fa-row-check', view.fit.text), view.fit.tone)); }
      row.appendChild(main);
      var chips = el('div', 'fa-chips');
      view.chips.forEach(function (c) { chips.appendChild(el('span', 'fa-chip', c)); });
      row.appendChild(chips);
    }

    function refreshRow(key) {
      items.forEach(function (item, i) { if (itemKey(item) === key && rows[i]) { fillRow(rows[i], item, i); } });
      markSelected();
    }

    function markSelected() {
      rows.forEach(function (row, i) {
        var on = i === keyState.index;
        row.className = 'fa-row focusable' + (on ? ' selected' : '');
      });
    }

    function paintPane() {
      var item = selectedItem();
      if (!item) { paneTitle.textContent = ''; paneBody.textContent = ''; return; }
      var view = rowView(merged(item), kind, keyState.index === 0);
      paneTitle.textContent = view.name;
      paneBody.textContent = '';
      var key = itemKey(item);
      var state = reader.state(key);
      var shown = merged(item);
      var lines = shown.preview_lines && shown.preview_lines.length ? shown.preview_lines : [];
      if (lines.length) {
        lines.slice(0, paneLimit(kind, keyState.full)).forEach(function (line) {
          paneBody.appendChild(el('div', 'fa-line', lineText(line, kind)));
        });
      } else if (state === 'reading') {
        paneBody.appendChild(el('div', 'fa-pane-note', WORDS.reading));
      } else if (state === 'failed') {
        paneBody.appendChild(el('div', 'fa-pane-note', WORDS.couldNotRead));
      } else {
        paneBody.appendChild(el('div', 'fa-pane-note', WORDS.notRead));
      }
    }

    function paintLanguageButton() {
      languageButton.textContent = WORDS.language + ': ' + language;
    }

    function paintLanguages() {
      languageBox.textContent = '';
      languages.forEach(function (name, i) {
        var b = el('button', 'btn focusable fa-lang' + (i === keyState.languageIndex ? ' selected' : ''), name);
        b.type = 'button';
        b.setAttribute('data-key', 'fa-lang-' + i);
        languageBox.appendChild(b);
      });
    }

    function paintLayout() {
      var hasList = items.length > 0;
      body.hidden = !hasList;
      footer.hidden = false;
      useButton.hidden = !hasList;
      notice.hidden = !message;
      notice.textContent = message;
      languageBox.hidden = keyState.zone !== 'languages';
    }

    function focusZone() {
      var target = null;
      if (keyState.zone === 'language') { target = languageButton; }
      else if (keyState.zone === 'list') { target = rows[keyState.index]; }
      else if (keyState.zone === 'pane') { target = pane; }
      else if (keyState.zone === 'use') { target = useButton; }
      else if (keyState.zone === 'cancel') { target = cancelButton; }
      else if (keyState.zone === 'languages') { target = languageBox.children[keyState.languageIndex]; }
      if (target) { TV.nav.focus(target); }
    }

    function paintAll() {
      paintLanguageButton();
      paintLayout();
      markSelected();
      paintPane();
      focusZone();
    }

    function showLoading() {
      loading = true;
      items = [];
      rows = [];
      list.textContent = '';
      message = kind === 'lyrics' ? WORDS.lookingLyrics : WORDS.looking;
      keyState.count = 0;
      keyState.index = 0;
      keyState.full = false;
      keyState.zone = 'language';
      paintAll();
      // The notice is the loading line; nothing else can be done until the list arrives except Cancel and Language.
    }

    function showList(found) {
      loading = false;
      message = '';
      items = found;
      list.textContent = '';
      rows = found.map(function (item, i) {
        var row = el('div', 'fa-row focusable');
        row.setAttribute('data-key', 'fa-row-' + i);
        fillRow(row, item, i);
        list.appendChild(row);
        return row;
      });
      keyState.count = found.length;
      keyState.index = 0;
      keyState.full = false;
      keyState.zone = 'list';
      // The best match already comes with its lines; anything without them is read now. Others wait for a second of rest.
      found.forEach(function (item) { if (item.preview_lines && item.preview_lines.length) { reader.seed(itemKey(item), null); } });
      paintAll();
      if (found.length && !(found[0].preview_lines && found[0].preview_lines.length)) { reader.readNow(itemKey(found[0])); }
    }

    function showProblem(sentence) {
      loading = false;
      items = [];
      rows = [];
      list.textContent = '';
      message = sentence;
      keyState.count = 0;
      keyState.index = 0;
      keyState.full = false;
      keyState.zone = 'cancel';
      paintAll();
    }

    function search() {
      var mine = ++generation;
      reader.cancel();
      showLoading();
      TV.api.findAlternatives(assetId, kind, language).then(function (answer) {
        if (closed || mine !== generation) { return; }
        var found = answer && answer.items ? answer.items : [];
        if (answer && answer.status === 'Found' && found.length) {
          showList(found);
        } else {
          var status = answer && answer.status === 'Found' ? 'NoneFound' : (answer ? answer.status : 'Unavailable');
          showProblem(statusSentence(status, answer ? answer.message : ''));
        }
      }, function (error) {
        if (closed || mine !== generation) { return; }
        showProblem(statusSentence(error && error.status === 403 ? 'NotAllowed' : 'Unavailable', ''));
      });
    }

    function finish(applied) {
      if (closed) { return; }
      closed = true;
      reader.close();
      generation++;
      TV.nav.setKeyHandler(options.restoreKeys || null);
      if (win.parentNode) { win.parentNode.removeChild(win); }
      if (applied) { if (options.onApplied) { options.onApplied(); } }
      else if (options.onClosed) { options.onClosed(); }
    }

    function apply() {
      var item = selectedItem();
      if (!item || applying) { return; }
      applying = true;
      message = WORDS.applying;
      paintLayout();
      TV.api.findChoose(assetId, { kind: kind, provider: item.provider, source_id: item.source_id, language: language }).then(function (answer) {
        applying = false;
        if (closed) { return; }
        if (answer && answer.status === 'Updated') { finish(true); return; }
        message = chooseFailureSentence(answer);
        paintLayout();
      }, function () {
        applying = false;
        if (closed) { return; }
        message = chooseFailureSentence(null);
        paintLayout();
      });
    }

    function onAction(action) {
      if (closed) { return false; }
      if (action !== 'left' && action !== 'right' && action !== 'up' && action !== 'down' && action !== 'enter' && action !== 'back') {
        return true; // media keys do nothing behind the window
      }
      if (applying) { return true; }
      var before = keyState.index;
      var beforeZone = keyState.zone;
      var result = step(keyState, action);
      keyState = result.state;
      if (result.effect === 'close') { finish(false); return true; }
      if (result.effect === 'apply') { apply(); return true; }
      if (result.effect === 'open-languages') {
        var at = 0;
        languages.forEach(function (name, i) { if (name.toLowerCase() === language.toLowerCase()) { at = i; } });
        keyState.languageIndex = at;
        paintLanguages();
      }
      if (result.effect === 'choose-language') {
        var chosen = languages[keyState.languageIndex];
        if (chosen) { language = chosen; search(); return true; }
      }
      if (result.effect === 'select') {
        var picked = selectedItem();
        if (picked) { reader.readNow(itemKey(picked)); }
      }
      if (result.effect === 'scroll-pane') { pane.scrollTop += result.delta * 60; }
      if (keyState.zone === 'list' && (keyState.index !== before || beforeZone !== 'list')) {
        var now = selectedItem();
        if (now) { reader.focus(itemKey(now)); }
      } else if (keyState.zone !== 'list' && keyState.zone !== 'pane' && keyState.zone !== 'use') {
        // Leaving the list for the header or the footer: nothing is waiting to be read.
        if (result.effect !== 'select') { reader.cancel(); }
      }
      paintLayout();
      markSelected();
      paintPane();
      focusZone();
      return true;
    }

    useButton.addEventListener('click', function () { if (keyState.zone === 'use') { apply(); } });
    cancelButton.addEventListener('click', function () { finish(false); });

    TV.nav.setScope(win);
    TV.nav.setKeyHandler(onAction);

    TV.api.playbackPreferences().then(function (prefs) {
      return prefs && prefs.subtitleLanguage ? text(prefs.subtitleLanguage) : '';
    }, function () { return ''; }).then(function (profileLanguage) {
      if (closed) { return; }
      if (profileLanguage) { language = profileLanguage; }
      languages = languageList(profileLanguage, language);
      keyState.languageCount = languages.length;
      search();
    });

    paintLanguageButton();
    return { close: function () { finish(false); } };
  }

  TV.findAnother = logic;
  TV.findAnother.open = open;
})(typeof window !== 'undefined' ? window : globalThis);
