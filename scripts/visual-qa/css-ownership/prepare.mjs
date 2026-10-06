// Normal application UI actions for the fixture-owned, declared state matrix.
// Routes must be resolved from the actual fixture manifests by the caller.
export async function prepareOwnershipState({ browser, tab, state, route, width, height }) {
  await (await browser.capabilities.get('viewport')).set({ width, height });
  const playerClose = tab.playwright.getByRole('button', { name: 'Collapse player', exact: true });
  if (await playerClose.count()) await playerClose.click();
  const nowPlayingClose = tab.playwright.getByRole('button', { name: 'Collapse Now Playing', exact: true });
  if (await nowPlayingClose.count()) await nowPlayingClose.first().click();
  const pickerClose = tab.playwright.locator('.artwork-picker-dialog').getByRole('button', { name: 'Close dialog', exact: true });
  if (await pickerClose.count()) await pickerClose.click();
  const existingEditor = tab.playwright.getByRole('button', { name: 'Close editor', exact: true });
  if (await existingEditor.count()) {
    await existingEditor.click();
    await tab.playwright.domSnapshot();
    const discard = tab.playwright.getByRole('button', { name: 'Discard', exact: true });
    if (await discard.count()) await discard.click();
  }
  if (state.prepare !== 'playlist-rename') {
    const rename = tab.playwright.getByRole('textbox', { name: 'Rename Coast Queue', exact: true });
    if (await rename.count()) await rename.press('Escape');
  }
  const url = 'http://localhost:5016' + route;
  if (await tab.url() !== url) {
    const link = tab.playwright.locator(`a[href="${route}"]`).filter({ visible: true });
    if (await link.count()) await link.first().click();
    else await tab.goto(url);
  } else {
    const close = tab.playwright.getByRole('button', { name: 'Close editor', exact: true });
    if (await close.count()) await close.click();
  }
  await tab.playwright.locator('#main-content').waitFor({ state: 'visible' });
  if (route.startsWith('/details/'))
    await tab.playwright.locator('.tl-detail-page h1').waitFor({ state: 'visible' });
  await tab.playwright.domSnapshot();
  if (width < 721) await tab.playwright.getByRole('navigation', { name: 'Primary mobile navigation', exact: true }).waitFor({ state: 'visible' });
  if (['overview', 'details', 'credits', 'cast', 'related'].includes(state.prepare)) {
    await tab.playwright.getByRole('tab', { name: state.prepare[0].toUpperCase() + state.prepare.slice(1), exact: true }).click();
  } else if (state.prepare === 'home' || state.prepare === 'home-focus') {
    const pause = tab.playwright.getByRole('button', { name: 'Pause featured item rotation', exact: true });
    if (await pause.count()) {
      await pause.click();
      await tab.playwright.getByRole('button', { name: 'Resume featured item rotation', exact: true }).waitFor({ state: 'visible' });
    }
    await tab.playwright.getByRole('button', { name: 'Show featured item 1 of 5: The Northern Signal Partial', exact: true }).click();
    await tab.playwright.getByRole('heading', { name: 'The Northern Signal Partial', exact: true }).waitFor({ state: 'visible' });
    if (state.prepare === 'home-focus')
      await tab.playwright.getByRole('link', { name: 'View details for The Northern Signal Untouched', exact: true }).first().press('Tab');
  } else if (state.prepare.startsWith('editor-')) {
    await tab.playwright.getByRole('button', { name: 'More actions', exact: true }).click();
    await tab.playwright.getByRole('menuitem', { name: 'Edit details', exact: true }).click();
    await tab.playwright.locator('.sme-shell').waitFor({ state: 'visible' });
    await tab.playwright.locator('.sme-loading').waitFor({ state: 'detached' });
    if (state.id === 'editor-tv') {
      const episode = tab.playwright.getByRole('navigation', { name: 'Editing target' })
        .getByRole('button', { name: /^Episode A Message Across the Water(?: 2024)?$/ });
      await episode.waitFor({ state: 'visible' });
      await episode.click();
      await tab.playwright.domSnapshot();
    }
    const label = { 'editor-details': 'Details', 'editor-artwork': 'Artwork', 'editor-history': 'History',
      'editor-match': 'Match & Identity', 'editor-candidates': 'Match & Identity',
      'editor-dirty': 'Details', 'editor-discard': 'Details', 'editor-picker': 'Artwork', 'editor-selected': 'Match & Identity' }[state.prepare];
    await tab.playwright.getByRole('navigation', { name: 'Editor sections' }).getByRole('button', { name: label, exact: true }).click();
    if (['editor-candidates', 'editor-selected'].includes(state.prepare)) {
      const results = tab.playwright.getByRole('region', { name: 'Match search results' });
      await results.getByRole('button', { name: 'Search', exact: true }).click();
      await results.getByRole('button', { name: /The First Coast Jamie Rivers/ }).waitFor({ state: 'visible' });
      if (state.prepare === 'editor-selected') await results.getByRole('button', { name: /The First Coast Jamie Rivers/ }).click();
    }
    if (['editor-dirty', 'editor-discard'].includes(state.prepare)) {
      await tab.playwright.getByRole('button', { name: 'Edit Title as a local override', exact: true }).click();
      await tab.playwright.getByRole('textbox', { name: 'Edit Title', exact: true }).fill('The First Coast — unsaved QA draft');
      if (state.prepare === 'editor-discard') await tab.playwright.getByRole('button', { name: 'Close editor', exact: true }).click();
    }
    if (state.prepare === 'editor-picker') {
      await tab.playwright.getByRole('button', { name: 'Add Artwork', exact: true }).click();
      await tab.playwright.getByRole('menuitem', { name: 'Choose from Library', exact: true }).click();
      await tab.playwright.locator('.artwork-picker-dialog').waitFor({ state: 'visible' });
    }
  } else if (state.prepare === 'playlist-rename') {
    await tab.playwright.getByRole('button', { name: 'Coast Queue options', exact: true }).click();
    await tab.playwright.getByRole('menuitem', { name: 'Rename', exact: true }).click();
    await tab.playwright.getByRole('textbox', { name: 'Rename Coast Queue', exact: true }).waitFor({ state: 'visible' });
  } else if (state.prepare === 'view-checked') {
    await tab.playwright.getByRole('checkbox', { name: 'Include subfolders', exact: true }).check();
  } else if (state.prepare === 'collection-editor') {
    await tab.playwright.getByRole('button', { name: 'More actions', exact: true }).click();
    await tab.playwright.getByRole('menuitem', { name: 'Edit details', exact: true }).click();
    await tab.playwright.locator('.collection-editor-workspace').waitFor({ state: 'visible' });
  } else if (state.prepare === 'gallery-editor') {
    await tab.playwright.getByRole('button', { name: 'Edit Gallery', exact: true }).click();
    await tab.playwright.locator('.collection-editor-workspace').waitFor({ state: 'visible' });
  } else if (state.prepare === 'player-lyrics') {
    await tab.playwright.getByRole('button', { name: 'Lyrics', exact: true }).click();
    await tab.playwright.locator('.playback-lyrics').waitFor({ state: 'visible' });
  } else if (state.prepare === 'player-phone') {
    await tab.playwright.getByRole('button', { name: 'Open full player', exact: true }).waitFor({ state: 'visible' });
    await tab.playwright.getByRole('button', { name: 'Open full player', exact: true }).click();
    await tab.playwright.locator('.playback-full').waitFor({ state: 'visible' });
  }
  if (state.id === 'book-missing') await tab.playwright.getByRole('checkbox', { name: 'Show missing', exact: true }).check();
  else if (state.prepare === 'overview' && await tab.playwright.getByRole('checkbox', { name: 'Show missing', exact: true }).count()) {
    await tab.playwright.getByRole('checkbox', { name: 'Show missing', exact: true }).uncheck();
    const reset = tab.playwright.getByRole('button', { name: 'Use the configured media default', exact: true });
    if (await reset.count()) await reset.click();
  }
  for (const effect of await tab.playwright.locator('.mud-ripple-effect').all())
    await effect.waitFor({ state: 'detached' });
  if (state.prepare.includes('editor') && state.prepare !== 'editor-picker' && state.prepare !== 'editor-discard' && await tab.playwright.locator('.sme-title').count())
    await tab.playwright.locator('.sme-title').click();
  for (const spinner of await tab.playwright.locator('.settings-page-canvas .mud-progress-circular').all())
    await spinner.waitFor({ state: 'detached' });
  await tab.playwright.domSnapshot();
  // Native keyboard scrolling keeps focus/scroll restoration and tab click
  // auto-scrolling from making top-of-page baselines depend on the previous route.
  if (!state.prepare.includes('editor')) {
    await tab.playwright.locator('#main-content').press('Control+Home');
    await tab.playwright.domSnapshot();
    for (let page = 0; page < (state.scrollPages ?? 0); page++)
      await tab.playwright.locator('#main-content').press('PageDown');
    if (state.scrollPages) await tab.playwright.domSnapshot();
    if (['home', 'home-focus'].includes(state.id)) {
      const pause = tab.playwright.getByRole('button', { name: 'Pause featured item rotation', exact: true });
      if (await pause.count()) await pause.click();
      await tab.playwright.getByRole('button', { name: 'Resume featured item rotation', exact: true }).waitFor({ state: 'visible' });
    }
    if (['home', 'home-focus'].includes(state.id)) {
      await tab.playwright.getByRole('button', { name: 'Resume featured item rotation', exact: true }).click();
      await tab.playwright.getByRole('button', { name: 'Pause featured item rotation', exact: true }).click();
      await tab.playwright.getByRole('button', { name: 'Resume featured item rotation', exact: true }).waitFor({ state: 'visible' });
      await tab.playwright.locator('#main-content').press('Control+Home');
    }
    if (['person', 'settings-overview', 'settings-network', 'playlist'].includes(state.id)) {
      const heading = tab.playwright.locator('#main-content h1, #main-content h2').filter({ visible: true });
      if (await heading.count()) await heading.first().click();
      await tab.playwright.locator('#main-content').press('Control+Home');
    }
    if (state.id === 'home-focus') await tab.playwright.getByRole('button', { name: 'Resume S2 E5', exact: true }).press('ArrowLeft');
  }
}
