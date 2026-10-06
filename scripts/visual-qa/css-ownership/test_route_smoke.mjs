import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { settingsRoutes, assertRouteResult } from './route-smoke.mjs';

test('every current SettingsNav item supplies one route and positive heading contract', async () => {
  const source = await fs.readFile(new URL('../../../src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs', import.meta.url), 'utf8');
  const routes = settingsRoutes(source);
  assert.ok(routes.length > 15);
  assert.equal(new Set(routes.map(item => item.route)).size, routes.length);
  assert.ok(routes.every(item => item.heading));
  assert.ok(routes.some(item => item.route === '/settings/libraries'));
  for (const [route, expectedRoute] of [['privacy', 'profile'], ['metadata', 'metadata/providers'], ['network', 'network/overview'], ['access', 'access/users'], ['review', 'recently-added']])
    assert.equal(routes.find(item => item.route === `/settings/${route}`).expectedRoute, `/settings/${expectedRoute}`);
});

test('a surviving shell cannot conceal a redirect, missing content, pending loading or access error', () => {
  const contract = { route: '/settings/account', heading: 'Security' };
  const actual = { pathname: '/settings/account', shell: true, content: true, loading: false, errors: [], headings: ['Security'] };
  assert.equal(assertRouteResult(actual, contract), actual);
  assert.equal(assertRouteResult(actual, { ...contract, route: '/old', expectedRoute: '/settings/account' }), actual);
  for (const change of [{ pathname: '/login' }, { content: false }, { shell: false }, { loading: true }, { errors: ['Access denied'] }, { headings: ['Profile'] }])
    assert.throws(() => assertRouteResult({ ...actual, ...change }, contract));
});

test('unrecognized SettingsNav syntax fails closed instead of silently omitting routes', () => {
  assert.throws(() => settingsRoutes('new(SettingsSection.Account, "account")'));
});
