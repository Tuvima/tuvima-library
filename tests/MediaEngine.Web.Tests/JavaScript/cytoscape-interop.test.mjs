import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// Loads cytoscape-interop.js into a sandbox with a fake cytoscape so the phone-facing contract can be checked without a
// browser: touch zoom and pan stay enabled, and the Zoom/Fit/Reset buttons drive the graph.
function load() {
    const source = readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/js/cytoscape-interop.js', import.meta.url), 'utf8');
    const animations = [];
    const calls = { options: null, unselect: 0 };
    const collection = { unselect() { calls.unselect += 1; }, style() { return collection; }, forEach() { }, };
    const cy = {
        on() { },
        destroy() { },
        zoom: () => 1,
        minZoom: () => 0.3,
        maxZoom: () => 3,
        width: () => 360,
        height: () => 400,
        animate(target) { animations.push(target); },
        elements() { return collection; },
        nodes() { return collection; },
        edges() { return collection; },
    };
    const sandbox = {
        window: {},
        document: { getElementById: () => ({}) },
        cytoscape(options) { calls.options = options; return cy; },
    };
    vm.runInNewContext(source, sandbox);
    return { interop: sandbox.window.CytoscapeInterop, animations, calls };
}

test('initGraph keeps pinch zoom and touch panning enabled', () => {
    const { interop, calls } = load();
    interop.initGraph('cytoscape-container', '[]', '[]', 'cose', null);
    assert.equal(calls.options.userZoomingEnabled, true);
    assert.equal(calls.options.userPanningEnabled, true);
});

test('zoomIn and zoomOut step about the middle of the graph and stay inside the zoom limits', () => {
    const { interop, animations } = load();
    interop.initGraph('cytoscape-container', '[]', '[]', 'cose', null);
    interop.zoomIn();
    interop.zoomOut();
    assert.equal(animations[0].zoom.level, 1.25);
    assert.deepEqual({ ...animations[0].zoom.renderedPosition }, { x: 180, y: 200 });
    assert.equal(animations[1].zoom.level, 0.8);
});

test('resetView clears the selection and fits the whole graph', () => {
    const { interop, animations, calls } = load();
    interop.initGraph('cytoscape-container', '[]', '[]', 'cose', null);
    interop.resetView();
    assert.equal(calls.unselect, 1);
    assert.ok(animations.some(target => target.fit));
});

test('the zoom helpers do nothing before a graph exists', () => {
    const { interop, animations } = load();
    interop.zoomIn();
    interop.fit();
    interop.resetView();
    assert.equal(animations.length, 0);
});
