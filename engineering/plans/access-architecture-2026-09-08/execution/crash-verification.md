# Crash verification before Access refactoring

## Duplicate-launch hardening — 2026-09-12

Windows Application event 1026 at 11:51:07 confirmed the recurring Engine process termination was an unhandled socket error 10048: a second `MediaEngine.Api` process tried to bind `https://127.0.0.1:61494` while an existing process still owned the address. Earlier validation logs recorded the same conflict on Dashboard port 5016.

The Engine and Dashboard now take separate operating-system process leases before host construction. A duplicate copy exits normally with an explicit already-running message and leaves the active copy alone. The local combined launcher also takes a launcher lease, waits for stopped processes to exit, and verifies ports 61494, 61495, and 5016 are released before starting replacements. An unrelated process occupying a configured port is still a real configuration error, but it is caught and reported without becoming an unhandled .NET process crash.

Regression evidence: the focused launcher/lease suite passed 5/5, the complete solution build passed with zero warnings and zero errors, and a live rebuilt Engine/Dashboard pair returned HTTP 200 from both liveness/readiness endpoints. While that pair was running, direct duplicate Engine and Dashboard launches both exited 0 with the already-running message; a second combined launcher invocation reported the existing supervisor and did not stop or replace either host. Both normal stderr logs remained empty, their checked logs contained no address-conflict/fatal/unhandled matches, and Windows recorded no new MediaEngine `.NET Runtime` crash event during the test.

Plain English: starting Tuvima twice no longer makes the second copy crash or disturb the copy that is already working. The launcher now also waits until an old copy has fully released its network ports before replacing it.

## Final integrated regression checkpoint — 2026-09-09

The final Access source through `4d1b1ffe`, including the accepted credential-recovery and ingestion changes, passed the complete solution suite: **3,632 passed, 37 existing provider skips, zero failed**. The warnings-as-errors build and full formatting/coverage gates pass. Evidence: `logs/access-complete-build.log`, `logs/access-complete-verified.log`, and its adjacent TRX/Cobertura directory in the integration checkout.

The normal Engine/Dashboard liveness checks returned HTTP 200 at 12:47 local time (`logs/access-complete-normal-health.json`). The final rebuilt isolated hosts also returned HTTP 200 at 12:57, with empty stderr and no checked fatal/unhandled output, and their Users/Applications/Authentication navigation and PIN unlock worked. The QA hosts were then stopped. These checks strengthen regression confidence for the identified credential failure; they do not guarantee that no unrelated crash can occur.

## Original diagnosis

Verified on 2026-09-08 before Access implementation. This note separates observed failures from symptoms that still need reproduction. It does not claim that the application can no longer crash.

## Evidence reviewed

- `logs/codex-crash-diagnosis-engine.err.log` and `logs/codex-crash-diagnosis-dashboard.err.log` are empty.
- `logs/codex-crash-diagnosis-engine.out.log` shows the Engine listening at 20:44:50 and continuing periodic work through 21:54:51. It contains no fatal, unhandled, or shutdown entry.
- `logs/codex-crash-diagnosis-dashboard.out.log` shows the Dashboard listening and continuing through 21:41:24. Its repeated 403 responses are caught `HttpRequestException` warnings after the authenticated session became invalid; there is no `CircuitHost`, renderer, fatal, or unhandled entry in that capture.
- Windows Application events from `.NET Runtime` contain one Dashboard circuit failure on 2026-09-08 at 15:43:35. `DashboardServiceCredentialProvider.GetToken()` threw because `src/MediaEngine.Web/config/.secrets/dashboard-engine.credential.json` was missing. The reported path indicates this launch did not resolve the repository `../../config` launch-setting path.
- Windows Application event 1026 records one Engine process termination on 2026-09-08 at 16:04:34. `DashboardServiceCredentialBootstrapper.EnsureAsync()` rejected a credential bundle whose token did not match the Engine database.
- Windows Application event 1026 records two Engine startup terminations on 2026-09-06 at 15:45:18 and 15:45:52. `StorageEpochGuard.BackupAndRemove()` could not move an obsolete database because the file was in use. This proves a locked-file startup failure, but the event itself does not identify which process held the file.
- The earlier SQLite read-only startup failure was produced by a sandbox-restricted validation launch. It is evidence about that launch environment, not a normal application crash.
- `logs/tuvima-20260906.log` contains six unhandled request exceptions at 15:33 caused by obsolete state missing `user_status.revision`. Each request returned HTTP 500; these are historical request failures, not evidence that either host process exited. They do not recur in the 2026-09-07, 2026-09-08, or crash-diagnosis captures.

## Implemented correction and verification

The missing-credential circuit stack exposed a current source defect: `Program.cs` loaded the credential while constructing HTTP clients and `ViewProfileAssertionHandler`. A missing or unreadable bundle could therefore escape before page-level error handling and terminate the circuit. The credential provider also cached a successfully loaded token for the Dashboard process lifetime, so an Engine credential rotation could leave it sending a stale token until restart.

The Dashboard now loads and validates the credential at request send time. Missing, malformed, or undecryptable state produces one warning per changed credential state and a local HTTP 503 without sending an unauthenticated upstream request. The small credential bundle is content-hashed on each request, which detects rotation even when replacement preserves file size and timestamp. View request signatures use the same current token, and the artwork proxy keeps its existing service-only authentication behavior. Focused tests cover missing state, malformed JSON, protection with the wrong key ring, recovery when the file appears, rotation, and refusal to reuse a previously cached token.

The live smoke check exposed a second path: the sign-in endpoint treated an unavailable Engine as an unhandled bootstrap GET failure. Identity GETs now preserve unknown status on HTTP, transport, or timeout failure. Sign-in renders a retry page with HTTP 503; only an explicit unconfigured bootstrap result opens first-time setup. This avoids accidentally offering setup during an outage.

Final verification: the solution build passed and all 3,308 tests passed, with 37 existing provider integration skips. Twelve focused credential/first-run tests cover recovery, rotation, unknown bootstrap status, and execution of the actual retry HTTP result. An isolated live Dashboard with no credential returned the retry page for `/auth/login` and its ingestion-return variant while `/health/live` stayed HTTP 200; stderr was empty and the smoke capture had no unhandled/fatal entries. Evidence: `logs/access-prerequisite-final-tests.log`, `logs/access-credential-smoke-results.json`, and `logs/access-credential-smoke-final.*.log`. The temporary server was stopped after verification.

The available evidence still supports several distinct startup/state failures and one historical request-schema failure. It does not support the broader claim that duplicate launches alone caused every reported crash.

The actual retry page was also visually inspected at 1920×1080 and 390×844 using a separate temporary Dashboard on port 5117 with an absent credential. It displayed the existing authentication shell, an Engine unavailable explanation, and a readable Try again action without setup controls. The action retained the ingestion return URL. The test tab and temporary server were closed afterward; the normal Engine and Dashboard remained running. Logs: `logs/access-retry-visual.*.log`. Screenshots were captured inline in this task.

To classify any remaining crash, record the time and whether the browser showed a reconnect/error screen, the Engine or Dashboard process exited, or the Codex desktop app closed. Correlate that time with Windows Application `.NET Runtime` events and both host logs before changing source. A successful single Engine/Dashboard observation reduces immediate concern but is not a recurrence guarantee.

Plain English: the Dashboard will now stay responsive when its private Engine credential is briefly missing or replaced; it reports the Engine as unavailable and retries safely on the next request. Other verified failures involved disposable development database state or a locked database, so the later clean run is still not a guarantee that every separately reported crash is fixed.

A September 9 follow-up checked the same normal processes without restarting them: both Engine and Dashboard `/health/live` returned 200; both stderr files remained empty, and their current stdout logs contained no unhandled-exception, fatal, or `fail:` matches. Evidence: `logs/access-runtime-followup-health.json`. This longer observation supports the recovery fix but does not extend coverage to unobserved failure modes.

The midday Access integration review again received HTTP 200 from both normal hosts (`logs/access-midday-normal-health.json`). The credential provider/handler and all nine ingestion card, drawer, list, and pager source files match the earlier verified checkout exactly after line-ending normalization (`logs/access-prerequisite-preservation.json`). The integrated broad run passed all 1,021 Web tests, including the crash and ingestion regressions; its one unrelated event guardrail failure is tracked in the execution status.
