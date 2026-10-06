import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const requiredKeyboardCoverage = ['popup', 'nested-dialog', 'dialog', 'select', 'tab', 'expansion'];

export function validateKeyboardMatrix(matrix) {
  if (matrix.schemaVersion !== 1 || !matrix.states?.length) throw new Error('Missing keyboard state matrix.');
  const declared = new Set(matrix.requiredCoverage ?? []);
  for (const category of requiredKeyboardCoverage)
    if (!declared.has(category)) throw new Error(`Required keyboard coverage not declared: ${category}.`);
  const actual = new Set(matrix.states.flatMap(state => state.coverage ?? []));
  for (const category of declared)
    if (!actual.has(category)) throw new Error(`Missing keyboard state coverage: ${category}.`);
  const ids = new Set();
  for (const state of matrix.states) {
    if (!/^[a-z0-9-]+$/.test(state.id ?? '') || ids.has(state.id)) throw new Error('Invalid or duplicate keyboard state ID.');
    ids.add(state.id);
    validateKeyboardState(state);
  }
  return matrix;
}

export function validateKeyboardState(state) {
    if (!/^[a-z0-9-]+$/.test(state.id ?? '')) throw new Error('Invalid keyboard state ID.');
    if (!state.targets?.length || !state.steps?.length || !state.steps.some(step => step.action?.type === 'press'))
      throw new Error(`Keyboard state ${state.id} requires targets, steps, and a key action.`);
    const targetIds = new Set();
    for (const target of state.targets) {
      if (!target.id || targetIds.has(target.id) || !target.selector) throw new Error(`Invalid keyboard target in ${state.id}.`);
      targetIds.add(target.id);
    }
    for (const step of state.steps) {
      if (!['press','click'].includes(step.action?.type) || !(step.action.target?.role || step.action.target?.selector) || !Object.keys(step.expect ?? {}).length)
        throw new Error(`Every keyboard step in ${state.id} needs an action and assertions.`);
      for (const id of Object.keys(step.expect.targets ?? {}))
        if (!targetIds.has(id)) throw new Error(`Assertion target ${id} was not declared.`);
    }
  return state;
}

export function assertCheckpoint(actual, expected, label = 'checkpoint') {
  function check(value, wanted, key) {
    if (Array.isArray(wanted)) {
      if (!Array.isArray(value) || value.length !== wanted.length) throw new Error(`${key}: array coverage differs.`);
      wanted.forEach((item,index) => check(value[index],item,`${key}[${index}]`));
    } else if (wanted && typeof wanted === 'object') {
      for (const [name,item] of Object.entries(wanted)) check(value?.[name], item, `${key}.${name}`);
    } else if (JSON.stringify(value) !== JSON.stringify(wanted))
      throw new Error(`${key}: expected ${JSON.stringify(wanted)}, found ${JSON.stringify(value)}.`);
  }
  check(actual, expected, label);
}

function actionLocator(tab, descriptor) {
  let scope = descriptor.within ? tab.playwright.locator(descriptor.within) : tab.playwright;
  return descriptor.role ? scope.getByRole(descriptor.role, { name: descriptor.name, exact: true })
    : scope.locator(descriptor.selector);
}

// Call only with an existing documented CUA tab. All mutations are ordinary
// locator actions; DOM evaluation records evidence without changing the page.
export async function runKeyboardState({ tab, state, outputRoot }) {
  validateKeyboardState(state);
  const checkpoints = [];
  for (const [index,step] of state.steps.entries()) {
    const locator = actionLocator(tab, step.action.target);
    const count = await locator.count();
    if (count !== 1) throw new Error(`${state.id} step ${index}: action requires one target, found ${count}.`);
    if (step.action.type === 'press') {
      if (!step.action.key) throw new Error('A press action requires a key.');
      await locator.press(step.action.key);
    } else await locator.click();
    await tab.playwright.domSnapshot();
    for (const wait of step.waitFor ?? []) {
      if (!['visible','hidden','attached','detached'].includes(wait.state)) throw new Error('Unsupported declared wait state.');
      await actionLocator(tab,wait.target).waitFor({ state: wait.state });
    }
    const checkpoint = await tab.playwright.evaluate(({ targets }) => {
      const entries = targets.map(target => ({ ...target, elements: [...document.querySelectorAll(target.selector)] }));
      const role = element => element?.getAttribute('role') ?? ({ BUTTON:'button', A: element?.hasAttribute('href') ? 'link' : null,
        SELECT:'combobox', TEXTAREA:'textbox', DIALOG:'dialog' }[element?.tagName])
        ?? (element?.tagName === 'INPUT' ? ({ checkbox:'checkbox',radio:'radio',range:'slider' }[element.type] ?? 'textbox') : null);
      const name = element => {
        if (!element) return null;
        const labelled = (element.getAttribute('aria-labelledby') ?? '').split(/\s+/).filter(Boolean)
          .map(id => document.getElementById(id)?.textContent ?? '').join(' ').trim();
        return (element.getAttribute('aria-label') ?? (labelled || [...(element.labels ?? [])].map(label => label.textContent).join(' ')
          || element.getAttribute('alt') || element.getAttribute('title') || element.textContent || '')).trim().replace(/\s+/g,' ').slice(0,240);
      };
      const identify = element => entries.find(entry => entry.elements.includes(element))?.id ?? null;
      const visible = element => {
        const rect = element.getBoundingClientRect();
        if (!rect.width || !rect.height) return false;
        for (let current = element; current; current = current.parentElement) {
          const style = getComputedStyle(current);
          if (current.hidden || style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) return false;
        }
        return true;
      };
      const controls = element => (element.getAttribute('aria-controls') ?? '').split(/\s+/).filter(Boolean).map(id => {
        const controlled = document.getElementById(id);
        return { target: identify(controlled), exists: !!controlled, role: role(controlled), name: name(controlled) };
      });
      const active = document.activeElement;
      return { document: { overflow: document.documentElement.style.overflow }, focus: { target: identify(active), role: role(active), name: name(active) },
        targets: Object.fromEntries(entries.map(entry => [entry.id, { count: entry.elements.length,
          elements: entry.elements.map(element => ({ role: role(element), name: name(element), focused: element === active,
            focusWithin: element.contains(active), visible: visible(element), open: element.tagName === 'DIALOG' ? element.open : null,
            text: element.textContent.trim().replace(/\s+/g, ' ').slice(0, 240), expanded: element.getAttribute('aria-expanded'),
            selected: element.getAttribute('aria-selected'), checked: element.getAttribute('aria-checked') ?? (element.matches(':checked') ? 'true' : null),
            disabled: element.matches(':disabled') || element.getAttribute('aria-disabled') === 'true',
            controls: controls(element), controlsResolved: controls(element).every(target => target.exists) })) }])) };
    }, { targets: state.targets });
    assertCheckpoint(checkpoint, step.expect, `${state.id} step ${index}`);
    checkpoints.push({ step: index, action: step.action, expected: step.expect, actual: checkpoint });
  }
  const trace = { schemaVersion: 1, state: state.id, coverage: state.coverage ?? [], checkpoints,
    limitations: ['Names use aria-label, aria-labelledby, native labels and text fallback; this is not a full accessibility-tree algorithm.',
      'Only declared locator key/click actions were exercised; no browser is launched and no unrecorded baseline is inferred.'] };
  if (outputRoot) {
    await fs.mkdir(outputRoot,{recursive:true});
    await fs.writeFile(path.join(outputRoot,`${state.id}.keyboard.json`),JSON.stringify(trace,null,2));
  }
  return trace;
}

export function compareKeyboardTraces(before, after) {
  if (before.schemaVersion !== 1 || after.schemaVersion !== 1) throw new Error('Unsupported keyboard trace schema.');
  const differences = [];
  for (const key of ['state','coverage','limitations'])
    if (JSON.stringify(before[key]) !== JSON.stringify(after[key])) differences.push(key);
  if (!before.checkpoints?.length || before.checkpoints.length !== after.checkpoints?.length) throw new Error('Keyboard checkpoint coverage differs or is empty.');
  before.checkpoints.forEach((point,index) => {
    for (const checkpoint of [point,after.checkpoints[index]])
      if (checkpoint.step !== index || !checkpoint.action?.type || !checkpoint.actual?.targets || !Object.keys(checkpoint.expected ?? {}).length)
        throw new Error('Incomplete keyboard checkpoint evidence.');
    const action = value => ({ type: value?.type, key: value?.key ?? null });
    if (JSON.stringify(action(point.action)) !== JSON.stringify(action(after.checkpoints[index].action)))
      differences.push(`checkpoints[${index}].action`);
    for (const key of ['step','expected','actual'])
      if (JSON.stringify(point[key]) !== JSON.stringify(after.checkpoints[index][key])) differences.push(`checkpoints[${index}].${key}`);
  });
  return { differences };
}

export async function compareKeyboardDirectories(beforeRoot, afterRoot, matrix) {
  validateKeyboardMatrix(matrix);
  const expected = matrix.states.map(state => `${state.id}.keyboard.json`).sort();
  for (const root of [beforeRoot,afterRoot]) {
    const files = (await fs.readdir(root)).filter(file => file.endsWith('.keyboard.json')).sort();
    if (JSON.stringify(files) !== JSON.stringify(expected)) throw new Error('Keyboard trace files do not cover the declared matrix.');
  }
  return Promise.all(expected.map(async file => {
    const state = matrix.states.find(state => `${state.id}.keyboard.json` === file);
    const traces = await Promise.all([beforeRoot,afterRoot].map(async root => JSON.parse(await fs.readFile(path.join(root,file),'utf8'))));
    for (const trace of traces) {
      if (trace.state !== state.id || JSON.stringify(trace.coverage) !== JSON.stringify(state.coverage)
        || trace.checkpoints?.length !== state.steps.length) throw new Error('Keyboard trace does not match declared state coverage.');
      trace.checkpoints.forEach((checkpoint,index) => {
        if (JSON.stringify(checkpoint.expected) !== JSON.stringify(state.steps[index].expect))
          throw new Error('Keyboard trace assertions differ from the declared matrix.');
        assertCheckpoint(checkpoint.actual,checkpoint.expected,`${state.id} recorded step ${index}`);
      });
    }
    return {file,...compareKeyboardTraces(...traces)};
  }));
}

if (typeof process !== 'undefined' && process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  try {
    for (const option of ['--before','--after','--matrix']) if (!args.includes(option))
      throw new Error('Usage: keyboard.mjs --before trace-directory --after trace-directory --matrix keyboard-states.json');
    const results = await compareKeyboardDirectories(args[args.indexOf('--before')+1],args[args.indexOf('--after')+1],
      JSON.parse(await fs.readFile(args[args.indexOf('--matrix')+1],'utf8')));
    console.log(JSON.stringify({ traces: results.length, differences: results }));
    process.exitCode = results.some(result => result.differences.length) ? 1 : 0;
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
