import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

test('date-filtered markers refresh while Journey source is loading, including zero results', () => {
  const source = fs.readFileSync(new URL('../src/MediaEngine.Web/wwwroot/js/view-map.js', import.meta.url), 'utf8')
    .replace(/^import .*;\r?\n/m, '').replace(/^export /gm, '');
  const element = () => ({ children: [], style: { setProperty() {} }, classList: { add() {} },
    appendChild(child) { this.children.push(child); }, prepend(child) { this.children.unshift(child); },
    setAttribute() {}, addEventListener() {} });
  let removed = 0;
  class Marker {
    constructor({ element }) { this.element = element; }
    setLngLat() { return this; }
    addTo() { return this; }
    getElement() { return this.element; }
    remove() { removed++; }
  }
  let loaded = true;
  const state = { mode: 'atlas', journeyEnabled: true, markers: [], overviewLabels: [], hotspots: [],
    map: { getZoom: () => 8, project: () => ({ x: 100, y: 100 }),
      loaded: () => loaded, isStyleLoaded: () => loaded,
      getSource: () => ({ setData() { loaded = false; } }) } };
  const container = {};
  const context = vm.createContext({ maplibregl: { setWorkerUrl() {}, Marker },
    document: { createElement: element }, state, container, Intl, console });
  vm.runInContext(source + '\nstates.set(container, state);', context);
  const hotspot = count => ({ key: 'chicago', name: 'Chicago', latitude: 41.88, longitude: -87.63,
    assetCount: count, imageCount: count, videoCount: 0, earliestAt: '2022-01-01' });
  context.updateHotspots(container, [hotspot(10)]);
  assert.equal(loaded, false);
  assert.equal(state.markers.length, 1);
  assert.equal(state.markers[0].element.children[0].children[0].textContent, '10');
  context.updateHotspots(container, [hotspot(2)]);
  assert.equal(state.markers[0].element.children[0].children[0].textContent, '2');
  context.updateHotspots(container, []);
  assert.equal(state.markers.length, 0);
  assert.equal(removed, 2);
  context.updateHotspots(container, [hotspot(10)]);
  assert.equal(state.markers[0].element.children[0].children[0].textContent, '10');
});
