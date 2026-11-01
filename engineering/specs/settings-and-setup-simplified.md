# Spec: Settings and first-run setup, simplified (three groups, one job per page, Advanced last)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-01 (review and wireframes approved by Shy 17:41Z to 18:00Z: "looks good - build") · **Related docs:** [Product overview](../product/overview.md) · [Design system](../design/design-system.md) · [Settings as a flat menu with page tabs](archive/settings-flat-menu.md) (Built; D1, D2, D11 to D14, D18 stand) · [First-run setup flow v3](archive/first-run-setup-flow.md) (Built; D6 to D21 stand) · [Libraries redesign](libraries-redesign.md) (Built) · [Plugins and Integrations screens v4](plugins-and-integrations-screens-v4.md) (Locked; owns the Plugins page, the Metadata step tiles, descriptions, removal safety and the no-source import hold) · [Language-aware wording](language-aware-wording.md) · Review: `plans/settings-review/settings-review.md` in the project's planning files · Mockups: [`engineering/design/mockups/settings-and-setup-simplified.html`](../design/mockups/settings-and-setup-simplified.html), published at https://claude.ai/artifact/4p3Af5hNcgH3tSBoNDamMW · Local-check findings: [`engineering/validation/local-check-2026-11-01.md`](../validation/local-check-2026-11-01.md) items 1, 2, 3, 5, 6, 7, 8, 9, 20, 21

## 1. Goal

Settings becomes something a home user can read in one glance: three plain groups, every page doing one job, advanced settings always in the same last tab, and the things that had no home (the Library's name, its address, language, notifications) on one General page. First-run setup gets the fixes Shy saw on the PC: a Restore button beside Continue, one name instead of two, a tidy Tools list, a short editable Libraries step that teaches the four areas and starts empty, and a Vault step that looks like the Libraries step. Buttons gain a visible hierarchy everywhere.

## 2. Scope

**In scope**
- The Settings menu, its groups, page names, tab names and which content sits under which tab (section 4).
- A new General page and a new stand-alone Tools page; Backups as its own menu row.
- One "Advanced" tab convention, drawn by the shared tab component.
- The button hierarchy (Primary, Secondary, Quiet) in the shared button component and the border token that makes Secondary visible.
- First-run setup steps Welcome, Tools, Libraries and Vault; the Metadata step only in what it inherits from the shared pieces.
- The Profile, Manage Profiles, Security & Devices, Libraries, Playback, Users & Households, Network & Remote Access, Local AI, Backups and System pages as described in section 4.
- `config/libraries.json` shipping with no libraries.
- Public docs pages for Settings and first-run setup in `tuvima-docs`.

**Out of scope**
- The Plugins page, plugin pages, the Metadata step's tiles and descriptions, the "no source, no import" hold and removal safety (Plugins v4).
- Developer Tools (to be retired) and the folder picker's speed (speed thread).
- Integrations' detail page (its tiles and rules are in Plugins v4 D39; the detail-page tab tidy is a later spec so the two builds do not collide).
- Moving Profile › Import out of Settings (it stays as the last tab for now).
- Any new Engine capability beyond storing the Library's friendly name and deriving the network name from it.
- Phones and TVs gain no new capability; today's per-page mobile rules stay.

## 3. Decisions made

- **D1 (Shy, 1A). Advanced is a tab, never a page.** Every page that has advanced settings gets the same last tab named Advanced, pushed to the right end of the tab row, drawn with a small gear icon and an amber tint instead of the purple accent (resting and open), with no pill or marker, and with one shared warning line at the top: "These settings are for experienced administrators. Everyday use never needs them." Nothing advanced appears outside it. Reason: a setting stays next to the thing it changes; a single Advanced page would be a grab bag.
- **D2 (Shy, 2A). Three groups: Personal, Library, Server.** Platform and System go as group names. Server lists General, Users & Households, Network & Remote Access, Tools, Local AI, Plugins, Integrations, Backups, System in that order. Reason: a group name should be a word a home user would look under.
- **D3 (Shy, 3A). A true first run starts with no libraries.** `config/libraries.json` ships with the storage location, View storage and personal-library policy but an empty `libraries` list. Any provider keys already saved on the server are read by setup and those sources show as connected (a convenience for testing; the plugin flow itself is Plugins v4).
- **D4 (Shy, 4A). Three button levels.** Primary = filled accent, one per screen. Secondary = outlined with a clearly visible border. Quiet = text only, for the least important action (Skip, Not now). The shared button component owns the look; the "Ghost" style is retired into Quiet. The default border token is raised so an outlined button reads as a button on the dark surface. Reason: Back, Cancel and Restore looked like plain text because the border was drawn at 16 % opacity.
- **D5 (Shy, 5A). One name in setup.** Welcome asks "What should we call this Library?"; the network name is derived from it (lower-case, hyphens, ASCII only) and shown as a read-only line, "Phones and TVs on your home network will find it at shayas-library.local", with a small Change link that reveals the network-name field. Both are editable later on General.
- **D6 (Shy, 6A). The Library & Ingestion overview goes.** Libraries is one page with five tabs: Libraries · Activity · Needs Review (n) · Matching · Rules. The live state is one status line above the table. "Now" and "Added" merge with Recently Added into Activity. Needs Review is a real tab with its count. Ingestion Flow is renamed Matching with unchanged content. The server-wide rules card becomes the Rules tab.
- **D7 (Shy, 7B). "What Kids can see" stays on Manage Profiles** beneath each household, because a household administrator reaches Manage Profiles and Households is a server-administrator page.
- **D8 (Shy, 17:54Z). The setup Libraries step teaches the four areas.** Titled "What do you have?", it always shows four area cards with their icons, Watch (movies, TV shows, home videos), Listen (music, audiobooks, podcasts), Read (books, comics, documents), View (photos and personal videos), each with one line on what fits and its own Add button, which opens the existing Add library pop-up already on that area ("What is it?" kinds, then the folder picker with the name in its footer). Added libraries are rows under their area (icon, name, folder path, rename or change folder, remove). An empty area says "Nothing yet". No search, filter, layout switch or per-row Add folder. The Vault step draws the Vault as a fifth card in the same style.
- **D9. The Tools row is one shared component** drawn the same in setup and Settings › Tools: name and version on the first line, a one-line description under it, a fixed Status column (Ready / Not installed / Downloading / Broken) and a fixed Action column (Install, Locate, Run installer, Cancel, or a dash); a summary line above ("5 tools detected · 4 ready · 1 needs setup"); Back · Check again · Continue along the bottom in setup. "Download now" tick boxes go: a Not-installed row offers Install.
- **D10. General is a new page with four panels and no tabs:** Name and address (the friendly name, the derived .local address with Change, the numeric address with Copy, the QR code and the app links that were hidden behind Profile's footer link), Language and region (moved from Configuration), Notifications (moved from Configuration, including the email test that was on Sign-in Methods), nothing else.
- **D11. Tools is its own page** (was the middle section of System › Configuration), drawn with the shared Tools row and one row that links to Local AI › Models for model files. Configuration no longer exists; the Notifications address opens General.
- **D12. Playback replaces Streaming** with two tabs: Quality (remote quality, upload protection, bandwidth test, one sentence that Library shares the connection automatically) and Advanced (hardware acceleration, optimized copies with the maintenance window as two time pickers, the encode-job list, diagnostics with FFmpeg shown once and a link to Tools). The disabled "Remote stream allocation" select goes.
- **D13. Users & Households is Users · Households · Advanced.** Users and Households are unchanged apart from the email test leaving. Advanced holds everything Sign-in Methods had, in three headed groups: How people sign in (mode, password, passkey, external-provider switches and the provider editor), Sessions and invitations (session hours, maximum sessions, invitation hours, trusted networks), Status (public address, passkeys, recovery delivery).
- **D14. Network & Remote Access is Home network · Remote access · Advanced.** Home network = status cards, address, Test connection, the "let devices find Library by name" switch. Remote access unchanged. Advanced = port, interface, IPv6, ports and binding, port forwarding, proxy trust, diagnostics, reset. The name field leaves this page (General owns it).
- **D15. Local AI is Status · Models · Advanced.** Status = engine card, the plugins using Local AI with this hour's counts, the "AI features are plugins" row. Models = resource profile, Whisper pack, benchmark, model files. Advanced = runtime settings, plugin allowance, invalidate benchmark, runtime diagnostics, the detailed usage table. The blocked notice shows once, in the page header.
- **D16. Backups is its own row with Backups · Destinations · Advanced.** Backups = status, Create backup, automatic schedule, one restore list that includes every destination's backups. Destinations = where backups go, add, test. Advanced = restored-progress waiting time, monthly restore check, archive facts.
- **D17. System is Health · Scheduled tasks · Logs · Advanced.** Health = today's Overview. Logs = Events and Technical log as its two views. Advanced = retention and export.
- **D18. Personal pages.** Profile tabs become Overview · Activity · Taste · Linked accounts · Import (Accounts renamed; Import last). Activity keeps search and dates in front; Scope, Device, Outcome, Show every session and Export sit behind the Filters button; Delete history stays in the "..." menu. A profile's Preferences tab gets an in-page section switch Watching · Listening · Reading · Home, one section at a time, with Lanes inside Home. Security & Devices renames "Devices & Sessions" to Devices, moves "Who viewed your photos" into the Access panel and renames "Linked sign-ins" to "Sign in with another account".
- **D19. No address forwarding** (flat-menu D18 stands): retired addresses are simply not found. Every in-app link, notice and docs page is updated to the new addresses in the same change, and the existing link test proves it.
- **D20. Housekeeping.** The three components no longer drawn anywhere (the metadata pipeline strip, the enrichment refresh schedule panel, the recently-added date row) are deleted. Wording the build touches moves into the wording file; it does not rewrite wording it does not touch.

## 4. UI behaviour

Every frame is in the mockup file; the table names what each person sees.

| Situation | What the person sees |
|---|---|
| Settings, server administrator, desktop | Left menu with three group labels, PERSONAL (Profile, Manage Profiles, Security & Devices), LIBRARY (Libraries, Playback), SERVER (General, Users & Households, Network & Remote Access, Tools, Local AI, Plugins, Integrations, Backups, System). Open page lit purple. Developer Tools only in a development build, as today. |
| Settings, household administrator or member | Personal pages; Users & Households › Households for a household administrator (as today); no Server pages otherwise. |
| Settings, Kids profile | Profile only (flat-menu D12). |
| Phone | Same groups in the page selector; today's per-page phone rules stand (summary-only pages, "Manage on a computer or tablet" notes). |
| Any page with an Advanced tab | The tab is last, at the right end, gear icon, amber tint; opening it shows the warning line first, then grouped cards. |
| Setup › Welcome | Language, Region, one name field, the derived .local line with Change, footer: Restore a backup (Secondary, left) · Continue (Primary, right). |
| Setup › Tools | Summary line, one shared Tools row per tool, footer Back · Check again · Continue. Nothing blocks Continue once the server check passed (v3 D9 stands). |
| Setup › Libraries, empty | "What do you have?", four area cards each "Nothing yet" with Add; footer Back · Skip for now (Quiet). |
| Setup › Libraries, with rows | Rows under their area with ✎ and ×; footer Back · Continue. Rename opens the small name dialog; change folder opens the folder picker; remove asks once and removes the library from the setup session. |
| Setup › Vault | Fifth area card, same Add; Not now (Quiet) · Continue. |
| Libraries page, first tab | Status line ("Up to date · last checked 10 min ago" or "Adding 38 files · about 4 min left") with "Check folders for changes" beside it, then the two-pane rail and table from the Libraries redesign. |
| Loading | Each page keeps its existing loading state and wording. |
| Error (Engine unreachable, not allowed, not found) | Each page keeps its existing error states; a retired address shows the existing "not found" page with a link to Settings. |
| Partly ready | Libraries' status line shows what is waiting (media tools, providers) exactly as the old Overview headline did. |

## 5. Data / model changes

- The Library's friendly name becomes a stored server setting the Dashboard can read and change (today it is only written during setup); the network name is derived from it unless the person changed it. Pre-release: existing installs keep whatever both values are now.
- `config/libraries.json` ships with an empty `libraries` list (D3). Pre-release: a reset is acceptable; existing installs keep their configured libraries.
- No data-store changes. Addresses change (D19); no forwarding.

## 6. Work packets

Wave A: WP1, WP2, WP3. Wave B: WP4, WP5, WP6. Wave C: WP7 to WP14 (two at a time, different files). Wave D: WP15.

### WP1 — Button hierarchy and the border token
- **What it does:** D4. `AppButtonStyle` keeps Filled, Outlined, Text; Ghost maps to Text. The outlined border uses a stronger token (raise `--tl-border-default` or point outlined buttons at `--tl-border-strong`); check the light theme too. Every setup footer uses Secondary for Back, Check again and Restore; Restore a backup moves into the Welcome footer row (left of Continue).
- **Files likely touched:** `src/MediaEngine.Web/Components/Shared/AppButton.razor`, `AppUiTypes.cs`, `wwwroot/tuvima.tokens.css`, `wwwroot/native-fields.css`, `wwwroot/app.css`, `Components/Setup/SetupWelcomeStage.razor`, `SetupStageFooter.razor`, `engineering/design/design-system.md`, `tests/MediaEngine.Web.Tests` button guardrails.
- **Depends on:** none · **Size:** Standard
- **Done when:** a bUnit test asserts the three classes; a computed-style check shows the outlined border at a contrast a person can see on the dark surface; the Welcome footer renders Restore and Continue in one row.

### WP2 — The menu, the pages and the Advanced tab
- **What it does:** D1, D2, D19. `SettingsNav.MenuGroups` becomes the three groups with the pages and tabs of section 2 of the review; `SettingsPageTab` gains an "Advanced" kind; the shared tab strip draws it last, right-aligned, gear icon, amber, and the page frame draws the warning line when that tab is open. New sections General and Tools; Backups, Playback, System routes; the alias list drops retired addresses; the Settings page routes each tab to its component (existing components for now, moved under the new tabs). Menu, breadcrumb, phone selector and link tests updated.
- **Files likely touched:** `src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs`, `Services/Navigation/SettingsMenuBuilder.cs`, `Components/Pages/Settings.razor`, `Components/Settings/Frame/SettingsPageTabs.razor`, `SettingsPageFrame.razor`, `Components/Shared/AppTabs*`, `tests/MediaEngine.Web.Tests/Navigation/*`, the in-app link test.
- **Depends on:** none · **Size:** Complex
- **Done when:** menu tests list exactly the three groups and their pages; every tab opens by address; the link test finds no link to a retired address; the Advanced tab renders with the gear and amber classes and the warning line.

### WP3 — The shared Tools row
- **What it does:** D9. `ToolRequirementRow` gets the two-line name and description, fixed status and action columns, and a summary line component; the setup Tools step and Settings › Tools use it; "Download now" tick boxes go (a Not-installed row's action is Install). The one-line descriptions come from the v2 wording table.
- **Files likely touched:** `src/MediaEngine.Web/Components/Shared/Tools/ToolRequirementRow.razor(.css)`, `Components/Setup/SetupToolsStage.razor`, `SetupToolRules.cs`, `Components/Settings/ToolsRequirementsTab.razor`, their tests.
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** bUnit renders each tool state with the status in the same column and the action in the same column; the summary line counts match the rows.

### WP4 — Welcome: one name, derived network name
- **What it does:** D5. The Welcome step shows one name field; the Engine derives the network name from it when none was set by hand; the derived line and Change link; the Setup contract carries whether the network name was derived. Wire snapshot refresh.
- **Files likely touched:** `src/MediaEngine.Web/Components/Setup/SetupWelcomeStage.razor`, `src/MediaEngine.Contracts/Setup/SetupServerIdentityDto.cs`, `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs`, `Services/Networking/*`, `tests/MediaEngine.Contracts.Tests` fixtures (`tools/Update-ContractFixtures.ps1`).
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** typing "Shaya's Library" shows "shayas-library.local"; Change reveals the field and a hand-set value survives a reload; contract fixtures pass.

### WP5 — Setup Libraries and Vault as area cards
- **What it does:** D8. A setup-only component draws the four area cards (icons from the areas), one-line "what fits", Add per area opening `CreateLibraryDialog` on that area, rows with rename, change folder and remove through the setup session; the Vault step reuses it with the Vault card only; the folder picker opens at one shared size with the name in its footer for every caller (local-check item 7). `LibrariesOverview Simple` mode is no longer used by setup.
- **Files likely touched:** `src/MediaEngine.Web/Components/Setup/SetupLibrariesStage.razor`, `SetupVaultOffer.razor`, new `Components/Setup/SetupAreaCards.razor(.css)`, `Components/Settings/Libraries/CreateLibraryDialog.razor`, `Components/Settings/Libraries/LibrariesOverview.razor`, `Services/Integration/EngineApiClient.Setup.cs` (rename, change folder, remove through the setup session; add Engine actions if missing), tests.
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** bUnit renders four cards with "Nothing yet"; adding a Watch library shows a row under Watch; rename, change folder and remove work in setup mode; the Vault step renders one card with the same Add button; the setup guardrail (nothing leaves the page) still passes.

### WP6 — Empty first run
- **What it does:** D3. `config/libraries.json` ships with no libraries; tests, fixtures and sample generators that assumed the seven are updated; the setup Metadata step already shows saved keys as connected (verify; fix if not).
- **Files likely touched:** `config/libraries.json`, `tests/**` fixtures that load it, `src/MediaEngine.Api` baseline-sample generator, docs that describe the defaults.
- **Depends on:** WP5 · **Size:** Standard
- **Done when:** a fresh Engine lists zero libraries; the setup Libraries step renders the empty state; the CI test filter passes.

### WP7 — General page
- **What it does:** D10. New page: Name and address (friendly name editable, derived address with Change, numeric address Copy, QR and app links moved from Profile › Apps), Language and region panel moved, Notifications panel moved with the email test. The Profile footer link and the Apps sub-page go.
- **Files likely touched:** new `src/MediaEngine.Web/Components/Settings/General/GeneralPage.razor`, `Components/Settings/LanguageRegionPanel.razor`, `NotificationsPanel.razor`, `Components/Settings/Overview/OverviewAppsPage.razor` (removed), `OverviewPage.razor`, `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs` (friendly name read/write), Contracts, fixtures.
- **Depends on:** WP2, WP4 · **Size:** Complex
- **Done when:** `/settings/general` shows the four panels; changing the name updates the derived address; the Notifications address opens General; the Profile page has no Apps link.

### WP8 — Libraries page: five tabs, no overview
- **What it does:** D6, D20. Status line above the table; Activity tab from the Now and Added panels plus Recently Added (one list, newest first, with type, status and date filters); Needs Review tab with count; Matching (renamed Ingestion Flow); Rules tab from the server rules card (empty "same name" row removed); the duplicate Recycle bin switch and the unmatched bar on the folders pane go; the three orphaned components are deleted; the library page's tab names move to the wording file.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor` (split), `LibrarySummary.razor` (removed), `IngestionNowPanel.razor`, `IngestionAddedSummary.razor`, `Components/Pages/RecentlyAddedPageContent.razor`, `Components/Settings/NeedsReviewPageContent.razor`, `MetadataSettingsPage.razor`, `Components/Settings/Libraries/LibraryServerRulesCard.razor`, `LibraryFoldersPanel.razor`, `Libraries/LibraryShell.razor`, `MetadataPipelineStrip.razor`, `EnrichmentRefreshSchedulePanel.razor`, `RecentlyAddedDateRow.razor` (deleted), tests.
- **Depends on:** WP2 · **Size:** Complex
- **Done when:** `/settings/libraries` shows the status line and table; the Activity tab lists what the old Now and Added panels and Recently Added listed; Needs Review tab shows the count; no file references the deleted components; menu attention shading for Needs Review still works.

### WP9 — Playback
- **What it does:** D12.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/StreamingLimitsPanel.razor`, `EncodeSettingsTab.razor`, `PlaybackDeliverySettingsTab.razor`, `PlaybackToolsStatusCard.razor`, `OfflineDownloadsTab.razor`, wording file, tests.
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** Quality and Advanced open by address; FFmpeg appears once on Advanced; the maintenance window is two time pickers that save the same value.

### WP10 — Users & Households
- **What it does:** D13.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/SecurityTab.razor` (becomes the Advanced tab's content in three groups), `UsersAccessSettingsTab.razor`, `HouseholdsTab.razor`, tests.
- **Depends on:** WP2, WP7 (email test moves) · **Size:** Standard
- **Done when:** three tabs open by address; every control Sign-in Methods had is on Advanced under one of the three headings; the household-administrator exception still shows only Households.

### WP11 — Network & Remote Access
- **What it does:** D14.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/NetworkRemoteAccessSettings.razor`, `NetworkOverviewPanel.razor`, `LocalNetworkSettingsPanel.razor`, `AdvancedNetworkSettingsPanel.razor`, tests.
- **Depends on:** WP2, WP7 (name field moves) · **Size:** Standard
- **Done when:** Home network shows status, address, test and the discovery switch and nothing to type; port, interface and IPv6 are on Advanced; the name field is gone from this page.

### WP12 — Local AI
- **What it does:** D15.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/LocalAiSettingsTab.razor`, `ModelsTab.razor`, `LocalAiRuntimePanel.razor`, `LocalAiBudgetPanel.razor`, `LocalAiUsagePanel.razor`, `Frame/LocalAiBlockedNotice.razor`, tests.
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** three tabs open by address; the blocked notice renders once; Budget and Usage addresses are not found.

### WP13 — Backups and System
- **What it does:** D16, D17.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/BackupRecoveryPanel.razor`, `BackupDestinationsSection.razor`, `SystemSummaryPage.razor`, `LogsPage.razor`, `LogRetentionPanel.razor`, `LogExportPanel.razor`, tests.
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** Backups has three tabs with one restore list; System has Health, Scheduled tasks, Logs (two views) and Advanced (retention, export).

### WP14 — Personal pages
- **What it does:** D18.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/Overview/*`, `Activity/ActivityPage.razor`, `ProfileDetailPage.razor`, `ProfileTabs.razor`, `PlaybackSettingsPage.razor`, `HomeSettingsPage.razor`, `HomeLanesSettings.razor`, `SecuritySettingsPage.razor`, `DevicesTab.razor`, wording file, tests.
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** Profile tabs read Overview, Activity, Taste, Linked accounts, Import; the Activity toolbar shows search, dates and Filters only; Preferences shows one section at a time; the Security tab names and panel moves match D18; `PublicDocsDriftTests` still pass.

### WP15 — Docs and wording
- **What it does:** `tuvima-docs` pages for Settings and first-run setup describe the new menu, General, Tools, the Advanced convention and the setup steps; `docs/product/presentation-rules.md` and `engineering/design/design-system.md` (button levels, Advanced tab) updated; wording the build touched is in the wording file; `engineering/README.md` row; Local checks appended to `engineering/validation/batch.md`.
- **Files likely touched:** `tuvima-docs/content/products/library/guides/*`, `docs/product/presentation-rules.md`, `engineering/design/design-system.md`, `engineering/README.md`, `engineering/validation/batch.md`.
- **Depends on:** WP7 to WP14 · **Size:** Standard
- **Done when:** the docs build passes; `PublicDocsDriftTests` pass; no page describes Library & Ingestion, Streaming, Configuration or Sign-in Methods as current.

## 7. Local checks

- **L1.** Fresh setup on SHY-DESKTOP: Welcome shows one name field; typing a name shows the .local line; Restore a backup sits beside Continue and looks like a button.
- **L2.** Tools step: every row has name, version where known, one-line description, status in one column, action in one column; Tesseract offers Install; the summary line counts are right.
- **L3.** Libraries step starts with four empty area cards; Add on Watch opens "What is it?" then the folder picker on `Z:\Development\testing\media\real_media`; the row appears under Watch; rename, change folder and remove work.
- **L4.** Vault step shows the fifth card with the same Add; the folder picker is the same size with the name in its footer.
- **L5.** Settings menu shows three groups in the agreed order; Developer Tools only in a development build.
- **L6.** Every page with an Advanced tab shows it last, amber, with the gear and the warning line; nothing advanced appears on the other tabs (walk Playback, Users & Households, Network, Local AI, Backups, System).
- **L7.** General: change the Library name and see the derived address change; the QR code pairs the Android app; language change applies household-wide; a test email sends.
- **L8.** Libraries page: status line changes while an ingestion runs; Activity lists what was added; Needs Review tab count matches the queue.
- **L9.** Back, Cancel and Secondary buttons are visibly bordered on every page at 1440 and 1280, light and dark themes.
- **L10.** Phone (Android): the page selector lists the three groups; summary-only pages still say to use a computer.

## 8. Risks and open questions

- **Risks:** the Plugins v4 build touches the same Settings page shell (menu row, Metadata step); the two builds must run on different files and the Plugins row's content is left untouched here. Scoped CSS ceiling: reuse shared components, do not add page CSS. Retired addresses (D19) break any external bookmark; acceptable pre-release. Fixtures and tests that lean on the seven default libraries will need updating (WP6). The friendly name needs a small Engine setting (WP4, WP7); keep it to one read and one write.
- **Open questions:** none (Shy answered 1 to 7 and approved the wireframes).

## Plain English summary

Settings gets three plain groups instead of four developer-named ones, every page does one job, and anything advanced is always in the same amber Advanced tab at the end of the row. A new General page holds the Library's name and address, language and notifications. The Libraries page absorbs the old overview and Recently Added, with Needs Review as a tab. First-run setup gets the fixes Shy saw: Restore beside Continue, one name, a tidy Tools list, four area cards that teach Watch, Listen, Read and View and start empty, and a matching Vault step. Buttons gain a visible hierarchy app-wide. Fifteen packets in four waves, built in the cloud, with ten Local checks for the batch.
