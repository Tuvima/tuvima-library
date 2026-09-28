let opener = null;
const states = new WeakMap();

export function initialize(root, stage) {
  opener = document.activeElement;
  root.focus({ preventScroll: true });
  const state = { scale: 1, x: 0, y: 0, dragging: false, px: 0, py: 0, pointers: new Map(), pinchDistance: 0, pinchScale: 1 };
  states.set(stage, state);
  const image = () => stage.querySelector('img');
  const render = () => { const target = image(); if (target) target.style.transform = `translate(${state.x}px,${state.y}px) scale(${state.scale})`; };
  stage.addEventListener('wheel', event => {
    if (!image()) return;
    event.preventDefault();
    state.scale = Math.max(1, Math.min(4, state.scale + (event.deltaY < 0 ? .2 : -.2)));
    if (state.scale === 1) state.x = state.y = 0;
    render();
  }, { passive: false });
  const distance = () => { const points = [...state.pointers.values()]; return points.length < 2 ? 0 : Math.hypot(points[0].x - points[1].x, points[0].y - points[1].y); };
  stage.addEventListener('pointerdown', event => {
    if (!image()) return;
    state.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    stage.setPointerCapture(event.pointerId);
    if (state.pointers.size === 2) { state.pinchDistance = distance(); state.pinchScale = state.scale; state.dragging = false; }
    else if (state.scale > 1) { state.dragging = true; state.px = event.clientX; state.py = event.clientY; }
  });
  stage.addEventListener('pointermove', event => {
    if (!state.pointers.has(event.pointerId)) return;
    state.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (state.pointers.size === 2 && state.pinchDistance > 0) {
      state.scale = Math.max(1, Math.min(4, state.pinchScale * distance() / state.pinchDistance));
      if (state.scale === 1) state.x = state.y = 0;
      render(); return;
    }
    if (!state.dragging) return;
    state.x += event.clientX - state.px; state.y += event.clientY - state.py; state.px = event.clientX; state.py = event.clientY; render();
  });
  const release = event => { state.pointers.delete(event.pointerId); state.dragging = false; if (state.pointers.size < 2) state.pinchDistance = 0; };
  stage.addEventListener('pointerup', release);
  stage.addEventListener('pointercancel', release);
}
export function setZoom(stage, scale) { const state = states.get(stage); if (!state) return; state.scale = scale; if (scale === 1) state.x = state.y = 0; const image = stage.querySelector('img'); if (image) image.style.transform = `translate(${state.x}px,${state.y}px) scale(${scale})`; }
export function fit(stage) { setZoom(stage, 1); }
export function fullscreen(root) { if (!document.fullscreenElement) return root.requestFullscreen?.(); return document.exitFullscreen?.(); }
export function toggleVideo(video) { if (!video) return; if (video.paused) video.play(); else video.pause(); }
export function readVideoState(video) {
  const textTracks = Array.from(video?.textTracks || []).map((track, index) => ({
    Index: index, Label: track.label || track.language || `Subtitles ${index + 1}`,
    Language: track.language || '', Selected: track.mode === 'showing',
  }));
  const audioTracks = Array.from(video?.audioTracks || []).map((track, index) => ({
    Index: index, Label: track.label || track.language || `Audio ${index + 1}`,
    Language: track.language || '', Selected: !!track.enabled,
  }));
  return { Position: video?.currentTime || 0, Duration: Number.isFinite(video?.duration) ? video.duration : 0,
    Paused: video?.paused ?? true, Muted: video?.muted ?? false, Volume: video?.volume ?? 1,
    Speed: video?.playbackRate ?? 1, TextTracks: textTracks, AudioTracks: audioTracks };
}
export function selectVideoTextTrack(video, selectedIndex) {
  if (!video?.textTracks || selectedIndex >= video.textTracks.length) return false;
  for (let index = 0; index < video.textTracks.length; index++)
    video.textTracks[index].mode = index === selectedIndex ? 'showing' : 'disabled';
  return true;
}
export function selectVideoAudioTrack(video, selectedIndex) {
  if (!video?.audioTracks || video.audioTracks.length < 2 || selectedIndex < 0 || selectedIndex >= video.audioTracks.length) return false;
  for (let index = 0; index < video.audioTracks.length; index++)
    video.audioTracks[index].enabled = index === selectedIndex;
  return true;
}
export function seekVideo(video, position) { if (video) video.currentTime = Math.max(0, Math.min(Number.isFinite(video.duration) ? video.duration : position, position)); }
export function setVideoMuted(video, muted) { if (video) video.muted = !!muted; }
export function setVideoVolume(video, volume) { if (video) video.volume = Math.max(0, Math.min(1, volume)); }
export function setVideoSpeed(video, speed) { if (video) video.playbackRate = Math.max(.5, Math.min(3, speed)); }
export function canVideoPiP(video) { return !!video && !!document.pictureInPictureEnabled && typeof video.requestPictureInPicture === 'function'; }
export function canVideoFullscreen(root) { return !!root && !!document.fullscreenEnabled && typeof root.requestFullscreen === 'function'; }
export async function toggleVideoPiP(video) {
  if (!canVideoPiP(video)) return false;
  try {
    if (document.pictureInPictureElement) { await document.exitPictureInPicture(); return false; }
    await video.requestPictureInPicture();
    return true;
  } catch (error) {
    console.debug('Personal video picture in picture was rejected.', error);
    return false;
  }
}
export function isEditableFocus() { return !!document.activeElement?.closest?.('input, textarea, select, [contenteditable="true"]'); }
export function restoreFocus() { opener?.focus?.({ preventScroll: true }); opener = null; }
export function dispose(stage) { states.delete(stage); }
