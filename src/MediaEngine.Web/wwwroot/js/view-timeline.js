import { activeTimelineSection, scrollToTimelineSection } from './timeline-scroll.js';
const observers = new WeakMap();

// Short desktop layouts move scrolling from the content pane to the shell.
// Resolve the bounded owner each time the viewport changes, rather than using
// the content's growing height to size its own sticky date navigator.
export function resolveTimelineScrollRoot(anchor) {
  for (let element=anchor?.parentElement; element; element=element.parentElement) {
    if (['auto','scroll'].includes(getComputedStyle(element).overflowY)) return element;
  }
  return null;
}

export function timelineRailHeight(rootRect, railTop, viewportHeight) {
  const bottom=Math.min(viewportHeight,rootRect.bottom);
  const top=Math.max(0,rootRect.top)+20;
  return Math.max(120,bottom-Math.max(top,Math.min(railTop,bottom-140))-20);
}

export function observeTimeline(anchor, dotnet, canLoadMore = false) {
  disconnectTimeline(anchor);
  if (!anchor?.isConnected) return;

  let root = resolveTimelineScrollRoot(anchor);
  const timeline = anchor.parentElement?.querySelector('.view-timeline');
  if (!root || !timeline) return;

  let loadObserver = null;
  const bindLoadObserver = () => {
    loadObserver?.disconnect(); loadObserver=null;
    if (!canLoadMore) return;
    loadObserver = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) dotnet.invokeMethodAsync('LoadMoreFromObserverAsync');
    }, { root, rootMargin:'700px 0px' });
    loadObserver.observe(anchor);
    if (observers.has(anchor)) observers.get(anchor).loadObserver=loadObserver;
  };
  bindLoadObserver();

  let frame = 0;
  let lastPeriod = '';
  const updateActivePeriod = () => {
    frame = 0;
    const currentRoot=resolveTimelineScrollRoot(anchor);
    if (currentRoot && currentRoot!==root) {
      root.removeEventListener('scroll',scheduleUpdate); resizeObserver.unobserve(root);
      root=currentRoot; root.addEventListener('scroll',scheduleUpdate,{passive:true}); resizeObserver.observe(root);
      if (observers.has(anchor)) observers.get(anchor).root=root;
      bindLoadObserver(); lastPeriod='';
    }
    const sections = [...timeline.querySelectorAll('.view-month[data-year][data-month]')];
    if (!sections.length) return;

    const rootRect = root.getBoundingClientRect();
    const rail = root.querySelector('.view-timeline-scrubber');
    if (rail) {
      const availableHeight=timelineRailHeight(rootRect,rail.getBoundingClientRect().top,innerHeight);
      rail.style.setProperty('--timeline-rail-height', `${availableHeight}px`);
      if (matchMedia('(min-width:901px)').matches) {
        const years = [...rail.querySelectorAll('.view-timeline-scrubber__year')];
        const stride = Math.max(1, Math.ceil(years.length / Math.max(2, Math.floor(availableHeight / 24))));
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
  window.addEventListener('resize',scheduleUpdate,{passive:true});
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
  const scroller = resolveTimelineScrollRoot(section);
  if (!scroller) return false;
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
  window.removeEventListener('resize',value.scheduleUpdate);
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
