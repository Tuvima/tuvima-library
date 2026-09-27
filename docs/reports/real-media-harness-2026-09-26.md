# Real-media harness implementation and execution

## Product outcome

The development library was cleared and rebuilt from the existing folders in `C:\Temp\real_import`. Accounts, profiles, permissions, and provider settings were retained. Catalogue files remain at their original paths; personal media uses two linked View sources for the existing Shy profile. Generated fixtures are excluded from this run.

The harness now provides a protected offline reset command, a source-integrity status card in Developer Tools, and a per-file audit. Startup hashes every source file before ingestion begins. Live filesystem monitoring and periodic inventory/configuration checks stop the Engine if protection is violated or monitoring loses coverage. Output goes to separate Tuvima storage. Generated reset/seed actions are blocked while real-media mode is active.

## Preservation incident — recorded, not erased

The first ingestion attempt exposed a pre-existing folder-health check that wrote and removed `.tuvima_probe_*` files in three read-only folders: `audiobooks`, `books`, and `tv`. The monitor detected it and stopped the Engine. Those three folders' last-write timestamps changed.

Full SHA-256 verification subsequently confirmed that all 563 original files retained their contents, names, sizes, recorded timestamps, attributes, and security descriptors. The probe check and settings path test were fixed to avoid writes in read-only sources. Original timestamps were not restored or otherwise edited. The immutable original baseline and violation evidence remain in the report directory; a separately recorded monitoring baseline permits continued observation after validating that the only differences were the evidenced directory timestamps. Consequently the run must not be described as a completely unchanged source tree or a passing original-baseline preservation audit.

## Implementation details

- `RealMediaHarness` validates exact approved sources, rejects overlapping output paths/reparse points/hard links, records a baseline, backs up SQLite/configuration, and transactionally clears content state with foreign-key verification.
- The reset retains security/configuration state and storage reservations, clears catalogue and View content/jobs, and uses a fresh derived-data namespace. Old media and fixtures on disk are not deleted.
- `RealMediaProtectionService` runs before other workers, validates protected configuration, watches source changes, and exposes administrator-only status/verification actions.
- `RealMediaAudit` produces per-file indexing outcomes, identity/operation states, subtitle inventory, outside-source checks, and an optional full hash comparison.
- Source mutation gates remain active. Rejected-file cleanup is restricted to validated staging paths. Folder-health checks no longer test write access on read-only or unknown sources.
- Restart recovery now checks incomplete durable ingestion operations before the known-file fingerprint shortcut and repairs missing identity jobs after interrupted registration, respecting local-only identity policy.
- `tools/Run-RealMediaHarness.ps1` performs the offline preparation and starts the apps; `Run-FullIntegration.ps1 -Mode Real` delegates to it. Developer Tools displays status and offers full verification. Reset is deliberately an offline command, not an in-process destructive UI action.

## Evidence and limits

Local evidence is under `tools/reports/real-media-20260926` (ignored by Git): database/configuration backup, original and monitoring baselines, preserved failure/recovery evidence, live protection status, ingestion ledger, video probes, and extracted subtitle samples.

The initial completed registration inventory contains 479 catalogue assets and 55 View file references, with zero assets outside the approved source root. These are file references, not distinct title counts. Provider enrichment and grouping are separate from registration and must be assessed through durable job state.

Real movie and episode samples decoded successfully with FFmpeg. Embedded text subtitles were identified and extracted to separate report files (13 movie cues and 23 episode cues in the sampled interval). This does not certify browser playback, rendered subtitles, every codec, ISO playback, or DRM-protected formats.

Validation completed: API and Dashboard builds; nine real-harness tests; eighteen source-mutation tests; three Developer Tools tests; the full 160-test ingestion suite followed by seven characterization tests including two new restart guards. Runtime verification details are recorded below when complete.

### Runtime checks

- Restart resumed exactly six interrupted registrations. All six completed, and the identity-job count reached 479. No interrupted file operations remained.
- Full original-baseline audit completed at `2026-09-27T02:52:27Z`: only the three documented directory timestamps differ. Every original file matches. The audit correctly exits with a preservation failure rather than hiding those differences.
- Developer Tools was checked at a 1440 × 1000 desktop viewport: the 563-file protection status and historical violation are visible, verification controls are available, and generated reset/seed controls are disabled.
- View Folders was verified for Shy: `personal_photos` displays 47 items and `personal_videos` displays 3. Both are linked sources, outside Shared, with timeline inclusion. The 50 displayed items have 55 indexed file references.
- The Movies browse was empty while its six assets were queued for identity and review. Direct detail access displayed the indexed original and technical subtitle indication. The tested work-detail Watch action did not launch playback; the documented player route was tested separately.
- Real movie player checks used *A Time to Kill* (VC-1) and *Dr. Horrible's Sing-Along Blog* (H.264). Both produced playback manifests and started/queued adaptive HLS preparation. During observation the browser remained at 0:00 with no attached video source and captions disabled. SQLite recorded both packages as `preparing`, with FFmpeg running against the separate variant cache. **Browser playback, rendered subtitles, seeking, and resume are not verified.** Complete-package preparation is an existing player limitation; no original was converted in place.
- Provider enrichment continues independently and is routing many track-level records to Review when no retail candidate is found. The run is not certified as fully enriched or fully playable. The live Operations page and `ingestion-report.json` carry the current durable state; report counts are snapshots.

## Plain-English summary

Tuvima now uses the real collection in place, with a repeatable clean-library setup and visible checks intended to catch accidental source changes. All original files survived unchanged, but the first run exposed and recorded a folder-timestamp defect. Successful indexing alone is not presented as proof that every title can play or that metadata matching has finished.
