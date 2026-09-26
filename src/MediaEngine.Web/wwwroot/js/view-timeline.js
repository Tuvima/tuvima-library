import { activeTimelineSection, scrollToTimelineSection } from './timeline-scroll.js';
const observers = new WeakMap();

export function observeTimeline(anchor, dotnet, canLoadMore = false) {
  disconnectTimeline(anchor);
  if (!anchor?.isConnected) return;

  const root = anchor.closest('.media-section-shell__content');
  const timeline = anchor.parentElement?.querySelector('.view-timeline');
  if (!root || !timeline) return;

  let loadObserver = null;
  if (canLoadMore) {
    loadObserver = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) {
        dotnet.invokeMethodAsync('LoadMoreFromObserverAsync');
      }
    }, { root, rootMargin: '700px 0px' });
    loadObserver.observe(anchor);
  }

  let frame = 0;
  let lastPeriod = '';
  const updateActivePeriod = () => {
    frame = 0;
    const sections = [...timeline.querySelectorAll('.view-month[data-year][data-month]')];
    if (!sections.length) return;

    const rootRect = root.getBoundingClientRect();
    const rail = root.querySelector('.view-timeline-scrubber');
    if (rail) {
      const top = Math.max(rootRect.top + 20, rail.getBoundingClientRect().top);
      rail.style.setProperty('--timeline-rail-height', `${Math.max(120, rootRect.bottom - top - 20)}px`);
      if (matchMedia('(min-width:901px)').matches) {
        const years = [...rail.querySelectorAll('.view-timeline-scrubber__year')];
        const stride = Math.max(1, Math.ceil(years.length / Math.max(2, Math.floor((rootRect.bottom - top - 20) / 24))));
        years.forEach((year, index) => { year.hidden = index % stride !== 0 && index !== years.length - 1 && !year.classList.contains('is-active') && !year.classList.contains('is-occupied'); });
      } else {
        rail.querySelectorAll('.view-timeline-scrubber__year').forEach(year => year.hidden = false);
      }
    }
    const current = activeTimelineSection(sections, root, innerHeight);

    const period = `${current.dataset.year}-${current.dataset.month}`;
    if (period === lastPeriod) return;
    lastPeriod = period;
    dotnet.invokeMethodAsync(
      'SetActiveTimelinePeriod',
      Number(current.dataset.year),
      Number(current.dataset.month)).then(() => requestAnimationFrame(() => {
        if (!anchor.isConnected || !rail || !matchMedia('(min-width:901px)').matches) return;
        const active = rail.querySelector('.view-timeline-scrubber__year.is-active');
        if (!active) return;
        const bounds = rail.getBoundingClientRect();
        const item = active.getBoundingClientRect();
        if (item.bottom > bounds.bottom) rail.scrollTop += item.bottom - bounds.bottom;
        else if (item.top < bounds.top) rail.scrollTop -= bounds.top - item.top;
      })).catch(() => { /* The owning Blazor circuit may have disconnected. */ });
  };

  const scheduleUpdate = () => {
    if (frame) return;
    frame = requestAnimationFrame(updateActivePeriod);
  };

  root.addEventListener('scroll', scheduleUpdate, { passive: true });
  const resizeObserver = new ResizeObserver(scheduleUpdate);
  resizeObserver.observe(timeline);
  resizeObserver.observe(root);
  updateActivePeriod();

  observers.set(anchor, {
    loadObserver,
    resizeObserver,
    root,
    scheduleUpdate,
    cancelFrame: () => frame && cancelAnimationFrame(frame)
  });
}

export function jumpToPeriod(year, month) {
  const section = document.querySelector(`.view-month[data-year="${year}"][data-month="${month}"]`);
  if (!section) return false;
  const scroller = section.closest('.media-section-shell__content');
  const sections = [...scroller.querySelectorAll('.view-month[data-year][data-month]')];
  scrollToTimelineSection(section, sections, scroller, innerHeight);
  return true;
}

export function disconnectTimeline(anchor) {
  const value = observers.get(anchor);
  if (!value) return;
  value.loadObserver?.disconnect();
  value.resizeObserver?.disconnect();
  value.root.removeEventListener('scroll', value.scheduleUpdate);
  value.cancelFrame();
  observers.delete(anchor);
}

export function focusFirstPeriod() {
  const heading = document.querySelector('.view-timeline .view-month > h2');
  if (!heading) return;
  heading.scrollIntoView({
    block: 'start',
    behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
  });
  heading.focus({ preventScroll: true });
}
