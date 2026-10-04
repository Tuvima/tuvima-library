import fs from 'node:fs/promises';
import path from 'node:path';

// Read the encoded JPEG frame size; never infer screenshot pixels from DOM size.
export function readJpegDimensions(bytes) {
  if (bytes.length < 4 || bytes[0] !== 0xff || bytes[1] !== 0xd8)
    throw new Error('Expected CUA JPEG screenshot; preserve actual returned format.');
  const frameMarkers = new Set([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf]);
  let offset = 2;
  while (offset < bytes.length) {
    if (bytes[offset++] !== 0xff) throw new Error('Invalid JPEG header marker.');
    while (offset < bytes.length && bytes[offset] === 0xff) offset++;
    if (offset >= bytes.length) throw new Error('Truncated JPEG marker.');
    const marker = bytes[offset++];
    if (marker === 0xda || marker === 0xd9) break;
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
    if (marker === 0x00 || marker === 0xd8) throw new Error('Invalid JPEG header marker.');
    if (offset + 2 > bytes.length) throw new Error('Truncated JPEG segment length.');
    const length = (bytes[offset] << 8) | bytes[offset + 1];
    if (length < 2 || offset + length > bytes.length) throw new Error('Invalid or truncated JPEG segment.');
    if (frameMarkers.has(marker)) {
      if (length < 8) throw new Error('Truncated JPEG frame header.');
      const height = (bytes[offset + 3] << 8) | bytes[offset + 4];
      const width = (bytes[offset + 5] << 8) | bytes[offset + 6];
      if (!width || !height) throw new Error('Invalid JPEG frame dimensions.');
      return { width, height };
    }
    offset += length;
  }
  throw new Error('JPEG has no readable frame dimensions.');
}

// CUA owns the browser. This helper receives existing documented CUA surfaces;
// it never starts Playwright, connects to CDP, or modifies the page.
export async function captureState({ browser, tab, width, height, label, outputRoot, route, textStress = false }) {
  if (!/^[a-z0-9-]+$/.test(label)) throw new Error('Capture labels must be lowercase letters, digits and hyphens.');
  const initialViewport = await tab.playwright.evaluate(() => ({ width: innerWidth, height: innerHeight }));
  const viewportChanged = initialViewport.width !== width || initialViewport.height !== height;
  if (viewportChanged) await (await browser.capabilities.get('viewport')).set({ width, height });
  if (route) await tab.goto(route);
  await tab.playwright.domSnapshot();
  const readGeometry = () => {
    // View grants are carried in the path as well as query parameters. Omit
    // the entire View URL/identity; retain only a constant purpose label.
    const safeUrl = value => {
      if (!value) return '';
      try {
        const url = new URL(value, location.href);
        const pathname = decodeURIComponent(url.pathname).toLowerCase();
        if (/(?:^|\/)(?:view-media|view)(?:\/|$)/.test(pathname)) return '[View image source omitted]';
        return ['http:', 'https:'].includes(url.protocol) ? url.origin + url.pathname : `[${url.protocol} source omitted]`;
      } catch { return '[unparseable source omitted]'; }
    };
    const safeSrcset = value => {
      if (!value) return '';
      if (/(?:data|blob):/i.test(value)) return '[inline source set omitted]';
      return value.split(',').map(candidate => {
        const parts = candidate.trim().split(/\s+/);
        return [safeUrl(parts[0]), ...parts.slice(1)].join(' ');
      }).join(', ');
    };
    const isVisible = image => {
      const rect = image.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return false;
      let left = Math.max(0, rect.left), right = Math.min(innerWidth, rect.right);
      let top = Math.max(0, rect.top), bottom = Math.min(innerHeight, rect.bottom);
      for (let element = image; element; element = element.parentElement) {
        const style = getComputedStyle(element);
        if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse'
          || Number(style.opacity) === 0 || style.contentVisibility === 'hidden') return false;
        if (element !== image) {
          const ancestor = element.getBoundingClientRect();
          if (['hidden', 'clip', 'auto', 'scroll'].includes(style.overflowX)) {
            left = Math.max(left, ancestor.left); right = Math.min(right, ancestor.right);
          }
          if (['hidden', 'clip', 'auto', 'scroll'].includes(style.overflowY)) {
            top = Math.max(top, ancestor.top); bottom = Math.min(bottom, ancestor.bottom);
          }
        }
      }
      return right > left && bottom > top;
    };
    const images = Array.from(document.images);
    const visible = images.filter(isVisible);
    const visibleImages = visible.map(image => {
      const rect = image.getBoundingClientRect();
      return {
        rect: { x: rect.x, y: rect.y, width: rect.width, height: rect.height },
        currentSrc: safeUrl(image.currentSrc), src: safeUrl(image.src), srcset: safeSrcset(image.srcset), sizes: image.sizes,
        naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight, complete: image.complete,
        objectFit: getComputedStyle(image).objectFit,
      };
    });
    return {
    width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scrollX, scrollY,
    pageState: {
      pageTitle: document.title.slice(0, 240),
      headings: Array.from(document.querySelectorAll('h1')).filter(isVisible).map(element => element.textContent.trim().slice(0, 240)),
      heroIdentity: Array.from(document.querySelectorAll('.cinematic-hero-carousel__detail-link')).filter(isVisible)
        .map(element => (element.getAttribute('aria-label') || '').slice(0, 240)),
      heroSubtitle: Array.from(document.querySelectorAll('.tl-detail-hero__subtitle')).filter(isVisible)
        .map(element => element.textContent.trim().slice(0, 240)),
      activeSlide: Array.from(document.querySelectorAll('.cinematic-hero-carousel__timeline [aria-current="true"]')).filter(isVisible)
        .map(element => (element.getAttribute('aria-label') || '').slice(0, 240)),
    },
    overflow: document.documentElement.scrollWidth > innerWidth,
    fontStatus: document.fonts.status,
    textStress: !!document.querySelector('#home-media-qa-text-stress'),
    visibleImages,
    deferredImageCount: images.length - visible.length,
    incompleteImages: visibleImages.filter(image => !image.complete || !image.naturalWidth),
    controls: Array.from(document.querySelectorAll('button,a[aria-label]')).map(control => {
      const rect = control.getBoundingClientRect();
      return { label: control.getAttribute('aria-label') || control.textContent.trim(), x:rect.x, y:rect.y, width:rect.width, height:rect.height };
    }).filter(control => control.y >= 0 && control.y < innerHeight),
    };
  };
  const geometry = await tab.playwright.evaluate(readGeometry);
  if (geometry.width !== width || geometry.height !== height) throw new Error(`Viewport mismatch: actual ${geometry.width}x${geometry.height}.`);
  if (geometry.fontStatus !== 'loaded' || geometry.incompleteImages.length) throw new Error('Artwork/fonts have not settled; retry after a fresh DOM snapshot.');
  if (textStress && !geometry.textStress) throw new Error('Fixture CSS text stress did not activate.');
  const bytes = await tab.screenshot({ fullPage: false });
  const afterCapture = await tab.playwright.evaluate(readGeometry);
  if (JSON.stringify(geometry) !== JSON.stringify(afterCapture))
    throw new Error('Page geometry/state changed during screenshot capture. Evidence refused; obtain a fresh settled DOM snapshot and capture again.');
  const pixels = readJpegDimensions(bytes);
  const cssPixels = pixels.width === geometry.width && pixels.height === geometry.height;
  const devicePixels = Number.isFinite(geometry.dpr) && geometry.dpr > 0
    && pixels.width === Math.round(geometry.width * geometry.dpr)
    && pixels.height === Math.round(geometry.height * geometry.dpr);
  if (!cssPixels && !devicePixels)
    throw new Error(`Screenshot export mismatch: JPEG ${pixels.width}x${pixels.height}, CSS viewport ${geometry.width}x${geometry.height}, DPR ${geometry.dpr}. Capture refused; reload/navigation through supported viewport controls is required.`);
  geometry.capturedPixelWidth = pixels.width;
  geometry.capturedPixelHeight = pixels.height;
  geometry.capturePixelScale = cssPixels ? 'CSS pixels' : 'device pixels at recorded DPR';
  await fs.mkdir(outputRoot, { recursive: true });
  const stem = `${label}-${width}x${height}`;
  const file = path.resolve(outputRoot, stem + '.jpg');
  if (path.dirname(file) !== path.resolve(outputRoot)) throw new Error('Capture path escaped destination.');
  await fs.writeFile(file, bytes);
  await fs.writeFile(path.resolve(outputRoot, stem + '.json'), JSON.stringify({
    label, route: safeEvidenceRoute(route), format:'JPEG', fullPage:false, coverage:'desktop browser viewport emulation',
    textStress: textStress ? 'CSS root font-size 200%, not browser zoom' : null,
    capturedPixelWidth: pixels.width, capturedPixelHeight: pixels.height,
    viewportChanged, geometryStableAcrossCapture: true,
    capturedAt:new Date().toISOString(), geometry,
    urlPrivacy:'Entire View image URL/identity omitted; other image and route queries/fragments omitted; raw DOM snapshot omitted.',
    limitations:['No documented CUA hover, touch gesture or reduced-motion emulation API; record those checks separately.'],
  }, null, 2));
  return { file, geometry };
}

function safeEvidenceRoute(value) {
  if (!value) return null;
  try {
    const url = new URL(value, 'http://fixture.invalid');
    if (/(?:^|\/)(?:view-media|view)(?:\/|$)/.test(decodeURIComponent(url.pathname).toLowerCase()))
      return '[View route identity omitted]';
    if (!['http:', 'https:'].includes(url.protocol)) return '[route omitted]';
    return url.origin === 'http://fixture.invalid' ? url.pathname : url.origin + url.pathname;
  } catch { return '[route omitted]'; }
}

export async function resetViewport(browser) {
  await (await browser.capabilities.get('viewport')).reset();
}
