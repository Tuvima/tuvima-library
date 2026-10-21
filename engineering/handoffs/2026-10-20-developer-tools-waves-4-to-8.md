# Handoff: Developer tools waves 4 to 8 and wave 3 follow-ups

Run this handoff end to end per `.claude/rules/end-to-end-runs.md`.

> Written 2026-10-20 by the wave 3 coordinator session, at Shy's request to close out wave 3 and list what is left. The Locked spec [`engineering/specs/developer-tools.md`](../specs/developer-tools.md) (v3) wins wherever this note disagrees. Re-check `main` (`git fetch`, `git log`) and running sessions before starting. The broader handoff [2026-10-21-developer-tools-and-follow-ups.md](2026-10-21-developer-tools-and-follow-ups.md) (thread D) points here for waves 4 to 8 and adds notes for later packets from waves 1, 2 and WP27 in its section 4; read both.

## 1. Where things stand

| Wave | Packets | State |
|---|---|---|
| 1 Foundation | WP0, WP13, WP3, WP32 | On main (#269) |
| 2 Trust and safety | WP14, WP2, WP5, WP9 (+ WP1) | On main (#306, fix-forwards #308–#310) |
| 3 Platform services | WP27, WP4, WP6, WP15 (+ WP15a) | On main (#319 for WP27; #336 for the rest) |
| 4 AI and plugin kinds | WP10, WP28, then WP17, WP18 | Not started |
| 5 Surfaces | WP7, WP16, then WP29, WP30 | Not started |
| 6 Lifecycle and providers | WP19, WP20, then WP21 | Not started |
| 7 Notifications, events and backups | WP23, WP25, then WP24, WP31, then WP26 | Not started |
| 8 Close-out | WP8, WP22, WP11 (final regression), WP12 (docs) | Not started |

What wave 3 added, in brief: webhook delivery history and Send test (data store step 0022); the live plugin directory with a saved copy, Check now, an off switch and trust labels (the Install button is hidden until WP19, Shy's decision); declared web addresses on plugin manifests, shown on the approval step and card (WP15a, added for Universes U-WP9 and WP26); plugin host services: own records, recurring jobs in Scheduled tasks, Library events, always-on work, and a rate-limited plugin web connection that refuses undeclared addresses (step 0023). Spec change and progress notes record the details.

## 2. Wave 3 follow-ups (do these first, or fold them into wave 4)

1. **Ingestion before/after comparison for wave 3 was not run** (Shy asked to close out before it could get an Engine turn). Wave 3 changes no identification or enrichment code, so the risk is low, but it must be shown. Run it at the wave 4 checkpoint with "before" = the `main` commit just before the #336 merge commit (`git log --merges --grep "#336" -1` then its first parent) and "after" = the wave 4 tip, so the one comparison covers waves 3 and 4. Recordings: generated sample `.tmp/ingestion-baseline/det-recordings/20261020-114455-det-rec-c` (`-Generate -ReplayFrom <folder>`); real media `.tmp/ingestion-baseline/dt-recordings/20261019-175921-real-rec-2` (`-SampleRoot Z:\Development\testing\media\real_media -ReplayFrom <folder>`). Known noise: comic series rollup, some cast QIDs, the Frank Herbert author link. Each run is its own Engine turn (`.claude/rules/one-engine-at-a-time.md`).
2. **Live on-screen check of the webhook delivery list and Send test** was not captured (scripting the setup of a test server integration in a headless browser kept timing out; the card is covered by 12 component tests). Do it in the wave 4 or 5 visual check: Integrations > New application > Server Integration with Events Subscribe, add a webhook to `http://127.0.0.1:9/hook`, approve, switch on, Send test, and confirm a "Failed" row with "Attempt 1" and a "Test" marker; check desktop and tablet widths.
3. **Two flaky tests seen on CI for #336** (both pass alone, three times out of three; wave 3 touches neither area): `MediaEngine.Ingestion.Tests.DurablePipelineTests.IngestionEngine_FileDeletedWhileSettling_ClosesItsIntakeOperationAsMissingInsteadOfLeavingItPending` (waits about 4 seconds for a "missing" event) and `MediaEngine.Web.Tests.UiShellRenderTests.NeedsReviewRoute_ForAServerAdministrator_ListsTheItem_AndDismissingItResolvesIt` ("Sequence contains no matching element", the review item not yet rendered). Make their waits deterministic. (A third, `ManagedAccessHouseholdTests.ServerAdministrator_StillSetsATemporaryPassword_FromTheUsersList`, was fixed on the wave 3 branch by waiting for the Users list menu.) Small, independent: a good separate session.
4. **Local AI menu entry shows a red "Off"** when Local AI is off. Spec D-D says nothing switched off is ever red. Older than wave 3; fix in WP30 (wave 5), which rebuilds the Local AI page and its menu state.
5. **Phones and the Developer area: decided.** Shy's decision D-Y (2026-10-21, recorded in the spec): phones, tablets and native apps never show the Developer area in their navigation, Plugins included; the Plugins page stays reachable by address from a mobile browser and lays out cleanly. Apply it in wave 5 (WP16 and WP30 touch these pages); the menu change itself can be done there or as a small separate fix.
6. **Universes hand-over (owned by the Universes coordinator, local session "Universe.md v4 engineering specs"):** with wave 3 on main, U-WP9 can (a) tighten `FandomHostRule` to `*.fandom.com` subdomains only (bare `fandom.com` is the company site; the manifest keeps two destinations), and (b) point the Universe page's "Add Fandom details" link at `/settings/plugins?plugin=tuvima.fandom-lore`. Universes U-WP4 changes ingestion output, so it must not land alongside WP28.

## 3. Waves 4 to 8: order, dependencies and what to watch

Run waves one after another, at most two packets at once, each with the checkpoint (CI-strength build, full tests once, ingestion comparison, independent reviewer, merge by label). Packet text lives in spec section 6; below are only the facts a coordinator needs.

| Wave | Packet | Depends on (all built unless noted) | Watch for |
|---|---|---|---|
| 4 | WP10 Plugin access to Local AI | WP1, WP2, WP27 | Plugins ask for abilities through `IAiEngineRoom`; JSON-only and separate-program kinds come in WP17/WP18, so give them the contract now and test with an in-server test plugin. |
| 4 | WP28 AI features as Official plugins | WP2, WP15, WP27 | Touches ingestion (Smart labelling, Type helper move behind an ingestion-assist hook). Needs the ingestion comparison with AI plugins off (identical) and on (expected). Must not land alongside Universes U-WP4. Use the WP15 scheduler (`IPluginScheduledJob`) for Series order, Vibe tags, Description insights; Taste summary needs `watch-activity`. |
| 4 | WP17 JSON-only plugin runtime | WP9, WP10, WP13 | Reuse the WP9 declarative provider format; WP15a `network` destinations apply to JSON plugins too. |
| 4 | WP18 Separate-program plugin host | WP13, WP14, WP15 | Python or Go sample plugin; enforce permissions at the Library's side; plugin-scoped key. |
| 5 | WP7 Verification and blocklist | WP6 | Extend the directory format v2 (`docs/reference/plugin-catalog.md`); blocklisted plugins switch off with a reason. |
| 5 | WP16 Plugin pages and menu items | WP15 | Library page types only; desktop and TV sizes; apply D-Y (no Developer area in phone, tablet or app navigation). |
| 5 | WP29 Show AI results | WP28 | Presentation rules and visual checks at phone, tablet and desktop. |
| 5 | WP30 Local AI Settings page | WP1, WP27, revamp frame | Includes follow-up 4 (no red when off). |
| 6 | WP19 Install, update, remove | WP2, WP6, WP7, WP13 | Flip `PluginDirectoryService.InstallSupported`; the approval preview already words each plugin kind. Signature checks. |
| 6 | WP20 Providers fully under Plugins | WP9, WP14, WP19 | Also takes every remaining place that treats a provider specially by name (wave 2 change note). |
| 6 | WP21 WebAssembly tier | WP15, WP18 | **Licence check first (AGPLv3-compatible). If it fails, stop and ask Shy**; the rest of the spec is unaffected. |
| 7 | WP23 Notification service core | WP1, WP14, WP15 | Moves the password-reset email onto it; new Manage > System > Notifications page (Q14). |
| 7 | WP25 Backup destinations core | WP3, WP13, WP14, WP23 | Passphrase never stored. |
| 7 | WP24 Notification channel plugins | WP17, WP23 | |
| 7 | WP31 One event list, key warnings, health | WP4, WP15, WP23 | One registry shared by webhooks, notifications and plugin events (`ApplicationEventRegistry`; WP15 already reads it). |
| 7 | WP26 First backup destinations | WP25 | Licence check for any S3/WebDAV library. WP15a destinations show each address at approval. |
| 8 | WP8 Community plugins repository | none | Only when the first community plugin is ready; otherwise list it as waiting. |
| 8 | WP22 Live TV readiness record | WP13, WP15, WP16 | Decision record plus one contract test; builds no Live TV. |
| 8 | WP11 Final ingestion regression | everything | Any intended difference needs Shy's approval before merge. |
| 8 | WP12 Docs | everything | Docs site build; the search list in WP12's done-when. |

Practical notes from wave 3:
- **Data store steps:** main has steps up to 0023 after wave 3. Other specs add steps too; check the highest number on `main` right before merging and renumber (re-run `pwsh -File tools/Update-SchemaSnapshot.ps1`).
- **Generated fixture files** (`tests/MediaEngine.Contracts.Tests/Fixtures/*.approved.txt`) conflict on almost every merge with main: take main's copy, rebuild, then `pwsh -File tools/Update-ContractFixtures.ps1`.
- **`tools/` projects are not in the solution** but CI builds them with warnings as errors; search the whole repo for implementers before changing an interface.
- **Visual checks without a visible browser pane:** use headless Chromium through `playwright-core` from a scratch folder (browsers are already in `%LOCALAPPDATA%\ms-playwright`), against an isolated copy (temp `TUVIMA_CONFIG_DIR`, `TUVIMA_DATA_DIR`, `TUVIMA_DATA_PROTECTION_DIR`; Engine and Dashboard on spare ports). First-run setup needs a password account; "Start without a password" only works from the Engine's own machine. Generate test credentials into the ignored run folder, never into chat.
- **One Engine at a time:** queue in the main checkout's `.tmp/engine-slot.md`; checks and ingestion runs are separate entries; 60 minutes per turn.

## 4. Owed by Shy

- WP21: a decision if the WebAssembly runtime's licence does not pass.
- Approval of any intended ingestion difference in WP28 and WP11.

## 5. Plain English summary

Three of the eight developer-tools waves are done: plugins can now keep their own data, run scheduled jobs, hear about Library events, run in the background, and reach only the web addresses they declare, which the approval step names. Webhooks show their recent deliveries with a Send test button, and the Plugins page lists plugins from the public directory. What is left: five more waves (AI as plugins, plugin pages, install and update, notifications and backups, then the final check and docs), plus a few wave 3 loose ends, chiefly the before/after ingestion comparison, one screen check and two unreliable tests.
