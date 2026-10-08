# Disposable Home media cards verification

This workflow exercises the real Engine, Dashboard and components with local catalogue and View data. The fixture never reads or seeds the configured user library. Setup uses the normal protected production endpoints; it does not change authentication, credentials, grants or onboarding records directly.

## Prepare and initialize

Run from the repository root. Use the bundled Python runtime returned by `load_workspace_dependencies`.

```powershell
& $qaPython scripts/visual-qa/home-media-cards/fixture.py prepare --root tools/reports/home-media-cards-visual/fixture-acceptance
$qaFixture = (Resolve-Path tools/reports/home-media-cards-visual/fixture-acceptance).Path
$qaEnvironment = Get-Content (Join-Path $qaFixture environment.json) | ConvertFrom-Json -AsHashtable
foreach ($qaEntry in $qaEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($qaEntry.Key, $qaEntry.Value, 'Process') }
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/MediaEngine.Api --no-launch-profile --urls http://localhost:61495
```

Stop existing Engine and Dashboard processes before starting. The fixture directory must be a new `fixture-*` child of `tools/reports/home-media-cards-visual`. Prepare refuses a nonempty path. Every later operation requires the exact task marker and resolved root. Do not set `TUVIMA_STORAGE_RESET` and do not call a reset endpoint.

Prepare creates isolated database/media/log/key/backup/model paths, no watched sources, disabled AI features/downloads, and disabled provider manifests whose active endpoints point only at the unused loopback port 9. It copies no secret directory or provider credential. The required provider manifests are present so all normal endpoint dependencies register.

After the Engine listens, run the ordinary setup harness in a second terminal with the same fixture environment:

```powershell
dotnet run --project scripts/visual-qa/home-media-cards/SetupHarness -- $qaFixture http://localhost:61495
```

The harness reads the production encrypted Dashboard service bundle using the shared provider and fixture protection keys. Every request obtains the current service credential. It begins a setup session, runs required preflight, creates a disposable administrator, defers media locations/providers, checks readiness, completes setup, and signs in. Generated credentials and session state stay in the fixture's `.qa-auth.json`; no service key, token or recovery code is printed. This file and `.qa-browser-cookies.json` are private disposable output and must never be committed or copied into the evidence report. The optional third argument signs in to a running Dashboard through its ordinary login form and antiforgery validator, saving that client's cookies privately.

Stop both apps, then seed using the actual setup profile:

```powershell
$qaProfile = (Get-Content (Join-Path $qaFixture .qa-auth.json) | ConvertFrom-Json).profileId
& $qaPython scripts/visual-qa/home-media-cards/fixture.py seed --root $qaFixture --profile-id $qaProfile
```

Seeding refuses an unknown marker, unsupported epoch, missing profile, nonempty catalogue or repeat seed. It writes RFC 4122 GUID blobs, real SHA-256 file hashes, bounded local artwork variants and current schema records. It leaves credentials, grants and setup status untouched. `manifest.json` records every work, asset, parent, View identity, route and expected visibility. Local video uses the existing repository media fixture; audio is a one-second silent WAV and is intended for presentation verification. Saved duration represents the display test state, not those media files' actual runtime.

Restart Engine and then Dashboard with the isolated environment, `--no-launch-profile` and their usual URLs. Dashboard receives `TUVIMA_ENGINE_URL=http://localhost:61495`. Authenticate the CUA browser with the disposable account through an approved normal sign-in method; the setup harness does not bypass browser authentication.

## Change saved playback state for captures

Run `ProgressHarness` from the repository root while the isolated Engine is running. It uses the existing `.qa-auth.json` session produced by normal setup/sign-in, validates the exact active account/profile against the manifest, and sends only authenticated progress requests. It does not sign in or create credentials. If the session expires, repeat the ordinary setup harness sign-in first. The Engine must use the fixture's `environment.json`; the helper selects its protection keys and service bundle explicitly from the supplied fixture and needs no additional environment variables.

```powershell
$qaFixture = (Resolve-Path tools/reports/home-media-cards-visual/fixture-acceptance-v2).Path
dotnet run --project scripts/visual-qa/home-media-cards/ProgressHarness -- $qaFixture http://localhost:61495 partial-show-episode 42 1260 3000
```

Arguments are the marked fixture root, loopback Engine origin, manifest item key, percent, position seconds and duration seconds. Only a direct `fixture-*` child of the task output directory with matching marker/manifest and isolated configuration is accepted; linked paths, arbitrary IDs, View items and remote URLs are refused. Existing progress properties are preserved and the revision is supplied to prevent overwriting concurrent playback. Success prints only the scenario/profile/percent and acknowledged asset, revision, timestamp and timing; errors omit private state.

Update `missing-still-show-episode 35 1050 3000` to make its show the latest started spotlight; use `next-show-episode 100 3000 3000` to bring the completed first episode's show forward with its next owned episode selected. For the all-owned-complete case, update `next-owned-episode 100 3000 3000` as well. Restore `next-owned-episode 0 0 3000` and then update `next-show-episode 100 3000 3000` to revisit the next-owned case. Refresh Home after each acknowledged change and confirm the projected action/artwork before capturing. This helper cannot remove a saved state: preserve the originally untouched show for the untouched screenshot.

The root owns building/running this helper and can exercise its refusal checks with an unknown fixture path, a GUID instead of a scenario key, an out-of-range percent, and a remote URL before recording acceptance evidence.

## Matrix and capture command

The seeded matrix covers untouched show, partial S2 E5, completed S2 E5 followed by owned S2 E7, provider-only S2 E6 exclusion, missing episode still, untouched/partial/completed movie, partially read long-title portrait book, active square album without completion semantics, portrait audiobook, two Mine View images and one inaccessible second-profile image. The private profile has no account grant. Shared Library is absent from this recent feed fixture.

Use only documented CUA browser surfaces. The root obtains a fresh tab through `browser.tabs.new()` because the in-app alias may return a stale tab. The named capture command is the exported `captureState` function; import it in CUA's Node runtime, passing that browser and tab:

```javascript
let captureModule = await import('file:///C:/Users/shaya/OneDrive/Documents/Source/Repos/tuvima-library/scripts/visual-qa/home-media-cards/capture.mjs?v=stable-geometry-5');
let validationBrowser = await agent.browsers.get('2');
let validationTab = await validationBrowser.tabs.new();
await validationTab.goto('http://localhost:5016');
await captureModule.captureState({
  browser: validationBrowser, tab: validationTab,
  width: 1536, height: 1024, label: 'home-partial-tv',
  outputRoot: 'C:/Users/shaya/OneDrive/Documents/Source/Repos/tuvima-library/.tmp/home-media-cards-2026-10-03'
});
```

This helper resizes through the documented viewport capability, records actual CSS dimensions/DPR, reads the actual DOM, requires loaded fonts/artwork, and exports the actual CUA screenshot bytes as JPEG. Image settlement applies only to images with nonzero rectangles intersecting the viewport and any scrolling/clipping ancestors, with visible computed display/visibility/opacity throughout their ancestor chain. Hidden hover layers and offscreen lazy images are counted separately as deferred images. A visible failed or unfinished image still blocks capture. Companion JSON records each visible image's rectangle, selected source, source set, sizes, natural dimensions and object fit for rendition review. URL queries/fragments and raw DOM snapshots are omitted to protect signed View grants. It does not launch another browser, connect to debugging ports, alter page content or convert JPEG into an asserted PNG. Refresh the DOM snapshot and retry if images/fonts are still loading. If the helper changes during a persistent CUA session, increment the import's `?v=` suffix to load the corrected module. Use normal CUA role/label locators to click carousel controls, choose recent filters, open details and press Tab/Enter. Capture every representative Home slide, mixed Continue, Recently Added All/View and TV detail with untouched/partial/completed rows at 1536×1024, 1920×1080, 1024×768 and 390×844. Stress hero/mixed shelves/filters at 320×568, 844×390 and 768×1024. Capture focus states and test whole-card navigation, progress status, next target, no music bar and private View exclusion. Record observed overflow and control dimensions from the companion JSON.

The available CUA API does not document hover, touch gesture or reduced-motion emulation. Do not claim those checks passed from viewport screenshots. Keyboard focus is supported. Record touch/hover/reduced-motion gaps or use another specifically approved documented capability if one becomes available. Viewport coverage is emulated, not a real phone/device.

The helper reads the JPEG frame header before saving evidence and records `capturedPixelWidth`/`capturedPixelHeight`. Both dimensions must match either the measured CSS viewport or that viewport multiplied by the recorded DPR and rounded to pixels. A cropped or otherwise mismatched export is refused even when DOM dimensions are correct. Use supported reload/navigation and viewport controls to resolve an export mismatch, then capture again; never relabel, crop, resize or upscale the export to make it pass.

When the current measured viewport already matches the requested size, the helper leaves it in place. Before each new size, the root sets the viewport through supported controls, reloads/navigates and obtains a fresh settled snapshot. The helper reads geometry immediately before and after the screenshot and refuses changing viewport, scroll, visible image/control bounds or visible slide state. Companion metadata records scroll positions, page title, visible heading/hero identity/subtitle and active-slide label without link targets or raw DOM. It does not wait blindly or adjust the page to make a capture pass.

Signed View grants occur in `/view-media/<grant>` paths. Image source/current source/source set entries from View are therefore replaced entirely with a constant purpose label, retaining their geometry, declared sizes and natural dimensions without a grant or asset identity. Other image URLs retain only origin/path, and raw DOM snapshots are omitted. Route metadata receives the same View identity redaction. Do not print raw live image URLs while measuring the page.

For text-size stress, set `TUVIMA_HOME_MEDIA_QA` to the marked fixture root in the Dashboard process and use `?qaTextScale=200` (or `&qaTextScale=200` on an existing query). The middleware requires Development, that exact opt-in, the matching task marker, and the matching config directory. It injects only the CSS root font-size stress style into fixture HTML; normal Development and production ignore the query. Pass `textStress:true` to `captureState` and label the evidence **CSS root text-size stress, not browser zoom**. Preserve normal page scrolling and inspect long-title/action/navigation overlap.

At the end:

```javascript
await captureModule.resetViewport(validationBrowser);
```

## Verification and cleanup

The root owns all restore/build/test/app execution; workers must not run concurrent builds or start apps. Focused suites include Home spotlight/state/profile/route tests, authorized display SQL tests, recent filtering/paging/View tests, tile/progress/sequence tests, hero/carousel tests and `HomeMediaQaTextMiddlewareTests`. Full solution gates and visual acceptance remain required.

`selftest.py` verifies the executable seed against the repository's current schema in a new marked task probe. It validates foreign keys, current IDs/hashes, saved states, Mine/private entries and rejection of unknown/repeat roots. It starts no app and is separate from acceptance evidence.

Preserve only accepted JPEGs/companion metadata and the compact fixture ID/route manifest in the final report. After stopping fixture processes, remove marked `fixture-selftest-*` probes, superseded screenshots, local `.qa-auth.json`/cookies, and disposable fixture runtime files under the verified task output root. Resolve every cleanup target and recheck its marker before removal; never remove the user's config/library or an unknown folder. Measure and report removed file count/bytes. Acceptance remains pending until Extra High reviews integrated behavioral and visual evidence.

## October 4 remediation coverage

Seeding includes two music URL shapes (unsized recording cover and inherited album art), three files for one audiobook, older partial episodes for one show, four timed chapter boundaries, positive/negative-offset LRC files, 26 authorized Mine photos, one Mine video, a gallery and a private profile photo. Metadata/lyrics and native media are local fixture data. The configured library remains browse-only.

`fixture.py enrich-playback --root <marked-fixture>` upgrades an existing task-owned fixture idempotently. `selftest.py` checks current-schema constraints, ownership, source paths, counts, artwork dimensions, recording hashes and the exact marker gate. Text stress is enabled only at startup in Development with the marked fixture and matching config directory; `?qaTextScale=200` has no effect elsewhere.

Preserve measured CSS viewport and exported bitmap dimensions in capture metadata. A screenshot with mismatched dimensions or unsettled images is unverified; do not resize or relabel it as a passing capture. Keep private harness auth/cookie files out of reports and commits.
