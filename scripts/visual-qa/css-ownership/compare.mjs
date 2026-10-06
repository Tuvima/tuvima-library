import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

function flatten(value, prefix = '', result = {}) {
  if (Array.isArray(value)) value.forEach((item, index) => flatten(item, `${prefix}[${index}]`, result));
  else if (value && typeof value === 'object') Object.entries(value).forEach(([key, item]) => {
    if (key !== 'scopeEvidence') flatten(item, prefix ? `${prefix}.${key}` : key, result);
  });
  else result[prefix] = value;
  return result;
}

export function compareDocuments(before, after) {
  if (before.schemaVersion !== 1 || after.schemaVersion !== 1) throw new Error('Unsupported capture schema.');
  if (!before.targets?.length || !after.targets?.length) throw new Error('Missing mandatory target coverage.');
  if (JSON.stringify(before.tolerances) !== JSON.stringify(after.tolerances))
    throw new Error('Tolerances must be unchanged from the reviewed baseline.');
  const a = flatten(before), b = flatten(after);
  const differences = [], tolerated = [];
  for (const key of new Set([...Object.keys(a), ...Object.keys(b)])) {
    if (a[key] === b[key]) continue;
    const tolerance = before.tolerances?.find(t => t.path === key && t.reason && Number.isFinite(t.maxDelta)
      && typeof a[key] === 'number' && typeof b[key] === 'number' && Math.abs(a[key] - b[key]) <= t.maxDelta);
    (tolerance ? tolerated : differences).push({ property: key, before: a[key], after: b[key], reason: tolerance?.reason });
  }
  return { differences, tolerated };
}

export function expectedCaptureFiles(matrix) {
  const files = new Set();
  const add = (state, width, height) => files.add(`${state.id}-${width}x${height}.styles.json`);
  const ids = new Set();
  for (const state of matrix.states) {
    if (ids.has(state.id)) throw new Error(`Duplicate matrix state: ${state.id}`);
    ids.add(state.id);
    for (const [width, height] of matrix.viewports) {
      if (state.desktopOnly && width < 721 || state.phoneOnly && width >= 721) continue;
      add(state, width, height);
    }
    if (state.lowerHeight) add(state, ...matrix.lowerHeightViewport);
  }
  for (const geometry of matrix.additionalViewports ?? []) {
    const state = matrix.states.find(state => state.id === geometry.state);
    if (!state) throw new Error(`Unknown additional viewport state: ${geometry.state}`);
    add(state, geometry.width, geometry.height);
  }
  return [...files].sort();
}

export async function compareDirectories(beforeDirectory, afterDirectory, matrix) {
  const beforeFiles = (await fs.readdir(beforeDirectory)).filter(name => name.endsWith('.styles.json')).sort();
  const afterFiles = (await fs.readdir(afterDirectory)).filter(name => name.endsWith('.styles.json')).sort();
  if (!beforeFiles.length || JSON.stringify(beforeFiles) !== JSON.stringify(afterFiles))
    throw new Error('Capture file coverage differs or is empty.');
  if (matrix && JSON.stringify(beforeFiles) !== JSON.stringify(expectedCaptureFiles(matrix)))
    throw new Error('Capture coverage does not match the declared state/viewport matrix.');
  const results = [];
  for (const name of beforeFiles) {
    const before = JSON.parse(await fs.readFile(path.join(beforeDirectory, name), 'utf8'));
    const after = JSON.parse(await fs.readFile(path.join(afterDirectory, name), 'utf8'));
    results.push({ file: name, ...compareDocuments(before, after) });
  }
  return results;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const before = args[args.indexOf('--before') + 1], after = args[args.indexOf('--after') + 1];
  try {
    if (!args.includes('--before') || !args.includes('--after')) throw new Error('Usage: --before directory --after directory');
    const matrix = args.includes('--matrix')
      ? JSON.parse(await fs.readFile(args[args.indexOf('--matrix') + 1], 'utf8')) : undefined;
    const results = await compareDirectories(before, after, matrix);
    for (const result of results) if (result.differences.length || result.tolerated.length) console.log(JSON.stringify(result));
    console.log(JSON.stringify({ files: results.length, differences: results.reduce((n, r) => n + r.differences.length, 0),
      tolerated: results.reduce((n, r) => n + r.tolerated.length, 0) }));
    process.exitCode = results.some(result => result.differences.length) ? 1 : 0;
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
