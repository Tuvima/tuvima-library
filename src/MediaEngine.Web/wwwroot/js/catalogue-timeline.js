import { activeTimelineSection, scrollToTimelineSection } from './timeline-scroll.js';
const observers = new WeakMap();
export function observe(root, dotnet) {
  if (!root?.isConnected) return;
  const url = new URL(location.href);
  url.searchParams.delete('period');
  url.hash = '';
  const storageKey = 'tuvima.timeline:' + url.pathname + url.search;
  if (observers.get(root)?.key === storageKey) return;
  disconnect(root);
  const scroller = root.closest('.media-section-shell__content, .listen-content') ?? document.scrollingElement;
  let frame = 0, last, restoring = true, leaving = false;
  const update = () => {
    frame = 0;
    if (restoring || leaving || !root.isConnected) return;
    const bounds = scroller.getBoundingClientRect();
    const sections = [...root.querySelectorAll('[data-timeline-key]')];
    const active = activeTimelineSection(sections, scroller, innerHeight);
    const rail = root.querySelector('.view-timeline-scrubber');
    if (rail) rail.style.setProperty('--timeline-rail-height', Math.max(160, Math.min(innerHeight, bounds.bottom) - Math.max(bounds.top + 20, rail.getBoundingClientRect().top) - 20) + 'px');
    const key = active?.dataset.timelineKey;
    if (key !== undefined && key !== last) {
      last = key;
      try { sessionStorage.setItem(storageKey, key); } catch { /* Storage may be disabled. */ }
      dotnet.invokeMethodAsync('SetActivePeriod', Number(key)).then(() => requestAnimationFrame(() => {
        const active = rail?.querySelector('.view-timeline-scrubber__year.is-active');
        if (!active || !matchMedia('(min-width:901px)').matches) return;
        const bounds = rail.getBoundingClientRect(), item = active.getBoundingClientRect();
        if (item.bottom > bounds.bottom) rail.scrollTop += item.bottom - bounds.bottom;
        else if (item.top < bounds.top) rail.scrollTop -= bounds.top - item.top;
      })).catch(() => {});
    }
  };
  const schedule = () => { if (!frame) frame = requestAnimationFrame(update); };
  const rememberItem = event => {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    const item = event.target.closest?.('.app-timeline__item');
    const key = item?.closest('[data-timeline-key]')?.dataset.timelineKey;
    if (key === undefined) return;
    leaving = true;
    try { sessionStorage.setItem(storageKey, key); } catch { /* Storage may be disabled. */ }
  };
  root.addEventListener('click', rememberItem, true);
  const target = scroller === document.scrollingElement ? window : scroller;
  target.addEventListener('scroll', schedule, {passive:true});
  const resize = new ResizeObserver(schedule); resize.observe(root); resize.observe(scroller);
  observers.set(root, { key: storageKey, dispose: () => { root.removeEventListener('click', rememberItem, true); target.removeEventListener('scroll', schedule); resize.disconnect(); cancelAnimationFrame(frame); } });
  let saved;
  try { saved = sessionStorage.getItem(storageKey); } catch { /* Storage may be disabled. */ }
  const restore = saved !== null && saved !== undefined && /^\d+$/.test(saved)
    ? dotnet.invokeMethodAsync('RestorePeriod', Number(saved)) : Promise.resolve();
  restore.catch(() => {}).finally(() => { restoring = false; if (root.isConnected) schedule(); });
}
export function jump(root, year) {
  const section = root?.querySelector(`[data-timeline-key="${Number(year)}"]`);
  if (!section) return false;
  const scroller = root.closest('.media-section-shell__content, .listen-content') ?? document.scrollingElement;
  scrollToTimelineSection(section, [...root.querySelectorAll('[data-timeline-key]')], scroller, innerHeight);
  return true;
}
export function disconnect(root) { observers.get(root)?.dispose(); observers.delete(root); }
