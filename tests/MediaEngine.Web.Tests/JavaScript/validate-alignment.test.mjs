import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// A very small DOM: elements carry a tag, classes, attributes, children and a fixed rect. The selector engine understands comma lists,
// descendant spaces, tags, .classes, [attr] and [attr="value"], which is all tools/validate-alignment.js uses.
const matchCompound = (el, compound) => {
    const tokens = compound.match(/^[a-z][\w-]*|\.[\w-]+|\[[^\]]+\]/gi) ?? [];
    return tokens.every(token => {
        if (token.startsWith('.')) return el.classes.includes(token.slice(1));
        if (token.startsWith('[')) {
            const [, name, value] = token.match(/^\[([\w-]+)(?:="([^"]*)")?\]$/);
            return value === undefined ? el.attrs[name] !== undefined : el.attrs[name] === value;
        }
        return el.tagName.toLowerCase() === token.toLowerCase();
    });
};
const matchGroup = (el, group) => {
    const parts = group.trim().split(/\s+>?\s*/);
    if (!matchCompound(el, parts.at(-1))) return false;
    let ancestor = el.parentElement, index = parts.length - 2;
    while (index >= 0) {
        while (ancestor && !matchCompound(ancestor, parts[index])) ancestor = ancestor.parentElement;
        if (!ancestor) return false;
        ancestor = ancestor.parentElement; index--;
    }
    return true;
};
const matches = (el, selector) => selector.split(',').some(group => matchGroup(el, group));

class El {
    constructor(tag, { classes = [], attrs = {}, rect = { left: 0, top: 0, width: 100, height: 44 }, style = {}, text = '' } = {}) {
        this.tagName = tag.toUpperCase(); this.classes = classes; this.attrs = attrs; this.rect = rect; this.cssText = style; this.children = []; this.parentElement = null; this.text = text;
    }
    add(child) { child.parentElement = this; this.children.push(child); return child; }
    get className() { return this.classes.join(' '); }
    get textContent() { return this.text + this.children.map(c => c.textContent).join(''); }
    getAttribute(name) { return this.attrs[name] ?? null; }
    getBoundingClientRect() { const r = this.rect; return { ...r, right: r.left + r.width, bottom: r.top + r.height }; }
    matches(selector) { return matches(this, selector); }
    closest(selector) { for (let el = this; el; el = el.parentElement) if (el.matches(selector)) return el; return null; }
    contains(other) { for (let el = other; el; el = el.parentElement) if (el === this) return true; return false; }
    querySelectorAll(selector) { const out = []; const walk = n => n.children.forEach(c => { if (c.matches(selector)) out.push(c); walk(c); }); walk(this); return out; }
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
}

const source = readFileSync(new URL('../../../tools/validate-alignment.js', import.meta.url), 'utf8');
const audit = (root, width = 1440) => {
    globalThis.document = { documentElement: { clientWidth: width }, querySelectorAll: selector => [root, ...root.querySelectorAll(selector)].filter(e => e.matches(selector)) };
    globalThis.getComputedStyle = el => ({ display: 'block', visibility: 'visible', opacity: '1', fontSize: '14px', lineHeight: '20px', flexDirection: 'row', rowGap: '0px', ...el.cssText });
    return (0, eval)('(' + source + ')')();
};

const flex = { display: 'flex' };
const rect = (left, top, width, height) => ({ left, top, width, height });
const button = (left, top, width, height, { primary = false, label = 'Button' } = {}) => {
    const b = new El('button', { classes: primary ? ['app-button', 'app-button--filled', 'app-tone--primary'] : ['app-button'], rect: rect(left, top, width, height) });
    b.add(new El('span', { classes: ['tl-button-label'], rect: rect(left + 8, top + 10, width - 16, 20), text: label }));
    return b;
};

// A settings row: label block left, control right, both centred on the 56 px row.
const settingsRow = ({ labelTop = 12, controlTop = 12, labelHeight = 32, rowHeight = 56 } = {}) => {
    const row = new El('div', { classes: ['app-setting-row'], rect: rect(0, 0, 800, rowHeight), style: { display: 'flex', flexDirection: 'column' } });
    const line = row.add(new El('div', { classes: ['app-setting-row__line'], rect: rect(0, 12, 800, 32), style: flex }));
    const text = line.add(new El('div', { classes: ['app-setting-row__text'], rect: rect(0, labelTop, 300, labelHeight) }));
    text.add(new El('span', { classes: ['app-setting-row__label'], rect: rect(0, labelTop, 120, 20), text: 'Language' }));
    const control = line.add(new El('div', { classes: ['app-setting-row__control'], rect: rect(600, controlTop, 200, 32) }));
    control.add(new El('button', { rect: rect(600, controlTop, 200, 32), text: 'English' }));
    return row;
};

test('a settings row with the label and the control on one centre line passes', () => {
    const report = audit(settingsRow());
    assert.equal(report.passed, true, JSON.stringify(report.failures));
});

test('a control that sits lower than its label fails the centre line check', () => {
    const report = audit(settingsRow({ controlTop: 22 }));
    assert.equal(report.passed, false);
    assert.ok(report.counts.centreLine >= 1);
});

test('a settings row whose height is off the 44 / 56 / 64 ladder fails', () => {
    const report = audit(settingsRow({ rowHeight: 70 }));
    assert.equal(report.passed, false);
    assert.equal(report.counts.rowHeight, 1);
});

test('a reserved status line under the row does not count toward the row height', () => {
    const row = settingsRow({ rowHeight: 56 + 8 + 20 });
    row.cssText.gap = '8px'; row.cssText.rowGap = '8px';
    row.add(new El('div', { classes: ['app-setting-row__status'], rect: rect(0, 64, 800, 20), text: 'Connected' }));
    const report = audit(row);
    assert.equal(report.passed, true, JSON.stringify(report.failures));
});

const footer = (buttons, extra = {}) => {
    const row = new El('footer', { classes: ['app-dialog-shell__footer'], rect: rect(0, 0, 640, 44), style: flex, ...extra });
    buttons.forEach(b => row.add(b));
    return row;
};

test('a footer with equal heights and the primary last passes', () => {
    const report = audit(footer([button(300, 0, 80, 44, { label: 'Reset' }), button(400, 0, 80, 44, { label: 'Discard' }), button(500, 0, 120, 44, { primary: true, label: 'Save changes' })]));
    assert.equal(report.passed, true, JSON.stringify(report.failures));
});

test('buttons of different heights in one row fail', () => {
    const report = audit(footer([button(400, 0, 80, 36, { label: 'Discard' }), button(500, 0, 120, 44, { primary: true, label: 'Save' })]));
    assert.equal(report.passed, false);
    assert.equal(report.counts.buttonHeights, 1);
});

test('a primary button that is not last fails', () => {
    const report = audit(footer([button(300, 0, 120, 44, { primary: true, label: 'Save' }), button(440, 0, 80, 44, { label: 'Discard' })]));
    assert.equal(report.passed, false);
    assert.equal(report.counts.primaryOrder, 1);
});

test('on a phone the stacked buttons put the primary first', () => {
    const stacked = primaryFirst => {
        const save = button(0, primaryFirst ? 0 : 52, 320, 44, { primary: true, label: 'Save' });
        const discard = button(0, primaryFirst ? 52 : 0, 320, 44, { label: 'Discard' });
        return footer(primaryFirst ? [save, discard] : [discard, save], { rect: rect(0, 0, 320, 96), style: { display: 'flex', flexDirection: 'column' } });
    };
    assert.equal(audit(stacked(true), 360).passed, true);
    const wrong = audit(stacked(false), 360);
    assert.equal(wrong.passed, false);
    assert.equal(wrong.counts.primaryOrder, 1);
});

test('a toolbar label that wraps onto two lines fails', () => {
    const bar = new El('div', { classes: ['app-action-bar'], rect: rect(0, 0, 400, 44), style: flex });
    const b = bar.add(button(0, 0, 90, 44, { label: 'Search Wikidata' }));
    b.children[0].rect = rect(8, 2, 74, 40); // two lines of 20 px
    const report = audit(bar);
    assert.equal(report.passed, false);
    assert.equal(report.counts.labelWraps, 1);
});

test('text placed under one button of a row lifts that button off the row axis and fails', () => {
    const row = new El('div', { classes: ['app-setting-row__line'], rect: rect(0, 0, 800, 70), style: flex });
    const test_ = row.add(new El('div', { rect: rect(0, 0, 80, 70), style: { display: 'flex', flexDirection: 'column' } }));
    test_.add(new El('button', { rect: rect(0, 0, 80, 40), text: 'Test' }));
    test_.add(new El('span', { attrs: { role: 'status' }, rect: rect(0, 46, 80, 18), text: 'Connected' }));
    row.add(new El('button', { rect: rect(100, 15, 80, 40), text: 'Remove' }));
    const report = audit(row);
    assert.equal(report.passed, false);
    assert.ok(report.counts.textOnControl >= 1, JSON.stringify(report.counts));
    assert.ok(report.counts.controlOffset >= 1, JSON.stringify(report.counts));
});

test('the Match row with the dropdown, an icon Search and the segmented control on one centre line passes', () => {
    const row = new El('div', { classes: ['tl-match__matchrow'], rect: rect(0, 0, 900, 44), style: flex });
    const start = row.add(new El('div', { rect: rect(0, 4, 300, 36), style: flex }));
    start.add(new El('button', { rect: rect(0, 4, 200, 36), text: 'TheTVDB' }));
    start.add(new El('button', { rect: rect(208, 4, 36, 36), attrs: { 'aria-label': 'Search' } }));
    const seg = row.add(new El('div', { classes: ['app-segmented-control'], rect: rect(640, 2, 260, 40) }));
    const item = seg.add(new El('button', { classes: ['app-segmented-control__item'], rect: rect(644, 4, 120, 36) }));
    item.add(new El('span', { rect: rect(650, 12, 108, 20), text: 'Media providers' }));
    const report = audit(row);
    assert.equal(report.passed, true, JSON.stringify(report.failures));
});

test('a form field label above its field is allowed inside a column of a row', () => {
    const row = new El('div', { attrs: { 'data-alignment-row': '' }, rect: rect(0, 0, 600, 64), style: flex });
    const field = row.add(new El('div', { classes: ['app-field'], rect: rect(0, 0, 280, 64), style: { display: 'flex', flexDirection: 'column' } }));
    field.add(new El('label', { classes: ['app-field__label'], rect: rect(0, 0, 100, 16), text: 'Title' }));
    field.add(new El('input', { rect: rect(0, 22, 280, 40) }));
    const other = row.add(new El('div', { classes: ['app-field'], rect: rect(300, 0, 280, 64), style: { display: 'flex', flexDirection: 'column' } }));
    other.add(new El('label', { classes: ['app-field__label'], rect: rect(300, 0, 100, 16), text: 'Year' }));
    other.add(new El('input', { rect: rect(300, 22, 280, 40) }));
    const report = audit(row);
    assert.equal(report.passed, true, JSON.stringify(report.failures));
});

// The shared form row (Editor v6 D12): a grid with a label line, a control line and a message line.
const formRow = ({ controlTops = [22, 22, 22], heights = [44, 44, 44], withMessage = true, display = 'grid', helpOn = -1 } = {}) => {
    const root = new El('div', { classes: ['app-form-row'], attrs: { 'data-alignment-row': '', 'data-form-row': '' }, rect: rect(0, 0, 900, 110), style: { display: 'flex', flexDirection: 'column' } });
    // The fields sit in an inner grid; the test marks it as an alignment row only to force the text-on-control check onto its columns.
    const row = root.add(new El('div', { classes: ['app-form-row__fields'], attrs: display === 'flex' ? { 'data-alignment-row': '' } : {}, rect: rect(0, 0, 900, 90), style: { display } }));
    controlTops.forEach((top, index) => {
        const left = index * 300;
        const field = row.add(new El('div', { classes: ['app-field'], rect: rect(left, 0, 280, 90), style: { display: 'grid' } }));
        field.add(new El('label', { classes: ['app-field__label'], rect: rect(left, 0, 100, 16), text: 'Label' }));
        field.add(new El('input', { rect: rect(left, top, 280, heights[index]) }));
        if (index === helpOn) field.add(new El('div', { classes: ['app-field__help'], rect: rect(left, top + heights[index] + 4, 200, 18), text: 'Used only for ordering' }));
    });
    if (withMessage) root.add(new El('div', { classes: ['app-form-row__message'], attrs: { 'data-control-text': '', role: 'status' }, rect: rect(0, 92, 900, 18), text: '10 results' }));
    return root;
};

test('a form row whose controls share one top and height passes, with a help line under one field and a message line', () => {
    const report = audit(formRow({ helpOn: 1 }));
    assert.equal(report.passed, true, JSON.stringify(report.failures));
});

test('a form row with one control lower than its neighbours fails the form row check', () => {
    const report = audit(formRow({ controlTops: [22, 40, 22] }));
    assert.equal(report.passed, false);
    assert.equal(report.counts.formRow, 1);
});

test('a form row with one control of a different height fails the form row check', () => {
    const report = audit(formRow({ heights: [44, 36, 44] }));
    assert.equal(report.passed, false);
    assert.equal(report.counts.formRow, 1);
});

test('the form row message line and a field help line in a multi-item form row are not reported as text on a control', () => {
    // Forced through the flex-row path so the text-on-control check actually looks at the row's columns.
    const report = audit(formRow({ display: 'flex', helpOn: 0 }));
    assert.equal(report.counts.textOnControl, undefined, JSON.stringify(report.failures));
});

test('a text field with a start adornment button is measured by its control frame, not by the button inside it', () => {
    const root = new El('div', { classes: ['app-form-row'], rect: rect(0, 0, 600, 90), style: { display: 'flex', flexDirection: 'column' } });
    const grid = root.add(new El('div', { classes: ['app-form-row__fields'], rect: rect(0, 0, 600, 90), style: { display: 'grid' } }));
    const plain = grid.add(new El('div', { classes: ['app-field'], rect: rect(0, 0, 280, 90), style: { display: 'grid' } }));
    plain.add(new El('label', { classes: ['app-field__label'], rect: rect(0, 0, 100, 16), text: 'Title' }));
    plain.add(new El('input', { rect: rect(0, 22, 280, 44) }));
    const adorned = grid.add(new El('div', { classes: ['app-field'], rect: rect(300, 0, 280, 90), style: { display: 'grid' } }));
    adorned.add(new El('label', { classes: ['app-field__label'], rect: rect(300, 0, 100, 16), text: 'Search' }));
    const frame = adorned.add(new El('div', { classes: ['tl-input-control'], rect: rect(300, 22, 280, 44) }));
    frame.add(new El('button', { classes: ['tl-input-adornment'], rect: rect(304, 28, 32, 32), attrs: { 'aria-label': 'Clear' } }));
    frame.add(new El('input', { rect: rect(340, 22, 240, 44) }));
    const report = audit(root);
    assert.equal(report.passed, true, JSON.stringify(report.failures));
});

test('a wrapper marked as a form row field is measured by its control, not by the whole wrapper with its label', () => {
    const root = new El('div', { classes: ['app-form-row'], rect: rect(0, 0, 600, 90), style: { display: 'flex', flexDirection: 'column' } });
    const grid = root.add(new El('div', { classes: ['app-form-row__fields'], rect: rect(0, 0, 600, 90), style: { display: 'grid' } }));
    const plain = grid.add(new El('div', { classes: ['app-field'], rect: rect(0, 0, 280, 90), style: { display: 'grid' } }));
    plain.add(new El('label', { classes: ['app-field__label'], rect: rect(0, 0, 100, 16), text: 'Title' }));
    plain.add(new El('input', { rect: rect(0, 22, 280, 44) }));
    const wrapper = (controlTop) => {
        const w = grid.add(new El('div', { attrs: { 'data-form-row-field': '' }, rect: rect(300, 0, 280, 90), style: { display: 'grid' } }));
        w.add(new El('label', { classes: ['app-field__label'], rect: rect(300, 0, 100, 16), text: 'Tags' }));
        w.add(new El('div', { rect: rect(300, controlTop, 280, 44) })).add(new El('input', { rect: rect(300, controlTop, 280, 44) }));
        return w;
    };
    wrapper(22);
    assert.equal(audit(root).passed, true, 'aligned control in a wrapper passes although the wrapper itself starts higher');
    grid.children.pop();
    wrapper(40);
    const report = audit(root);
    assert.equal(report.passed, false);
    assert.equal(report.counts.formRow, 1);
});

test('a button or checkbox centred on the control line passes, and one off the line fails the form row check', () => {
    const build = checkboxTop => {
        const root = new El('div', { classes: ['app-form-row'], rect: rect(0, 0, 600, 90), style: { display: 'flex', flexDirection: 'column' } });
        const grid = root.add(new El('div', { classes: ['app-form-row__fields'], rect: rect(0, 0, 600, 90), style: { display: 'grid' } }));
        const field = grid.add(new El('div', { classes: ['app-field'], rect: rect(0, 0, 280, 90), style: { display: 'grid' } }));
        field.add(new El('label', { classes: ['app-field__label'], rect: rect(0, 0, 100, 16), text: 'Title' }));
        field.add(new El('input', { rect: rect(0, 22, 280, 44) }));
        const box = grid.add(new El('label', { classes: ['app-checkbox'], rect: rect(300, checkboxTop, 160, 24) }));
        box.add(new El('input', { rect: rect(300, checkboxTop, 24, 24) }));
        return root;
    };
    assert.equal(audit(build(32)).passed, true);
    assert.equal(audit(build(60)).counts.formRow, 1);
});

test('an icon button inside a display: contents tooltip wrapper is measured by the button, so one placed off the control line fails', () => {
    const build = buttonTop => {
        const root = new El('div', { classes: ['app-form-row'], rect: rect(0, 0, 600, 110), style: { display: 'flex', flexDirection: 'column' } });
        const grid = root.add(new El('div', { classes: ['app-form-row__fields'], rect: rect(0, 0, 600, 110), style: { display: 'grid' } }));
        const field = grid.add(new El('div', { classes: ['app-field'], rect: rect(0, 0, 280, 90), style: { display: 'grid' } }));
        field.add(new El('label', { classes: ['app-field__label'], rect: rect(0, 0, 100, 16), text: 'Title' }));
        field.add(new El('input', { rect: rect(0, 22, 280, 44) }));
        const owner = grid.add(new El('span', { classes: ['playback-tooltip-owner'], rect: rect(0, 0, 0, 0), style: { display: 'contents' } }));
        owner.add(new El('button', { classes: ['app-icon-button'], rect: rect(300, buttonTop, 44, 44), attrs: { 'aria-label': 'Search' } }));
        return root;
    };
    assert.equal(audit(build(22)).passed, true);
    const wrong = audit(build(66));
    assert.equal(wrong.passed, false);
    assert.equal(wrong.counts.formRow, 1);
});

// A wrapped form row (AppFormRow Wrap) is several visual lines: each line is judged by itself, so the second line sits lower than the first.
const wrappedRow = ({ secondLineButtonTop = 144, secondLineFieldTop = 144, helpOnFirstLine = true } = {}) => {
    const root = new El('div', { classes: ['app-form-row'], attrs: { 'data-alignment-row': '', 'data-form-row': '' }, rect: rect(0, 0, 600, 220), style: { display: 'flex', flexDirection: 'column' } });
    const grid = root.add(new El('div', { classes: ['app-form-row__fields'], rect: rect(0, 0, 600, 220), style: { display: 'flex', flexWrap: 'wrap' } }));
    const field = (left, top, height, withHelp) => {
        const f = grid.add(new El('div', { classes: ['app-field'], rect: rect(left, top - 22, 280, withHelp ? 90 : 66), style: { display: 'flex', flexDirection: 'column' } }));
        f.add(new El('label', { classes: ['app-field__label'], rect: rect(left, top - 22, 100, 16), text: 'Label' }));
        f.add(new El('input', { rect: rect(left, top, 280, height) }));
        if (withHelp) f.add(new El('div', { classes: ['app-field__help'], rect: rect(left, top + height + 4, 200, 18), text: 'Used only for ordering' }));
        return f;
    };
    field(0, 22, 44, helpOnFirstLine);
    field(300, 22, 44, false);
    // The button is a wrapper with a blank label line, so its control top is the same as the fields' on its line.
    const wrapper = grid.add(new El('div', { attrs: { 'data-form-row-field': 'fit' }, rect: rect(0, 100, 80, 66), style: { display: 'flex', flexDirection: 'column' } }));
    wrapper.add(new El('button', { rect: rect(0, secondLineButtonTop, 80, 44) }));
    field(300, secondLineFieldTop, 44, false);
    return root;
};

test('a wrapped form row passes when the controls of each visual line share a top, even with help text under a field on the first line', () => {
    const report = audit(wrappedRow());
    assert.equal(report.passed, true, JSON.stringify(report.failures));
});

test('a wrapped form row fails when the button sits above or below the controls of its own line', () => {
    const report = audit(wrappedRow({ secondLineButtonTop: 134 }));
    assert.equal(report.passed, false);
    assert.equal(report.counts.formRow, 1);
});

test('a wrapped form row fails when a field on the second line sits lower than the first control of that line', () => {
    const report = audit(wrappedRow({ secondLineFieldTop: 154 }));
    assert.equal(report.passed, false);
    assert.equal(report.counts.formRow, 1);
});

test('help text inside a flex row nested in a form row column is still reported as text on a control', () => {
    const root = new El('div', { classes: ['app-form-row'], rect: rect(0, 0, 600, 90), style: { display: 'flex', flexDirection: 'column' } });
    const grid = root.add(new El('div', { classes: ['app-form-row__fields'], rect: rect(0, 0, 600, 90), style: { display: 'grid' } }));
    const wrapper = grid.add(new El('div', { attrs: { 'data-form-row-field': '' }, rect: rect(0, 0, 600, 90), style: { display: 'block' } }));
    const nested = wrapper.add(new El('div', { classes: ['app-field-group'], attrs: { 'data-alignment-row': '' }, rect: rect(0, 0, 600, 90), style: flex }));
    const column = nested.add(new El('div', { classes: ['app-field'], rect: rect(0, 0, 280, 90), style: { display: 'flex', flexDirection: 'column' } }));
    column.add(new El('input', { rect: rect(0, 0, 280, 44) }));
    column.add(new El('div', { classes: ['app-field__help'], rect: rect(0, 50, 200, 18), text: 'Not found' }));
    nested.add(new El('input', { rect: rect(300, 0, 280, 44) }));
    const report = audit(root);
    assert.ok(report.counts.textOnControl >= 1, JSON.stringify(report.counts));
});

test('helper text inside a column of an ordinary alignment row is still reported as text on a control', () => {
    const row = new El('div', { attrs: { 'data-alignment-row': '' }, rect: rect(0, 0, 600, 80), style: flex });
    const column = row.add(new El('div', { rect: rect(0, 0, 280, 80), style: { display: 'flex', flexDirection: 'column' } }));
    column.add(new El('input', { rect: rect(0, 0, 280, 44) }));
    column.add(new El('div', { classes: ['app-field__help'], rect: rect(0, 50, 200, 18), text: 'Not found' }));
    row.add(new El('input', { rect: rect(300, 0, 280, 44) }));
    const report = audit(row);
    assert.ok(report.counts.textOnControl >= 1, JSON.stringify(report.counts));
});
