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
  if (![1,2].includes(before.schemaVersion) || before.schemaVersion !== after.schemaVersion)
    throw new Error('Unsupported or mixed capture schema; legacy captures must retain their reviewed coverage.');
  if (!before.targets?.length || !after.targets?.length) throw new Error('Missing mandatory target coverage.');
  if (JSON.stringify(before.tolerances) !== JSON.stringify(after.tolerances))
    throw new Error('Tolerances must be unchanged from the reviewed baseline.');
  const normalize = document => {
    if (document.schemaVersion === 1) return document;
    const copy = structuredClone(document);
    const ids = new Set();
    for (const target of copy.targets) {
      if (!target.id || ids.has(target.id) || !target.elements?.length) throw new Error('Missing or duplicate semantic target coverage.');
      ids.add(target.id);
    }
    if (!copy.targetContract?.length || copy.targetContract.length !== ids.size)
      throw new Error('Missing semantic target contract.');
    const contractIds = new Set();
    for (const contract of copy.targetContract) {
      if (contractIds.has(contract.id) || !Number.isInteger(contract.min) || contract.min < 1)
        throw new Error('Invalid semantic target contract.');
      contractIds.add(contract.id);
      const target = copy.targets.find(target => target.id === contract.id);
      if (!target || target.elements.length < contract.min || (contract.max !== null && target.elements.length > contract.max))
        throw new Error(`Missing declared semantic coverage: ${contract.id}.`);
    }
    copy.targets.sort((a,b) => a.id.localeCompare(b.id));
    copy.targetContract.sort((a,b) => a.id.localeCompare(b.id));
    // Selectors locate the same reviewed semantic targets across implementations.
    // All geometry, styles, pseudo-elements, state, counts and contracts remain compared.
    copy.targets.forEach(target => delete target.selector);
    return copy;
  };
  const a = flatten(normalize(before)), b = flatten(normalize(after));
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

export async function compareDirectories(beforeDirectory, afterDirectory, matrix, { visualReview, requireVisualReview = false } = {}) {
  const beforeFiles = (await fs.readdir(beforeDirectory)).filter(name => name.endsWith('.styles.json')).sort();
  const afterFiles = (await fs.readdir(afterDirectory)).filter(name => name.endsWith('.styles.json')).sort();
  if (!beforeFiles.length || JSON.stringify(beforeFiles) !== JSON.stringify(afterFiles))
    throw new Error('Capture file coverage differs or is empty.');
  if (matrix && JSON.stringify(beforeFiles) !== JSON.stringify(expectedCaptureFiles(matrix)))
    throw new Error('Capture coverage does not match the declared state/viewport matrix.');
  if (visualReview) {
    if (visualReview.schemaVersion !== 1 || !Array.isArray(visualReview.pairs)) throw new Error('Unsupported screenshot review manifest.');
    const captures = visualReview.pairs.map(pair => pair.capture);
    if (new Set(captures).size !== captures.length || captures.some(file => !beforeFiles.includes(file)))
      throw new Error('Duplicate or unknown screenshot review pair.');
  }
  const results = [];
  for (const name of beforeFiles) {
    const before = JSON.parse(await fs.readFile(path.join(beforeDirectory, name), 'utf8'));
    const after = JSON.parse(await fs.readFile(path.join(afterDirectory, name), 'utf8'));
    const screenshot = name.replace(/\.styles\.json$/, '.jpg');
    for (const document of [before,after])
      if (document.screenshotReview && document.screenshotReview.file !== screenshot)
        throw new Error(`Screenshot metadata does not match the capture: ${name}.`);
    const pair = visualReview?.pairs?.find(pair => pair.capture === name);
    let visualReviewStatus = 'pending';
    if (pair) {
      if (!['pass','fail'].includes(pair.decision) || !pair.reviewer?.trim() || !pair.notes?.trim() || !Number.isFinite(Date.parse(pair.reviewedAt)))
        throw new Error(`Incomplete paired screenshot review: ${name}.`);
      visualReviewStatus = pair.decision;
    }
    if (before.screenshotReview?.required || after.screenshotReview?.required || requireVisualReview || pair) {
      await fs.access(path.join(beforeDirectory, screenshot));
      await fs.access(path.join(afterDirectory, screenshot));
    }
    if (requireVisualReview && visualReviewStatus === 'pending') throw new Error(`Paired screenshot review is required: ${name}.`);
    results.push({ file: name, ...compareDocuments(before, after), screenshotReview: {
      before: path.join(beforeDirectory,screenshot), after: path.join(afterDirectory,screenshot),
      status: visualReviewStatus, method: 'Paired human review; no automated pixel comparison.',
      ...(pair ? { reviewer: pair.reviewer, notes: pair.notes, reviewedAt: pair.reviewedAt } : {}) } });
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
    const visualReview = args.includes('--visual-review')
      ? JSON.parse(await fs.readFile(args[args.indexOf('--visual-review') + 1], 'utf8')) : undefined;
    const results = await compareDirectories(before, after, matrix, { visualReview,
      requireVisualReview: args.includes('--require-visual-review') });
    for (const result of results) if (result.differences.length || result.tolerated.length) console.log(JSON.stringify(result));
    console.log(JSON.stringify({ files: results.length, differences: results.reduce((n, r) => n + r.differences.length, 0),
      tolerated: results.reduce((n, r) => n + r.tolerated.length, 0),
      screenshotsPendingReview: results.filter(result => result.screenshotReview.status === 'pending').length,
      screenshotReviewFailures: results.filter(result => result.screenshotReview.status === 'fail').length,
      automatedPixelComparison: false }));
    process.exitCode = results.some(result => result.differences.length || result.screenshotReview.status === 'fail') ? 1 : 0;
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
