import fs from 'node:fs/promises';
import path from 'node:path';

export function settingsRoutes(source) {
  const items = source.match(/public static readonly SettingsItemDef\[\] AllItems\s*=\s*\[([\s\S]*?)\n\s*\];/)?.[1];
  if (!items) throw new Error('SettingsNav.AllItems was not found.');
  const landing = new Set([...((source.match(/_landingSections\s*=\s*\[([\s\S]*?)\];/)?.[1]) ?? '').matchAll(/SettingsSection\.(\w+)/g)].map(match => match[1]));
  if (!landing.size) throw new Error('SettingsNav landing policy was not found.');
  const subsections = new Map([...source.matchAll(/\[SettingsSection\.(\w+)\]\s*=\s*\[\s*new\("([^"]+)"/g)].map(match => [match[1], match[2]]));
  const entries = [...items.matchAll(/new\(SettingsSection\.(\w+),\s*"[^"]+",\s*"([^"]+)",\s*[^,]+,\s*"([^"]+)"/g)];
  const routes = entries.map(match => {
    const [section, slug, heading] = match.slice(1);
    const route = `/settings/${slug}`;
    const suffix = section === 'Access' ? 'users' : !landing.has(section) ? subsections.get(section) : null;
    return { route, expectedRoute: suffix ? `${route}/${suffix}` : route, selector: '.settings-shell-content', heading };
  });
  if (!routes.length || routes.length !== (items.match(/new\(SettingsSection\./g) ?? []).length)
    throw new Error('Incomplete SettingsNav route extraction.');
  // Existing disabled Privacy and retired Review destinations deliberately redirect.
  // Assert their policy markers so a future policy edit requires revisiting the contract.
  if (!/section == SettingsSection\.Privacy\)\s*\{\s*return false;/.test(source))
    throw new Error('Revisit the Privacy route contract after its availability policy changes.');
  const overview = routes[entries.findIndex(entry => entry[1] === 'Overview')];
  const recent = routes[entries.findIndex(entry => entry[1] === 'RecentlyAdded')];
  for (const [section, destination] of [['Privacy', overview], ['Review', recent]]) {
    const route = routes[entries.findIndex(entry => entry[1] === section)];
    if (!route || !destination) throw new Error(`Missing ${section} redirect destination.`);
    Object.assign(route, { expectedRoute: destination.route, heading: destination.heading });
  }
  return routes;
}

export function smokeRoutes(settingsSource, manifest) {
  const routes = [
    { route: '/', selector: '.home-discovery-page' },
    ...['read', 'watch', 'listen'].map(lane => ({ route: `/${lane}`, selector: '.media-section-shell' })),
    { route: '/listen/music', selector: '.browse-shell' },
    { route: '/listen/audiobooks', selector: '.browse-shell' },
    { route: '/collections', selector: '.collections-overview' },
    { route: '/search', selector: '.search-page', heading: 'Search' },
    { route: '/view', selector: '.view-timeline-layout', heading: 'Photos' },
    { route: '/recently-added', selector: '.recent-browser' },
    ...settingsRoutes(settingsSource),
  ];
  const fixtureKeys = { book: 'book-partial', movie: 'movie-unstarted', tvshow: 'untouched-show-episode', musicalbum: 'album-active' };
  for (const entity of Object.keys(fixtureKeys)) {
    const item = manifest.items?.find(entry => entry.route?.startsWith(`/details/${entity}/`))
      ?? manifest.items?.find(entry => entry.key === fixtureKeys[entity]);
    if (!item) throw new Error(`Missing fixture detail route: ${entity}.`);
    const identity = ['tvshow', 'musicalbum'].includes(entity) ? item.parentWorkId : item.workId;
    const route = item.route?.startsWith(`/details/${entity}/`) ? item.route : `/details/${entity}/${identity}`;
    if (!identity) throw new Error(`Missing fixture identity: ${entity}.`);
    routes.push({ route, selector: '.tl-detail-route-shell .cinematic-hero-surface--detail' });
  }
  routes.push({ route: '/not-found', selector: '.text-center', heading: 'Page Not Found' });
  return routes;
}

export function assertRouteResult(actual, contract) {
  const expected = new URL(contract.expectedRoute ?? contract.route, 'http://fixture.invalid').pathname;
  if (actual.pathname !== expected) throw new Error(`Unexpected navigation: ${actual.pathname}; expected ${expected}.`);
  if (!actual.shell || !actual.content) throw new Error('Missing navigation shell or page-specific content.');
  if (actual.loading) throw new Error('Page is still loading.');
  if (actual.errors.length) throw new Error(`Page failed: ${actual.errors.join('; ')}.`);
  if (contract.heading && !actual.headings.includes(contract.heading)) throw new Error(`Missing page heading: ${contract.heading}.`);
  return actual;
}

// Existing authenticated CUA tab only. No browser launcher, debug connection,
// authentication bypass, or DOM mutation. Spacing protects the fixture rate limit.
export async function runRouteSmoke({ tab, baseUrl, routes, outputRoot, timeoutMs = 20000 }) {
  const results = [];
  let previousNavigation = 0;
  for (const contract of routes) {
    let actual;
    try {
      const spacing = 2000 - (Date.now() - previousNavigation);
      if (spacing > 0) await new Promise(resolve => setTimeout(resolve, spacing));
      previousNavigation = Date.now();
      // Prefer visible normal navigation to retain the Blazor circuit. Repeated full
      // document loads otherwise exhaust the Engine's SignalR connection limiter.
      if (new URL(await tab.url()).pathname !== contract.route) {
        const sidebar = tab.playwright.locator(`[aria-label="Settings navigation"] a[href="${contract.route}"]`).filter({ visible: true });
        const navigation = tab.playwright.locator(`a[href="${contract.route}"]`).filter({ visible: true });
        if (await sidebar.count() === 1) await sidebar.click();
        else if (await navigation.count() === 1) await navigation.click();
        else await tab.goto(new URL(contract.route, baseUrl).href);
      }
      const deadline = Date.now() + timeoutMs;
      let settled = 0;
      do {
        await tab.playwright.domSnapshot();
        actual = await tab.playwright.evaluate(({ selector }) => {
          const visible = element => !!element && !!element.getBoundingClientRect().width && !!element.getBoundingClientRect().height
            && getComputedStyle(element).visibility !== 'hidden' && !element.closest('[hidden]');
          const main = document.querySelector('main,[role="main"]');
          const text = main?.innerText ?? document.body?.innerText ?? '';
          const errors = [...document.querySelectorAll(':is(main,[role="main"]) .app-error-state,:is(main,[role="main"]) .app-page-state--error,:is(main,[role="main"]) .tl-detail-error')]
            .filter(visible).map(element => element.innerText.slice(0, 180));
          for (const message of ['Screen failed to load', 'Administrator access could not be verified', 'Administrator settings are locked', 'Engine state could not be loaded'])
            if (text.includes(message)) errors.push(message);
          return { pathname: location.pathname, shell: !!main && !!text.trim() && [...document.querySelectorAll('nav,header')].some(visible),
            content: [...document.querySelectorAll(selector)].some(visible),
            headings: [...document.querySelectorAll(':is(main,[role="main"]) :is(h1,h2,h3,h4,h5,h6,.app-page-header__title)')].filter(visible).map(element => element.textContent.trim()),
            loading: [...document.querySelectorAll(':is(main,[role="main"]) :is([aria-busy="true"],.app-skeleton,.home-discovery-loading)')].some(visible), errors };
        }, { selector: contract.selector });
        if (actual.errors.length) break;
        try { assertRouteResult(actual, contract); settled++; }
        catch { settled = 0; }
        if (settled >= 2) break;
        await new Promise(resolve => setTimeout(resolve, 250));
      } while (Date.now() < deadline);
      assertRouteResult(actual, contract);
      results.push({ route: contract.route, passed: true, actual });
    } catch (error) {
      results.push({ route: contract.route, passed: false, error: error.message, actual });
    }
  }
  const report = { schemaVersion: 1, verified: results.every(result => result.passed), results };
  if (outputRoot) {
    await fs.mkdir(outputRoot, { recursive: true });
    await fs.writeFile(path.join(outputRoot, 'route-smoke.json'), JSON.stringify(report, null, 2) + '\n');
  }
  return report;
}
