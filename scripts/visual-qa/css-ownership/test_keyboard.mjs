import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { validateKeyboardMatrix, assertCheckpoint, runKeyboardState, compareKeyboardTraces, requiredKeyboardCoverage } from './keyboard.mjs';

const config = JSON.parse(await fs.readFile(new URL('./keyboard-states.json',import.meta.url),'utf8'));

test('CUA can import keyboard exports without a process global or starting its CLI', async () => {
  const url = new URL('./keyboard.mjs', import.meta.url).href;
  const script = `await import('node:fs/promises'); await import('node:path'); await import('node:url');
    globalThis.process = undefined;
    const helper = await import(${JSON.stringify(url)});
    if (typeof helper.runKeyboardState !== 'function') throw new Error('Missing CUA runner export');`;
  const result = await promisify(execFile)(process.execPath, ['--input-type=module', '--eval', script]);
  assert.equal(result.stdout, '');
  assert.equal(result.stderr, '');
});
test('committed keyboard matrix declares all required interaction coverage', () => {
  assert.equal(validateKeyboardMatrix(config),config);
});
test('nested picker Escape assertions require child dismissal and native parent focus restoration before editor dismissal', () => {
  const nested = config.states.find(item => item.id === 'nested-dialog-keyboard');
  assert.equal(nested.steps.length, 2);
  assert.equal(nested.steps[0].action.key, 'Escape');
  assert.equal(nested.steps[1].action.key, 'Escape');
  const childClosed = { targets: {
    editor: { count: 1, elements: [{ focusWithin: true }] }, picker: { count: 0 },
  } };
  assertCheckpoint(childClosed, nested.steps[0].expect);
  assert.throws(() => assertCheckpoint({targets:{editor:{count:1,elements:[{focusWithin:false}]},picker:{count:0}}},nested.steps[0].expect), /focusWithin/);
  assert.throws(() => assertCheckpoint({targets:{editor:{count:0,elements:[]},picker:{count:0}}},nested.steps[0].expect), /count/);
  assertCheckpoint({targets:{editor:{count:0},picker:{count:0}}},nested.steps[1].expect);
  assert.equal(nested.steps[1].action.target.name, 'Close editor');
});
test('missing nested-dialog coverage is refused', () => {
  const incomplete = structuredClone(config);
  incomplete.states = incomplete.states.filter(state => !state.coverage.includes('nested-dialog'));
  assert.throws(() => validateKeyboardMatrix(incomplete), /Missing keyboard state coverage/);
});
test('a key action without assertions cannot become evidence', () => {
  const incomplete = structuredClone(config); incomplete.states[0].steps[0].expect = {};
  assert.throws(() => validateKeyboardMatrix(incomplete), /assertions/);
});
test('partial assertions retain strict target counts and nested semantic expectations', () => {
  const actual = { targets:{ select:{count:1,elements:[{expanded:'true',role:'combobox',controlsResolved:true}]} } };
  assertCheckpoint(actual,{targets:{select:{count:1,elements:[{expanded:'true',controlsResolved:true}]}}});
  assert.throws(() => assertCheckpoint(actual,{targets:{select:{elements:[{expanded:'false'}]}}}), /expanded/);
  assert.throws(() => assertCheckpoint(actual,{targets:{select:{elements:[]}}}), /coverage/);
});

const state = () => ({ id:'unit-keyboard',coverage:requiredKeyboardCoverage,
  targets:[{id:'select',selector:'[role=combobox]'}],
  steps:[{action:{type:'press',target:{role:'combobox',name:'Choices'},key:'Escape'},
    waitFor:[{target:{selector:'[role=listbox]'},state:'detached'}],
    expect:{focus:{target:'select',role:'combobox',name:'Choices'},targets:{select:{count:1,elements:[{expanded:'false',controlsResolved:true}]}}}}] });
const checkpoint = () => ({focus:{target:'select',role:'combobox',name:'Choices'},
  targets:{select:{count:1,elements:[{expanded:'false',controlsResolved:true}]}}});
function fakeTab(actual = checkpoint(), count = 1) {
  const calls = [];
  const locator = { count:async () => count,press:async key => calls.push(['press',key]),click:async () => calls.push(['click']),
    waitFor:async value => calls.push(['waitFor',value.state]) };
  return { calls,playwright:{getByRole:() => locator,locator:() => locator,
    domSnapshot:async () => calls.push(['snapshot']),evaluate:async () => actual} };
}
test('runner uses documented locator actions and condition waits before recording verified evidence', async () => {
  const tab = fakeTab(); const trace = await runKeyboardState({tab,state:state()});
  assert.deepEqual(tab.calls,[['press','Escape'],['snapshot'],['waitFor','detached']]);
  assert.equal(trace.checkpoints[0].actual.focus.name,'Choices');
});
test('missing action target and failed assertions refuse traces', async () => {
  await assert.rejects(runKeyboardState({tab:fakeTab(checkpoint(),0),state:state()}), /one target/);
  const bad = checkpoint(); bad.targets.select.elements[0].expanded = 'true';
  await assert.rejects(runKeyboardState({tab:fakeTab(bad),state:state()}), /expanded/);
});
test('keyboard comparison reports changed focus, semantics, and key sequence', async () => {
  const before = await runKeyboardState({tab:fakeTab(),state:state()});
  const after = structuredClone(before);
  assert.deepEqual(compareKeyboardTraces(before,after).differences,[]);
  after.checkpoints[0].actual.focus.name = 'Wrong control';
  after.checkpoints[0].action.key = 'Enter';
  assert.deepEqual(compareKeyboardTraces(before,after).differences,['checkpoints[0].action','checkpoints[0].actual']);
});
test('empty and incomplete keyboard traces cannot pass', () => {
  assert.throws(() => compareKeyboardTraces({schemaVersion:1,checkpoints:[]},{schemaVersion:1,checkpoints:[]}), /coverage/);
});
