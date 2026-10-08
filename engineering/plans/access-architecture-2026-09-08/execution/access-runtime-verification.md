# Access runtime verification

## Final mutation acceptance — 2026-09-09 15:32

The latest integration binaries ran against the isolated QA configuration with zero libraries and an explicit `TUVIMA_DB_PATH` pointing at `.tmp/access-runtime-qa/data/library.db`. The existing synthetic administrator signed in and unlocked administrator settings with its grant PIN. Browser acceptance then completed these persisted workflows:

- created a local-only account and its first profile;
- changed the account's feature permissions;
- added a second profile grant and made that profile the default;
- created a Server Integration Application with explicit read and event permissions;
- generated a replaceable one-time Application credential; and
- created an enabled, signed `ingestion.completed` webhook for an explicitly allowed private-network receiver.

The webhook was initially and correctly rejected because the Application had `events.subscribe` without the underlying `ingestion.status.read` permission. After that read permission was granted and the Application was saved, the same webhook saved successfully and reported `Waiting for new events`, with no delivery attempt. This verifies that webhook admission uses current Application authority rather than merely trusting the form selection. One-time credential and webhook secrets were observed only to verify their reveal-once presentation and were not copied into documentation or retained for use.

Read-only inspection of the intended QA database confirmed two accounts, three profiles/grants, two Applications, one active Application credential, one webhook, and zero webhook deliveries. The QA browser console contained no errors. Both application stderr files were empty, stdout contained no fatal or unhandled matches, and both identified QA processes were stopped. The test did not configure libraries, start ingestion, send external email, use a real identity, or touch original media.

An earlier restart exposed an isolated-fixture configuration mistake: the current data-path resolver does not select the SQLite file from the legacy `core.json` `database_path` field. Without `TUVIMA_DB_PATH`, it selected the database under `library_root/.data/database`. The hosts were stopped, the mismatched disposable Dashboard bundle was preserved for diagnosis, and acceptance restarted against the intended database with `TUVIMA_DB_PATH` set explicitly. No product source or normal runtime state was changed by that correction. The cutover procedure now calls out this path check.

## Final source smoke — 2026-09-09 12:57

Source through `4d1b1ffe` passed the complete solution suite (3,632 passed, 37 provider skips, zero failed), warnings-as-errors build, formatting, and coverage gates. The final built Engine and Dashboard were started only against the existing isolated QA configuration/database, with zero configured libraries.

Both hosts returned HTTP 200 on `/health/live`. After a fresh browser reload, the existing synthetic administrator needed the grant PIN; successful unlock restored Authentication. Users, Applications, and Authentication rendered at 1920×1080, with the existing single administrator and built-in native client Application. No account, profile, Application, credential, or webhook was created. No configuration was saved. Screenshots were captured inline in the task; the viewport was reset afterward.

Both stderr captures were empty and checked stdout contained no fatal/unhandled matches. The two processes were verified as belonging to the integration checkout and stopped. Evidence: `logs/access-qa-complete-verified-health.json` and `logs/access-qa-complete-verified-{engine,dashboard}.{out,err}.log`. This final smoke complements the responsive and drawer checks below; it does not complete the still-blocked synthetic-identity browser mutation matrix.

Historical checkpoints follow; their open implementation findings are superseded by [current execution status](./status.md).

## Initial disposable smoke — 2026-09-09

This is provisional verification, not acceptance of the full Access cutover.

- Built the isolated integration Engine and Dashboard after the typed system-service admission checkpoint. Both builds passed with zero warnings and errors.
- Runtime state is under `.tmp/access-integration/.tmp/access-runtime-qa`. Configuration was copied from tracked templates, then given an empty library list, exclusively disposable storage paths, disabled metadata providers, disabled AI downloads/features, disabled discovery, and no external authentication providers. No original database, credentials, media, or configured source directories were copied.
- Engine listens on `http://127.0.0.1:61515`; Dashboard on `http://127.0.0.1:5116`. These are development test processes, separate from normal ports.
- Browser verification completed first-run administrator creation and sign-in using a synthetic test account, deferred media locations, completed setup, and rendered the empty authenticated Home surface.
- Both `/health/live` endpoints returned HTTP 200. The smoke logs had no `Unhandled`, `[FTL]`, or `fail:` entries at the check. Evidence: integration `logs/access-runtime-qa-health.json` and `logs/access-runtime-qa-{engine,dashboard}.{out,err}.log`.
- **Open P05 blocker:** direct navigation to `/settings/access/users` redirected the newly authenticated administrator to `/settings/profile`. Administration was absent from the interactive navigation despite successful server authentication. The suspected boundary is request-scoped cookie validation versus circuit-scoped authority initialization, compounded by route normalization before authority loading. Terra owns the fix and a first-navigation regression test; root will repeat the browser check on the corrected build.

The synthetic test password and recovery codes are intentionally not retained in this document. This smoke does not establish catalogue, View, provider, plugin, event, telemetry, or webhook acceptance.

The two disposable QA processes were stopped before integrating the next code checkpoints. The regular Engine and Dashboard were then restored from the existing verified builds on ports 61495 and 5016. Both liveness checks returned HTTP 200 at 08:32 local time; their checked logs had no fatal/unhandled entries. Evidence is in the original checkout at `logs/access-normal-resumed-health.json` and `logs/access-normal-resumed-{engine,dashboard}.{out,err}.log`. The regular runtime still uses the original architecture; it is not evidence that the new Access cutover is complete.

## Direct Access navigation corrected

The initial circuit seeding and validation coalescing fixes did not fully resolve direct navigation. Runtime diagnostics showed successful live administrator validation followed by `ActiveProfileSessionService.ResolveActiveProfileAsync` overwriting that authority with a null projection from the retained cookie principal. Profile presentation loading now preserves the current session and authority, including a more recent profile switch.

- The independent profile, authority, Settings navigation, and shell suite passed **80/80**. The new regression checks that profile load/refresh preserves validated actions, navigation, session revision, and the current profile when the cookie names an older profile. The old concurrent profile test now supplies an explicit authenticated identity rather than assuming a seed-profile fallback.
- Fresh browser document navigations to `/settings/access/users` and `/settings/access/applications` remained on the requested route and rendered their authenticated content. This verifies the actual runtime failure, beyond the earlier component tests.
- Evidence: `logs/access-profile-authority/profile-authority-preservation.trx` and `logs/access-qa-p05-profile-preserved-{engine,dashboard}.{out,err}.log` in integration. This resolves the first-navigation blocker; complete Users/Applications workflows and the wider security gates remain open.

## Mobile and personal navigation

- Runtime inspection found all canonical Access subroutes still rejected by the old mobile route rule. Users, Applications, and Authentication are now explicitly mobile-enabled. At 390×844 the Authentication form renders, all three tab labels fit, and the current tab reports `aria-selected=true`; the shared tab component had previously emitted the wrong attribute name.
- Settings navigation now consumes a server-authorized boolean directly, with no string-role overloads. Non-administrators retain Profile, Account & Security, and Playback & Reading; Review and administrator settings remain unavailable.
- The combined navigation, shell, shared-control, and launch-contract tests passed **123/123** (`logs/access-mobile-authentication/access-navigation-final.trx`). Browser evidence is in the task's mobile screenshot/accessibility capture and `logs/access-qa-navigation-verified-*`.
- Authentication visual review exposed session/remote controls behind a retired `session-policy` branch. P07 owns moving them into the canonical Authentication page, completing provider configuration, and making effective enabled/ready states truthful. This visual pass does not accept the unfinished P07 form.

## Users, Applications, and administrator protection

- Disposable runtime `users-gate` verified actual optional PIN activation. Protection immediately removed administrator controls; entering the PIN restored them after server unlock and fresh authority validation. No original account was changed. Synthetic secrets are deliberately omitted from this record.
- The shared drawer backdrop originally occupied the same stacking level as the drawer and covered its content. Read-only DOM inspection confirmed the corrected drawer/backdrop ordering (1300/1299), and a new screenshot showed usable content.
- Runtime `authentication-complete` uses isolated Engine/Dashboard binaries and database/configuration beneath `.tmp/access-runtime-qa`, with zero configured libraries and no original media. The updated Users drawer receives focus on its close button and removes its form from the accessibility tree when closed. The Applications drawer shows registry-backed presets, current permissions, and disabled controls with reasons for unavailable services.
- Mobile review is ongoing. Do not count visual rendering alone as completed account/application mutation acceptance. Automatic approval review blocked synthetic account creation; explicit authorization is pending. Automated account lifecycle and mutation retry checks remain passing evidence independent of this blocked browser action.

## Final drawer geometry check

The isolated `access-mobile-final` runtime verified Users and Applications at 390×844. With the browser's 110% scale, each drawer spans CSS x=0 through 345.45 and fits within the 767.27-high content viewport. The footer occupies y=697.27–767.27 at stacking level 1302, above the mobile dock at level 1300. Both forms scroll within the drawer, focus their close button on opening, and disappear when closed. Applications has exactly one Custom preset. Forms were closed without saving; the browser viewport was reset and the isolated runtime stopped before further builds.

Authentication correctly reports the missing Dashboard callback URL and SMTP configuration. The test-email action is absent when SMTP is unconfigured. No real provider credentials, SMTP settings, original accounts, or media were changed. This geometry/readiness check does not replace the pending browser mutation acceptance.

## Midday final integration smoke

The `resume-final` QA runtime used the same isolated state and the latest integrated P10/P12/profile cleanup build. Engine and Dashboard started; stderr stayed empty and checked stdout contained no fatal/unhandled matches. A fresh page correctly required the existing synthetic administrator's grant PIN, and successful unlock restored Applications. At 1920×1080, the persisted native application row and editor rendered with the close control focused and footer visible. Credentials and unavailable permissions remained explicit. The editor was cancelled without saving.

Authentication was inspected at 1920×1080, 1280×720, and 390×844. Navigation, the selected tab, lock state, and form remained readable with contained scrolling. Screenshots were captured inline in the task. The viewport was reset and both identified QA processes stopped before further builds. No account/application was created, no provider configuration was changed, and no media was touched. Service-application webhook browser acceptance remains pending the requested authorization for synthetic test identities.
