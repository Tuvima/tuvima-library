import fs from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
const check = process.argv.includes('--check');
const pairs = [
  ['CONTRIBUTING.md','docs/develop/contributing.md','Contribute to Tuvima Library','Set up development, make a focused change and submit documentation, plugin or application improvements.','developer','guide'],
  ['SECURITY.md','docs/product/security.md','Report a security vulnerability','Report vulnerabilities privately and understand the current supported development line and response goals.','administrator','policy'],
];
for (const [source, target, title, description, audience, category] of pairs) {
  // Compare with LF endings so Windows checkouts (core.autocrlf) match the CI result.
  const original = (await fs.readFile(path.join(root, source), 'utf8')).replace(/\r\n/g, '\n');
  const body = original.replace(/\]\((CODE_OF_CONDUCT\.md|LICENSE|AGENTS\.md|SECURITY\.md)\)/g, '](../../$1)');
  const expected = `---\ntitle: ${JSON.stringify(title)}\ndescription: ${JSON.stringify(description)}\naudience: ${audience}\ncategory: ${category}\nproduct_area: project\nstatus: current\n---\n\n<!-- Generated from ${source} by scripts/docs/sync-community-docs.mjs. -->\n\n${body}`;
  const destination = path.join(root, target);
  if (check) {
    if ((await fs.readFile(destination, 'utf8')).replace(/\r\n/g, '\n') !== expected) throw new Error(`${target} is out of sync with ${source}`);
  } else {
    await fs.mkdir(path.dirname(destination), { recursive: true });
    await fs.writeFile(destination, expected);
  }
}
console.log(check ? 'Community policy copies match.' : 'Community policy copies regenerated.');
