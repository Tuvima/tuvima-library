// Behaviour checks for clients/tv-web/universe.js: the Universe page's totals line, cast and adaptation lines, and
// which rows each Series shows. Runs against the real checked-in fixture. Plain Node, no packages.
'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');

globalThis.window = globalThis;
require(path.join(__dirname, '..', '..', 'clients', 'tv-web', 'universe.js'));
const universe = window.TV.universe;

const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', 'native-client-v1', 'universe.json'), 'utf8'));
const labels = { series: 'Series', show: 'Show' };
const labelOf = (groupType) => labels[groupType] || '';

const tests = [];
function test(name, fn) { tests.push([name, fn]); }

test('the fixture view lists the three Series in the order the Engine gave', () => {
  const view = universe.view(fixture, labelOf);
  assert.deepStrictEqual(view.shelves.map((s) => s.collectionId), fixture.shelves.map((s) => s.collection_id));
  assert.strictEqual(view.shelves.length, 3);
});

test('the totals line reads 3 Series, 5 Works, 2 formats for the fixture', () => {
  assert.strictEqual(universe.totalsLine(fixture), '3 Series · 5 Works · 2 formats');
});

test('the totals line uses singular forms and leaves out zero parts', () => {
  assert.strictEqual(universe.totalsLine({ series_count: 1, item_count: 1, media_types: ['Movie'] }), '1 Series · 1 Work · 1 format');
  assert.strictEqual(universe.totalsLine({ series_count: 0, item_count: 14, media_types: ['Movie', 'Book'] }), '14 Works · 2 formats');
  assert.strictEqual(universe.totalsLine({ series_count: 2, item_count: 0, media_types: [] }), '2 Series');
  assert.strictEqual(universe.totalsLine({}), '');
});

test('a Universe of only TV shows says shows, like the Dashboard', () => {
  const show = { group_type: 'show' };
  assert.strictEqual(universe.totalsLine({ series_count: 1, item_count: 1, media_types: ['TV'], shelves: [show] }), '1 show · 1 Work · 1 format');
  assert.strictEqual(universe.totalsLine({ series_count: 2, item_count: 3, media_types: ['TV'], shelves: [show, show] }), '2 shows · 3 Works · 1 format');
  assert.strictEqual(universe.totalsLine({ series_count: 2, item_count: 3, media_types: ['TV'], shelves: [show, { group_type: 'series' }] }), '2 Series · 3 Works · 1 format');
});

test('a Series with a numbered run shows its run row and no Also row', () => {
  const alpha = universe.view(fixture, labelOf).shelves[0];
  assert.strictEqual(alpha.showRun, true);
  assert.strictEqual(alpha.runItems.length, 2);
  assert.strictEqual(alpha.showAlso, false);
  assert.strictEqual(alpha.runTitle, 'Series Alpha');
  assert.strictEqual(alpha.runMeta, 'Series · 2 items');
});

test('a Series with an empty run shows only its Also row and its heading never says 0', () => {
  const gamma = universe.view(fixture, labelOf).shelves[2];
  assert.strictEqual(gamma.showRun, false);
  assert.strictEqual(gamma.runItems.length, 0);
  assert.strictEqual(gamma.showAlso, true);
  assert.strictEqual(gamma.alsoItems.length, 1);
  assert.strictEqual(gamma.alsoTitle, 'Series Gamma');
  assert.strictEqual(gamma.alsoMeta, 'Series · Also in this world · 1 item');
  assert.ok(!/0/.test(gamma.alsoTitle + gamma.alsoMeta));
});

test('a Series with a run and Also items shows both, the Also row under the wire title', () => {
  const entry = JSON.parse(JSON.stringify(fixture.shelves[0]));
  entry.also_in_this_world = fixture.shelves[2].also_in_this_world;
  entry.also_in_this_world_count = 1;
  const plan = universe.shelfPlan(entry, 'Series');
  assert.strictEqual(plan.showRun, true);
  assert.strictEqual(plan.showAlso, true);
  assert.strictEqual(plan.alsoTitle, 'Also in this world');
  assert.strictEqual(plan.alsoMeta, '1 item');
});

test('a Series with neither a run nor Also items is skipped', () => {
  const entry = { collection_id: 'x', group_type: 'series', item_count: 0, shelf: { title: 'Empty', items: [] }, also_in_this_world: null, also_in_this_world_count: 0 };
  assert.strictEqual(universe.shelfPlan(entry, 'Series'), null);
  assert.strictEqual(universe.view({ shelves: [entry] }, labelOf).shelves.length, 0);
});

test('the heading uses a singular item and the Show label', () => {
  const entry = { collection_id: 'x', group_type: 'show', item_count: 1, shelf: { title: 'One Show', items: [{ id: 'a' }] } };
  assert.strictEqual(universe.shelfPlan(entry, 'Show').runMeta, 'Show · 1 item');
});

test('a cast member with characters reads as A, B and one without has no line', () => {
  const view = universe.view(fixture, labelOf);
  assert.deepStrictEqual(view.cast.map((m) => m.name), ['Ann Actor', 'Ben Narrator']);
  assert.strictEqual(view.cast[0].line, 'as Hero');
  assert.strictEqual(view.cast[1].line, null);
  assert.strictEqual(universe.castLine({ characters: [{ label: 'Hero' }, { label: 'Villain' }] }), 'as Hero, Villain');
  assert.strictEqual(universe.castLine({}), null);
});

test('an adaptation reads Title, a dash, the capitalised relationship and the source', () => {
  assert.deepStrictEqual(universe.view(fixture, labelOf).adaptations, ['The Film — Based on The Novel']);
  assert.strictEqual(universe.adaptationLine({ label: 'A', relationship: 'derivative of', source_label: 'B' }), 'A — Derivative of B');
});

test('a detail missing the new fields still produces a page view', () => {
  const old = { qid: 'Q1', name: 'Old', shelves: [{ collection_id: 'c', group_type: 'series', shelf: { title: 'S', items: [{ id: 'a' }, { id: 'b' }] } }] };
  const view = universe.view(old, labelOf);
  assert.strictEqual(view.totals, '');
  assert.strictEqual(view.description, '');
  assert.strictEqual(view.shelves.length, 1);
  assert.strictEqual(view.shelves[0].showRun, true);
  assert.strictEqual(view.shelves[0].runMeta, 'Series · 2 items');
  assert.strictEqual(view.shelves[0].showAlso, false);
  assert.deepStrictEqual(view.cast, []);
  assert.deepStrictEqual(view.adaptations, []);
  assert.doesNotThrow(() => universe.view(null, labelOf));
  assert.doesNotThrow(() => universe.view({}));
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
