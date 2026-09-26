// Follow normal scrolling without snapping or forcing the final period.
// The reading line moves continuously through the visible pane as scrolling
// progresses, so short final sections become active before the scroll limit.
function geometry(sections, scroller, viewportHeight) {
  const bounds = scroller.getBoundingClientRect();
  const top = Math.max(0, bounds.top);
  const bottom = Math.min(viewportHeight, bounds.bottom);
  const start = Math.min(top + 120, bottom - 24);
  const range = scroller.scrollHeight - scroller.clientHeight;
  const lastHeight = sections.at(-1)?.getBoundingClientRect().height || 0;
  const travel = Math.max(0, bottom - lastHeight - 24 - start);
  return {start, range, travel};
}
export function activeTimelineSection(sections, scroller, viewportHeight) {
  if (!sections.length) return undefined;
  const {start, range, travel} = geometry(sections, scroller, viewportHeight);
  const progress = range > 0 ? Math.max(0, Math.min(1, scroller.scrollTop / range)) : 0;
  const line = start + travel * progress;
  let current = sections[0];
  for (const section of sections) {
    if (section.getBoundingClientRect().top <= line + 1) current = section;
    else break;
  }
  return current;
}

export function scrollToTimelineSection(section, sections, scroller, viewportHeight) {
  const {start, range, travel} = geometry(sections, scroller, viewportHeight);
  if (!(range > 0) || !scroller.scrollTo) {
    section.scrollIntoView({block:'start', behavior:'auto'});
    return;
  }
  const position = section.getBoundingClientRect().top + scroller.scrollTop;
  const target = (position - start) / (1 + travel / range);
  scroller.scrollTo({top:Math.max(0, Math.min(range, target)), behavior:'auto'});
}
