// Read-only browser audit of the Look v3 D15 alignment rules. Evaluate in the rendered page at 360, 768 and 1440 px: on the design-system
// preview page and on Settings Playback, the editor Match tab, a details page and the folder picker. Same shape as validate-touch-targets.js:
// one arrow function, no changes to the page, a report object back (`passed` is false when anything below fails).
//
// Fails when:
//   centreLine     a label block and its control (or any two items of one row) differ in centre line by more than 2 px
//   controlOffset  two controls on one row differ in vertical centre by more than 2 px (the "text rides on a control" symptom)
//   buttonHeights  buttons in one row do not share one height
//   primaryOrder   the primary button is not last (first, when the row is stacked on a phone)
//   labelWraps     a toolbar, footer or segmented label wraps onto a second line
//   rowHeight      a single-line row is off the 44 / 56 / 64 ladder (a settings row whose label wraps is allowed to grow)
//   textOnControl  helper, status, error or result text sits above or below a control inside a column of a multi-item row
//                  (allowed: the shared form row's own message line, and a field's help inside a multi-item .app-form-row, where it
//                  sits on the shared help line)
//   formRow        the control boxes of the fields of one shared .app-form-row (Editor v6 D12) do not share one top and one height, or a
//                  non-field child (button, checkbox) is off the control line's centre; a row that wraps is judged per visual line
() => {
    const TOLERANCE = 2;
    const LADDER = [44, 56, 64];
    const viewportWidth = document.documentElement.clientWidth;
    const ROW_SELECTOR = '.app-setting-row__line, .app-action-bar, .app-filter-bar, .app-dialog-shell__footer, .tl-match__matchrow, [data-alignment-row]';
    const BUTTON_ROW_SELECTOR = '.app-action-bar, .app-dialog-shell__footer, [data-button-row]';
    const CONTROL_SELECTOR = 'button, input, select, textarea, [role="combobox"], [role="switch"], [role="button"]';
    const PRIMARY_SELECTOR = '.app-button--filled.app-tone--primary, [data-primary]';
    const LABEL_SELECTOR = '.app-action-bar .tl-button-label, .app-filter-bar .tl-button-label, .app-dialog-shell__footer .tl-button-label, .app-segmented-control__item > span, [data-toolbar-label]';
    const HELPER_SELECTOR = '.app-field__help, .tl-field-error, [role="status"], [role="alert"], [data-control-text]';

    const style = el => getComputedStyle(el);
    const visible = el => {
        const rect = el.getBoundingClientRect(), s = style(el);
        return rect.width > 0 && rect.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0' && !el.closest('[hidden], [inert], [aria-hidden="true"], .sr-only, .visually-hidden');
    };
    const centre = el => { const r = el.getBoundingClientRect(); return r.top + r.height / 2; };
    const describe = el => {
        const r = el.getBoundingClientRect();
        const name = (el.getAttribute('aria-label') || el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 30);
        const cls = typeof el.className === 'string' && el.className.trim() ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.') : '';
        return `${el.tagName.toLowerCase()}${cls} "${name}" ${Math.round(r.width)}x${Math.round(r.height)}`;
    };
    const lineHeightOf = el => {
        const s = style(el), fontSize = parseFloat(s.fontSize) || 14, lh = parseFloat(s.lineHeight);
        return Number.isFinite(lh) ? lh : fontSize * 1.3;
    };
    const items = row => [...row.children].filter(visible);
    // The shared form row (AppFormRow) gives helper text two homes: its own message line, and the help of a field that is a direct column of
    // the row's field grid (it sits on the shared help line). Help inside anything nested deeper, such as a flex row inside a column, is still checked.
    const FIELD_SELECTOR = '.app-field, [data-form-row-field]';
    const LABEL_OR_HELP_SELECTOR = '.app-field__label, label, .app-field__help';
    const isFormRowText = text => {
        if (text.closest('.app-form-row__message')) return true;
        if (!text.matches('.app-field__help')) return false;
        let field = text.closest(FIELD_SELECTOR);
        while (field && field.parentElement && field.parentElement.matches(FIELD_SELECTOR)) field = field.parentElement;
        const grid = field && field.parentElement;
        return !!grid && grid.matches('.app-form-row__fields') && items(grid).length >= 2;
    };
    // The element on a field's control line: the first part of the field that is not its label or help (the control frame, not an adornment
    // button inside it), looking through a wrapper marked data-form-row-field to the field it holds.
    const controlBox = field => {
        const part = items(field).find(k => !k.matches(LABEL_OR_HELP_SELECTOR));
        if (!part) return field;
        return part.matches(FIELD_SELECTOR) ? controlBox(part) : part;
    };
    const failures = [];
    const fail = (rule, el, detail) => failures.push({ rule, element: describe(el), detail });
    const firstControl = el => (el.matches(CONTROL_SELECTOR) ? el : el.querySelector(CONTROL_SELECTOR));
    const isControl = el => el.matches(CONTROL_SELECTOR) || !!el.querySelector(CONTROL_SELECTOR);

    const rows = [...document.querySelectorAll(ROW_SELECTOR)].filter(visible);
    for (const row of rows) {
        const kids = items(row);
        if (kids.length < 2) continue;
        const rowStyle = style(row);
        const isRow = rowStyle.display.includes('flex') && !String(rowStyle.flexDirection || 'row').startsWith('column');
        if (!isRow) continue;
        const rects = kids.map(k => k.getBoundingClientRect());
        const tallest = Math.max(...rects.map(r => r.height));
        // A wrapped toolbar is several lines (one item lies wholly below another); each line is checked by its own height only.
        if (rects.some(a => rects.some(b => a.top >= b.bottom - 1))) continue;

        // centreLine: every item sits on the centre of the tallest item (the row's axis).
        const axis = centre(kids.find(k => k.getBoundingClientRect().height === tallest));
        for (const kid of kids) {
            const delta = Math.abs(centre(kid) - axis);
            if (delta > TOLERANCE) fail('centreLine', kid, `centre is ${delta.toFixed(1)} px off the row's centre line`);
        }

        // controlOffset: the first control inside each item (a button in a column that also holds status text rides high or low).
        const controls = kids.map(firstControl).filter(c => c && visible(c));
        if (controls.length >= 2) {
            const base = centre(controls[0]);
            for (const c of controls.slice(1)) {
                const delta = Math.abs(centre(c) - base);
                if (delta > TOLERANCE) fail('controlOffset', c, `control is ${delta.toFixed(1)} px off the first control on the row`);
            }
        }

        // textOnControl: a column item that holds a control plus helper/status text above or below it.
        for (const kid of kids) {
            if (!isControl(kid) || kid.matches(CONTROL_SELECTOR)) continue;
            const control = firstControl(kid);
            if (!control || !visible(control)) continue;
            const c = control.getBoundingClientRect();
            for (const text of kid.querySelectorAll(HELPER_SELECTOR)) {
                if (!visible(text) || text.contains(control) || control.contains(text) || !(text.textContent || '').trim()) continue;
                if (isFormRowText(text)) continue;
                const t = text.getBoundingClientRect();
                if (t.bottom <= c.top + 1 || t.top >= c.bottom - 1) fail('textOnControl', text, 'status or helper text sits above or below a control inside the row; give it its own full-width line under the row');
            }
        }
    }

    // formRow: in one shared form row the control box of every field (the element on its control line, not its label, help or an adornment
    // button inside the frame) has the same top and height. A child that is not a field (a button, a checkbox, an icon button) is centred on
    // the control line, so it only has to share the centre line of the first field's control.
    const formRows = [...document.querySelectorAll('.app-form-row')].filter(visible);
    for (const row of formRows) {
        if (row.matches('.app-form-row--stack')) continue;
        const grid = row.querySelector('.app-form-row__fields') || row;
        // Every child counts, including a wrapper with no box of its own (display: contents, such as the tooltip wrapper around an icon
        // button): it is measured by the visible control inside it, so a button that was placed off the control line is found.
        const boxOf = kid => {
            if (kid.matches(FIELD_SELECTOR)) return controlBox(kid);
            const r = kid.getBoundingClientRect();
            return r.width > 0 && r.height > 0 ? kid : (firstControl(kid) || kid.querySelector('*') || kid);
        };
        const parts = [...grid.children].filter(kid => style(kid).display !== 'none' && !kid.matches('[hidden]'))
            .map(kid => ({ kid, field: kid.matches(FIELD_SELECTOR), box: boxOf(kid) })).filter(p => visible(p.box));
        if (parts.length < 2) continue;
        // A row that wraps is several visual lines (a child whose box lies wholly below the others starts a new one); the controls of one
        // line share a top and a height, and each line is judged by itself. An unwrapped row is one line.
        const spanOf = p => {
            const own = p.kid.getBoundingClientRect();
            return own.width > 0 && own.height > 0 ? own : p.box.getBoundingClientRect();
        };
        const lines = [];
        for (const part of [...parts].sort((a, b) => spanOf(a).top - spanOf(b).top)) {
            const span = spanOf(part);
            const line = lines[lines.length - 1];
            if (line && span.top < line.bottom - 1) {
                line.parts.push(part);
                line.bottom = Math.max(line.bottom, span.bottom);
            } else lines.push({ parts: [part], bottom: span.bottom });
        }
        for (const line of lines) {
            if (line.parts.length < 2) continue;
            const reference = (line.parts.find(p => p.field) || line.parts[0]).box;
            const first = reference.getBoundingClientRect();
            for (const part of line.parts) {
                if (part.box === reference) continue;
                const r = part.box.getBoundingClientRect();
                if (!part.field) {
                    const delta = Math.abs(centre(part.box) - centre(reference));
                    if (delta > TOLERANCE) fail('formRow', part.box, `control centre is ${delta.toFixed(1)} px off the control line of the form row`);
                } else if (Math.abs(r.top - first.top) > TOLERANCE) fail('formRow', part.box, `control top is ${Math.abs(r.top - first.top).toFixed(1)} px off the first control of its line in the form row`);
                else if (Math.abs(r.height - first.height) > TOLERANCE) fail('formRow', part.box, `control height is ${Math.round(r.height)} px, the first control of its line in the form row is ${Math.round(first.height)} px`);
            }
        }
    }

    // buttonHeights / primaryOrder
    for (const row of [...document.querySelectorAll(BUTTON_ROW_SELECTOR)].filter(visible)) {
        const buttons = items(row).filter(el => el.matches('button, a, .app-button'));
        if (buttons.length < 2) continue;
        const heights = buttons.map(b => b.getBoundingClientRect().height);
        if (Math.max(...heights) - Math.min(...heights) > 1) fail('buttonHeights', row, `buttons in one row have heights ${heights.map(h => Math.round(h)).join(' / ')}`);
        const primaries = buttons.filter(b => b.matches(PRIMARY_SELECTOR));
        if (primaries.length === 0) continue;
        const primary = primaries[primaries.length - 1];
        const rects = buttons.map(b => b.getBoundingClientRect()), pr = primary.getBoundingClientRect();
        const stacked = viewportWidth < 600 && rects.every(r => Math.abs(r.left - rects[0].left) <= 1);
        if (stacked) {
            if (rects.some(r => r.top < pr.top - 1)) fail('primaryOrder', primary, 'on a phone the buttons stack full width and the primary button comes first');
        } else if (rects.some(r => r.left > pr.left + 1)) fail('primaryOrder', primary, 'the primary button must be the last (rightmost) button in the row');
    }

    // labelWraps
    for (const label of [...document.querySelectorAll(LABEL_SELECTOR)].filter(visible)) {
        const height = label.getBoundingClientRect().height;
        if (height > lineHeightOf(label) * 1.5) fail('labelWraps', label, `label is ${Math.round(height)} px tall, more than one line`);
    }

    // rowHeight: settings and list rows measured without their reserved status line; a wrapped label may grow its row.
    for (const row of [...document.querySelectorAll('.app-setting-row, [data-row-ladder]')].filter(visible)) {
        const status = row.querySelector('.app-setting-row__status');
        const label = row.querySelector('.app-setting-row__label');
        if (label && label.getBoundingClientRect().height > lineHeightOf(label) * 1.5) continue;
        const helper = row.querySelector('.app-setting-row__helper');
        if (helper && helper.getBoundingClientRect().height > lineHeightOf(helper) * 1.5) continue;
        let height = row.getBoundingClientRect().height;
        if (status && visible(status)) height -= status.getBoundingClientRect().height + (parseFloat(style(row).rowGap || style(row).gap) || 0);
        if (!LADDER.some(step => Math.abs(height - step) <= 1)) fail('rowHeight', row, `row is ${Math.round(height)} px; rows are 44, 56 or 64 px`);
    }
    for (const bar of [...document.querySelectorAll('.app-action-bar, .app-filter-bar, .tl-match__matchrow')].filter(visible)) {
        const kids = items(bar);
        if (kids.length === 0) continue;
        const tallest = Math.max(...kids.map(k => k.getBoundingClientRect().height));
        const height = bar.getBoundingClientRect().height;
        if (height > tallest * 1.9) continue; // wrapped onto several lines
        if (!LADDER.some(step => Math.abs(height - step) <= 1)) fail('rowHeight', bar, `toolbar row is ${Math.round(height)} px; rows are 44, 56 or 64 px`);
    }

    const counts = {};
    for (const f of failures) counts[f.rule] = (counts[f.rule] || 0) + 1;
    return {
        passed: failures.length === 0,
        viewportWidth,
        rowsChecked: rows.length,
        failureCount: failures.length,
        counts,
        failures: failures.slice(0, 60)
    };
}
