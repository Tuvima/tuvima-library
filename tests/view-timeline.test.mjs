import { test } from 'node:test';
import assert from 'node:assert/strict';
import { observeTimeline, disconnectTimeline, resolveTimelineScrollRoot, timelineRailHeight } from '../src/MediaEngine.Web/wwwroot/js/view-timeline.js';

test('empty filtered results have no timeline element and must not throw', () => {
  for (const anchor of [null, undefined, { isConnected: false }]) {
    assert.doesNotThrow(() => observeTimeline(anchor, null));
    assert.doesNotThrow(() => disconnectTimeline(anchor));
  }
});

test('timeline follows the bounded shell when short-view content overflow becomes visible', () => {
  const shell={parentElement:null,overflowY:'auto'}, content={parentElement:shell,overflowY:'visible'}, anchor={parentElement:content};
  globalThis.getComputedStyle=element=>element;
  assert.equal(resolveTimelineScrollRoot(anchor),shell);
  content.overflowY='auto'; assert.equal(resolveTimelineScrollRoot(anchor),content);
  delete globalThis.getComputedStyle;
});
test('date navigator height is bounded by the viewport even when content has grown', () => {
  assert.equal(timelineRailHeight({top:65,bottom:200000},100,720),600);
  assert.equal(timelineRailHeight({top:65,bottom:720},100,720),600);
});
