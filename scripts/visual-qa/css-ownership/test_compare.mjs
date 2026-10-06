import test from 'node:test';
import assert from 'node:assert/strict';
import { compareDocuments, expectedCaptureFiles, compareDirectories } from './compare.mjs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

const fixture = () => ({ schemaVersion: 1, state: 'book', viewport: { width: 1920, height: 1080, dpr: 1 },
  targets: [{ selector: '.cover', elements: [{ rect: { width: 400 }, styles: { color: 'red' }, scopeEvidence: ['b-old'] }] }], tolerances: [] });
test('identical capture passes', () => assert.equal(compareDocuments(fixture(), fixture()).differences.length, 0));
test('changed scope hash is evidence rather than appearance', () => {
  const after = fixture(); after.targets[0].elements[0].scopeEvidence = ['b-new'];
  assert.equal(compareDocuments(fixture(), after).differences.length, 0);
});
test('changed geometry fails', () => {
  const after = fixture(); after.targets[0].elements[0].rect.width = 401;
  assert.equal(compareDocuments(fixture(), after).differences.length, 1);
});
test('missing target or element fails', () => {
  const after = fixture(); after.targets[0].elements = [];
  assert.ok(compareDocuments(fixture(), after).differences.length);
  assert.throws(() => compareDocuments(fixture(), { ...fixture(), targets: [] }));
});
test('reviewed exact property tolerance reports the difference', () => {
  const before = fixture(); before.tolerances = [{ path: 'targets[0].elements[0].rect.width', maxDelta: 0.01, reason: 'Subpixel rounding' }];
  const after = structuredClone(before); after.targets[0].elements[0].rect.width += 0.001;
  const result = compareDocuments(before, after);
  assert.equal(result.differences.length, 0); assert.equal(result.tolerated.length, 1);
  after.targets[0].elements[0].styles.color = 'blue';
  assert.equal(compareDocuments(before, after).differences.length, 1);
});
test('new tolerance cannot conceal a regression', () => {
  const after = fixture(); after.tolerances = [{ path: 'anything', maxDelta: 100, reason: 'Not reviewed' }];
  assert.throws(() => compareDocuments(fixture(), after));
});
test('matrix includes conditional, lower-height and additional geometries', () => {
  assert.deepEqual(expectedCaptureFiles({ states: [{ id: 'book', lowerHeight: true }, { id: 'player', phoneOnly: true }],
    viewports: [[1920,1080],[390,844]], lowerHeightViewport: [1920,900],
    additionalViewports: [{ state: 'player', width: 420, height: 780 }] }),
    ['book-1920x1080.styles.json','book-1920x900.styles.json','book-390x844.styles.json',
      'player-390x844.styles.json','player-420x780.styles.json']);
});
test('equally incomplete before and after sets fail declared matrix coverage', async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'css-ownership-compare-'));
  try {
    await fs.writeFile(path.join(directory, 'book-1920x1080.styles.json'), JSON.stringify(fixture()));
    await assert.rejects(compareDirectories(directory, directory,
      { states: [{ id: 'book' }], viewports: [[1920,1080],[390,844]] }), /declared/);
  } finally {
    const target = await fs.realpath(directory);
    assert.equal(path.dirname(target), await fs.realpath(os.tmpdir()));
    assert.ok(path.basename(target).startsWith('css-ownership-compare-'));
    await fs.rm(target, { recursive: true, force: true });
  }
});
