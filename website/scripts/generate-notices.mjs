import fs from 'node:fs/promises';
import path from 'node:path';

const packages = [
  'astro', '@astrojs/internal-helpers', '@astrojs/starlight',
  'starlight-sidebar-topics', 'pagefind', '@pagefind/default-ui',
  'bcp-47', 'is-alphabetical', 'is-alphanumerical', 'is-decimal',
  'expressive-code', 'astro-expressive-code', 'rehype-expressive-code',
  '@expressive-code/core', '@expressive-code/plugin-frames',
  '@expressive-code/plugin-shiki', '@expressive-code/plugin-text-markers',
  'shiki', '@shikijs/core', '@shikijs/types', '@shikijs/langs',
  '@shikijs/themes', '@shikijs/engine-javascript', '@shikijs/engine-oniguruma',
  '@shikijs/primitive', '@shikijs/vscode-textmate',
];

async function licenseFiles(directory, relative = '') {
  const found = [];
  for (const entry of await fs.readdir(path.join(directory, relative), { withFileTypes: true })) {
    const name = path.join(relative, entry.name);
    if (entry.isDirectory() && /^(licenses?|notices?)$/i.test(entry.name)) found.push(...await licenseFiles(directory, name));
    else if (entry.isFile() && (/^(licen[sc]e|copying|notice|copyright)(?:[.-].*)?$/i.test(entry.name) || /^(licenses?|notices?)[\\/]/i.test(name))) found.push(name);
  }
  return found.sort();
}

function fontCopyright(buffer) {
  let offset;
  for (let i = 0; i < buffer.readUInt16BE(4); i++) {
    const record = 12 + i * 16;
    if (buffer.toString('ascii', record, record + 4) === 'name') offset = buffer.readUInt32BE(record + 8);
  }
  if (offset === undefined) throw new Error('Bundled font has no name table');
  const start = offset + buffer.readUInt16BE(offset + 4);
  const notices = new Set();
  for (let i = 0; i < buffer.readUInt16BE(offset + 2); i++) {
    const record = offset + 6 + i * 12;
    if (buffer.readUInt16BE(record + 6) !== 0) continue;
    const platform = buffer.readUInt16BE(record);
    const length = buffer.readUInt16BE(record + 8);
    const begin = start + buffer.readUInt16BE(record + 10);
    const value = Buffer.from(buffer.subarray(begin, begin + length));
    notices.add(platform === 0 || platform === 3 ? value.swap16().toString('utf16le') : value.toString('latin1'));
  }
  if (!notices.size) throw new Error('Bundled font has no copyright record');
  return [...notices].join('\n');
}

/** Emit complete upstream notices with no network access or invented copyright. */
export async function generateNotices(websiteRoot, outputDirectory) {
  const notices = ['Tuvima Library documentation: third-party notices',
    'These upstream notices cover the documentation framework, browser search/UI and syntax-rendering dependencies. Some listed tools run only during the build. Original license/copyright text follows verbatim.'];
  for (const name of packages) {
    const directory = path.join(websiteRoot, 'node_modules', name);
    const metadata = JSON.parse(await fs.readFile(path.join(directory, 'package.json'), 'utf8'));
    const files = await licenseFiles(directory);
    notices.push(`\n===== ${name} ${metadata.version} (${metadata.license}) =====`);
    if (!files.length && name === '@pagefind/default-ui') {
      // The UI package omits a license file; it is part of the same Pagefind
      // repository, author and MIT grant. Preserve Pagefind's full notice.
      notices.push('Source: Pagefind repository MIT license; default-ui package metadata declares MIT and author Pagefind.');
      notices.push(await fs.readFile(path.join(websiteRoot, 'node_modules/pagefind/LICENSE/LICENSE'), 'utf8'));
    } else if (!files.length) throw new Error(`${name}: no upstream license notice found`);
    for (const file of files) notices.push(`\n--- ${name}/${file.replaceAll('\\', '/')} ---\n${await fs.readFile(path.join(directory, file), 'utf8')}`);
  }
  notices.push('\n===== Svelte runtime bundled in Pagefind Default UI (MIT) =====',
    'Source: original npm svelte@4.2.1 package LICENSE.md, retained because default-ui bundles Svelte 4 runtime code.',
    await fs.readFile(path.join(websiteRoot, 'scripts/licenses/Svelte-MIT.txt'), 'utf8'));
  for (const [font, license] of [['Montserrat-VariableFont_wght.ttf', 'Montserrat-OFL.txt'], ['JetBrainsMono-Regular.ttf', 'JetBrainsMono-OFL.txt']]) {
    notices.push(`\n===== ${font} (SIL OFL 1.1) =====`,
      `Copyright embedded in the actual bundled font:\n${fontCopyright(await fs.readFile(path.join(websiteRoot, 'src/fonts', font)))}`,
      await fs.readFile(path.join(websiteRoot, 'src/fonts', license), 'utf8'));
  }
  await fs.mkdir(outputDirectory, { recursive: true });
  await fs.writeFile(path.join(outputDirectory, 'THIRD-PARTY.txt'), `${notices.join('\n\n')}\n`);
  return { packages: packages.length, fonts: 2 };
}
