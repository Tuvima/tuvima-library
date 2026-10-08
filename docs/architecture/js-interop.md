---
title: "JavaScript Interop Lifecycle"
description: "Rules for safe JavaScript interop lifecycle management in Blazor components."
audience: "developer"
category: "architecture"
product_area: "dashboard"
status: current
---

# JavaScript Interop Lifecycle

## In this page

Rules for safe JavaScript interop lifecycle management in Blazor components.

## Where this lives in the code

- `src/MediaEngine.Web/wwwroot/js`
- `src/MediaEngine.Web/Components`
- `src/MediaEngine.Web/Services/Playback`

Dashboard components that register JavaScript callbacks must also unregister them.

## Pattern

1. JavaScript `register...` functions should replace an existing handler or return/store a handle that can be passed to `unregister...`.
2. JavaScript `unregister...` or `dispose...` functions remove event listeners, clear timers, cancel animation frames, close channels that are owned by that registration, and clear stored .NET references.
3. Razor components that register listeners or create `DotNetObjectReference` should implement `IAsyncDisposable`.
4. `DisposeAsync` calls the JavaScript unregister function and then disposes the `DotNetObjectReference`.
5. Ignore `JSDisconnectedException` during disposal. Log other disposal failures at Debug when a logger is available.

## Current High-Risk Registrations

- Ctrl+K command palette: `registerCtrlK` / `unregisterCtrlK`.
- Media tile hover: `registerMediaTileHover` / `unregisterMediaTileHover`.
- Listen playback callbacks: `configure`, `registerStateHandler`, `registerCommandHandler`, `registerAudioStateObserver`, `registerPlayerShortcuts`, and popup unload registration have paired unregister methods where they register listeners or .NET references.
- EPUB reader and Cytoscape registrations expose dispose/destroy methods and should keep using them from component disposal.

## Route-Scoped Script Loading

Large, single-route scripts are not loaded globally from `App.razor`. Cytoscape (`lib/cytoscape/cytoscape.min.js` + `js/cytoscape-interop.js`) is loaded on demand by the Chronicle Explorer page, and `js/epub-reader.js` by the EPUB reader page, through the shared `window.lazyLoad.loadScript(src)` helper in `js/lazy-load.js` (dedupes by src, preserves insertion order for UMD bundles). Pages that lazy-load a script must guard their disposal calls behind a loaded flag so navigating away before the script loads does not throw.

Avoid adding fire-and-forget JavaScript cleanup from synchronous `Dispose`; use `IAsyncDisposable` when JS interop is involved.

## Render updates and element ownership

Use the rendered element's identity and the relevant binding inputs to decide when registration is required. Unrelated renders must not recreate listeners or observers. Element replacement, portal reopening, and a changed callback owner can still require registration. Keep genuine state updates separate from registration; native playback timing and focus reconciliation must remain current.

`AppSelect` caches its playback binding by root, popover class, accessible label, and parent owner. Its intrinsic-sizing helper retains one `ResizeObserver` and updates the selected label in place. `PlaybackIdentityLink` retains its listener for the current anchor while refreshing the rendered snapshot on every render, so activation still uses current playback authority. Disposal releases the owned registration.

See the [CSS and interop cleanup evidence](../../engineering/reports/css-cleanup-2026-10-05.md) for measured calls, retained registrations, and verification limits.

## Listen Playback Storage

The Listen playback bridge stores only Web client mechanics under `tuvima.playback.v2.*` localStorage keys:

- `tuvima.playback.v2.state`
- `tuvima.playback.v2.command`
- `tuvima.playback.v2.device-id`

Do not read the retired `listen-playback-state` or `listen-playback-command` keys. Browser timing and popup defaults are injected from `config/ui/playback-client.json` through `listenPlayback.configure(...)`; user listening preferences stay in the playback settings API.

## Native control hosts

The shared popup, dialog, toast, select and autocomplete components own their
JavaScript registrations and release them from `DisposeAsync`. Register against
the rendered element and current callback owner, update relevant binding state,
and preserve portal release/focus restoration before removing popup content.
Native dialogs use the browser modal top layer; popup placement must follow the
active modal/fullscreen owner rather than relying on a larger z-index. Nested
Escape handling closes only the permitted top interaction and still honors the
editor's unsaved-change guard. Native temporary drawers use
`native-structure.js` to pair `showModal`/close and return focus while reserving
actual app-bar and playback/navigation dock offsets.

Use `scripts/visual-qa/css-ownership/keyboard.mjs` with an existing documented CUA
tab for declared key/click actions and semantic focus/control traces. It does not
launch a browser or expose a debugging connection. Record actual interaction
coverage; unit tests of the helper are not browser keyboard evidence.

### Application-owned dialog dismissal

`app-dialog.js` sets `closedby="none"` and intercepts Escape in the topmost
managed dialog before a browser close request can run. The `cancel` event alone
cannot protect a draft: repeated Escape can produce a non-cancelable close request.
The listener respects composition, native picker controls and first-party popovers.
An editing field declares `data-app-escape-owner="field"`; its key still reaches
the Blazor handler while native dialog dismissal is prevented. Capture runs before
field handlers, so checking `defaultPrevented` alone cannot establish ownership.

The last `update` instruction supplies the expected open state. If a native `close`
arrives while that state remains open, the module reopens the dialog without taking
another scroll lock or replacing its opener, restores its inner focus and asks the
.NET close guard when Escape dismissal is enabled. Cancellation requests are
coalesced while pending. An intentional close or detach clears expected state,
releases the lock and restores eligible opener focus. Reopening retains popup/toast
top-layer ordering. Keep editing/Discard remains the editor's responsibility.

`route-smoke.mjs` checks every source-derived Settings destination and representative
fixture routes using an existing authenticated browser tab. It checks the expected
canonical path, loaded page content and heading as well as the shell and error
states. Prefer ordinary visible navigation to retain the Blazor circuit; full
document loads can exhaust the fixture's connection limiter even with spacing.
