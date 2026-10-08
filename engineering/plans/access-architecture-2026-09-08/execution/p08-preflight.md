# Plugin host permission preflight

Read-only implementation preparation against the combined Access branch, 2026-09-09. P08 acceptance still follows checkpoint A. No plugin side effects were executed during this review.

## Exact seams

- `MediaEngine.Plugins/PluginContracts.cs` exposes `IPluginExecutionContext`, `IPluginToolRuntime`, and `IPluginAiClient`. Tool resolution and AI calls currently accept caller-supplied plugin IDs; process execution has no bound identity. Replace plugin-facing use with wrappers created by a host-only context factory, retaining raw runners only inside host infrastructure.
- Context creation is currently spread across `PluginEndpoints`, `PluginSegmentDetectionService`, and `PluginUniverseLoreService`. All three must use the factory. Gate before temporary-directory creation and before passing media paths/metadata to capabilities. `CanAnalyze` must not receive unauthorized media first.
- `PluginToolRuntime` creates its tool root in its constructor, probes configured/PATH/cached tools, downloads and extracts archives, and starts processes. Separate tool installation authorization from process execution; require a live bound plugin before each side effect. Validate tool ID/version, archive entry, working-directory and executable containment. Preserve pinned hashes. Eliminate constructor directory creation where it would precede admission.
- `PluginAiClient` already checks manifest AI role/resource declarations; retain those checks behind the bound plugin gate and recheck enabled/current registration on each call. A matching string ID alone is insufficient.
- Fandom's provider holds its own static `HttpClient`; route its calls through the gated context HTTP client. CommercialSkip reads EDL files directly and MediaSegments probes file existence; move these into the permitted media/storage boundary. Update all bundled manifests to declare exactly the host operations actually used.
- Registration lives in `TuvimaPluginServiceCollectionExtensions`. Existing manifests use `media.read`, `process.execute`, `tool.download`, and `network.http`; compare with the canonical P01 registry before extending names.

## Bounded delivery

1. Host identity, live gate, and context factory with denied-operation spy tests.
2. Bound media/HTTP/storage/tool/AI wrappers and all production call-site conversions, including health and installation paths.
3. Bundled manifest validation, containment/disabled/spoofing tests, and full bundled regression checks.

These are trusted in-process plugins. Wrappers enforce supported host APIs; they do not sandbox arbitrary hostile managed code. P09 must expose a typed host-owned service adapter through the Application gateway, not permit arbitrary plugin route mapping or identity claims.
