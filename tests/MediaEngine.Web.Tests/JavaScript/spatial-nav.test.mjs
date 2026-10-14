import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/app.js', import.meta.url), 'utf8');
const start = source.indexOf('window.tuvimaSpatialNav = (function');
const end = source.indexOf('// -- Spatial navigation start', start);

function rect(left, top, width = 100, height = 60) {
    return { left, top, right: left + width, bottom: top + height };
}

// A fake element: closest() answers from a small map keyed by the selector fragment it recognises.
function element(name, box, options = {}) {
    const el = {
        name, box, tagName: options.tagName ?? 'A', type: options.type, disabled: false, isContentEditable: !!options.editable,
        attrs: options.attrs ?? {}, shelf: options.shelf ?? null, row: options.row ?? null, focused: 0, clicked: 0, scrolled: null,
        getBoundingClientRect: () => box,
        getClientRects: () => (options.hidden ? [] : [box]),
        getAttribute(attr) { return attr in this.attrs ? this.attrs[attr] : null; },
        hasAttribute(attr) { return attr in this.attrs; },
        closest(selector) {
            if (selector.includes('media-tile-shelf-scroll')) return this.row;
            if (selector.includes('media-tile-shelf,')) return this.shelf;
            if (selector.includes('layout-shell__intent-dock')) return options.dock ? this : null;
            if (selector.includes('inert')) return null;
            if (selector.includes('data-spatial-nav')) return options.off ? this : null;
            return null;
        },
        focus() { this.focused++; doc.activeElement = this; },
        scrollIntoView(args) { this.scrolled = args; },
        click() { this.clicked++; }
    };
    return el;
}

let doc;
function setup(elements, { formFactor = 'tv', modal = false, active = null, history = 3 } = {}) {
    const handlers = new Map();
    const body = { tagName: 'BODY' };
    const attrs = { 'data-form-factor': formFactor };
    doc = {
        body, activeElement: active ?? body,
        documentElement: { getAttribute: name => attrs[name] ?? null },
        querySelectorAll(selector) {
            if (selector.includes('aria-modal')) return modal ? [{ getClientRects: () => [1] }] : [];
            if (selector.includes('aria-expanded')) return [];
            return elements;
        }
    };
    doc.ownerDocument = doc;
    doc.contains = () => true;
    doc.addEventListener = (name, handler) => handlers.set(name, handler);
    doc.removeEventListener = name => handlers.delete(name);
    doc.querySelectorAll = doc.querySelectorAll.bind(doc);
    const backCalls = [];
    const win = { innerHeight: 800, history: { length: history, back: () => backCalls.push(1) }, matchMedia: () => ({ matches: false }) };
    win.document = doc;
    vm.runInNewContext(source.slice(start, end), { window: win, document: doc, Math, String });
    const unregister = win.registerSpatialNav(doc);
    const press = (key, extra = {}) => {
        const event = { key, target: doc.activeElement, prevented: false, preventDefault() { this.prevented = true; }, ...extra };
        handlers.get('keydown')?.(event);
        return event;
    };
    return { press, win, handlers, unregister, backCalls };
}

test('pickNext chooses the nearest item in the pressed direction and ignores the opposite side', () => {
    const { win } = setup([]);
    const nav = win.tuvimaSpatialNav;
    const from = rect(300, 300);
    const items = [{ id: 'left', rect: rect(100, 300) }, { id: 'right', rect: rect(500, 300) }, { id: 'farRight', rect: rect(900, 300) },
        { id: 'up', rect: rect(300, 100) }, { id: 'down', rect: rect(300, 500) }, { id: 'diagonal', rect: rect(520, 480) }];
    assert.equal(nav.pickNext(from, items, 'right').id, 'right');
    assert.equal(nav.pickNext(from, items, 'left').id, 'left');
    assert.equal(nav.pickNext(from, items, 'up').id, 'up');
    assert.equal(nav.pickNext(from, items, 'down').id, 'down');
    assert.equal(nav.pickNext(rect(900, 300), [{ id: 'self', rect: rect(900, 300) }], 'right'), null);
});

test('arrows stay inside a shelf sideways and move between shelves vertically on tv', () => {
    const rowA = {}, rowB = {};
    const a1 = element('a1', rect(0, 100), { row: rowA, shelf: rowA });
    const a2 = element('a2', rect(120, 100), { row: rowA, shelf: rowA });
    const b1 = element('b1', rect(0, 400), { row: rowB, shelf: rowB });
    const b2 = element('b2', rect(120, 400), { row: rowB, shelf: rowB });
    const { press } = setup([a1, a2, b1, b2], { active: a1 });

    assert.equal(press('ArrowRight').prevented, true);
    assert.equal(doc.activeElement, a2);
    assert.equal(a2.scrolled.inline, 'center');
    const end = press('ArrowRight');
    assert.equal(end.prevented, true, 'the end of a shelf holds focus');
    assert.equal(doc.activeElement, a2);
    press('ArrowDown');
    assert.equal(doc.activeElement, b2);
    assert.equal(b2.scrolled.block, 'center');
    press('ArrowUp');
    assert.equal(doc.activeElement, a2);
});

test('tv focuses the first visible control when nothing is focused', () => {
    const lower = element('lower', rect(0, 300));
    const upper = element('upper', rect(0, 50));
    const { press } = setup([lower, upper]);
    assert.equal(press('ArrowDown').prevented, true);
    assert.equal(doc.activeElement, upper);
});

test('arrows are left alone inside fields, sliders and when a modal is open', () => {
    const a = element('a', rect(0, 0));
    const b = element('b', rect(200, 0));
    const input = element('input', rect(0, 100), { tagName: 'INPUT', type: 'text' });
    const slider = element('slider', rect(0, 200), { tagName: 'DIV', attrs: { role: 'slider', tabindex: '0' } });
    const editable = element('editable', rect(0, 300), { tagName: 'DIV', editable: true });
    for (const field of [input, slider, editable]) {
        const { press } = setup([a, b, field], { active: field });
        assert.equal(press('ArrowRight').prevented, false, field.name);
        assert.equal(press('Backspace').prevented, false, field.name);
    }
    const modal = setup([a, b], { active: a, modal: true });
    assert.equal(modal.press('ArrowRight').prevented, false);
    assert.equal(modal.press('Escape').prevented, false);
});

test('outside tv it arms only after an arrow press on a shelf tile', () => {
    const row = {};
    const tile = element('tile', rect(0, 0), { row, shelf: row });
    const next = element('next', rect(200, 0), { row, shelf: row });
    const plain = element('plain', rect(0, 400));

    const idle = setup([tile, next, plain], { formFactor: 'desktop', active: plain });
    assert.equal(idle.press('ArrowDown').prevented, false);
    assert.equal(idle.press('Escape').prevented, false, 'back is inactive until armed');

    const armed = setup([tile, next, plain], { formFactor: 'desktop', active: tile });
    assert.equal(armed.press('ArrowRight').prevented, true);
    assert.equal(doc.activeElement, next);
});

test('Enter activates custom controls, Escape and Backspace go back, unregister removes the listener', () => {
    const custom = element('custom', rect(0, 0), { tagName: 'DIV', attrs: { tabindex: '0', role: 'button' } });
    const link = element('link', rect(200, 0));
    const { press, backCalls, handlers, unregister } = setup([custom, link], { active: custom });

    assert.equal(press('Enter').prevented, true);
    assert.equal(custom.clicked, 1);
    doc.activeElement = link;
    assert.equal(press('Enter').prevented, false, 'native links activate themselves');
    assert.equal(press('Escape').prevented, true);
    assert.equal(press('Backspace').prevented, true);
    assert.equal(backCalls.length, 2);

    unregister();
    assert.equal(handlers.size, 0);
});

test('registerSpatialNav is exported and starts on the document', () => {
    assert.ok(start > 0 && end > start);
    assert.match(source, /window\.registerSpatialNav\(document\);/);
});

test('arrows pass over the fixed dock while content remains below, then fall back to it', () => {
    const hero = element('hero', rect(0, 100));
    const shelf = {};
    const tile = element('tile', rect(0, 600), { row: shelf, shelf });
    const dock = element('dock', rect(0, 900), { dock: true });
    const { press } = setup([hero, tile, dock], { active: hero });

    press('ArrowDown');
    assert.equal(doc.activeElement, tile, 'content below the hero is reached before the dock');
    press('ArrowDown');
    assert.equal(doc.activeElement, dock, 'the dock is the way down from the last shelf');
    press('ArrowUp');
    assert.equal(doc.activeElement, tile, 'up from the dock returns to the nearest content');
});
