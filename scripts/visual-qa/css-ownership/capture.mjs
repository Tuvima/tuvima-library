import fs from 'node:fs/promises';
import path from 'node:path';
import { captureState } from '../home-media-cards/capture.mjs';

export const styleProperties = [
  'display', 'position', 'z-index', 'opacity', 'transform', 'overflow', 'overflow-x', 'overflow-y',
  'width', 'height', 'min-width', 'max-width', 'min-height', 'max-height', 'object-fit', 'object-position',
  'font-family', 'font-size', 'font-weight', 'font-style', 'line-height', 'letter-spacing', 'text-align',
  'color', 'background-color', 'background-image', 'background-size', 'background-position',
  'border-top', 'border-right', 'border-bottom', 'border-left', 'border-radius', 'box-shadow',
  'padding-top', 'padding-right', 'padding-bottom', 'padding-left',
  'margin-top', 'margin-right', 'margin-bottom', 'margin-left', 'gap', 'row-gap', 'column-gap',
  'grid-template-columns', 'grid-template-rows', 'align-items', 'justify-content', 'flex-direction',
  'filter', 'mask-image', 'content',
];

// Existing documented CUA handles only. UI preparation uses ordinary locators
// outside this helper; page evaluation remains read-only.
export async function captureOwnershipState({ browser, tab, state, width, height, outputRoot }) {
  const specs = state.selectors;
  if (!specs?.length) throw new Error('State must declare mandatory selector coverage.');
  const viewport = await tab.playwright.evaluate(() => ({ width: innerWidth, height: innerHeight }));
  if (viewport.width !== width || viewport.height !== height)
    await (await browser.capabilities.get('viewport')).set({ width, height });
  await tab.playwright.domSnapshot();
  const readStyles = () => tab.playwright.evaluate(({ specs, properties }) => {
    const targets = specs.map(spec => {
      const elements = [...document.querySelectorAll(spec.selector)];
      const minimum = spec.min ?? 1;
      const maximum = spec.max ?? Number.MAX_SAFE_INTEGER;
      if (elements.length < minimum || elements.length > maximum)
        throw new Error(`Mandatory target ${spec.selector}: expected ${minimum}..${maximum}, found ${elements.length}.`);
      return {
        selector: spec.selector,
        elements: elements.map(element => {
          const rect = element.getBoundingClientRect();
          const read = pseudo => {
            const style = getComputedStyle(element, pseudo);
            return Object.fromEntries(properties.map(property => [property, style.getPropertyValue(property)]));
          };
          return {
            rect: { x: rect.x, y: rect.y, width: rect.width, height: rect.height },
            styles: read(null),
            pseudo: Object.fromEntries((spec.pseudo ?? []).map(pseudo => [pseudo, read(pseudo)])),
            state: { hovered: element.matches(':hover'), checked: element.matches(':checked'), disabled: element.matches(':disabled'),
              focused: element.matches(':focus'), focusWithin: element.matches(':focus-within'),
              ariaCurrent: element.getAttribute('aria-current'), ariaExpanded: element.getAttribute('aria-expanded') },
            scopeEvidence: [...element.attributes].filter(attribute => attribute.name.startsWith('b-')).map(attribute => attribute.name),
          };
        }),
      };
    });
    return { viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio },
      scroll: { x: scrollX, y: scrollY }, targets };
  }, { specs, properties: styleProperties });
  const before = await readStyles();
  const result = await captureState({ browser, tab, width, height, label: state.id, outputRoot });
  const after = await readStyles();
  if (JSON.stringify(before) !== JSON.stringify(after)) {
    const changes = [];
    for (let i = 0; i < before.targets.length; i++) {
      const a = before.targets[i], b = after.targets[i];
      if (a.elements.length !== b.elements.length)
        changes.push(`${a.selector} count: ${a.elements.length} -> ${b.elements.length}`);
      for (let j = 0; j < a.elements.length && j < b.elements.length; j++) {
        for (const group of ['rect', 'state', 'pseudo'])
          if (JSON.stringify(a.elements[j][group]) !== JSON.stringify(b.elements[j][group]))
            changes.push(`${a.selector}[${j}] ${group}: ${JSON.stringify(a.elements[j][group])} -> ${JSON.stringify(b.elements[j][group])}`);
        for (const key of Object.keys(a.elements[j].styles))
          if (a.elements[j].styles[key] !== b.elements[j].styles[key])
            changes.push(`${a.selector}[${j}] ${key}: ${a.elements[j].styles[key]} -> ${b.elements[j].styles[key]}`);
      }
    }
    throw new Error('Computed state changed across capture; evidence refused. ' + changes.slice(0, 3).join('; '));
  }
  const document = { schemaVersion: 1, state: state.id, viewport: after.viewport, scroll: after.scroll,
    targets: after.targets, tolerances: state.tolerances ?? [], limitations: state.limitations ?? [] };
  await fs.writeFile(path.join(outputRoot, `${state.id}-${width}x${height}.styles.json`), JSON.stringify(document, null, 2));
  return { file: result.file, targetCount: after.targets.length };
}
