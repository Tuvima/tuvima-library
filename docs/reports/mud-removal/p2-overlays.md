# P2 â€” Dialogs, popups, toasts and focus

Scoped services in `Services/Ui` and AppPopoverHost, AppDialogHost and AppToastHost
replace the framework providers in the four layouts. First-party dialog options,
parameters, references, results and contexts retain the application call shapes.
Native modal dialogs use the browser top layer; shared JS handles dismissal,
positioning and focus lifecycle. Toasts preserve severity, actionable callbacks,
stacking, duration and pause behavior through first-party contracts.

The media editor remains above the current detail URL, with its draft-close guard.
Nested confirmations/pickers preserve their parent; a child Escape must not cancel
the editor. Editor protection tests cover Keep editing/Discard/Cancel paths and
state ownership. Overlay service and JS lifecycle tests cover callback routing,
nested teardown, focus and owned popup behavior. Playback coordinator contracts
remain in force; this work does not move session ownership into overlay services.

Original browser evidence showed the artwork-library picker has Escape dismissal
enabled. Escape removes the picker and retains the editor, but original focus was
on the body. Native acceptance additionally requires return focus within the
retained editor. The matrix and report explicitly treat that as an accessibility
improvement, not an identical original trace. The original select's focus owner
also lacks native combobox semantics. See [P0](p0-safety-net.md) for evidence rules.

Dependency inventory: 21 `IDialogService` and 49 `ISnackbar` source identifiers
became zero. Native dialog/toast service registrations and all four host sets are
guarded. Native select and nested-dialog keyboard assertions passed, as did two
targeted lifecycle tests. Menu preparation waits for actual rendered-close state
before opening a child. Browser review confirmed dirty inline Escape retains the
draft and Close followed by Keep editing or Discard follows the existing guard.
Complete toast/fullscreen/popout/no-scroll-change matrix coverage is not established;
representative checks must not be described as exhaustive overlay acceptance.

## Plain-English completion summary

Dialogs, dropdowns and notifications now have first-party owners, while editing
keeps its existing draft protections. Automated checks protect nested behavior;
representative browser review confirms editing and focus behavior. Complete popup
coverage remains an explicit limit of this delivery's evidence.
