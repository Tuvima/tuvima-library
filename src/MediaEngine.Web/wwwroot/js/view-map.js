import * as maplibregl from '../vendor/maplibre/maplibre-gl.mjs';

maplibregl.setWorkerUrl('/vendor/maplibre/maplibre-gl-worker.mjs');

const states = new WeakMap();
const detailedStyleUrl = 'https://tiles.openfreemap.org/styles/dark';

async function preferredStyle() {
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(), 6000);
  try {
    const response = await fetch(detailedStyleUrl, {
      cache: 'force-cache',
      mode: 'cors',
      signal: controller.signal
    });
    if (!response.ok) throw new Error(`Detailed map style returned ${response.status}`);
    return {
      style: detailedStyleUrl,
      attribution: '© OpenFreeMap · © OpenStreetMap contributors',
      detailed: true
    };
  } catch {
    return {
      style: baseStyle(),
      attribution: 'Natural Earth · Tuvima Atlas',
      detailed: false
    };
  } finally {
    window.clearTimeout(timeout);
  }
}

function baseStyle() {
  return {
    version: 8,
    sources: {
      world: { type: 'geojson', data: '/maps/world-countries.geojson' }
    },
    layers: [
      { id: 'atlas-background', type: 'background', paint: { 'background-color': '#030712' } },
      { id: 'atlas-countries', type: 'fill', source: 'world', paint: {
        'fill-color': ['case', ['boolean', ['feature-state', 'active'], false], '#20253a', '#111827'],
        'fill-opacity': 0.92
      } },
      { id: 'atlas-borders', type: 'line', source: 'world', paint: {
        'line-color': '#34405a', 'line-width': 0.65, 'line-opacity': 0.72
      } }
    ]
  };
}

function normalizedHotspot(value) {
  return {
    key: value.key ?? value.Key,
    name: value.name ?? value.Name ?? 'Location',
    latitude: Number(value.latitude ?? value.Latitude),
    longitude: Number(value.longitude ?? value.Longitude),
    assetCount: Number(value.assetCount ?? value.AssetCount ?? 0),
    imageCount: Number(value.imageCount ?? value.ImageCount ?? 0),
    videoCount: Number(value.videoCount ?? value.VideoCount ?? 0),
    earliestAt: value.earliestAt ?? value.EarliestAt ?? null,
    latestAt: value.latestAt ?? value.LatestAt ?? null,
    thumbnailUrl: value.thumbnailUrl ?? value.ThumbnailUrl ?? null
  };
}

function clearMarkers(state) {
  for (const marker of state.markers) marker.remove();
  state.markers = [];
}

function clearOverviewLabels(state) {
  for (const marker of state.overviewLabels) marker.remove();
  state.overviewLabels = [];
}

function collapseAttribution(container) {
  const attribution = container.querySelector('details.maplibregl-ctrl-attrib');
  if (!attribution) return;
  attribution.classList.remove('maplibregl-compact-show');
  attribution.removeAttribute('open');
}

function atlasLabelMinZoom(layer) {
  const identity = `${layer.id ?? ''} ${layer['source-layer'] ?? ''}`.toLowerCase();
  if (/(country|admin-0|admin_0)/.test(identity)) return 2.75;
  if (/(state|province|admin-1|admin_1)/.test(identity)) return 4.25;
  if (/(capital|city)/.test(identity)) return 5.75;
  if (/(town|village|locality|settlement|place)/.test(identity)) return 7.5;
  if (/(motorway|trunk|primary|road-label|road_label|highway|route-shield)/.test(identity)) return 9.5;
  if (/(road|street|rail|transit|airport|water_name|water-name)/.test(identity)) return 11;
  if (/(poi|point-of-interest|address|housenumber|building)/.test(identity)) return 13;
  return 11;
}

function applyAtlasLabelPolicy(map) {
  const layers = map.getStyle()?.layers ?? [];
  for (const layer of layers) {
    if (layer.type !== 'symbol') continue;
    const minimum = atlasLabelMinZoom(layer);
    const maximum = Number(layer.maxzoom ?? 24);
    try { map.setLayerZoomRange(layer.id, minimum, maximum); } catch { }
  }
}

const atlasOverviewLabels = [
  ['North America', -105, 45, 'continent'],
  ['South America', -60, -18, 'continent'],
  ['Europe', 18, 50, 'continent'],
  ['Africa', 20, 6, 'continent'],
  ['Asia', 90, 40, 'continent'],
  ['Oceania', 137, -25, 'continent'],
  ['Arctic Ocean', 0, 73, 'ocean'],
  ['North Pacific', -150, 8, 'ocean'],
  ['South Pacific', -135, -32, 'ocean'],
  ['Atlantic Ocean', -32, 8, 'ocean'],
  ['Indian Ocean', 78, -26, 'ocean'],
  ['Southern Ocean', 18, -54, 'ocean']
];

function renderOverviewLabels(state) {
  if (state.mode !== 'atlas' || !state.map) return;
  const visible = state.map.getZoom() < 2.7;
  if (!state.overviewLabels.length) {
    for (const [name, longitude, latitude, kind] of atlasOverviewLabels) {
      const label = document.createElement('span');
      label.className = `tuvima-map-overview-label is-${kind}`;
      label.textContent = name;
      label.setAttribute('aria-hidden', 'true');
      state.overviewLabels.push(new maplibregl.Marker({ element: label, anchor: 'center' })
        .setLngLat([longitude, latitude]).addTo(state.map));
    }
  }
  for (const marker of state.overviewLabels) marker.getElement().hidden = !visible;
}

function renderHotspots(state) {
  // DOM markers only need the camera transform. GeoJSON setData can temporarily
  // make isStyleLoaded false; gating on it leaves old counts after filtering.
  if (state.mode !== 'atlas' || !state.map) return;
  renderOverviewLabels(state);
  clearMarkers(state);
  const zoom = state.map.getZoom();
  const cell = zoom < 2 ? 92 : zoom < 4 ? 78 : zoom < 7 ? 66 : 52;
  const groups = new Map();
  for (const hotspot of state.hotspots) {
    if (!Number.isFinite(hotspot.latitude) || !Number.isFinite(hotspot.longitude)) continue;
    const point = state.map.project([hotspot.longitude, hotspot.latitude]);
    const key = `${Math.floor(point.x / cell)}:${Math.floor(point.y / cell)}`;
    const group = groups.get(key) ?? { items: [], count: 0, images: 0, videos: 0, representative: null };
    group.items.push(hotspot);
    group.count += hotspot.assetCount;
    group.images += hotspot.imageCount;
    group.videos += hotspot.videoCount;
    if (hotspot.thumbnailUrl && (!group.representative || hotspot.assetCount > group.representative.assetCount))
      group.representative = hotspot;
    groups.set(key, group);
  }

  for (const group of groups.values()) {
    // A cluster must remain attached to a real place. Averaging geographic
    // coordinates can put a representative photo between cities (or offshore),
    // especially at the world zoom and near the antimeridian.
    const anchorHotspot = group.items.length === 1
      ? group.items[0]
      : group.representative ?? group.items.reduce((best, item) =>
        item.assetCount > best.assetCount ? item : best, group.items[0]);
    const latitude = anchorHotspot.latitude;
    const longitude = anchorHotspot.longitude;
    const anchor = document.createElement('div');
    anchor.className = 'tuvima-map-hotspot-anchor';
    const element = document.createElement('button');
    element.type = 'button';
    element.className = `tuvima-map-hotspot${group.items.length === 1 && zoom >= 7 ? ' is-place' : ''}`;
    const count = document.createElement('span');
    count.textContent = new Intl.NumberFormat(undefined, { notation: group.count > 999 ? 'compact' : 'standard' }).format(group.count);
    element.appendChild(count);
    const mediaTotal = Math.max(1, group.images + group.videos);
    element.style.setProperty('--video-share', `${Math.round(group.videos / mediaTotal * 360)}deg`);
    if (group.representative?.thumbnailUrl) {
      const image = document.createElement('img');
      image.src = group.representative.thumbnailUrl;
      image.alt = '';
      element.prepend(image);
      element.classList.add('has-thumbnail');
      if (group.items.length > 1) element.classList.add('is-cluster');
    }
    const label = group.items.length === 1
      ? `${group.items[0].name}, ${group.count} media item${group.count === 1 ? '' : 's'}`
      : `${group.count} media items in ${group.items.length} nearby places`;
    element.setAttribute('aria-label', label);
    element.title = label;
    anchor.appendChild(element);
    element.addEventListener('click', event => {
      event.stopPropagation();
      if (group.items.length === 1) {
        state.dotnet.invokeMethodAsync('SelectHotspot', group.items[0].key);
        const detailZoom = state.detailedStyle ? 8 : 4.25;
        state.map.easeTo({ center: [longitude, latitude], zoom: Math.max(state.map.getZoom(), detailZoom), duration: reducedMotion() ? 0 : 650 });
        return;
      }
      const bounds = new maplibregl.LngLatBounds();
      for (const item of group.items) bounds.extend([item.longitude, item.latitude]);
      const northEast = bounds.getNorthEast();
      const southWest = bounds.getSouthWest();
      if (northEast.lng === southWest.lng && northEast.lat === southWest.lat)
        state.map.easeTo({ center: [longitude, latitude], zoom: Math.min(14, zoom + 2), duration: reducedMotion() ? 0 : 650 });
      else
        state.map.fitBounds(bounds, { padding: 92, maxZoom: state.detailedStyle ? 10 : 4.25, duration: reducedMotion() ? 0 : 700 });
    });
    state.markers.push(new maplibregl.Marker({ element: anchor, anchor: 'center' }).setLngLat([longitude, latitude]).addTo(state.map));
  }
}

function reducedMotion() {
  return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
}

function atlasWorldPadding(map) {
  const width = Math.max(1, map.getContainer().clientWidth);
  const height = Math.max(1, map.getContainer().clientHeight);
  return {
    top: Math.min(76, height * .1),
    right: Math.min(34, width * .025),
    bottom: Math.min(174, height * .21),
    left: Math.min(76, width * .055)
  };
}

function fitAtlasWorld(state, animate = true) {
  if (!state?.map) return;
  state.map.fitBounds([[-179.5, -58], [179.5, 78]], {
    padding: atlasWorldPadding(state.map),
    pitch: 0,
    bearing: 0,
    duration: animate && !reducedMotion() ? 650 : 0
  });
}

function journeyGeoJson(hotspots) {
  const ordered = hotspots.filter(item => item.earliestAt).sort((left, right) => new Date(left.earliestAt) - new Date(right.earliestAt));
  const lines = [];
  let current = [];
  let previous = null;
  for (const item of ordered) {
    const at = new Date(item.earliestAt);
    if (previous && at - previous > 1000 * 60 * 60 * 24 * 21) {
      if (current.length > 1) lines.push(current);
      current = [];
    }
    current.push([item.longitude, item.latitude]);
    previous = at;
  }
  if (current.length > 1) lines.push(current);
  return { type: 'Feature', properties: {}, geometry: { type: 'MultiLineString', coordinates: lines } };
}

function updateJourney(state, enabled) {
  state.journeyEnabled = enabled === true;
  if (!state.map?.loaded()) return;
  const source = state.map.getSource('atlas-journey');
  if (source) source.setData(journeyGeoJson(state.journeyEnabled ? state.hotspots : []));
}

export async function initialize(container, dotnet, options) {
  if (!container) return;
  await dispose(container);
  const mode = options.mode ?? 'location';
  const state = { map: null, dotnet, mode, markers: [], overviewLabels: [], hotspots: (options.hotspots ?? []).map(normalizedHotspot), locationMarker: null, resizeObserver: null, detailedStyle: false, fallbackApplied: false, journeyEnabled: options.journeyEnabled === true };
  states.set(container, state);
  try {
    const styleSelection = await preferredStyle();
    state.detailedStyle = styleSelection.detailed;
    state.fallbackApplied = !styleSelection.detailed;
    const requestedZoom = Number(options.zoom ?? 11);
    const map = new maplibregl.Map({
      container,
      style: styleSelection.style,
      center: mode === 'atlas' ? [0, 12] : [Number(options.longitude ?? 0), Number(options.latitude ?? 0)],
      // The bundled fallback has country geometry rather than street tiles. Keep
      // it at a regional zoom so it remains visibly geographic instead of
      // becoming a featureless dark square around a city coordinate.
      zoom: mode === 'atlas' ? 0 : styleSelection.detailed ? requestedZoom : Math.min(requestedZoom, 4.25),
      minZoom: mode === 'atlas' ? 0 : 1,
      maxZoom: 18,
      renderWorldCopies: false,
      pitch: 0,
      bearing: 0,
      dragRotate: false,
      pitchWithRotate: false,
      attributionControl: false,
      dragPan: options.interactive !== false,
      scrollZoom: mode === 'atlas' && options.interactive !== false,
      doubleClickZoom: options.interactive !== false,
      touchZoomRotate: options.interactive !== false
    });
    state.map = map;
    map.touchZoomRotate?.disableRotation();
    state.resizeObserver = new ResizeObserver(() => map.resize());
    state.resizeObserver.observe(container);
    map.addControl(new maplibregl.AttributionControl({ compact: true, customAttribution: styleSelection.attribution }), 'bottom-right');
    window.requestAnimationFrame(() => collapseAttribution(container));
    if (options.showNavigation !== false) map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'top-left');
    map.on('load', () => {
      if (mode === 'atlas') {
        try { map.setProjection({ type: 'mercator' }); } catch { }
        applyAtlasLabelPolicy(map);
        map.addSource('atlas-journey', { type: 'geojson', data: journeyGeoJson(state.journeyEnabled ? state.hotspots : []) });
        map.addLayer({ id: 'atlas-journey-glow', type: 'line', source: 'atlas-journey', paint: { 'line-color': '#7c3aed', 'line-width': 8, 'line-opacity': .16, 'line-blur': 6 } });
        map.addLayer({ id: 'atlas-journey', type: 'line', source: 'atlas-journey', paint: { 'line-color': '#c4b5fd', 'line-width': 2, 'line-opacity': .72, 'line-dasharray': [2, 2] } });
        map.resize();
        fitAtlasWorld(state, false);
        renderHotspots(state);
        map.once('idle', () => { renderOverviewLabels(state); renderHotspots(state); });
      } else {
        const marker = new maplibregl.Marker({ color: '#9f67ff', draggable: options.editable === true })
          .setLngLat([Number(options.longitude ?? 0), Number(options.latitude ?? 0)])
          .addTo(map);
        state.locationMarker = marker;
        if (options.editable === true) {
          marker.on('dragend', () => {
            const point = marker.getLngLat();
            state.dotnet.invokeMethodAsync('CoordinateChanged', point.lat, point.lng);
          });
          map.on('click', event => {
            marker.setLngLat(event.lngLat);
            state.dotnet.invokeMethodAsync('CoordinateChanged', event.lngLat.lat, event.lngLat.lng);
          });
        }
      }
      state.dotnet.invokeMethodAsync('MapReady');
    });
    if (mode === 'atlas') {
      map.on('moveend', () => {
        renderHotspots(state);
        const center = map.getCenter();
        state.dotnet.invokeMethodAsync('CameraChanged', center.lat, center.lng, map.getZoom());
      });
    }
    map.on('error', () => {
      if (!map.isStyleLoaded() && state.detailedStyle && !state.fallbackApplied) {
        state.fallbackApplied = true;
        state.detailedStyle = false;
        map.setStyle(baseStyle());
        if (mode === 'atlas') map.once('style.load', () => applyAtlasLabelPolicy(map));
        if (mode !== 'atlas') map.jumpTo({ zoom: Math.min(requestedZoom, 4.25) });
      }
    });
  } catch (error) {
    console.error('Tuvima map initialization failed', error);
    state.dotnet.invokeMethodAsync('MapFailed', error?.message ?? 'The map could not be initialized.');
  }
}

export function updateHotspots(container, hotspots) {
  const state = states.get(container);
  if (!state) return;
  state.hotspots = (hotspots ?? []).map(normalizedHotspot);
  updateJourney(state, state.journeyEnabled);
  renderHotspots(state);
}

export function setJourney(container, enabled) {
  const state = states.get(container);
  if (state) updateJourney(state, enabled);
}

export function updateLocation(container, latitude, longitude) {
  const state = states.get(container);
  if (!state?.map || !Number.isFinite(latitude) || !Number.isFinite(longitude)) return;
  state.locationMarker?.setLngLat([longitude, latitude]);
  state.map.easeTo({ center: [longitude, latitude], duration: reducedMotion() ? 0 : 350 });
}

export function resetWorld(container) {
  const state = states.get(container);
  fitAtlasWorld(state, true);
}

export function fitHotspots(container) {
  const state = states.get(container);
  if (!state?.map || state.hotspots.length === 0) return;
  const bounds = new maplibregl.LngLatBounds();
  for (const item of state.hotspots) bounds.extend([item.longitude, item.latitude]);
  state.map.fitBounds(bounds, { padding: 110, maxZoom: state.detailedStyle ? 9 : 4.25, duration: reducedMotion() ? 0 : 700 });
}

export function zoomBy(container, delta) {
  const state = states.get(container);
  if (!state?.map) return;
  state.map.easeTo({ zoom: Math.max(state.map.getMinZoom(), Math.min(state.map.getMaxZoom(), state.map.getZoom() + Number(delta ?? 0))), duration: reducedMotion() ? 0 : 260 });
}

export function resize(container) {
  states.get(container)?.map?.resize();
}

export async function dispose(container) {
  const state = states.get(container);
  if (!state) return;
  clearMarkers(state);
  clearOverviewLabels(state);
  state.resizeObserver?.disconnect();
  state.locationMarker?.remove();
  state.map?.remove();
  states.delete(container);
}
