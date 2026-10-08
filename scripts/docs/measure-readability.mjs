/**
 * Ad hoc acceptance check; does not add a documentation build dependency.
 * Install the pinned MIT CLI first:
 * npm install --prefix .tmp/docs-overhaul/readability-tools --no-save --ignore-scripts textlens@1.0.11
 * node scripts/docs/measure-readability.mjs
 */
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';

const repo = fileURLToPath(new URL('../../', import.meta.url));
const requireWebsite = createRequire(new URL('../../website/package.json', import.meta.url));
const YAML = requireWebsite('yaml');
const { load } = requireWebsite('cheerio');
const { createMarkdownProcessor } = await import(pathToFileURL(requireWebsite.resolve('@astrojs/markdown-remark')).href);
const cliRoot = path.join(repo, '.tmp/docs-overhaul/readability-tools/node_modules/textlens');
const cliMetadata = JSON.parse(await fs.readFile(path.join(cliRoot, 'package.json'), 'utf8'));
if (cliMetadata.version !== '1.0.11' || cliMetadata.license !== 'MIT') throw new Error('Install the pinned MIT textlens@1.0.11 CLI before measuring readability.');
const output = path.join(repo, '.tmp/docs-overhaul/readability-cli');
await fs.mkdir(output, { recursive: true });
const manifest = JSON.parse(await fs.readFile(path.join(repo, 'website/publication.json'), 'utf8'));
const renderer = await createMarkdownProcessor({ syntaxHighlight: false });
const results = [];

for (const page of manifest.pages) {
  if (!/^(guides|install|tutorials)\//.test(page.source)) continue;
  const source = path.join(repo, 'docs', page.source);
  const text = await fs.readFile(source, 'utf8');
  const frontmatter = text.match(/^\uFEFF?---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/);
  if (!frontmatter) throw new Error(`${page.source}: missing metadata`);
  const metadata = YAML.parse(frontmatter[1]);
  if (!['user', 'administrator'].includes(metadata.audience) || !['tutorial', 'installation', 'guide'].includes(metadata.category)) continue;
  if (page.source.endsWith('.mdx')) throw new Error(`${page.source}: MDX requires explicit rendered-prose extraction review`);
  const { code } = await renderer.render(text.slice(frontmatter[0].length), { fileURL: pathToFileURL(source) });
  const $ = load(code);
  // Closing link-only navigation is not instructional prose.
  $('h2, h3').each((_, element) => {
    if (/^(Next steps|Related|Related pages)$/i.test($(element).text().trim())) {
      $(element).nextUntil('h2').remove();
      $(element).remove();
    }
  });
  $('pre, code, table, nav, script, style, h1, h2, h3, h4, h5, h6, summary').remove();
  // Preserve actual paragraph/list boundaries; do not manufacture punctuation.
  $('p, li, blockquote').each((_, element) => $(element).append('\n'));
  const prose = $('body').text().replace(/[^\S\n]+/g, ' ').replace(/\n\s*\n+/g, '\n\n').trim();
  if (!prose) throw new Error(`${page.source}: no prose remains`);
  const input = path.join(output, page.source.replaceAll('/', '__').replace(/\.md$/, '.txt'));
  await fs.writeFile(input, `${prose}\n`);
  const invocation = spawnSync(process.execPath, [path.join(cliRoot, 'bin/textlens.js'), input, '--json'], { encoding: 'utf8', maxBuffer: 4 * 1024 * 1024 });
  if (invocation.status !== 0) throw new Error(`${page.source}: CLI failed: ${invocation.stderr}`);
  const raw = JSON.parse(invocation.stdout);
  await fs.writeFile(input.replace(/\.txt$/, '.json'), `${invocation.stdout.trim()}\n`);
  results.push({ source: page.source, sourceSha256: createHash('sha256').update(text).digest('hex'), proseSha256: createHash('sha256').update(prose).digest('hex'), audience: metadata.audience, category: metadata.category, words: raw.statistics.words, sentences: raw.statistics.sentences, syllables: raw.statistics.syllables, fleschKincaidGrade: raw.readability.fleschKincaidGrade.score });
}
results.sort((a, b) => a.source.localeCompare(b.source));
const average = results.reduce((sum, result) => sum + result.fleschKincaidGrade, 0) / results.length;
const totals = results.reduce((sum, result) => ({ words: sum.words + result.words, sentences: sum.sentences + result.sentences, syllables: sum.syllables + result.syllables }), { words: 0, sentences: 0, syllables: 0 });
const pooledGrade = 0.39 * (totals.words / totals.sentences) + 11.8 * (totals.syllables / totals.words) - 15.59;
const report = {
  tool: 'textlens', version: cliMetadata.version, license: cliMetadata.license, measuredAt: new Date().toISOString(),
  scope: 'Published user/administrator pages with category tutorial, installation, or guide.',
  extraction: 'Source Markdown rendered with Astro Markdown Remark; exclude code, tables, headings, summary labels, and closing Next steps/Related navigation. Include all remaining prose and procedural list text, including technical-details prose. Do not add punctuation.',
  limitations: ['English formula with rule-based syllable estimates; product names and technical terms can be miscounted.', 'Heading removal is a structural exclusion; required prose and security instructions remain included.', 'CLI --version has a stale v1.0.0 banner; installed package metadata and locked temporary install are 1.0.11.'],
  pages: results.length, pageAverage: Number(average.toFixed(2)), pooledGrade: Number(pooledGrade.toFixed(2)), targetAverage: 9, passed: average <= 9,
  results,
};
await fs.writeFile(path.join(output, 'report.json'), `${JSON.stringify(report, null, 2)}\n`);
await fs.writeFile(path.join(output, 'report.md'), `# Readability CLI acceptance\n\nTool: textlens ${report.version}, MIT. ${report.scope}\n\n${report.extraction}\n\n${results.length} pages; mean Flesch–Kincaid grade **${report.pageAverage}**; target mean ≤ 9: **${report.passed ? 'PASS' : 'FAIL'}**. Pooled corpus grade: ${report.pooledGrade}.\n\n| Page | Words | Sentences | Grade |\n| --- | ---: | ---: | ---: |\n${results.map(row => `| ${row.source} | ${row.words} | ${row.sentences} | ${row.fleschKincaidGrade} |`).join('\n')}\n\nLimitations: ${report.limitations.join(' ')}\n`);
console.log(JSON.stringify({ pages: report.pages, pageAverage: report.pageAverage, pooledGrade: report.pooledGrade, passed: report.passed, report: path.join(output, 'report.json') }, null, 2));
if (!report.passed) process.exitCode = 1;
