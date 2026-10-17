import test from 'node:test';
import assert from 'node:assert/strict';
import { findStaleUrls } from './check-project-urls.mjs';

const contract = { base: '/tuvima-library', origin: 'https://tuvima.github.io', repoOwner: 'Tuvima', repoName: 'tuvima-library' };

test('current site and repository addresses pass', () => {
  const text = 'See https://tuvima.github.io/tuvima-library/install/docker/ and https://github.com/Tuvima/tuvima-library/issues or https://raw.githubusercontent.com/Tuvima/tuvima-library/main/docker-compose.yml and git clone https://github.com/Tuvima/tuvima-library.git';
  assert.deepEqual(findStaleUrls(text, contract), []);
});

test('renamed site base and repository names are reported', () => {
  const text = 'Old https://tuvima.github.io/tuvima_library/ and https://github.com/Tuvima/tuvima_library/issues and https://raw.githubusercontent.com/Tuvima/tuvima_library/main/x.yml';
  assert.deepEqual(findStaleUrls(text, contract), [
    'https://tuvima.github.io/tuvima_library',
    'github.com/Tuvima/tuvima_library',
    'raw.githubusercontent.com/Tuvima/tuvima_library',
  ]);
});

test('container image names, sibling repositories and other owners are ignored', () => {
  assert.deepEqual(findStaleUrls('ghcr.io/tuvima/tuvima_library:latest https://github.com/someone/else https://github.com/Tuvima/tuvima-wikidata https://tuvima.github.io/other-site/', contract), []);
});
