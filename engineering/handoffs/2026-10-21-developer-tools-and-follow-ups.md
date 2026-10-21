Run this handoff end to end per `.claude/rules/end-to-end-runs.md`.

# Handoff: developer tools after wave 2, and the follow-ups it turned up

> Written 2026-10-21 by the session that finished developer tools wave 2 and WP27 ("Developer tools spec v3 implementation"), for Shy and for whichever session picks up each thread. Snapshot of `main` on 2026-10-21; re-check with `git fetch` and `git log` before each thread. The Locked spec (`engineering/specs/developer-tools.md`, v3) wins over this note wherever they disagree. Only one Engine may run on the machine at a time (`.claude/rules/one-engine-at-a-time.md`); queue in the main checkout's `.tmp/engine-slot.md`.

## 1. Where things stand

| Area | State |
|---|---|
| Developer tools, waves 1 and 2 (WP0, WP13, WP3, WP32; WP1, WP14, WP5, WP2, WP9) | Merged: [#269](https://github.com/Tuvima/tuvima-library/pull/269), [#306](https://github.com/Tuvima/tuvima-library/pull/306) |
| Developer tools, WP27 (Local AI engine room) | Merged: [#319](https://github.com/Tuvima/tuvima-library/pull/319) |
| Developer tools, wave 3 (WP4, WP6, WP15, WP15a) | Built and reviewed; open as [#336](https://github.com/Tuvima/tuvima-library/pull/336), owned by the session "Developer tools spec wave" (it coordinates waves 3 to 8) |
| Developer tools, waves 4 to 8 | Not started; the same session chains them after #336 merges (see section 3) |
| Baseline tool (WP0) | Record and replay, unrecorded requests answered "not found" and listed, nightly maintenance and AI schedules held during a run ([#310](https://github.com/Tuvima/tuvima-library/pull/310), [#321](https://github.com/Tuvima/tuvima-library/pull/321), [#335](https://github.com/Tuvima/tuvima-library/pull/335)) |
| Run-to-run differences in ingestion | Nine causes fixed ([#327](https://github.com/Tuvima/tuvima-library/pull/327)); three left (thread C) |

Also merged during this run, as fixes found along the way: provider files load in the same order on every operating system ([#308](https://github.com/Tuvima/tuvima-library/pull/308)); scheduled tasks wait safely for a run more than 49 days away ([#329](https://github.com/Tuvima/tuvima-library/pull/329)); the best main poster survives a second artwork pass and artwork is re-ranked after re-identification ([#332](https://github.com/Tuvima/tuvima-library/pull/332), [#347](https://github.com/Tuvima/tuvima-library/pull/347), [#350](https://github.com/Tuvima/tuvima-library/pull/350)); Reset & Seed no longer hangs and works on the default folder layout ([#339](https://github.com/Tuvima/tuvima-library/pull/339), [#354](https://github.com/Tuvima/tuvima-library/pull/354)); CI fix-forwards ([#309](https://github.com/Tuvima/tuvima-library/pull/309), [#341](https://github.com/Tuvima/tuvima-library/pull/341), [#351](https://github.com/Tuvima/tuvima-library/pull/351)).

## 2. Threads to finish

| Thread | What is left | Needs Shy? | Runs beside |
|---|---|---|---|
| A. Doubled category folder | Fix filing so managed media lands in `library\Books\...`, not `library\Books\Books\...` | **Yes: pick one of the two fixes** in the session "Fix doubled category folder when filing managed media" | B, D |
| B. Blank Dashboard after an Engine restart | Investigate and fix (proposed card, not started) | No (start the card) | A, C |
| C. Last three run-to-run differences | Comic series rollup, cast Wikidata links, Frank Herbert / Frank Miller author link | No | B, D (needs the Engine slot for replays) |
| D. Developer tools waves 4 to 8 | WP10, WP28, WP17, WP18, WP7, WP16, WP29, WP30, WP19, WP20, WP21, WP23, WP25, WP24, WP31, WP26, WP22, WP11, WP12 (WP8 waits for a real community plugin) | Only at WP21's licence check if it fails, and the decisions in section 4 | A, B |
| E. Housekeeping | Main checkout and finished sessions (section 5) | Yes, small | anything |

### Thread A brief: doubled category folder
The session "Fix doubled category folder when filing managed media" found five filing routes that join the category folder onto a source folder that already is that category, so managed Books file as `library\Books\Books\...` (the same for other types). It has a plan with two possible fixes and is waiting for Shy's choice; nothing is built. When Shy has chosen: build it, with a test per filing route and a check that existing items are not moved without asking, CI-strength build and tests, independent review, merge by label. Data resets are acceptable pre-release.

### Thread B brief: blank Dashboard after an Engine restart
Seen twice on an isolated development Engine: the Dashboard page went blank and stopped updating after an Engine restart, and once right after creating the administrator in first-run setup. It may be what made the original "Reset & Seed hangs" report look like a hang, and it would also hit real people after an update or restore. The full brief is the proposed card "Dashboard goes blank after Engine restart": reproduce as a `[check]` in the Engine queue, capture console, circuit state and both logs, make the page recover or show the existing reconnect or error state, add a test, Dashboard visual check, review, merge. If it does not reproduce in a few tries, report what was tried.

### Thread C brief: last three run-to-run differences
Two replays of one recording on the same build still differ in about 124 values, from three causes found by the session that merged #327:
1. Whether comic issues roll up into their Series (Batman, Sandman, Saga) depends on which issues share a Wikidata bridge batch. This is most of the 124.
2. Some cast members get a person Wikidata link in one run and not the other (Dune, The Matrix, The Expanse cast).
3. Dune's book and audiobook can lose the Frank Herbert author link; the same person-creation race affected Batman and Frank Miller (#275).
Fix each with deterministic ordering, not delays. Use the recording `det-rec-c` in the main checkout's `.tmp/ingestion-baseline/det-recordings/20261020-114455-det-rec-c` (`-Generate -ReplayFrom <that folder>`), record afresh on the fixed build, and show two replays of one recording with zero differences. Each round takes about three hours of the Engine slot, so queue as `[ingestion]`, one run per turn.

### Thread D brief: developer tools waves 4 to 8
Already owned by the session "Developer tools spec wave", which chains the waves per its run list (`.tmp/runs/developer-tools.md` in the main checkout). Baselines to compare against: generated `det-rec-c` (above) or `rec-2`, and real media `real-rec-2`, both in `.tmp/ingestion-baseline/dt-recordings/`; real media sample folder `Z:\Development\testing\media\real_media`. Known noise until thread C lands: the three causes above. Carry the notes in section 4 into the packets they name.

## 3. Developer tools packets still to build (from the spec's waves)

| Wave | Packets | Depends on |
|---|---|---|
| 3 | WP4, WP6, WP15, WP15a | in #336 |
| 4 | WP10 (plugin access to Local AI), WP28 (AI features as Official plugins), then WP17 (JSON-only plugin runtime), WP18 (separate-program plugin host) | wave 3 |
| 5 | WP7 (verification and blocklist), WP16 (plugin pages), then WP29 (show AI results), WP30 (Local AI Settings page) | wave 4 |
| 6 | WP19 (install, update, remove with signatures), WP20 (providers fully under Plugins), then WP21 (WebAssembly tier: licence check first) | wave 5 |
| 7 | WP23 (notification service), WP25 (backup destinations), then WP24, WP31, then WP26 | wave 6 |
| 8 | WP22 (Live TV readiness record), WP11 (final ingestion regression), WP12 (docs); WP8 when a real community plugin exists | wave 7 |

## 4. Notes for later packets (decided or found during waves 2 and WP27)

- **WP20:** the places that still treat a provider specially by name are listed in the allowlist of `tests/MediaEngine.Providers.Tests/ProviderNamingGuardrailTests.cs`; WP20 converts them and empties that list.
- **WP30:** with no AI feature switched on and the AI runtime or model missing, the Local AI engine card says "Broken" (red) while the menu says "Off". WP30 rebuilds the page and should show the plain blocker ("Model not downloaded", "AI runtime not installed") and follow D-D (nothing switched off is red).
- **WP28 and WP30:** WP27 treats "Local AI is on" as "any AI feature switch or the speech pack is on", because there is no master switch. When the features become plugins, "on" should come from the AI plugins.
- **WP31:** changing an integration's key is not asked to be approved again, because its permissions don't change (documented in `docs/reference/api-endpoints.md`). Key expiry warnings belong to WP31; consider whether a changed key may outlive the old one's expiry.
- **WP27 small gaps:** text reading's rotation check (`DetectRotationAsync`) leaves no usage record; the startup hardware check waits behind background work and retries every 60 seconds.
- **Security, needs Shy before anyone builds it:** the WP14 secrets store keeps keys in the config folder as plain files, as before (the same protection as today, not encrypted at rest). Encrypting them would be new scope.

## 5. Housekeeping that needs Shy

- **Main checkout:** `E:\Source Code\tuvima-library` is on the already-merged branch `claude/activity-today-timezone-flake`, with Shy's own uncommitted edits to `config/core.json` and `config/libraries.json`. Switching to `main` was refused because main also changed those files. Shy: keep or discard those two edits, then switch the checkout to `main`.
- **Finished sessions to archive** (their work is merged; archiving removes their work copies): "Developer tools spec v3 implementation", "Developer tools wave 2 (replay baseline, WP14, WP2, WP5, WP9)", "Fix remaining ingestion run-to-run differences", "Long schedules must not stop the Engine", "Keep the best poster when artwork passes run again", "Re-rank artwork after an item is re-identified", "Remove unreachable TMDB TV code in images pass", "Baseline runs: also hold the 3 a.m. AI series check", "Stop 'Reset & Seed Test Library' hanging".

## 6. Open proposed changes from other threads (for visibility, owned elsewhere)

[#336](https://github.com/Tuvima/tuvima-library/pull/336) developer tools wave 3 · [#322](https://github.com/Tuvima/tuvima-library/pull/322) Universes U-WP4 · [#346](https://github.com/Tuvima/tuvima-library/pull/346) Videos wave 1 · [#355](https://github.com/Tuvima/tuvima-library/pull/355) main requires Build & Test · [#356](https://github.com/Tuvima/tuvima-library/pull/356) album artists and series authors from the list table · [#357](https://github.com/Tuvima/tuvima-library/pull/357) draft spec: person credits and series search.

## 7. Plain English summary

The first two parts of the developer tools work, and the Local AI engine room, are finished and merged, together with a run of smaller fixes they uncovered. The third part is built and waiting to merge in its own session, which will carry on through the remaining parts. Four things are left outside it: a choice from Shy on how to stop media being filed into a doubled folder; a blank Dashboard page after an Engine restart, ready to start as its own task; the last three places where ingestion can give a slightly different answer from run to run; and a little housekeeping on the main checkout and finished sessions.
