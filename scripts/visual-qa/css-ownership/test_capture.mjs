import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { captureState } from '../home-media-cards/capture.mjs';
import { captureOwnershipState } from './capture.mjs';

const viewport = { width: 1440, height: 900 };
const geometry = () => ({ ...viewport, dpr: 1, fontStatus: 'loaded', incompleteImages: [],
  controls: [{ label: 'First', x: 10 }, { label: 'Second', x: 20 }],
  pageState: { headings: ['Library'], activeSlide: ['One'] } });
const styles = () => ({ viewport: { ...viewport, dpr: 1 }, scroll: { x: 0, y: 0 },
  targets: [{ selector: '.cover', elements: [
    { rect: { x: 10, y: 0, width: 200, height: 300 }, styles: { color: 'red', display: 'block' },
      state: { focused: false, ariaExpanded: 'false' }, pseudo: {}, scopeEvidence: ['b-a'] },
    { rect: { x: 220, y: 0, width: 200, height: 300 }, styles: { color: 'blue', display: 'block' },
      state: { focused: false, ariaExpanded: 'false' }, pseudo: {}, scopeEvidence: ['b-a'] },
  ] }] });

// Simulate a transport that preserves values but inserts object keys differently.
function reordered(value) {
  if (Array.isArray(value)) return value.map(reordered);
  if (value && typeof value === 'object')
    return Object.fromEntries(Object.entries(value).reverse().map(([key, entry]) => [key, reordered(entry)]));
  return value;
}

// A stub SOF header supplies the exported frame size; it is never used as visual evidence.
function frame(width = viewport.width, height = viewport.height) {
  return Buffer.from([0xff, 0xd8, 0xff, 0xc0, 0, 8, 8, height >> 8, height & 255, width >> 8, width & 255, 0]);
}
function scriptedTab(responses, bytes = frame()) {
  const pending = [...responses], clips = [];
  return { clips, playwright: {
    domSnapshot: async () => {},
    evaluate: async () => {
      assert.ok(pending.length, 'Unexpected extra browser read');
      return structuredClone(pending.shift());
    },
  }, screenshot: async options => { clips.push(options); return bytes; },
  assertComplete: () => assert.equal(pending.length, 0, 'Every stability read must occur') };
}
async function withOutput(run) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'css-ownership-capture-test-'));
  try { await run(directory); }
  finally {
    const target = await fs.realpath(directory);
    assert.equal(path.dirname(target), await fs.realpath(os.tmpdir()));
    assert.ok(path.basename(target).startsWith('css-ownership-capture-test-'));
    await fs.rm(target, { recursive: true, force: true });
  }
}
const captureArgs = outputRoot => ({ ...viewport, label: 'unit-capture', outputRoot });

test('viewport capture accepts reordered transport objects and explicitly includes the scrollbar gutter', async () => {
  await withOutput(async outputRoot => {
    const tab = scriptedTab([viewport, geometry(), reordered(geometry())]);
    const result = await captureState({ tab, ...captureArgs(outputRoot) });
    tab.assertComplete();
    assert.deepEqual(tab.clips, [{ clip: { x: 0, y: 0, ...viewport } }]);
    assert.equal(result.geometry.capturedPixelWidth, viewport.width);
    assert.equal(result.geometry.capturedPixelHeight, viewport.height);
  });
});

for (const [name, change] of [
  ['geometry', value => { value.width++; }],
  ['state value', value => { value.pageState.headings[0] = 'Different library'; }],
  ['array order', value => { value.controls.reverse(); }],
]) {
  test(`viewport capture refuses changed ${name} even when object keys are reordered`, async () => {
    await withOutput(async outputRoot => {
      const after = reordered(geometry()); change(after);
      const tab = scriptedTab([viewport, geometry(), after]);
      await assert.rejects(captureState({ tab, ...captureArgs(outputRoot) }), /geometry\/state changed/);
      assert.deepEqual(await fs.readdir(outputRoot), [], 'Unstable captures must not be saved');
    });
  });
}

test('capture instability reports actual changed groups and labelled control coordinates', async () => {
  await withOutput(async outputRoot => {
    const after = reordered(geometry());
    after.controls[0].x = 30;
    after.pageState.headings[0] = 'Different library';
    const tab = scriptedTab([viewport, geometry(), after]);
    await assert.rejects(captureState({ tab, ...captureArgs(outputRoot) }), error => {
      assert.match(error.message, /Changed groups: controls, pageState/);
      assert.match(error.message, /controls\[0\] "First": x 10 -> 30/);
      return true;
    });
    assert.deepEqual(await fs.readdir(outputRoot), []);
  });
});

test('a clipped or rescaled screenshot is refused despite stable DOM evidence', async () => {
  await withOutput(async outputRoot => {
    const tab = scriptedTab([viewport, geometry(), reordered(geometry())], frame(1430, 894));
    await assert.rejects(captureState({ tab, ...captureArgs(outputRoot) }), /Screenshot export mismatch/);
    assert.deepEqual(await fs.readdir(outputRoot), []);
  });
});

async function ownershipCapture(outputRoot, after) {
  const tab = scriptedTab([viewport, styles(), viewport, geometry(), reordered(geometry()), after]);
  const result = await captureOwnershipState({ tab, ...viewport, outputRoot,
    state: { id: 'unit-capture', selectors: [{ selector: '.cover' }] } });
  tab.assertComplete();
  return result;
}
test('ownership capture accepts reordered nested style, rectangle, and state objects', async () => {
  await withOutput(async outputRoot => {
    const result = await ownershipCapture(outputRoot, reordered(styles()));
    assert.equal(result.targetCount, 1);
    const saved = JSON.parse(await fs.readFile(path.join(outputRoot, 'unit-capture-1440x900.styles.json'), 'utf8'));
    assert.deepEqual(saved.targets, styles().targets);
  });
});
for (const [name, change] of [
  ['geometry', value => { value.targets[0].elements[0].rect.width++; }],
  ['style value', value => { value.targets[0].elements[0].styles.color = 'green'; }],
  ['semantic state', value => { value.targets[0].elements[0].state.ariaExpanded = 'true'; }],
  ['element order', value => { value.targets[0].elements.reverse(); }],
]) {
  test(`ownership capture refuses changed ${name} with reordered object keys`, async () => {
    await withOutput(async outputRoot => {
      const after = reordered(styles()); change(after);
      await assert.rejects(ownershipCapture(outputRoot, after), /Computed state changed/);
      assert.equal((await fs.readdir(outputRoot)).some(file => file.endsWith('.styles.json')), false);
    });
  });
}
