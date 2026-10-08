# P3 â€” Native form controls

The shared select family, autocomplete, text/textarea/numeric fields, checkbox and
switch rows, buttons, icon buttons, chips, spinner and file upload now render
native controls behind first-party APIs. The implementation preserves binding,
validation, helper/adornment state, disabled options, sizing and upload contracts.
Autocomplete cancellation/debounce and select keyboard/navigation behavior remain
owned by shared controls rather than feature-page markup.

Selectors use the native popup infrastructure and expose labelled combobox/listbox
state. Intrinsic sizing, ellipsis and the full-label tooltip remain shared owners.
The existing playback selection adapters now resolve the native owner/portal;
their tests retain the one-scroller and nested-sheet contracts. InputFile replaces
the framework upload component without changing the existing upload flow.

NativeFieldsBehaviorTests and JS field/select adapter tests verify controlled
state, closed input editing keys, popup navigation, cancellation, sizing and
cleanup. The observed original Intrinsic series selector labels a hidden input
while its focusable div has a null role. Native combobox semantics are documented
as an improvement, not silently copied into a purported original baseline.

Native selector keyboard assertions passed. Shared checkbox/control tests passed
33 cases; browser review restored the original 42px checkbox target with a pinned
21px glyph at 1.5rem, 14px/600 control typography and 105.1875px textarea height. The final
137 passing Node tests include field, selector and overlay lifecycle checks.
Representative gallery/editor review supports the control implementation; complete
focused/invalid/disabled, autocomplete, upload and playback-sheet visual coverage
is not established. A passing unit test is not screenshot parity.
V16 retains restored generic button minimum height and original typography precedence:
original/native edit buttons both measure 42px and every visible metadata row has
matching positions and heights in the editor-inner measurements. Outlined/text
colors, neutral filled background at 6% opacity and disabled palette values retain
the original mappings. The hidden original Additional rows omitted from the native
DOM do not alter measured visible geometry.
The final gallery confirms checkbox/switch thumbs at RGB(182,194,214) and the
original 135-degree primary gradient from RGB(136,82,252) to RGB(118,82,214).

## Plain-English completion summary

Users retain the same shared editing controls and workflows, now backed by native
HTML and first-party state. Automated tests and representative browser checks
protect their interactions and appearance; complete visual coverage remains limited.
