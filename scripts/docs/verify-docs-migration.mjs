import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { validateSite } from '../../website/scripts/validate-site.mjs';
import { base, origin } from '../../website/scripts/content-contract.mjs';

const root = path.resolve(fileURLToPath(new URL('../..', import.meta.url)));
const requireFromWebsite = createRequire(new URL('../../website/package.json', import.meta.url));
const { load } = requireFromWebsite('cheerio');
const expectedInventory = path.join(root, 'engineering', 'docs-overhaul');
const evidencePath = path.join(expectedInventory, 'migration-verification.json');
const expectedExceptionPath = 'docs/ui/ui-consistency-audit.md';
const YAML = requireFromWebsite('yaml');

function parseArgs(args) {
  const options = {
    sourceOnly: false,
    siteDir: path.join(root, 'website', 'dist'),
    protectedHashes: null,
  };
  for (let index = 0; index < args.length; index += 1) {
    const value = args[index];
    if (value === '--source-only') options.sourceOnly = true;
    else if (value === '--site-dir' || value === '--protected-hashes') {
      const next = args[index + 1];
      if (!next || next.startsWith('--')) throw new Error(`${value} requires a path`);
      options[value === '--site-dir' ? 'siteDir' : 'protectedHashes'] = path.resolve(root, next);
      index += 1;
    } else if (value === '--help' || value === '-h') options.help = true;
    else throw new Error(`Unknown option: ${value}`);
  }
  return options;
}

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

async function hashFile(filePath) {
  return sha256(await fs.readFile(filePath));
}

async function readJson(filePath, label) {
  try {
    return JSON.parse(await fs.readFile(filePath, 'utf8'));
  } catch (error) {
    throw new Error(`Could not read ${label} at ${path.relative(root, filePath) || filePath}: ${error.message}`);
  }
}

async function loadPreparedRoutes(publication) {
  const routePath = repoPath('website/.generated/routes.json');
  if (await exists(routePath)) return { records: await readJson(routePath, 'prepared website routes'), source: 'website/.generated/routes.json' };

  const pages = [];
  const sourceRoutes = {};
  for (const page of publication.pages) {
    const source = slash(page.source);
    const text = await fs.readFile(repoPath(`docs/${source}`), 'utf8');
    const match = text.match(/^\uFEFF?---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/);
    if (!match) throw new Error(`${source}: YAML front matter is required to rebuild route validation records`);
    const metadata = YAML.parse(match[1]);
    if (typeof metadata?.title !== 'string' || !metadata.title.trim()) throw new Error(`${source}: page title is missing from YAML front matter`);
    const route = `${base.replace(/\/+$/, '')}/${page.slug ? `${page.slug}/` : ''}`;
    sourceRoutes[source] = route;
    pages.push({ ...page, route, title: metadata.title });
  }
  return {
    records: { base: base.replace(/\/+$/, ''), pages, sourceRoutes, redirects: publication.redirects ?? {} },
    source: 'reconstructed from website/publication.json and docs/ front matter (prepared route file absent)',
  };
}

function repoPath(relativePath) {
  const normalized = String(relativePath).replaceAll('\\', '/');
  if (path.posix.isAbsolute(normalized) || normalized.split('/').includes('..')) {
    throw new Error(`Unsafe repository-relative path: ${relativePath}`);
  }
  const resolved = path.resolve(root, ...normalized.split('/'));
  const relative = path.relative(root, resolved);
  if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
    throw new Error(`Path escapes repository: ${relativePath}`);
  }
  return resolved;
}

function slash(value) {
  return String(value).replaceAll('\\', '/');
}

function addFailure(report, code, message, details = undefined) {
  report.failures.push({ code, message, ...(details === undefined ? {} : { details }) });
}

function requireCondition(report, condition, code, message, details = undefined) {
  if (!condition) addFailure(report, code, message, details);
}

async function exists(filePath) {
  try {
    await fs.access(filePath);
    return true;
  } catch {
    return false;
  }
}

async function listMarkdown(directory, prefix = '') {
  const output = [];
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) output.push(...await listMarkdown(full, relative));
    else if (entry.isFile() && /\.(?:md|mdx)$/i.test(entry.name)) output.push(relative.replaceAll('\\', '/'));
  }
  return output.sort();
}

async function listHtml(directory) {
  const output = [];
  async function visit(folder) {
    for (const entry of await fs.readdir(folder, { withFileTypes: true })) {
      const full = path.join(folder, entry.name);
      if (entry.isDirectory()) await visit(full);
      else if (entry.isFile() && entry.name.toLowerCase().endsWith('.html')) {
        output.push(path.relative(directory, full).replaceAll('\\', '/'));
      }
    }
  }
  await visit(directory);
  return output.sort();
}

function siteSuffix(rawUrl) {
  const url = new URL(rawUrl, origin);
  if (url.origin !== origin) throw new Error(`Route is outside the configured site origin: ${rawUrl}`);
  const prefix = base.replace(/\/+$/, '');
  if (url.pathname !== prefix && !url.pathname.startsWith(`${prefix}/`)) {
    throw new Error(`Route is outside the configured site base ${prefix}: ${rawUrl}`);
  }
  return url.pathname.slice(prefix.length) || '/';
}

function routeDiskPath(siteDir, rawUrl) {
  const url = new URL(rawUrl, origin);
  if (url.origin !== origin) throw new Error(`URL is outside the site origin: ${rawUrl}`);
  const prefix = base.replace(/\/+$/, '');
  if (url.pathname !== prefix && !url.pathname.startsWith(`${prefix}/`)) {
    throw new Error(`URL is outside the configured site base ${prefix}: ${rawUrl}`);
  }
  let relative = decodeURIComponent(url.pathname.slice(prefix.length)).replace(/^\/+/, '');
  if (!relative) relative = 'index.html';
  else if (relative.endsWith('/')) relative += 'index.html';
  const resolved = path.resolve(siteDir, ...relative.replaceAll('\\', '/').split('/'));
  const withinSite = path.relative(siteDir, resolved);
  if (withinSite === '..' || withinSite.startsWith(`..${path.sep}`) || path.isAbsolute(withinSite)) {
    throw new Error(`URL path escapes site output: ${rawUrl}`);
  }
  return resolved;
}

function pageOutputPath(page) {
  if (page.slug === '404') return '404.html';
  return page.slug ? `${page.slug.replaceAll('\\', '/')}/index.html` : 'index.html';
}

function redirectOutputPath(from) {
  return `${from.replace(/^\/+|\/+$/g, '').replaceAll('\\', '/')}/index.html`;
}

async function hasAnchor(filePath, id) {
  const $ = load(await fs.readFile(filePath, 'utf8'));
  const expected = decodeURIComponent(id);
  return $('[id], a[name]').toArray().some(element => $(element).attr('id') === expected || $(element).attr('name') === expected);
}

function localMarkdownTargets(readmeText) {
  const targets = [];
  for (const match of readmeText.matchAll(/!?\[[^\]]*\]\(\s*(<[^>]+>|[^)\s]+)(?:\s+[^)]*)?\)/g)) {
    const target = match[1].startsWith('<') && match[1].endsWith('>') ? match[1].slice(1, -1) : match[1];
    targets.push(target);
  }
  return targets;
}

async function checkProtectedHashes(report, options, manifest, authorization) {
  const ledger = await readJson(repoPath('engineering/docs-overhaul/reference-ledger.json'), 'reference ledger');
  const protectedRecords = options.protectedHashes
    ? await readJson(options.protectedHashes, 'protected hash override')
    : (ledger.protectedFiles ?? []).map(file => ({ path: file.path, sha256: file.sha256 }));
  requireCondition(report, Array.isArray(protectedRecords), 'protected-hash-format', 'Protected hash list must be a JSON array.');
  if (!Array.isArray(protectedRecords)) return;

  const publishByPath = new Map(manifest.files
    .filter(file => file.proposedAction === 'PUBLISH')
    .map(file => [slash(file.path), file]));
  const seen = new Set();
  const verified = [];
  for (const record of protectedRecords) {
    const rawPath = record.Path ?? record.path;
    const expectedHash = String(record.Hash ?? record.sha256 ?? '').toLowerCase();
    if (!rawPath || !/^[a-f0-9]{64}$/.test(expectedHash)) {
      addFailure(report, 'protected-hash-record', 'A protected hash record is missing a valid path or SHA-256 hash.', record);
      continue;
    }
    const normalizedRecordPath = slash(rawPath).toLowerCase();
    const matches = [...publishByPath.keys()].filter(candidate => normalizedRecordPath === candidate.toLowerCase() || normalizedRecordPath.endsWith(`/${candidate.toLowerCase()}`));
    if (matches.length !== 1) {
      addFailure(report, 'protected-hash-path', `Protected path does not uniquely match a PUBLISH manifest file: ${rawPath}`, { matches });
      continue;
    }
    const relativePath = matches[0];
    const manifestEntry = publishByPath.get(relativePath);
    seen.add(relativePath);
    requireCondition(report, manifestEntry.sha256.toLowerCase() === expectedHash, 'protected-hash-manifest', `Protected hash disagrees with the immutable file manifest: ${relativePath}`, { protectedHash: expectedHash, manifestHash: manifestEntry.sha256 });
    const actualHash = await hashFile(repoPath(relativePath));
    requireCondition(report, actualHash === expectedHash, 'protected-hash-source', `Protected source hash changed: ${relativePath}`, { expected: expectedHash, actual: actualHash });
    verified.push({ path: relativePath, sha256: actualHash });
  }

  const ledgerProtected = new Map((ledger.protectedFiles ?? []).map(file => [slash(file.path), String(file.sha256).toLowerCase()]));
  requireCondition(report, protectedRecords.length === ledgerProtected.size, 'protected-hash-count', 'Protected hash list and immutable reference ledger have different file counts.', { list: protectedRecords.length, ledger: ledgerProtected.size });
  for (const [relativePath, hash] of ledgerProtected) {
    requireCondition(report, seen.has(relativePath), 'protected-hash-coverage', `Protected file is missing from the local hash list: ${relativePath}`);
    const record = protectedRecords.find(item => {
      const candidate = slash(item.Path ?? item.path ?? '').toLowerCase();
      return candidate === relativePath.toLowerCase() || candidate.endsWith(`/${relativePath.toLowerCase()}`);
    });
    if (record) requireCondition(report, String(record.Hash ?? record.sha256 ?? '').toLowerCase() === hash, 'protected-hash-ledger', `Protected hash list disagrees with the reference ledger: ${relativePath}`, { expected: hash, actual: record.Hash ?? record.sha256 });
  }
  report.checks.protectedHashes = { source: options.protectedHashes ? path.relative(root, options.protectedHashes) : 'engineering/docs-overhaul/reference-ledger.json#protectedFiles', files: verified.length, verified };
}

async function checkCleanup(report, manifest, authorization, execution, exception) {
  const actualCounts = {};
  for (const file of manifest.files) {
    const action = file.proposedAction;
    actualCounts[action] ??= { files: 0, bytes: 0 };
    actualCounts[action].files += 1;
    actualCounts[action].bytes += file.bytes;
  }
  requireCondition(report, manifest.files.length === 846, 'manifest-file-count', 'The frozen cleanup manifest no longer has 846 file records.', { actual: manifest.files.length });
  for (const [action, expected] of Object.entries(authorization.counts)) {
    requireCondition(report, actualCounts[action]?.files === expected.files && actualCounts[action]?.bytes === expected.bytes, 'manifest-cleanup-totals', `Manifest cleanup totals changed for ${action}.`, { expected, actual: actualCounts[action] });
    requireCondition(report, manifest.counts[action]?.files === expected.files && manifest.counts[action]?.bytes === expected.bytes, 'manifest-counts-block', `Manifest counts block changed for ${action}.`, { expected, actual: manifest.counts[action] });
  }

  const expectedMoved = manifest.files.filter(file => file.proposedAction === 'KEEP-MOVE');
  const expectedDeleted = manifest.files.filter(file => file.proposedAction === 'DELETE');
  const movedByPath = new Map((execution.moved ?? []).map(file => [slash(file.path), file]));
  const deletedByPath = new Map((execution.deleted ?? []).map(file => [slash(file.path), file]));
  requireCondition(report, execution.results?.movedFiles === expectedMoved.length && movedByPath.size === expectedMoved.length, 'execution-move-count', 'Cleanup execution move record does not match the immutable manifest.', { expected: expectedMoved.length, resultCount: execution.results?.movedFiles, records: movedByPath.size });
  requireCondition(report, execution.results?.deletedFiles === expectedDeleted.length && deletedByPath.size === expectedDeleted.length, 'execution-delete-count', 'Cleanup execution delete record does not match the immutable manifest.', { expected: expectedDeleted.length, resultCount: execution.results?.deletedFiles, records: deletedByPath.size });
  requireCondition(report, execution.sourceHashValidation?.keepMoveMatched === expectedMoved.length && execution.sourceHashValidation?.deleteMatched === expectedDeleted.length && execution.sourceHashValidation?.mismatches?.length === 0, 'execution-source-hashes', 'Cleanup execution does not show a clean source-hash validation.');

  const readmeRecord = execution.results?.createdReadme;
  const readmePath = repoPath('engineering/README.md');
  if (readmeRecord) {
    const readmeExists = await exists(readmePath);
    requireCondition(report, readmeExists, 'engineering-readme-missing', 'engineering/README.md is missing.');
    if (readmeExists) {
      const actualReadmeHash = await hashFile(readmePath);
      requireCondition(report, actualReadmeHash === String(readmeRecord.sha256).toLowerCase(), 'engineering-readme-hash', 'engineering/README.md differs from its cleanup execution record.', { expected: readmeRecord.sha256, actual: actualReadmeHash });
    }
  }

  const exceptionPath = slash(exception.path);
  const exceptionEntry = expectedMoved.find(file => slash(file.path) === exceptionPath);
  requireCondition(report, Boolean(exceptionEntry), 'ui-exception-manifest-entry', `The preserved docs/ui exception is absent from the manifest move list: ${exceptionPath}`);
  const movedDetails = { moved: 0, retainedSourceExceptions: 0, deleted: 0, screenshots: 0, generatedQaLogs: 0 };
  const moveMismatches = [];
  for (const entry of expectedMoved) {
    const source = repoPath(entry.path);
    const destination = repoPath(entry.destination);
    const recorded = movedByPath.get(slash(entry.path));
    requireCondition(report, Boolean(recorded && slash(recorded.destination) === slash(entry.destination) && recorded.originalSha256 === entry.sha256), 'execution-move-record', `Move execution record does not match the manifest: ${entry.path}`);
    if (slash(entry.path) === exceptionPath) {
      movedDetails.retainedSourceExceptions += 1;
      const sourceExists = await exists(source);
      requireCondition(report, sourceExists, 'ui-exception-source', 'The explicitly preserved docs/ui source is missing.', { source: entry.path });
      if (sourceExists) {
        const sourceHash = await hashFile(source);
        requireCondition(report, sourceHash === entry.sha256.toLowerCase(), 'ui-exception-source-hash', 'The preserved docs/ui source no longer matches its manifest bytes.', { expected: entry.sha256, actual: sourceHash });
      }
      const pointerExists = await exists(destination);
      requireCondition(report, pointerExists, 'ui-exception-pointer', 'The engineering pointer for the preserved docs/ui source is missing.', { destination: entry.destination });
      if (pointerExists) {
        const pointerText = await fs.readFile(destination, 'utf8');
        const expectedLink = '../../docs/ui/ui-consistency-audit.md';
        requireCondition(report, pointerText.includes(expectedLink) && pointerText.toLowerCase().includes('excluded from the documentation site'), 'ui-exception-pointer-content', 'The engineering pointer must link to the retained docs/ui source and state that it is unpublished.', { destination: entry.destination });
        requireCondition(report, await exists(path.resolve(path.dirname(destination), expectedLink)), 'ui-exception-pointer-target', 'The engineering pointer target does not resolve to the retained docs/ui source.');
      }
      continue;
    }

    const sourceExists = await exists(source);
    const destinationExists = await exists(destination);
    requireCondition(report, !sourceExists, 'move-source-remains', `Moved source still exists: ${entry.path}`);
    requireCondition(report, destinationExists, 'move-destination-missing', `Moved destination is missing: ${entry.destination}`);
    if (sourceExists || !destinationExists) continue;
    const actualHash = await hashFile(destination);
    if (recorded && actualHash !== String(recorded.finalSha256).toLowerCase()) {
      moveMismatches.push({ path: entry.path, expected: recorded.finalSha256, actual: actualHash });
    }
    movedDetails.moved += 1;
  }
  requireCondition(report, moveMismatches.length === 0, 'moved-destination-hash', 'Moved file bytes differ from the recorded cleanup execution hash.', moveMismatches);

  const deleteMismatches = [];
  for (const entry of expectedDeleted) {
    const source = repoPath(entry.path);
    const recorded = deletedByPath.get(slash(entry.path));
    requireCondition(report, Boolean(recorded && recorded.originalSha256 === entry.sha256 && recorded.purpose === entry.purpose), 'execution-delete-record', `Delete execution record does not match the manifest: ${entry.path}`);
    if (await exists(source)) deleteMismatches.push(entry.path);
    if (entry.purpose === 'screenshot') movedDetails.screenshots += 1;
    else if (entry.purpose === 'qa-log') movedDetails.generatedQaLogs += 1;
  }
  requireCondition(report, deleteMismatches.length === 0, 'deleted-source-remains', 'Files listed for deletion still exist at their original paths.', deleteMismatches.slice(0, 30));
  movedDetails.deleted = expectedDeleted.length - deleteMismatches.length;
  requireCondition(report, movedDetails.screenshots === 358 && movedDetails.generatedQaLogs === 10, 'cleanup-delete-purpose-counts', 'Screenshot and generated-log cleanup totals differ from the authorized counts.', movedDetails);

  const expectedImmutable = execution.results?.immutableInventorySha256 ?? [];
  const inventoryMismatches = [];
  for (const record of expectedImmutable) {
    const actualPath = repoPath(record.path);
    if (!(await exists(actualPath)) || await hashFile(actualPath) !== String(record.sha256).toLowerCase()) inventoryMismatches.push(record.path);
  }
  requireCondition(report, expectedImmutable.length === 12 && inventoryMismatches.length === 0, 'inventory-immutable-hashes', 'Frozen docs-overhaul inventory files changed after cleanup.', { expectedFiles: 12, records: expectedImmutable.length, mismatches: inventoryMismatches });
  report.checks.cleanup = {
    manifestFiles: manifest.files.length,
    actionTotals: actualCounts,
    executedMoves: expectedMoved.length,
    finalMovedSources: movedDetails.moved,
    retainedSourceExceptions: movedDetails.retainedSourceExceptions,
    deletions: movedDetails.deleted,
    deletedScreenshots: movedDetails.screenshots,
    deletedGeneratedQaLogs: movedDetails.generatedQaLogs,
    immutableInventoryFiles: expectedImmutable.length,
    docsUiException: { source: exceptionPath, action: exception.action, publication: exception.publication, engineeringPointer: slash(exception.engineeringPointer) },
  };
}

async function checkPublication(report, publication, generatedRoutes, exception) {
  const publishedSources = new Set(publication.pages.map(page => slash(page.source)));
  const generatedSources = new Set((generatedRoutes.pages ?? []).map(page => slash(page.source)));
  const generatedContentRoot = repoPath('website/.generated/content');
  const generatedContent = new Set(await listMarkdown(generatedContentRoot));
  const docsRoot = repoPath('docs');
  const docsSources = new Set(await listMarkdown(docsRoot));
  const exceptionSource = slash(exception.path).replace(/^docs\//, '');

  const missingSources = [...publishedSources].filter(source => !docsSources.has(source));
  const generatedMissing = [...publishedSources].filter(source => !generatedSources.has(source) || !generatedContent.has(source));
  const generatedOrphans = [...generatedSources].filter(source => !publishedSources.has(source));
  const docsOrphans = [...docsSources].filter(source => !publishedSources.has(source) && source !== exceptionSource);
  const exceptionPublished = publishedSources.has(exceptionSource);

  requireCondition(report, publication.pages.length === 99, 'publication-page-count', 'The publication manifest should contain 99 pages.', { actual: publication.pages.length });
  requireCondition(report, missingSources.length === 0, 'publication-source-missing', 'Published pages are missing from docs/.', missingSources);
  requireCondition(report, generatedMissing.length === 0, 'generated-page-missing', 'Published pages are missing from the prepared content or generated route list.', generatedMissing);
  requireCondition(report, generatedOrphans.length === 0, 'generated-content-orphan', 'Prepared website content includes pages absent from publication.json.', generatedOrphans);
  requireCondition(report, docsOrphans.length === 0, 'docs-source-orphan', 'docs/ contains Markdown pages absent from publication.json and explicit exceptions.', docsOrphans);
  requireCondition(report, !exceptionPublished && docsSources.has(exceptionSource), 'docs-ui-publication-exception', 'The explicit docs/ui source exception must remain in docs/ but excluded from publication.json.', { exceptionSource, published: exceptionPublished, present: docsSources.has(exceptionSource) });
  requireCondition(report, JSON.stringify(Object.entries(generatedRoutes.redirects ?? {}).sort()) === JSON.stringify(Object.entries(publication.redirects ?? {}).sort()), 'generated-redirect-config', 'Prepared redirect records do not match website/publication.json.');

  const staleIndexSource = await exists(repoPath('docs/index.md'));
  const staleGeneratedIndex = await exists(repoPath('website/.generated/content/index.md'));
  requireCondition(report, !staleIndexSource && !staleGeneratedIndex, 'stale-index-md', 'A stale root index.md remains after migration; docs/index.mdx is the published source.', { docsIndexMd: staleIndexSource, generatedIndexMd: staleGeneratedIndex });
  requireCondition(report, publishedSources.has('index.mdx') && generatedContent.has('index.mdx'), 'index-mdx-source', 'The published root page must come from index.mdx.');

  report.checks.publication = {
    pages: publication.pages.length,
    generatedRoutes: generatedSources.size,
    preparedContentFiles: generatedContent.size,
    docsMarkdownSources: docsSources.size,
    missingPublishedSources: missingSources,
    generatedOrphans,
    docsOrphans,
    explicitUnpublishedException: exceptionSource,
    rootIndexSource: 'index.mdx',
    staleRootIndexMd: false,
  };
}

async function checkRouteDecisions(report, routeManifest, finalDecisions, publication, generatedRoutes, siteDir) {
  const baselineRoutes = routeManifest.routes ?? [];
  const routes = finalDecisions.routes ?? [];
  const baselineByUrl = new Map(baselineRoutes.map(route => [route.oldUrl, route]));
  const finalByUrl = new Map(routes.map(route => [route.oldUrl, route]));
  const generatedPageRoutes = new Set((generatedRoutes.pages ?? []).map(page => new URL(page.route, origin).pathname));
  const redirects = publication.redirects ?? {};
  const routeProblems = [];
  const knownFragments = [];
  const htmlFiles = await listHtml(siteDir);
  const parsedHtml = new Map();
  async function getHtml(relative) {
    if (!parsedHtml.has(relative)) parsedHtml.set(relative, load(await fs.readFile(path.join(siteDir, ...relative.split('/')), 'utf8')));
    return parsedHtml.get(relative);
  }

  requireCondition(report, baselineRoutes.length === 186 && routes.length === 186, 'old-route-count', 'The immutable old-route manifest and final route decisions must each retain 186 routes.', { baseline: baselineRoutes.length, final: routes.length });
  requireCondition(report, finalByUrl.size === routes.length, 'final-route-unique', 'Final route decisions contain duplicate old URLs.');
  requireCondition(report, finalDecisions.pages === publication.pages.length && finalDecisions.redirects === Object.keys(redirects).length, 'final-route-counts', 'Final route-decision totals disagree with publication.json.', { decisions: { pages: finalDecisions.pages, redirects: finalDecisions.redirects }, publication: { pages: publication.pages.length, redirects: Object.keys(redirects).length } });
  const finalDispositionCounts = routes.reduce((counts, route) => { counts[route.disposition] = (counts[route.disposition] ?? 0) + 1; return counts; }, {});
  requireCondition(report, finalDispositionCounts.redirect === finalDecisions.redirects, 'final-redirect-count', 'Final route-decision redirect count disagrees with its redirect dispositions.', { header: finalDecisions.redirects, dispositions: finalDispositionCounts });
  const missingFinalRoutes = baselineRoutes.filter(route => !finalByUrl.has(route.oldUrl)).map(route => route.oldUrl);
  const extraFinalRoutes = routes.filter(route => !baselineByUrl.has(route.oldUrl)).map(route => route.oldUrl);
  requireCondition(report, missingFinalRoutes.length === 0 && extraFinalRoutes.length === 0, 'final-route-coverage', 'Final route decisions must cover exactly the immutable old-route inventory.', { missing: missingFinalRoutes, extra: extraFinalRoutes });
  const knownBaselineFragments = baselineRoutes.flatMap(route => (route.relevantFragments ?? []).filter(item => item.inboundKnown).map(item => `${route.oldUrl}#${item.id}`)).sort();
  const knownFinalFragments = routes.flatMap(route => (route.fragments ?? []).map(id => `${route.oldUrl}#${id}`)).sort();
  requireCondition(report, JSON.stringify(knownFinalFragments) === JSON.stringify(knownBaselineFragments), 'final-known-fragment-coverage', 'Final route decisions must preserve the exact known inbound-fragment inventory.', { baseline: knownBaselineFragments, final: knownFinalFragments });

  const decisionCounts = {};
  for (const route of routes) {
    const baseline = baselineByUrl.get(route.oldUrl);
    const countKey = `${baseline?.finalStatus ?? 'unknown'}:${route.disposition}`;
    decisionCounts[countKey] = (decisionCounts[countKey] ?? 0) + 1;
    if (!baseline || route.source !== baseline.oldPath) {
      routeProblems.push({ oldUrl: route.oldUrl, issue: 'final decision does not identify its immutable source path', expectedSource: baseline?.oldPath ?? null, actualSource: route.source ?? null });
    }
    const oldSuffix = siteSuffix(route.oldUrl);
    const redirectTarget = redirects[oldSuffix];
    const oldOutput = routeDiskPath(siteDir, route.oldUrl);
    const oldHtmlOutput = baseline?.oldHtmlUrl ? routeDiskPath(siteDir, baseline.oldHtmlUrl) : oldOutput;

    if (route.disposition === 'preserve') {
      if (route.target !== null) routeProblems.push({ oldUrl: route.oldUrl, issue: 'preserved route unexpectedly declares a redirect target', target: route.target });
      if (redirectTarget) routeProblems.push({ oldUrl: route.oldUrl, issue: 'preserved route is redirected in publication.json', actualTarget: redirectTarget });
      if (!(await exists(oldOutput))) routeProblems.push({ oldUrl: route.oldUrl, issue: 'preserved route output is missing' });
      if (!generatedPageRoutes.has(new URL(route.oldUrl, origin).pathname) && !route.oldUrl.endsWith('/404.html')) {
        routeProblems.push({ oldUrl: route.oldUrl, issue: 'preserved route is not a generated page route' });
      }
    } else if (route.disposition === 'redirect') {
      if (!route.target) routeProblems.push({ oldUrl: route.oldUrl, issue: 'redirect decision has no target' });
      else {
        const expectedTarget = siteSuffix(route.target);
        if (redirectTarget !== expectedTarget) routeProblems.push({ oldUrl: route.oldUrl, issue: 'configured redirect does not match final route decision', expectedTarget, actualTarget: redirectTarget ?? null });
        const targetOutput = routeDiskPath(siteDir, route.target);
        if (!(await exists(oldOutput))) routeProblems.push({ oldUrl: route.oldUrl, issue: 'redirect output page is missing' });
        if (!(await exists(targetOutput))) routeProblems.push({ oldUrl: route.oldUrl, issue: 'redirect target output is missing', target: route.target });
        if (!generatedPageRoutes.has(new URL(route.target, origin).pathname) && !route.target.endsWith('/404.html')) {
          routeProblems.push({ oldUrl: route.oldUrl, issue: 'redirect target is not a generated page route', target: route.target });
        }
      }
    } else if (route.disposition === 'retire') {
      if (route.target !== null) routeProblems.push({ oldUrl: route.oldUrl, issue: 'retired route unexpectedly declares a redirect target', target: route.target });
      if (redirectTarget) routeProblems.push({ oldUrl: route.oldUrl, issue: 'retired route still has a configured redirect', actualTarget: redirectTarget });
      if (await exists(oldOutput) || await exists(oldHtmlOutput)) routeProblems.push({ oldUrl: route.oldUrl, issue: 'retired route still has an output page' });
    } else {
      routeProblems.push({ oldUrl: route.oldUrl, issue: 'unknown final route disposition', disposition: route.disposition });
    }

    for (const fragment of route.fragments ?? []) {
      if (route.disposition !== 'retire') {
        const destinationUrl = route.disposition === 'redirect' ? route.target : route.oldUrl;
        const destination = destinationUrl ? routeDiskPath(siteDir, destinationUrl) : null;
        if (!destination || !(await exists(destination)) || !(await hasAnchor(destination, fragment))) {
          routeProblems.push({ oldUrl: route.oldUrl, issue: 'known inbound fragment is missing from its final route target', fragment, destinationUrl });
        }
        knownFragments.push({ oldUrl: route.oldUrl, fragment, outcome: 'published-anchor-verified' });
      } else {
        let liveIncomingLink = false;
        const candidatePaths = new Set([route.oldUrl, baseline?.oldHtmlUrl].filter(Boolean).map(value => new URL(value, origin).pathname));
        for (const relative of htmlFiles) {
          const $ = await getHtml(relative);
          for (const element of $('a[href], area[href]').toArray()) {
            const href = $(element).attr('href');
            if (!href) continue;
            try {
              const url = new URL(href, origin);
              if (url.origin === origin && candidatePaths.has(url.pathname) && decodeURIComponent(url.hash.slice(1)) === fragment.id) liveIncomingLink = true;
            } catch {}
          }
          if (liveIncomingLink) break;
        }
        if (liveIncomingLink) routeProblems.push({ oldUrl: route.oldUrl, issue: 'published site still links to a known fragment on a retired route', fragment });
        knownFragments.push({ oldUrl: route.oldUrl, fragment, outcome: liveIncomingLink ? 'live-link-to-retired-anchor' : 'retired-anchor-has-no-published-inbound-link' });
      }
    }
  }

  requireCondition(report, routeProblems.length === 0, 'route-decision-output', 'One or more old-route decisions do not match the built website output.', routeProblems);

  const expectedHtml = new Set(['404.html']);
  for (const page of publication.pages) expectedHtml.add(pageOutputPath(page));
  for (const from of Object.keys(redirects)) expectedHtml.add(redirectOutputPath(from));
  const actualHtml = new Set(htmlFiles);
  const missingHtml = [...expectedHtml].filter(file => !actualHtml.has(file));
  const orphanHtml = [...actualHtml].filter(file => !expectedHtml.has(file));
  requireCondition(report, missingHtml.length === 0, 'built-page-missing', 'Expected published pages or redirects are absent from website/dist.', missingHtml);
  requireCondition(report, orphanHtml.length === 0, 'built-html-orphan', 'website/dist contains HTML pages absent from publication.json and its redirect table.', orphanHtml);
  report.checks.routes = {
    decisions: routes.length,
    decisionCounts,
    finalDispositionCounts: routes.reduce((counts, route) => { counts[route.disposition] = (counts[route.disposition] ?? 0) + 1; return counts; }, {}),
    builtPageCount: publication.pages.length,
    redirectCount: Object.keys(redirects).length,
    builtHtmlCount: htmlFiles.length,
    knownInboundFragments: knownFragments.length,
    knownFragmentResults: knownFragments,
    missingHtml,
    orphanHtml,
  };
}

async function checkReadmeLinks(report, siteDir) {
  const text = await fs.readFile(repoPath('README.md'), 'utf8');
  const docsLinks = localMarkdownTargets(text).filter(target => {
    try {
      const url = new URL(target, origin);
      return url.origin === origin && (url.pathname === base || url.pathname.startsWith(`${base.replace(/\/+$/, '')}/`));
    } catch {
      return false;
    }
  });
  const failures = [];
  for (const target of docsLinks) {
    const url = new URL(target, origin);
    const output = routeDiskPath(siteDir, url.href);
    if (!(await exists(output))) {
      failures.push({ target, output: path.relative(siteDir, output) });
      continue;
    }
    if (url.hash && output.endsWith('.html') && !(await hasAnchor(output, url.hash.slice(1))) && !url.hash.startsWith('#:~:text=')) {
      failures.push({ target, output: path.relative(siteDir, output), issue: 'missing fragment' });
    }
  }
  requireCondition(report, failures.length === 0, 'readme-doc-link', 'README.md contains documentation links that do not resolve in website/dist.', failures);
  report.checks.readmeLinks = { docsLinks: docsLinks.length, broken: failures };
}

async function checkRuntimeDiff(report) {
  let status = '';
  try {
    status = execFileSync('git', ['status', '--porcelain=v1', '--untracked-files=all', '--', 'src', 'config', 'tests', 'test'], { cwd: root, encoding: 'utf8' });
  } catch (error) {
    addFailure(report, 'git-status', `Could not inspect runtime/config/test changes: ${error.message}`);
    return;
  }
  const changedPaths = status.split(/\r?\n/).filter(Boolean);
  requireCondition(report, changedPaths.length === 0, 'runtime-config-test-diff', 'Unexpected changes exist under src/, config/, or test directories.', changedPaths);
  report.checks.runtimeConfigTests = { changedPaths };
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    console.log('Usage: node scripts/docs/verify-docs-migration.mjs [--source-only] [--site-dir PATH] [--protected-hashes PATH]');
    console.log('Default mode checks the freshly built website/dist and writes engineering/docs-overhaul/migration-verification.json.');
    return 0;
  }

  const report = {
    schemaVersion: 1,
    recordedAt: new Date().toISOString(),
    repositoryRoot: root,
    mode: options.sourceOnly ? 'source-only' : 'full-site',
    siteDir: path.relative(root, options.siteDir),
    failures: [],
    checks: {},
  };

  try {
    const authorization = await readJson(repoPath('engineering/docs-overhaul/authorization.json'), 'cleanup authorization');
    const manifestPath = repoPath('engineering/docs-overhaul/file-manifest.json');
    const routeManifestPath = repoPath('engineering/docs-overhaul/route-manifest.json');
    const manifest = await readJson(manifestPath, 'immutable cleanup manifest');
    const routeManifest = await readJson(routeManifestPath, 'immutable route manifest');
    const finalRouteDecisions = await readJson(repoPath('engineering/docs-overhaul/route-decisions.json'), 'final route decisions');
    const execution = await readJson(repoPath('engineering/docs-overhaul/cleanup-execution.json'), 'cleanup execution record');
    const exception = await readJson(repoPath('engineering/docs-overhaul/integration-exceptions.json'), 'migration integration exceptions');
    const publication = await readJson(repoPath('website/publication.json'), 'website publication manifest');
    const preparedRoutes = await loadPreparedRoutes(publication);
    const generatedRoutes = preparedRoutes.records;

    const manifestHash = await hashFile(manifestPath);
    const routeManifestHash = await hashFile(routeManifestPath);
    requireCondition(report, manifestHash === authorization.appliesToManifestSha256, 'manifest-authorization-hash', 'The file manifest does not match its authorization hash.', { expected: authorization.appliesToManifestSha256, actual: manifestHash });
    requireCondition(report, routeManifestHash === authorization.appliesToRouteManifestSha256, 'route-authorization-hash', 'The route manifest does not match its authorization hash.', { expected: authorization.appliesToRouteManifestSha256, actual: routeManifestHash });
    requireCondition(report, generatedRoutes.base === base.replace(/\/+$/, '') && publication.base === undefined, 'site-base', 'Prepared routes use an unexpected base path.', { generated: generatedRoutes.base, expected: base });
    report.checks.frozenManifests = { fileManifestSha256: manifestHash, routeManifestSha256: routeManifestHash, authorizationHashesMatch: manifestHash === authorization.appliesToManifestSha256 && routeManifestHash === authorization.appliesToRouteManifestSha256 };
    report.checks.preparedRoutes = { source: preparedRoutes.source, pages: generatedRoutes.pages.length, redirects: Object.keys(generatedRoutes.redirects ?? {}).length };

    requireCondition(report, exception.path === expectedExceptionPath && exception.action === 'KEEP-IN-PLACE' && exception.publication === 'excluded' && exception.engineeringPointer === 'engineering/ui/ui-consistency-audit.md', 'integration-exception-shape', 'The single docs/ui preservation exception changed unexpectedly.', exception);
    await checkProtectedHashes(report, options, manifest, authorization);
    await checkCleanup(report, manifest, authorization, execution, exception);
    await checkRuntimeDiff(report);

    const generatedContentRoot = repoPath('website/.generated/content');
    requireCondition(report, await exists(generatedContentRoot), 'generated-content-missing', 'Prepared website content is missing; run the website build first.');
    await checkPublication(report, publication, generatedRoutes, exception);

    if (options.sourceOnly) {
      report.checks.siteOutput = { status: 'deferred', reason: 'Run the default command after the website build to verify website/dist routes, links, anchors, and README links.' };
    } else if (!(await exists(options.siteDir))) {
      addFailure(report, 'site-output-missing', `Built site output is missing at ${options.siteDir}; run npm run build in website/ or use --source-only for source checks.`);
      report.checks.siteOutput = { status: 'missing', path: options.siteDir };
    } else {
      const siteValidation = await validateSite(options.siteDir, generatedRoutes);
      await checkRouteDecisions(report, routeManifest, finalRouteDecisions, publication, generatedRoutes, options.siteDir);
      await checkReadmeLinks(report, options.siteDir);
      report.checks.siteOutput = { status: 'passed', path: path.relative(root, options.siteDir), validation: siteValidation };
    }

    report.status = report.failures.length ? 'failed' : (options.sourceOnly ? 'source-checks-passed' : 'passed');
    report.summary = {
      publicationPages: publication.pages.length,
      oldRouteDecisions: routeManifest.routes.length,
      cleanupFiles: manifest.files.length,
      failureCount: report.failures.length,
    };
  } catch (error) {
    addFailure(report, 'verifier-error', error.stack ?? error.message);
    report.status = 'failed';
  }

  try {
    await fs.mkdir(path.dirname(evidencePath), { recursive: true });
    await fs.writeFile(evidencePath, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
  } catch (error) {
    console.error(`Could not write migration verification evidence: ${error.message}`);
    return 1;
  }

  console.log(JSON.stringify({ status: report.status, evidence: path.relative(root, evidencePath), summary: report.summary, failures: report.failures }, null, 2));
  return report.failures.length ? 1 : 0;
}

const exitCode = await main();
process.exitCode = exitCode;
