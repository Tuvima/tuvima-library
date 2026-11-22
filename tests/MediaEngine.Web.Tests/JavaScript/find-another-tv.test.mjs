// Behaviour checks for clients/tv-web/find-another.js: the one-second rest rule, the read-once cache, the remote's key map
// (including the Back order) and the words each row and state shows. The logic half of the file has no DOM, so plain Node runs it.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const fa = require(path.join(here, '..', '..', '..', 'clients', 'tv-web', 'find-another.js'));

// A hand-written clock: timers fire only when the test moves time forward.
function fakeClock() {
  let now = 0;
  let nextId = 1;
  const timers = new Map();
  return {
    setTimeout(fn, ms) { const id = nextId++; timers.set(id, { at: now + ms, fn }); return id; },
    clearTimeout(id) { timers.delete(id); },
    advance(ms) {
      const target = now + ms;
      for (;;) {
        const due = [...timers.entries()].filter(([, t]) => t.at <= target).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) { break; }
        now = due[1].at;
        timers.delete(due[0]);
        due[1].fn();
      }
      now = target;
    },
    pending() { return timers.size; }
  };
}

// A reader whose reads are recorded and answered by the test.
function readerWith(clock, answer) {
  const reads = [];
  const changes = [];
  const reader = fa.createReader({
    setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout,
    read(key) { reads.push(key); return answer ? answer(key) : Promise.resolve({ key }); },
    onChange(key) { changes.push(key + ':' + reader.state(key)); }
  });
  return { reader, reads, changes };
}

const settle = () => new Promise((resolve) => setImmediate(resolve));

// ---- the one-second rest rule ----

test('a row is read only after focus has rested on it for one second', async () => {
  const clock = fakeClock();
  const { reader, reads } = readerWith(clock);
  reader.focus('a');
  clock.advance(999);
  assert.deepEqual(reads, [], 'nothing is read before a full second');
  clock.advance(1);
  await settle();
  assert.deepEqual(reads, ['a']);
});

test('scrolling quickly through ten rows reads nothing', async () => {
  const clock = fakeClock();
  const { reader, reads } = readerWith(clock);
  for (let i = 0; i < 10; i++) {
    reader.focus('row-' + i);
    clock.advance(200);
  }
  await settle();
  assert.deepEqual(reads, [], 'no row rested for a second');
  assert.equal(reader.pending(), 'row-9', 'only the row focus is on is waiting');
});

test('focus moving on cancels the wait, so only the row focus finally rests on is read', async () => {
  const clock = fakeClock();
  const { reader, reads } = readerWith(clock);
  reader.focus('a');
  clock.advance(900);
  reader.focus('b');
  clock.advance(900);
  assert.deepEqual(reads, []);
  clock.advance(100);
  await settle();
  assert.deepEqual(reads, ['b']);
});

test('the rest delay is one second', () => {
  assert.equal(fa.DWELL_MS, 1000);
});

test('leaving the list cancels a wait that has not finished', async () => {
  const clock = fakeClock();
  const { reader, reads } = readerWith(clock);
  reader.focus('a');
  clock.advance(500);
  reader.cancel();
  clock.advance(5000);
  await settle();
  assert.deepEqual(reads, []);
  assert.equal(clock.pending(), 0);
});

test('a window that closed while a row was being read ignores the answer', async () => {
  const clock = fakeClock();
  let finish;
  const { reader, changes } = readerWith(clock, () => new Promise((resolve) => { finish = resolve; }));
  reader.readNow('a');
  await settle();
  reader.close();
  finish({ ok: true });
  await settle();
  assert.deepEqual(changes, ['a:reading'], 'no change is reported after close');
});

// ---- the read-once cache ----

test('a row already read is not read again when focus rests on it later', async () => {
  const clock = fakeClock();
  const { reader, reads } = readerWith(clock);
  reader.focus('a');
  clock.advance(1000);
  await settle();
  reader.focus('b');
  reader.focus('a');
  clock.advance(5000);
  await settle();
  assert.deepEqual(reads, ['a'], 'a was read once; b never rested');
  assert.equal(reader.state('a'), 'read');
});

test('a row seeded with the list (the best match) is never read again', async () => {
  const clock = fakeClock();
  const { reader, reads } = readerWith(clock);
  reader.seed('best', { preview_lines: [{ text: 'x' }] });
  reader.focus('best');
  clock.advance(5000);
  await reader.readNow('best');
  assert.deepEqual(reads, []);
  assert.equal(reader.state('best'), 'read');
});

test('the best match without lines is read at once, with no waiting', async () => {
  const clock = fakeClock();
  const { reader, reads } = readerWith(clock);
  reader.readNow('best');
  await settle();
  assert.deepEqual(reads, ['best']);
});

test('a row being read is not asked for twice', async () => {
  const clock = fakeClock();
  const { reader, reads } = readerWith(clock, () => new Promise(() => { /* never answers */ }));
  reader.readNow('a');
  reader.readNow('a');
  reader.focus('a');
  clock.advance(5000);
  await settle();
  assert.deepEqual(reads, ['a']);
  assert.equal(reader.state('a'), 'reading');
});

test('a row that could not be read is marked failed and is not asked again', async () => {
  const clock = fakeClock();
  const { reader, reads } = readerWith(clock, () => Promise.reject(new Error('limit')));
  reader.focus('a');
  clock.advance(1000);
  await settle();
  assert.equal(reader.state('a'), 'failed');
  reader.focus('b');
  reader.focus('a');
  clock.advance(5000);
  await settle();
  assert.deepEqual(reads, ['a']);
});

test('an answer with no item counts as failed', async () => {
  const clock = fakeClock();
  const { reader } = readerWith(clock, () => Promise.resolve(null));
  reader.readNow('a');
  await settle();
  assert.equal(reader.state('a'), 'failed');
});

test('a row the reader has not seen reads as unread', () => {
  const { reader } = readerWith(fakeClock());
  assert.equal(reader.state('nope'), 'unread');
});

// ---- the key map ----

const base = { zone: 'list', count: 3, index: 0, languageCount: 4, languageIndex: 0, full: false };
const press = (state, ...actions) => actions.reduce((acc, action) => {
  const r = fa.step(acc.state, action);
  return { state: r.state, effects: [...acc.effects, r.effect], delta: r.delta };
}, { state, effects: [], delta: 0 });

test('Up and Down move through the list and stop at the last row', () => {
  const r = press(base, 'down', 'down', 'down');
  assert.equal(r.state.index, 2);
  assert.equal(r.state.zone, 'list');
  assert.equal(press(r.state, 'up').state.index, 1);
});

test('Up from the first row reaches the Language button and Down comes back to the list', () => {
  const up = press(base, 'up').state;
  assert.equal(up.zone, 'language');
  assert.equal(press(up, 'down').state.zone, 'list');
});

test('Right goes from the list to the pane, then to Use this; Left goes back toward the list', () => {
  const pane = press(base, 'right').state;
  assert.equal(pane.zone, 'pane');
  const use = press(pane, 'right').state;
  assert.equal(use.zone, 'use');
  assert.equal(press(use, 'left').state.zone, 'pane');
  assert.equal(press(pane, 'left').state.zone, 'list');
});

test('Up and Down in the pane scroll it', () => {
  const pane = { ...base, zone: 'pane' };
  const down = fa.step(pane, 'down');
  assert.equal(down.effect, 'scroll-pane');
  assert.equal(down.delta, 1);
  assert.equal(fa.step(pane, 'up').delta, -1);
});

test('Select on a row opens its words in full and moves focus to Use this', () => {
  const r = fa.step({ ...base, index: 1 }, 'enter');
  assert.equal(r.effect, 'select');
  assert.equal(r.state.zone, 'use');
  assert.equal(r.state.full, true);
  assert.equal(r.state.index, 1);
});

test('Select on Use this applies the result', () => {
  assert.equal(fa.step({ ...base, zone: 'use', full: true }, 'enter').effect, 'apply');
});

test('Select on Cancel closes the window', () => {
  assert.equal(fa.step({ ...base, zone: 'cancel' }, 'enter').effect, 'close');
  assert.equal(press({ ...base, zone: 'use' }, 'right').state.zone, 'cancel');
});

test('Select on Language opens the short list; Select on a language chooses it', () => {
  const language = { ...base, zone: 'language' };
  const opened = fa.step(language, 'enter');
  assert.equal(opened.effect, 'open-languages');
  assert.equal(opened.state.zone, 'languages');
  const moved = press(opened.state, 'down', 'down');
  assert.equal(moved.state.languageIndex, 2);
  const chosen = fa.step(moved.state, 'enter');
  assert.equal(chosen.effect, 'choose-language');
  assert.equal(chosen.state.languageIndex, 2);
  assert.equal(chosen.state.zone, 'language');
});

test('the language list stops at its first and last language', () => {
  const open = { ...base, zone: 'languages', languageCount: 2 };
  assert.equal(press(open, 'up').state.languageIndex, 0);
  assert.equal(press(open, 'down', 'down', 'down').state.languageIndex, 1);
});

test('Back closes the language list first, then the full words, then the window', () => {
  const languages = { ...base, zone: 'languages', full: true };
  const first = fa.step(languages, 'back');
  assert.equal(first.effect, null);
  assert.equal(first.state.zone, 'language');
  assert.equal(first.state.full, true, 'closing the language list leaves the full words alone');

  const full = { ...base, zone: 'use', full: true };
  const second = fa.step(full, 'back');
  assert.equal(second.effect, null);
  assert.equal(second.state.full, false);
  assert.equal(second.state.zone, 'list');

  assert.equal(fa.step(second.state, 'back').effect, 'close');
});

test('Back from the plain list closes the window', () => {
  assert.equal(fa.step(base, 'back').effect, 'close');
});

test('a problem screen with no rows keeps focus on Cancel and Up reaches Language', () => {
  const problem = { ...base, zone: 'cancel', count: 0 };
  assert.equal(fa.step(problem, 'left').state.zone, 'cancel');
  assert.equal(fa.step(problem, 'up').state.zone, 'language');
  assert.equal(fa.step({ ...problem, zone: 'language' }, 'down').state.zone, 'cancel');
  assert.equal(fa.step(problem, 'enter').effect, 'close');
  assert.equal(fa.step(problem, 'back').effect, 'close');
});

test('the step reducer never changes the state it was given', () => {
  const before = JSON.stringify(base);
  fa.step(base, 'down');
  assert.equal(JSON.stringify(base), before);
});

// ---- wording ----

const release = {
  provider: 'subdl', source_id: '1', release_name: 'Ferrari.2023.1080p.WEB-DL', confidence: 0.95,
  is_hearing_impaired: false, is_current: false, preview_lines: []
};

test('a subtitle row made for the file reads Made for your file', () => {
  const view = fa.rowView(release, 'subtitles', false);
  assert.equal(view.name, 'Ferrari.2023.1080p.WEB-DL');
  assert.equal(view.check.text, 'Made for your file');
  assert.equal(view.fit, null, 'the timing check waits until the file has been read');
  assert.equal(view.hearing, '');
});

test('a subtitle row from another release says it may be out of step', () => {
  const view = fa.rowView({ ...release, confidence: 0.5 }, 'subtitles', false);
  assert.equal(view.check.text, 'A different release, may be out of step');
});

test('Hearing impaired shows for a hearing impaired row', () => {
  assert.equal(fa.rowView({ ...release, is_hearing_impaired: true }, 'subtitles', false).hearing, 'Hearing impaired');
});

test('Best match sits on the first row and In use on the current one', () => {
  assert.deepEqual(fa.rowView(release, 'subtitles', true).chips, ['Best match']);
  assert.deepEqual(fa.rowView({ ...release, is_current: true }, 'subtitles', true).chips, ['Best match', 'In use']);
  assert.deepEqual(fa.rowView({ ...release, is_current: true }, 'subtitles', false).chips, ['In use']);
});

test('once read, a file that ends with the film reads Fits the film', () => {
  const view = fa.rowView({ ...release, length_check: 'Fits', length_difference_seconds: 20 }, 'subtitles', false);
  assert.equal(view.fit.text, 'Fits the film');
});

test('once read, a file that ends early or late says how many minutes short or long', () => {
  const short = fa.rowView({ ...release, length_check: 'Off', length_difference_seconds: -300 }, 'subtitles', false);
  assert.equal(short.fit.text, '5 minutes short');
  assert.equal(short.fit.tone, 'warn');
  const long = fa.rowView({ ...release, length_check: 'Off', length_difference_seconds: 3600 }, 'subtitles', false);
  assert.equal(long.fit.text, '60 minutes long');
  assert.equal(long.fit.tone, 'bad');
  const one = fa.rowView({ ...release, length_check: 'Off', length_difference_seconds: -61 }, 'subtitles', false);
  assert.equal(one.fit.text, '1 minute short');
});

test('lyrics rows read Synced or Words only, and Fits the song or the seconds off', () => {
  const song = { provider: 'lrclib', source_id: '9', title: 'Smile', artist: 'Empty Soul', length_seconds: 222, is_timed: true };
  const view = fa.rowView({ ...song, length_check: 'Off', length_difference_seconds: 4 }, 'lyrics', true);
  assert.equal(view.name, 'Smile');
  assert.equal(view.meta, 'Empty Soul · 3:42');
  assert.equal(view.check.text, 'Synced');
  assert.equal(view.fit.text, '4 seconds longer');
  assert.equal(fa.rowView({ ...song, is_timed: false }, 'lyrics', false).check.text, 'Words only, not timed');
  assert.equal(fa.rowView({ ...song, length_check: 'Fits', length_difference_seconds: 0 }, 'lyrics', false).fit.text, 'Fits the song');
});

test('a row with no name reads Untitled result', () => {
  assert.equal(fa.rowView({ ...release, release_name: null }, 'subtitles', false).name, 'Untitled result');
});

test('a pane line shows the time each subtitle appears', () => {
  assert.equal(fa.lineText({ at_seconds: 65, text: 'Hello.' }, 'subtitles'), '1:05   Hello.');
  assert.equal(fa.lineText({ at_seconds: 3725, text: 'Late.' }, 'subtitles'), '1:02:05   Late.');
  assert.equal(fa.lineText({ text: 'A lyric line' }, 'lyrics'), 'A lyric line');
});

test('the pane shows a few lines, about twenty once a row is opened, and six for lyrics', () => {
  assert.equal(fa.paneLimit('subtitles', false), 3);
  assert.equal(fa.paneLimit('subtitles', true), 20);
  assert.equal(fa.paneLimit('lyrics', false), 6);
});

test('the fixed wording matches the spec', () => {
  const w = fa.WORDS;
  assert.equal(w.findSubtitles, 'Find subtitles');
  assert.equal(w.footerRow, 'Find subtitles…');
  assert.equal(w.looking, 'Looking for subtitles…');
  assert.equal(w.reading, 'Reading…');
  assert.equal(w.couldNotRead, 'Could not read this one');
  assert.equal(w.useThis, 'Use this');
  assert.equal(w.cancel, 'Cancel');
  assert.equal(w.language, 'Language');
});

// ---- states ----

test('a problem answer shows the Engine\'s own sentence when it sent one', () => {
  assert.equal(fa.statusSentence('NoSourceOn', 'Switch on a subtitle source in Plugins.'), 'Switch on a subtitle source in Plugins.');
});

test('every status the Engine can answer has a plain sentence when it sends none', () => {
  const statuses = ['NoneFound', 'NoSourceOn', 'NotAllowed', 'Blocked', 'Unavailable', 'AssetMissing', 'Unsupported',
    'IdentityUnverified', 'SourceKeyRequired', 'LimitReached', 'LanguageUnknown'];
  for (const status of statuses) {
    const sentence = fa.statusSentence(status, '');
    assert.ok(sentence.length > 10, status);
    assert.notEqual(sentence, fa.statusSentence('SomethingNew', ''), status + ' has its own sentence');
  }
  assert.equal(fa.statusSentence('SomethingNew', '  '), 'The search could not be completed.');
});

test('the Find subtitles row shows only when the source status is Ready', () => {
  assert.equal(fa.sourceReady({ status: 'Ready' }), true);
  for (const status of ['NoSourceOn', 'NotAllowed', 'Unsupported', 'Blocked', 'SourceKeyRequired', 'AssetMissing']) {
    assert.equal(fa.sourceReady({ status }), false, status);
  }
  assert.equal(fa.sourceReady(null), false);
});

test('a refused Use this shows the Engine\'s words, or a plain sentence', () => {
  assert.equal(fa.chooseFailureSentence({ status: 'Failed', message: 'Could not download this one.' }), 'Could not download this one.');
  assert.equal(fa.chooseFailureSentence(null), 'Could not use this one.');
});

test('the Language list starts with the profile\'s language and lists each language once', () => {
  const list = fa.languageList('French', 'english');
  assert.equal(list[0], 'French');
  assert.equal(list[1], 'english');
  assert.equal(list.filter((name) => name.toLowerCase() === 'english').length, 1);
  assert.ok(list.includes('Spanish'));
  assert.deepEqual(fa.languageList('', ''), fa.COMMON_LANGUAGES);
});
