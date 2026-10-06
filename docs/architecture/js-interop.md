---
title: "JavaScript Interop Lifecycle"
summary: "Rules for safe JavaScript interop lifecycle management in Blazor components."
audience: "developer"
category: "architecture"
product_area: "dashboard"
---

# JavaScript Interop Lifecycle

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

See the [CSS and interop cleanup evidence](../reports/css-cleanup-2026-10-05.md) for measured calls, retained registrations, and verification limits.

## Listen Playback Storage

The Listen playback bridge stores only Web client mechanics under `tuvima.playback.v2.*` localStorage keys:

- `tuvima.playback.v2.state`
- `tuvima.playback.v2.command`
- `tuvima.playback.v2.device-id`

Do not read the retired `listen-playback-state` or `listen-playback-command` keys. Browser timing and popup defaults are injected from `config/ui/playback-client.json` through `listenPlayback.configure(...)`; user listening preferences stay in the playback settings API.
