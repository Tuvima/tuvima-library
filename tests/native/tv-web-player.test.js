// Behaviour checks for clients/tv-web/player-logic.js: the television player of clients/contracts/v1/player-controls.md
// (transport and tool rows, the tv-keys table, the panels, paused details, end card, bookmarks) and the Samsung and LG key
// tables. The words are compared with the contract file itself, so the app cannot drift from it. Plain Node, no packages.
'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');

globalThis.window = globalThis;
globalThis.document = { addEventListener() {}, createElement() { return {}; } };

const root = path.join(__dirname, '..', '..', 'clients', 'tv-web');
const L = require(path.join(root, 'player-logic.js'));
const contract = fs.readFileSync(path.join(__dirname, '..', '..', 'clients', 'contracts', 'v1', 'player-controls.md'), 'utf8');

// The rows of one of the contract's marked tables, as arrays of cells.
function table(name) {
  const lines = contract.split(/\r?\n/);
  const start = lines.findIndex((l) => l.trim() === '<!-- contract-table: ' + name + ' -->');
  assert.ok(start >= 0, 'the contract has no table ' + name);
  const rows = [];
  for (let i = start + 1; i < lines.length; i++) {
    if (!lines[i].startsWith('|')) { if (rows.length) { break; } continue; }
    rows.push(lines[i].split('|').slice(1, -1).map((c) => c.trim()));
  }
  return rows.slice(2); // header and separator
}

const tests = [];
function test(name, fn) { tests.push([name, fn]); }

/* ---- transport and tools against the contract tables ---- */

test('the TV episode transport row is the contract transport-tv table, Previous and Next dimmed in place', () => {
  const rows = table('transport-tv');
  const built = L.transportFor('episode', { back: 10, forward: 30 }, { playing: false, canPrevious: false, canNext: false });
  assert.strictEqual(built.length, rows.length);
  built.forEach((b, i) => {
    assert.strictEqual(b.key, rows[i][1]);
    assert.strictEqual(b.label, rows[i][2]);
    assert.strictEqual(b.dimmed ? 'yes' : 'no', rows[i][3]);
  });
});

test('the film, folder video and lesson transport row is the contract transport-film table', () => {
  const rows = table('transport-film');
  ['film', 'lesson'].forEach((kind) => {
    const built = L.transportFor(kind, { back: 10, forward: 30 }, {});
    assert.deepStrictEqual(built.map((b) => [b.key, b.label, b.dimmed ? 'yes' : 'no']), rows.map((r) => [r[1], r[2], r[3]]));
  });
});

test('Previous and Next act when there is something before or after, and Play reads Pause while playing', () => {
  const built = L.transportFor('episode', L.DEFAULT_SKIP, { playing: true, canPrevious: true, canNext: true });
  assert.deepStrictEqual(built.map((b) => b.dimmed), [false, false, false, false, false]);
  assert.strictEqual(built[2].label, 'Pause');
});

test('the skip buttons carry the profile seconds and show them inside the circle', () => {
  const built = L.transportFor('episode', { back: 15, forward: 60 }, {});
  assert.strictEqual(built[1].label, 'Skip back 15 s');
  assert.strictEqual(built[1].seconds, 15);
  assert.strictEqual(built[3].label, 'Skip forward 60 s');
  assert.strictEqual(built[3].seconds, 60);
});

test('a song has no skip buttons, only Previous track, Play and Next track', () => {
  assert.deepStrictEqual(L.transportFor('music', L.DEFAULT_SKIP, {}).map((b) => b.label), ['Previous track', 'Play', 'Next track']);
});

// The tool row is the Dashboard's tools without Volume, Fullscreen, Close and Picture in picture.
function dashboardTools(name) {
  return table(name).map((r) => r[2]).filter((label) => !['Volume', 'Fullscreen', 'Close', 'Picture in picture'].includes(label));
}

test('the TV episode tool row is the contract tools-tv order without Volume, Fullscreen and Close', () => {
  assert.deepStrictEqual(L.toolsFor('episode', { hasEpisodes: true, canBookmark: true }).map((t) => t.label), dashboardTools('tools-tv'));
  assert.deepStrictEqual(dashboardTools('tools-tv'), ['Episodes', 'Audio & subtitles', 'Bookmarks', 'Playback settings']);
});

test('the film tool row is the contract tools-film order, with no Episodes', () => {
  assert.deepStrictEqual(L.toolsFor('film', { hasEpisodes: true, canBookmark: true }).map((t) => t.label), dashboardTools('tools-film'));
});

test('a folder video says Lessons, only when other videos follow, and a lesson has the film tools', () => {
  assert.deepStrictEqual(L.toolsFor('lesson', { hasEpisodes: true, canBookmark: true }).map((t) => t.label), dashboardTools('tools-lesson'));
  assert.deepStrictEqual(L.toolsFor('lesson', { hasEpisodes: false, canBookmark: true }).map((t) => t.label), dashboardTools('tools-film'));
});

test('a tool the media or the app does not support is absent, never a dead button', () => {
  assert.deepStrictEqual(L.toolsFor('episode', { hasEpisodes: false, canBookmark: false }).map((t) => t.label), ['Audio & subtitles', 'Playback settings']);
  assert.deepStrictEqual(L.toolsFor('music', {}).map((t) => t.label), ['Lyrics']);
});

test('the kind comes from the app, then the media type, then whether a show is known', () => {
  assert.strictEqual(L.kindOf({ kind: 'lesson' }), 'lesson');
  assert.strictEqual(L.kindOf({ mediaType: 'Movies' }), 'film');
  assert.strictEqual(L.kindOf({ mediaType: 'TV' }), 'episode');
  assert.strictEqual(L.kindOf({ mediaType: 'Videos' }), 'lesson');
  assert.strictEqual(L.kindOf({ mediaType: 'Music' }), 'music');
  assert.strictEqual(L.kindOf({ mediaType: 'Audiobooks' }), 'audiobook');
  assert.strictEqual(L.kindOf({ showId: 'abc' }), 'episode');
  assert.strictEqual(L.kindOf({}), 'film');
});

/* ---- the profile's skip seconds ---- */

test('the skip seconds are 10 and 30 when nothing is saved', () => {
  assert.deepStrictEqual(L.skipSeconds(null, null), { back: 10, forward: 30 });
  assert.deepStrictEqual(L.skipSeconds({}, {}), { back: 10, forward: 30 });
  const rows = table('skip-defaults');
  assert.strictEqual(String(L.DEFAULT_SKIP.back), rows[0][1]);
  assert.strictEqual(String(L.DEFAULT_SKIP.forward), rows[1][1]);
});

test('the profile pair is used, a device choice wins, and a nonsense value falls back', () => {
  assert.deepStrictEqual(L.skipSeconds({ skipBackSeconds: 15, skipForwardSeconds: 60 }, null), { back: 15, forward: 60 });
  assert.deepStrictEqual(L.skipSeconds({ skipBackSeconds: 15, skipForwardSeconds: 60 }, { skipBack: 5 }), { back: 5, forward: 60 });
  assert.deepStrictEqual(L.skipSeconds({ skipBackSeconds: 0, skipForwardSeconds: 'x' }, null), { back: 10, forward: 30 });
});

test('the video choices are the contract skip-defaults table', () => {
  const rows = table('skip-defaults');
  assert.deepStrictEqual(L.SKIP_CHOICES.back.map(String), rows[0][2].split(',').map((s) => s.trim()));
  assert.deepStrictEqual(L.SKIP_CHOICES.forward.map(String), rows[1][2].split(',').map((s) => s.trim()));
});

test('autoplay is the device choice, else the profile preference, else on', () => {
  assert.strictEqual(L.autoplayOn(null, null), true);
  assert.strictEqual(L.autoplayOn({ autoplayNextEpisode: false }, null), false);
  assert.strictEqual(L.autoplayOn({ autoplayNextEpisode: false }, { autoplay: true }), true);
});

/* ---- keys: the contract tv-keys table ---- */

const idle = { overlayVisible: true, panelOpen: false, panelView: 'root', endCard: false, zone: 'other' };

test('the tv-keys table lists the keys this player decides', () => {
  assert.deepStrictEqual(table('tv-keys').map((r) => r[0]), [
    'Any key', 'Left and Right with the timeline focused', 'Left and Right elsewhere', 'Up and Down',
    'Select', 'Play/Pause', 'Rewind', 'Fast-forward', 'Back']);
});

test('any key shows the overlay when it is hidden', () => {
  ['left', 'right', 'up', 'down', 'enter'].forEach((action) => {
    assert.deepStrictEqual(L.keyEffect({ ...idle, overlayVisible: false }, action), { effect: 'show-overlay', consume: true });
  });
});

test('Left and Right on the timeline skip back and forward', () => {
  assert.strictEqual(L.keyEffect({ ...idle, zone: 'timeline' }, 'left').effect, 'skip-back');
  assert.strictEqual(L.keyEffect({ ...idle, zone: 'timeline' }, 'right').effect, 'skip-forward');
});

test('Left and Right elsewhere, Up, Down and Select use the normal focus movement', () => {
  ['left', 'right', 'up', 'down', 'enter'].forEach((action) => {
    assert.deepStrictEqual(L.keyEffect({ ...idle, zone: 'other' }, action), { effect: 'move', consume: false });
  });
  assert.strictEqual(L.keyEffect({ ...idle, zone: 'timeline' }, 'down').effect, 'move');
});

test('Play/Pause, Rewind and Fast-forward work whether or not the overlay is showing', () => {
  [true, false].forEach((visible) => {
    const s = { ...idle, overlayVisible: visible };
    assert.strictEqual(L.keyEffect(s, 'playpause').effect, 'toggle-play');
    assert.strictEqual(L.keyEffect(s, 'play').effect, 'play');
    assert.strictEqual(L.keyEffect(s, 'pause').effect, 'pause');
    assert.strictEqual(L.keyEffect(s, 'rewind').effect, 'skip-back');
    assert.strictEqual(L.keyEffect(s, 'forward').effect, 'skip-forward');
  });
});

test('Rewind and Fast-forward also work while a panel is open', () => {
  assert.strictEqual(L.keyEffect({ ...idle, panelOpen: true }, 'rewind').effect, 'skip-back');
  assert.strictEqual(L.keyEffect({ ...idle, panelOpen: true }, 'forward').effect, 'skip-forward');
});

test('Back closes the open panel first, then hides the overlay, then leaves the player', () => {
  assert.strictEqual(L.keyEffect({ ...idle, panelOpen: true }, 'back').effect, 'close-panel');
  assert.strictEqual(L.keyEffect({ ...idle, panelOpen: false }, 'back').effect, 'hide-overlay');
  assert.strictEqual(L.keyEffect({ ...idle, overlayVisible: false }, 'back').effect, 'close-player');
});

test('Back in a nested panel view returns to the panel first', () => {
  assert.strictEqual(L.keyEffect({ ...idle, panelOpen: true, panelView: 'speed' }, 'back').effect, 'panel-root');
  assert.strictEqual(L.keyEffect({ ...idle, panelOpen: true, panelView: 'root' }, 'back').effect, 'close-panel');
});

test('Back on the end card cancels it; the other keys move between its buttons', () => {
  const card = { ...idle, endCard: true };
  assert.strictEqual(L.keyEffect(card, 'back').effect, 'cancel-end-card');
  assert.strictEqual(L.keyEffect(card, 'left').effect, 'move');
});

test('the overlay never hides while a panel, the Find window or the end card is open, nor while paused', () => {
  assert.strictEqual(L.OVERLAY_HIDE_MS, 5000);
  assert.strictEqual(L.overlayShouldHide({ paused: false }), true);
  assert.strictEqual(L.overlayShouldHide({ paused: false, panelOpen: true }), false);
  assert.strictEqual(L.overlayShouldHide({ paused: false, finderOpen: true }), false);
  assert.strictEqual(L.overlayShouldHide({ paused: false, endCard: true }), false);
  assert.strictEqual(L.overlayShouldHide({ paused: true }), false);
});

/* ---- panel state ---- */

test('only one panel is open at a time: opening another closes the first', () => {
  let s = L.panelsInitial();
  assert.strictEqual(s.open, null);
  s = L.openPanel(s, 'episodes');
  s = L.enterView(s, 'seasons');
  assert.deepStrictEqual(s, { open: 'episodes', view: 'seasons' });
  s = L.openPanel(s, 'bookmarks');
  assert.deepStrictEqual(s, { open: 'bookmarks', view: 'root' });
  assert.deepStrictEqual(L.closePanel(s), { open: null, view: 'root' });
});

test('every tool opens the panel of the same name', () => {
  assert.deepStrictEqual(L.PANEL_OF_TOOL, {
    episodes: 'episodes', 'audio-subtitles': 'audio', bookmarks: 'bookmarks', 'playback-settings': 'settings', lyrics: 'lyrics'
  });
});

/* ---- words against the Panels table ---- */

test('the fixed words of the panels are the contract Panels table words', () => {
  const panels = table('panels');
  const byPanel = (name) => { const row = panels.find((r) => r[0] === name); assert.ok(row, name); return row[3] + ' ; ' + row[4]; };
  const has = (name, word) => assert.ok(byPanel(name).includes(word), name + ' lacks "' + word + '"');
  has('Episodes', L.WORDS.season);
  has('Episodes', L.WORDS.upNext);
  has('Episodes', L.WORDS.playing);
  has('Episodes', L.WORDS.paused);
  has('Episodes', L.WORDS.autoplay);
  has('Lessons', L.WORDS.autoplay);
  ['audio', 'subtitles', 'notSwitchable', 'subtitleAppearance'].forEach((k) => has('Audio & subtitles', L.WORDS[k]));
  ['style', 'background', 'resetToDefault', 'changesSaved'].forEach((k) => has('Subtitle appearance', L.WORDS[k]));
  L.APPEARANCE_STYLES.concat(L.APPEARANCE_BACKGROUNDS).forEach((w) => has('Subtitle appearance', w));
  ['playbackSpeed', 'skipBack', 'skipForward', 'playbackInformation', 'resetPreferences'].forEach((k) => has('Playback settings', L.WORDS[k]));
  ['bookmarkAt', 'edit', 'remove', 'keep', 'name', 'save', 'cancel', 'couldNotSave', 'noGlobal', 'noMine', 'selectBookmark', 'showOnTimeline']
    .forEach((k) => has('Bookmarks', L.WORDS[k].replace('{0}', '{0}')));
  has('Playback speed', 'Current speed');
  has('Playback speed', L.WORDS.resetSpeed);
});

test('the speed presets are the contract speed-presets table', () => {
  assert.deepStrictEqual(L.SPEED_PRESETS.map((p) => L.speedLabel(p)), table('speed-presets').map((r) => r[1]));
});

test('speed takes fine steps of 0.05 between 0.5 and 3, and a preset is ticked only when the speed equals it', () => {
  assert.strictEqual(L.stepSpeed(1, 1), 1.05);
  assert.strictEqual(L.stepSpeed(1, -1), 0.95);
  assert.strictEqual(L.stepSpeed(3, 1), 3);
  assert.strictEqual(L.stepSpeed(0.5, -1), 0.5);
  assert.strictEqual(L.speedLabel(1.25), '1.25×');
  assert.strictEqual(L.isPreset(1.25), true);
  assert.strictEqual(L.isPreset(1.05), false);
});

/* ---- the one line, paused details and lengths ---- */

test('the one line reads like the contract: episode, folder video and film', () => {
  assert.strictEqual(L.detailLine('episode', { season: 1, episode: 1, title: 'The First Wish', seconds: 22 * 60 + 41 }), 'S1 E1 · The First Wish · 22 min');
  assert.strictEqual(L.detailLine('lesson', { number: 3, title: 'Ball handling', seconds: 12 * 60 }), '3 · Ball handling · 12 min');
  assert.strictEqual(L.detailLine('film', { year: 2019, seconds: 112 * 60 }), '2019 · 1h 52m');
});

test('a length under an hour reads min, from an hour h and m, and an unknown length is left out', () => {
  assert.strictEqual(L.lengthText(22 * 60), '22 min');
  assert.strictEqual(L.lengthText(3600), '1h 0m');
  assert.strictEqual(L.lengthText(112 * 60), '1h 52m');
  assert.strictEqual(L.lengthText(0), '');
  assert.strictEqual(L.detailLine('episode', { season: 2, title: 'Pilot' }), 'S2 · Pilot');
  assert.strictEqual(L.detailLine('film', {}), '');
});

test('the timeline shows the time played and the time left', () => {
  assert.strictEqual(L.formatTime(462), '7:42');
  assert.strictEqual(L.timeLeft(462, 1361), '-14:59');
  assert.strictEqual(L.timeLeft(10, 0), '');
});

/* ---- end card ---- */

test('the end card follows the contract end-card table', () => {
  const rows = table('end-card');
  const words = Object.fromEntries(rows.map((r) => [r[0], r[1]]));
  const card = L.endCardView({ title: 'Pet Show' }, 'episode', 8);
  assert.strictEqual(card.heading, words.Heading);
  assert.strictEqual(card.countdown, words.Countdown.replace('{0}', '8'));
  assert.strictEqual(card.kind, words['Kind (TV)']);
  assert.strictEqual(card.cancel + '; ' + card.playNow, words.Buttons);
  assert.strictEqual(card.title, 'Pet Show');
  assert.strictEqual(L.endCardView({ title: 'X' }, 'lesson', 5).kind, words['Kind (lesson)']);
});

test('with autoplay off the card has no countdown', () => {
  assert.strictEqual(L.endCardView({ title: 'Pet Show' }, 'episode', null).countdown, '');
  assert.strictEqual(L.END_CARD_SECONDS, 10);
});

/* ---- episodes ---- */

const showDetail = {
  sequencePlacement: {
    groups: [
      { key: 's1', title: 'Season 1', items: [
        { title: 'The First Wish', duration: '22 min', description: 'Two genies.', isOwned: true, episodeContext: { episodeAssetId: 'a1', episodeWorkId: 'w1', seasonNumber: 1, episodeNumber: 1, state: 'InProgress' } },
        { title: 'Pet Show', duration: '22 min', description: 'A spell.', isOwned: true, episodeContext: { episodeAssetId: 'a2', episodeWorkId: 'w2', seasonNumber: 1, episodeNumber: 2, state: 'Unstarted' } },
        { title: 'Not owned', isOwned: false, episodeContext: { episodeAssetId: 'a9', episodeNumber: 3 } },
        { title: 'No file', isOwned: true, episodeContext: { episodeNumber: 4 } }
      ] },
      { key: 's2', title: 'Season 2', items: [
        { title: 'Back Again', duration: '21 min', isOwned: true, positionSeconds: 250, durationSeconds: 1320, episodeContext: { episodeAssetId: 'b1', episodeWorkId: 'x1', seasonNumber: 2, episodeNumber: 1, state: 'InProgress' } }
      ] },
      { key: 's3', title: 'Empty', items: [] }
    ]
  }
};

test('the seasons list owned episodes with a file, and leaves out a season with none', () => {
  const seasons = L.episodeSeasons(showDetail);
  assert.deepStrictEqual(seasons.map((s) => s.title), ['Season 1', 'Season 2']);
  assert.deepStrictEqual(seasons[0].items.map((i) => i.assetId), ['a1', 'a2']);
  assert.deepStrictEqual(L.episodeSeasons({}), []);
});

test('Previous and Next are the episodes before and after, across seasons; none at the ends', () => {
  const seasons = L.episodeSeasons(showDetail);
  assert.deepStrictEqual([L.neighbours(seasons, 'a1').previous, L.neighbours(seasons, 'a1').next.assetId], [null, 'a2']);
  assert.deepStrictEqual([L.neighbours(seasons, 'a2').previous.assetId, L.neighbours(seasons, 'a2').next.assetId], ['a1', 'b1']);
  assert.deepStrictEqual([L.neighbours(seasons, 'b1').previous.assetId, L.neighbours(seasons, 'b1').next], ['a2', null]);
  assert.deepStrictEqual(L.neighbours(seasons, 'zzz'), { previous: null, next: null });
  assert.strictEqual(L.seasonIndexOf(seasons, 'b1'), 1);
});

test('an episode row reads Playing or Paused for the current one, and Up next marks the next', () => {
  const seasons = L.episodeSeasons(showDetail);
  const ctx = { assetId: 'a1', playing: true, position: 143, duration: 1361, nextAssetId: 'a2' };
  const current = L.episodeRow(seasons[0].items[0], ctx);
  assert.strictEqual(current.title, '1 · The First Wish');
  assert.strictEqual(current.sub, 'Playing · 2:23 of 22:41');
  assert.strictEqual(current.current, true);
  assert.strictEqual(L.episodeRow(seasons[0].items[0], { ...ctx, playing: false }).sub, 'Paused · 2:23 of 22:41');
  const next = L.episodeRow(seasons[0].items[1], ctx);
  assert.strictEqual(next.upNext, true);
  assert.strictEqual(next.sub, '22 min · A spell.');
  const partly = L.episodeRow(seasons[1].items[0], ctx);
  assert.strictEqual(partly.sub, 'Paused · 4:10 of 22:00');
  assert.strictEqual(partly.upNext, false);
});

test('the count reads 20 episodes or 12 lessons', () => {
  assert.strictEqual(L.countLabel(20, 'episode'), '20 episodes');
  assert.strictEqual(L.countLabel(12, 'lesson'), '12 lessons');
  assert.strictEqual(L.countLabel(1, 'episode'), '1 episode');
});

/* ---- bookmarks ---- */

const marks = [
  { id: 'b2', positionSeconds: 331, label: 'Funny wish', isYours: true, isGlobal: false },
  { id: 'b1', positionSeconds: 130, label: '', note: 'Genie bottle opens', isYours: true, isGlobal: false, frameUrl: '/f1' },
  { id: 'b3', positionSeconds: 415, isYours: true, isGlobal: false },
  { id: 'g1', positionSeconds: 60, label: 'Great start', isYours: false, isGlobal: true, ownerName: 'Maya' }
];

test('Mine is the default and the choice shows only when a Global set exists', () => {
  const tabs = L.bookmarkTabs(marks);
  assert.strictEqual(tabs.showChoice, true);
  assert.deepStrictEqual(tabs.tabs.map((t) => t.label), ['Mine · 3', 'Global · 1']);
  assert.strictEqual(L.bookmarkTabs(marks.slice(0, 3)).showChoice, false);
  assert.strictEqual(L.bookmarkTabs([]).showChoice, false);
});

test('a bookmark made by the person who is also an administrator never lands in Mine when it is Global', () => {
  const both = [{ id: 'x', positionSeconds: 5, isYours: true, isGlobal: true }];
  const tabs = L.bookmarkTabs(both);
  assert.strictEqual(tabs.mine.length, 0);
  assert.strictEqual(tabs.global.length, 1);
});

test('rows are in time order with the time, the name or note, and Edit and Delete only on Mine', () => {
  const tabs = L.bookmarkTabs(marks);
  const mine = L.bookmarkRows(tabs.mine, 'mine');
  assert.deepStrictEqual(mine.map((r) => r.time), ['2:10', '5:31', '6:55']);
  assert.deepStrictEqual(mine.map((r) => r.sub), ['Genie bottle opens', 'Funny wish', 'Bookmark at 6:55']);
  assert.ok(mine.every((r) => r.canChange));
  assert.strictEqual(mine[0].frameUrl, '/f1');
  const global = L.bookmarkRows(tabs.global, 'global');
  assert.strictEqual(global[0].sub, 'Great start · Maya');
  assert.strictEqual(global[0].canChange, false);
});

test('"Bookmark at {0}" saves at the moment pressed, at once, with no note', () => {
  assert.strictEqual(L.bookmarkAtLabel(2052), 'Bookmark at 34:12');
  const body = L.bookmarkRequest('asset', 462.04, 1361.5);
  assert.deepStrictEqual(body, { assetId: 'asset', positionSeconds: 462, isGlobal: false, durationSeconds: 1361.5 });
  assert.ok(!('note' in body));
  assert.ok(!('label' in body));
});

test('empty states are the contract words', () => {
  assert.strictEqual(L.bookmarkEmpty('global'), 'No Global bookmarks on this title yet');
  assert.strictEqual(L.bookmarkEmpty('mine'), 'No bookmarks of yours yet');
});

test('Show on timeline puts a tick where each bookmark is, and none past the end', () => {
  assert.deepStrictEqual(L.bookmarkTicks([{ positionSeconds: 50 }, { positionSeconds: 150 }], 100), [50]);
  assert.deepStrictEqual(L.bookmarkTicks([{ positionSeconds: 50 }], 0), []);
});

/* ---- audio, subtitles and appearance ---- */

test('audio rows show the facts, put commentary last and list an unswitchable track as such', () => {
  const tracks = [
    { displayName: 'English', codec: 'eac3', channels: 8 },
    { displayName: 'English · Director commentary', codec: 'aac', channels: 2 },
    { displayName: 'Spanish', codec: 'ac3', channels: 6 }
  ];
  const rows = L.audioRows(tracks, 0, 3);
  assert.deepStrictEqual(rows.map((r) => r.name), ['English', 'Spanish', 'English · Director commentary']);
  assert.strictEqual(rows[0].sub, 'Dolby Digital Plus · 7.1');
  assert.strictEqual(rows[0].selected, true);
  const limited = L.audioRows(tracks, 0, 1);
  assert.strictEqual(limited.find((r) => r.name === 'Spanish').selectable, false);
  assert.strictEqual(limited.find((r) => r.name === 'Spanish').sub, 'Not switchable in this browser');
  assert.strictEqual(limited[0].selectable, true);
  assert.strictEqual(L.audioRows(tracks, 0, null)[1].selectable, false);
});

test('subtitle rows say SDH and Forced', () => {
  assert.strictEqual(L.subtitleLabel({ displayName: 'English' }, 0), 'English');
  assert.strictEqual(L.subtitleLabel({ displayName: 'English', isHearingImpaired: true }, 0), 'English · SDH');
  assert.strictEqual(L.subtitleLabel({ language: 'es', isForced: true }, 1), 'es · Forced');
});

test('Subtitle appearance starts from the profile and a device choice wins', () => {
  assert.deepStrictEqual(L.appearanceOf({ subtitleSize: 'Large', subtitleBackground: true }, null), { style: 'Large text', background: 'Box' });
  assert.deepStrictEqual(L.appearanceOf({ subtitleSize: 'Medium', subtitleBackground: false }, null), { style: 'Default', background: 'None' });
  assert.deepStrictEqual(L.appearanceOf({}, { subtitleStyle: 'High contrast', subtitleBackground: 'Shadow' }), { style: 'High contrast', background: 'Shadow' });
  assert.deepStrictEqual(L.appearanceClasses({ style: 'Large text', background: 'Shadow' }), ['sub-large', 'sub-bg-shadow']);
});

test('playback information lists the picture, the sound, the delivery and the connection', () => {
  const lines = L.playbackInfoLines({
    technical: { height: 1080, videoCodec: 'h264', audioCodec: 'aac', channels: 2, fileName: 'a.mkv' },
    playbackPath: { rung: 'repackage' },
    connection: { path: 'local' }
  });
  assert.deepStrictEqual(lines.map((l) => l.label + ': ' + l.value), ['Video: 1080p · H264', 'Audio: AAC · Stereo', 'Delivery: Converted', 'Connection: Local', 'File: a.mkv']);
});

/* ---- Samsung and LG key tables ---- */

function freshNav() {
  delete require.cache[require.resolve(path.join(root, 'nav.js'))];
  require(path.join(root, 'nav.js'));
  return window.TV.nav;
}

test('the Samsung table maps Return to Back and the Rewind and Fast-forward keys to skip', () => {
  delete window.TV_PLATFORM_KEYS;
  require(path.join(root, 'platforms', 'tizen', 'tizen-keys.js'));
  const nav = freshNav();
  assert.strictEqual(nav.actionFor({ keyCode: 10009 }), 'back');
  assert.strictEqual(nav.actionFor({ keyCode: 412 }), 'rewind');
  assert.strictEqual(nav.actionFor({ keyCode: 417 }), 'forward');
  assert.strictEqual(nav.actionFor({ keyCode: 10252 }), 'playpause');
  assert.strictEqual(window.TV_PLATFORM_KEYS[10009], 'back');
});

test('the LG table maps Back to Back and the Rewind and Fast-forward keys to skip', () => {
  delete window.TV_PLATFORM_KEYS;
  require(path.join(root, 'platforms', 'webos', 'webos-keys.js'));
  const nav = freshNav();
  assert.strictEqual(nav.actionFor({ keyCode: 461 }), 'back');
  assert.strictEqual(nav.actionFor({ keyCode: 412 }), 'rewind');
  assert.strictEqual(nav.actionFor({ keyCode: 417 }), 'forward');
  assert.strictEqual(window.TV_PLATFORM_KEYS[461], 'back');
});

let failed = 0;
for (const [name, fn] of tests) {
  try {
    fn();
    console.log('ok   - ' + name);
  } catch (e) {
    failed++;
    console.log('FAIL - ' + name);
    console.log(e && e.stack ? e.stack : e);
  }
}
console.log((tests.length - failed) + ' passed' + (failed ? ', ' + failed + ' failed' : ''));
if (failed) { process.exit(1); }
