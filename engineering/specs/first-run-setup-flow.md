# Spec: First-run setup, redesigned (seven honest steps, one shared UI)

> **Status:** Locked · **Version:** 3 · **Last updated:** 2026-10-30 · **Related docs:** [Product overview](../product/overview.md) · Libraries redesign (Draft, in the project's planning files, `plans/libraries-redesign/libraries-redesign.md`) · Plugins and Integrations screens v3 (Draft, `plans/plugins-redesign/plugins-and-integrations-screens-v3.md`) · [Library setup and add-folder flow](library-setup-add-folder.md) (v2, In progress; D14 to D21 setup View and Vault) · [Users, profiles and households](users-profiles-households.md) (D3) · [First run and prerequisites (archived)](archive/first-run-prerequisites.md) · Mockups: `plans/first-run-setup-redesign/first-run-setup-mockups.html` in the project's planning files, published at https://claude.ai/artifact/2EfCoGM89JCyvoyT7LUMyJ

> Written 2026-10-30 after Shy's note in the Plugins thread: "would this update the mechanics of the first run setup? We should use the same UI rather than maintain two. The first run setup probably needs an overhaul as well from a UI perspective as well as logical layout etc." Everything about today's setup below was read in the code on main (commit 2bc38e0b); nothing was run. Version 3 keeps every decision of Version 2 (D1 to D5; its packets WP1 to WP3 are Built, awaiting Local checks, and are listed in section 10) and adds the overhaul on top. **Locked by Shy on 2026-10-30** ("Agree with the answers. Lock it"): Q1 to Q6 were each answered with the recommended option A and are recorded as D16 to D21.

## Plain-English product walkthrough

### What setup is today, and why it feels rough

The wizard today has **six sidebar entries but eight screens**, in this order: Name your server → (a silent server check that sometimes becomes a screen) → Your account → Libraries → Language → Enhance your library → All set. Read in the code, the causes of what Shy saw are:

1. **The sidebar is not the real list of steps.** The Engine keeps six step keys (server, server check, administrator, media locations, providers, readiness). The Dashboard hides one (the server check), splits another into two (Language and Enhance your library both save under "providers"), and shows the Vault offer inside the Libraries step with no entry at all. So the list on the left and what happens on the right drift apart, "Later" marks land on the wrong entry, and nobody can tell where they are. (Maintenance and Ease of use.)
2. **Creating the first administrator jumps out of setup.** The administrator step sends the browser to the sign-in page and back; without a password that detour landed on "Who's using Library?" (F2). v2 fixed the landing (D1), but the detour itself remains: the setup page unloads, reloads, and re-reads its state. Any hiccup there (a slow Engine, a browser that is not signed in) leaves the last screen spinning for ever (B20).
3. **The tools story is told in three places.** The server check runs silently and only becomes a screen the first time a Required tool is missing; Tesseract is offered on the last screen; FFmpeg's download shows at the start but its result only at the end. v2 tidied the wording (D2) but the three places stay, so Shy still cannot see at a glance "what was checked, what is downloading, what is mine to decide" (F5, B19).
4. **The Libraries step is a second copy of Settings.** Setup renders the Libraries table in a "Simple" mode, but rows have at times navigated into Settings and abandoned setup (F75), and the table does not match the target design (F4, F6). The Libraries redesign draft already proposes "setup mode is a flag on the shared components, not a copy" (its D13). This spec adopts it.
5. **Enhance your library is its own design.** Provider cards here are a layout nobody else uses; the Plugins redesign makes every metadata provider a tile with one status pill ("Needs a key") and a Configure action. Setup keeps a separate card design and a separate "Ready without signup" panel.
6. **Language sits in the middle.** The wizard asks for the display language after the libraries, so the first half of setup cannot be in the person's language, and metadata language is asked before anyone knows which media types exist.

### What it feels like after

**Seven steps, one question each, and the list on the left is exactly the list the Engine keeps:**

| # | Step | The one question | Can be skipped? |
|---|---|---|---|
| 1 | **Welcome** | What language, and what should this server be called? | No (a name is needed; defaults are pre-filled) |
| 2 | **Your profile** | Who are you, and how do you want to sign in? | No |
| 3 | **Tools** | Here is what Library needs; download the optional ones now? | Continue is always allowed |
| 4 | **Libraries** | What do you have? | Skip for now |
| 5 | **Vault** (optional; first administrator on a desktop only) | Turn on the Vault? | Not now, no trace |
| 6 | **Metadata** | Connect the sources that need a key? | Skip for now |
| 7 | **All set** | Everything checked; open your library. | No |

1. **Welcome** names the server and picks the display language and region in one short screen. The language switches as soon as it is chosen, so every later screen is already in it. "Restore a backup instead" stays as a quiet link. While the person types, the Engine has already started its server check in the background (data folder, free space, tools), so step 3 has its answers ready.
2. **Your profile** is one screen: your name, then two choice rows, "Just my name" (recommended on this computer: everyone on the home network goes straight in) and "Email and password" (use Library on other devices). Choosing one shows only its fields underneath; a PIN is a small "Add a profile PIN" link that expands, never four extra boxes. Create profile signs the person in **without leaving the setup page**: no sign-in page flash, no profile choice (v2 D1 stays). Recovery codes, when a password was set, appear on the same screen with "I saved them, continue".
3. **Tools** is a real step with every tool on it, ready or not: FFmpeg (required; already downloading automatically on Windows, with its progress bar), Tesseract and ExifTool (optional, each with a "Download now" tick that is on by default when the tool is missing), the image engine (always ready, shipped inside). One sentence per tool from the single wording table (v2 D2). When everything is ready, the screen is four green ticks and a Continue. Local AI stays out of setup (v2 D3). **Nothing here blocks:** a missing tool is a plain line and a next step, and downloads carry on in the background through the rest of setup.
4. **Libraries** is the Libraries redesign's overview table in setup mode: one table grouped by Watch, Listen, Read and View, **+ Add library** opening the same two-step dialog (What is it? then the folder browser), rows with "+ Add folder" instead of a chevron, and a Cards / List switch like the Plugins page. Nothing in this step navigates anywhere; Continue and Skip for now are the only ways out. View folders are added from the View rows with the View form Settings uses.
5. **Vault** stays exactly as the add-folder v2 spec built it (its D17 to D21), but as a step with its own entry in the list, shown only to the first administrator on a desktop or tablet. Not now leaves no trace, including in the list.
6. **Metadata** shows the plugin tiles from the Plugins redesign, filtered to the libraries just added (Movies added, so TMDB appears; no Comics, so Comic Vine does not): the same tile, the same "Needs a key" or "No key needed" pill, a **Connect** button that opens the one-screen key dialog (prerequisites D9), and the same Cards / List switch. Sources that need no key are tiles too, with a green pill, so nobody wonders where MusicBrainz went. Metadata language is one quiet line above the tiles, pre-filled from the display language.
7. **All set** lists the seven steps with a tick, "Later" or "Needs attention", one short line each, and a single **Open my library** button. The tools list is not repeated here; if a download is still running, one line says so. If this browser is not signed in (B20), the screen says so and offers Sign in, never a spinner.

**The logic underneath, in plain words.**
- **One list of steps.** The Engine's list and the sidebar are the same seven keys; the Dashboard never maps, hides or splits an entry. Refreshing the page returns to the current step; any earlier step can be reopened from the list; "Return to current step" appears only when you went back.
- **Nothing leaves the page.** Creating the profile, adding libraries and connecting keys all happen on `/setup`. A guardrail test fails if any setup component navigates anywhere except the two allowed places (Sign in when the browser lost its session, and Open my library at the end).
- **Every step knows its state:** not started, current, done, later (skipped), needs attention. "Later" steps are listed on All set with a link into the right Settings page.
- **Shared screens, one switch.** The Libraries table, the Add library dialog, the folder pop-up, the plugin tile, the Cards / List switch and the key dialog are the Settings components rendered with a `Setup` flag. Setup has no component of its own for any of them.
- **Tools are checked once at the start, re-checked on the Tools step and on All set,** and every tool's sentence comes from one table. A download started in setup finishes in the background and the Tools and requirements page in Settings carries the same rows.

**What stays the same.** The setup code (the eight-character code for Docker and NAS installs), the home-network-only rule, the no-password start only on this computer, passwords, PINs, recovery codes, the profile choice on every later sign-in (Users, profiles and households D3; v2 D1 limits the skip to setup's own session), the folder checks and refusals, Read-only protection, the Vault's rules, Local AI being set up in Settings, and restoring a backup from the first screen.

**Representative journeys.**
- *Shy on the PC, no password.* Welcome (English, United Kingdom, "Shy's Library") → Your profile (Shy, Just my name, Create profile) → Tools (FFmpeg downloading 62%, Tesseract ticked, Continue) → Libraries (Add library → TV Shows → Z:\TV Shows → Add library; Add library → Music → Z:\Music; Continue) → Vault (Not now) → Metadata (TMDB Needs a key → Connect → paste → Test → Connected; MusicBrainz No key needed; Continue) → All set (seven ticks, Open my library). Eleven screens including the two dialogs, no page reload, no profile choice.
- *A NAS owner from a laptop.* The setup code screen first, then the same seven steps; "Just my name" is not offered because the browser is not on the server; Tools says FFmpeg is bundled in the Docker image (Ready); the Vault step is offered on the laptop (desktop form factor) and skipped.
- *Coming back later.* Closing the browser on step 4 and reopening `/setup` lands on step 4 with steps 1 to 3 ticked. Sign-in is still valid, so nothing is asked again.

### Observable acceptance criteria

- **A1.** The sidebar lists exactly the Engine's step keys, in order, with the same words everywhere (sidebar, All set, the Engine's status). No sidebar entry maps to a different key. (WP1)
- **A2.** After Create profile, in both sign-in modes, the next screen is Tools, reached without a full page load; the browser address stays `/setup`. A later sign-in by the same person still shows the profile choice. (WP2)
- **A3.** The Tools step lists FFmpeg, Tesseract, ExifTool and the image engine with a state each, including Ready; Local AI is absent; Continue is enabled whatever the states; an optional tool ticked for download is downloading before step 4 opens; the rows are the same component Settings › Tools and requirements renders (a guardrail test asserts it), and that page shows the same states after setup. (WP3)
- **A4.** The Libraries step renders the shared overview and Add library components with the setup flag; no row, heading or button in the step changes the browser address; a guardrail test enforces both. Cards / List switch present. (WP4)
- **A5.** The Metadata step renders the shared plugin tile and list view; a provider that needs a key shows "Needs a key" and Connect; one that does not shows "No key needed"; only providers relevant to the libraries added are listed (all of them when no library was added). (WP5)
- **A6.** All set lists seven rows with tick, Later or Needs attention and never repeats the tools list; a browser without a session sees "Sign in to finish setup" within a few seconds, not a spinner. (WP6)
- **A7.** The Vault step appears as an entry only for the first administrator on a desktop or tablet, after Libraries; after Not now the entry and every Vault word are gone from the page source. (WP1, WP4)
- **A8.** Welcome saves language, region and server name together; the interface language changes on the next screen. (WP1)
- **A9.** Reloading `/setup` on any step returns to that step with earlier steps marked done or later. (WP1)
- **A10.** At 1440 px the sidebar is a 240 px column; at 1280 px the same; at 1100 px and below the steps become a horizontal strip above the stage; dialogs are centred and one column. (WP7)

## 1. Goal

First-run setup should be seven honest steps that read like a list, each with one question, built entirely from the screens Settings already uses, so a first-time person can see where they are, nothing throws them out of setup, and the Library can never show two versions of the same screen. Reason: Ease of use, Maintenance (one UI), Reliability (no detours that can strand the page).

## 2. Scope

**In scope**
- The step list and its order: Welcome, Your profile, Tools, Libraries, Vault (optional), Metadata, All set; the Engine's step keys renamed to match.
- Your profile as one screen with inline sign-in, recovery codes inline, PIN folded in.
- Tools as a real step with every tool and the optional-download ticks.
- Libraries and Metadata rendered from the shared Settings components (Libraries redesign setup mode; Plugins redesign tiles and list view) with a `Setup` flag and a guardrail.
- All set as a seven-row summary with the signed-out state fixed.
- Sidebar and stage layout at 1440, 1280 and narrower; the setup frame's look brought in line with the shared tab, pill and card parts.
- Companion notes for the Libraries redesign, Plugins v3 and add-folder v2 specs (section 9).

**Out of scope**
- Local AI in setup (v2 D3 stands; see Q3).
- The setup code and the home-network-only rule (unchanged).
- How keys are tested, how folders are checked, the Vault's PIN rules (unchanged, reused).
- Restoring a backup (unchanged, same link).
- Native and TV clients (setup is browser-only).
- Any new download source or tool.

## 3. Decisions made

Version 2's D1 to D5 stand unchanged. D6 to D15 were proposed on 2026-10-30 and approved by Shy the same day with the answers below; D16 to D21 record those answers.

- **D6 (Shy, 2026-10-30). Seven steps, one list.** The Engine's step keys become `welcome`, `profile`, `tools`, `libraries`, `vault`, `metadata`, `done`, in that order, and the sidebar shows exactly those with friendly names. The silent server check is no longer a step; it is work the Engine does at session start and whose result the Tools step shows. Pre-release, so the old keys are simply replaced (project rule 6: no shims). Reason: Maintenance (no mapping layer) and Ease of use (the list is true).
- **D7 (Shy, 2026-10-30). Language first.** Display language and region move to Welcome, beside the server name. Metadata language stays a separate value but is asked on the Metadata step as one pre-filled line. Reason: the wizard can be in the person's language from screen two. See D17.
- **D8 (Shy, 2026-10-30). Your profile is one screen with inline sign-in.** Name first; two choice rows (Just my name / Email and password) with their fields under the chosen one; "Add a profile PIN" as an expanding link; recovery codes shown on the same screen. Creating the profile asks the Engine to issue setup's own session (v2 D1) and the page continues to step 3 without a full reload. Reason: Reliability (no detour) and Shy's "fewer controls". See D21.
- **D9 (Shy, 2026-10-30). Tools is a step, always shown.** Every tool, ready or not, with one sentence each from the v2 wording table; Required tools that Library can fetch (FFmpeg on Windows) are already downloading when the step opens; optional tools that are missing have a "Download now" tick, on by default; Continue never blocks; downloads continue in the background. All set does not repeat the list. **The rows are the Settings › Tools and requirements rows rendered with the `Setup` flag** (same tool list, same state wording, same Download, Run installer and Check again actions), so a download ticked here shows its progress and result on that Settings page afterwards, and a wording fix in one place fixes both; setup adds only the Download-now ticks and the Continue. Reason: trust (F5: "I cannot tell FFmpeg was checked"), one place for one story, B19's "choose at the start", and one UI (Shy, 2026-10-30). See D16.
- **D10 (Shy, 2026-10-30). Libraries = shared components in setup mode.** Adopts Libraries redesign D13: the overview table and Add library dialog (two steps, the second being the folder pop-up) rendered with a `Setup` flag: no rail, no library pages, no Import folder, "+ Add folder" per row, Vault group only after the Vault step; nothing navigates. Adds the Cards / List switch Shy asked for, shared with Settings. Reason: Maintenance; F4, F6, F75.
- **D11 (Shy, 2026-10-30). Vault is its own optional entry.** Add-folder v2 D17 to D21 stand; the only change is that the offer is a listed step ("Vault · optional") rather than a hidden phase of Libraries, shown only for the first administrator on a desktop or tablet. Not now removes the entry. Reason: the list stays true (D6). See D20.
- **D12 (Shy, 2026-10-30). Metadata = plugin tiles in setup mode.** Adopts Plugins v3 D15, D19 and D23: each provider is a tile with its logo, media pills and one status pill; setup adds a **Connect** button for providers that need a key (opening the existing one-screen key dialog, prerequisites D9) and hides the switch and "…" menu. The list is filtered to providers whose media types match the libraries added; with no libraries added, all are shown. Cards / List switch shared with Plugins. Reason: Uniformity. See D19.
- **D13 (Shy, 2026-10-30). All set is a seven-row summary.** One row per step: tick, Later (with a link to the Settings page that finishes it), or Needs attention (with the one thing to do). A single Open my library button; a one-line note if a download is still running. A browser without a session sees a Sign in card within a few seconds. Reason: B20; "no walls of text".
- **D14 (Shy, 2026-10-30). Nothing leaves the page.** A guardrail test asserts that setup components navigate only to Sign in (lost session) and to the library (finish). Reason: F75 and the F2 class of bugs cannot return.
- **D15 (Shy, 2026-10-30). Narrow layouts.** At 1180 px and above, a 240 px step column beside the stage; below 1180 the steps are a horizontal strip of numbered dots with the current step's name; below 600 the stage is one column with full-width buttons. Breakpoints stay 600 / 840 / 1180 (design system). Reason: existing guardrail.
- **D16 (Shy, 2026-10-30, Q1 answered A). The Tools step is always shown,** even when every tool is ready: four green ticks and one Continue. Reason: you can see FFmpeg was checked.
- **D17 (Shy, 2026-10-30, Q2 answered A). Language and region sit on the Welcome screen** beside the server name.
- **D18 (Shy, 2026-10-30, Q3 answered A). Local AI stays out of setup;** the Tools step carries one line pointing to Settings › Local AI. A download button for the local AI engine needs its own spec first (v2 D3).
- **D19 (Shy, 2026-10-30, Q4 answered A). Metadata tiles are filtered to the libraries added,** with a "Show all sources" link; with no library added every provider is shown.
- **D20 (Shy, 2026-10-30, Q5 answered A). The Vault is a listed optional entry** in the step list, shown only to the first administrator on a desktop or tablet; Not now removes it.
- **D21 (Shy, 2026-10-30, Q6 answered A). The profile PIN stays in setup as an expanding "Add a profile PIN" link** on the Email and password choice.

## 4. UI behaviour

Screen by screen (desktop; differences noted). Mockups in `first-run-setup-mockups.html`.

**Frame.** Left: Library icon and name, the seven steps (number or tick, name, "Later" where skipped). Right: a stage card with an icon, a title, one line, the content, and a footer with Back on the left and the step's one or two buttons on the right. The frame uses the shared tab, pill and button parts so it looks like Settings.

| Step | Normal | Empty / first visit | Loading | Error | Partly ready |
|---|---|---|---|---|---|
| Welcome | Language, region, server name, network name preview, Continue; "Restore a backup instead" link | Defaults pre-filled from the browser language | Short spinner only if the Engine has not answered | "Setup is not available from here" or the setup code screen, as today | — |
| Your profile | Name; two choice rows; fields under the chosen one; Create profile | "Just my name" preselected on this computer; "Email and password" preselected elsewhere (the only choice there) | Button shows Loading | Plain line under the fields ("Starting without a password only works on the computer that runs Library") | Recovery codes shown; Continue |
| Tools | Four rows with tick, progress bar or one sentence; "Download now" ticks on missing optional tools; Continue | All Ready: four ticks | "Checking your tools…" line | "Library could not check this server" with Retry; Continue still allowed | A download running: progress bar, Continue allowed |
| Libraries | Shared overview table in setup mode; + Add library; Cards / List; Continue or Skip for now | Table shows the four areas with "nothing yet" group rows | Existing table loading state | "Libraries could not be loaded", Try again | Scanning status on rows (scans start after setup) |
| Vault | Turn on the Vault? Not now / Set up the Vault, then PIN screens, then the Vault area table | Only the first administrator on desktop or tablet sees the entry | — | Engine refusal shown as one line | — |
| Metadata | Metadata language line; tiles filtered to the libraries added; Connect on "Needs a key" tiles; Cards / List; Continue or Skip for now | No libraries added: every provider shown | Tile skeletons | "Sources could not be loaded", Try again | Connected tiles show the green pill |
| All set | Seven rows; Open my library | — | "Checking your setup…" for a moment | "Sign in to finish setup" card with a Sign in button | "Needs attention" rows with one action each |

**Devices.** Setup is desktop-and-browser. 1440 and 1280: as described. Tablet (600 to 1180): step strip above; dialogs centred, one column; the Vault entry is offered (desktop or tablet form factor). Phone: usable but the Libraries step is read-only (editing is desktop-only); it says "Add libraries from a computer, or Skip for now". TV: not offered.

**Accessibility.** The step list is a `nav` with `aria-current="step"`; every state is a word plus an icon, never colour alone; dialogs trap focus; Cards / List is a labelled switch; progress bars carry text.

## 5. Data / model changes

- **Step keys.** The Engine's stored step list changes to the seven keys of D6. Pre-release: an in-progress setup on the old keys starts again (no conversion, project rule 6). The wire shape (`SetupStatusDto`, step status rows) is unchanged apart from the key values; Contracts fixtures are updated.
- **Welcome saves locale and server identity together** (one call or two in sequence; to be confirmed in WP1). No new stored values.
- **Tools step.** No new storage: the existing tools check, download and installer actions are reused. One new flag per optional tool in the setup request: "download now" (true by default when missing). The Engine records nothing new; the download is the existing background job.
- **Metadata filtering** uses the libraries already saved and each provider's existing media-type list; nothing stored.
- **Cards / List** preference is a per-browser convenience, remembered in local storage (not shared, not stored by the Engine).
- No data-store changes.

## 6. Work packets

Built in the cloud in waves (at most two packets at once), the reviewer after each wave; validated locally in one batch. **Depends on the Libraries redesign WP1, WP5 and WP7 and on Plugins v3 WP7 and WP9 being built first**, because this spec reuses their components; until then WP4 and WP5 cannot start. WP1 to WP3 and WP6 can.

### WP1 — One list of steps, Welcome first
- **What it does:** Replaces the Engine's six step keys with the seven of D6; removes the preflight step (the server check runs at session start and its result is read by the Tools step); Welcome saves display language, region and server name; the sidebar renders the Engine's keys directly with no mapping; the Vault entry appears per D11; reload returns to the current step; the Return to current step rule is unchanged.
- **Files likely touched:** `src/MediaEngine.Contracts/Setup/SetupContracts.cs` (`SetupWorkflow.StepKeys`), `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs`, `src/MediaEngine.Storage/OnboardingRepository.cs`, `src/MediaEngine.Web/Components/Pages/SetupPage.razor`, `Components/Setup/SetupStepper.razor`, new `Components/Setup/SetupWelcomeStage.razor` (from `SetupServerStage.razor` + `SetupLocaleStage.razor`), Contracts fixtures (`tools/Update-ContractFixtures.ps1`), `tests/MediaEngine.Web.Tests`, `tests/MediaEngine.Api.Tests` (to be confirmed).
- **Depends on:** none.
- **Size:** Complex.
- **Done when:** A1, A8, A9; a test renders the sidebar from a status with all seven keys and asserts the same seven labels; a status with Vault absent renders six.

### WP2 — Your profile, inline sign-in
- **What it does:** One screen per D8 (choice rows, fields under the chosen row, PIN as an expanding link, recovery codes inline); after Create profile the Dashboard completes setup's own sign-in (v2 D1 and WP1) and the Blazor circuit continues to the Tools step without a full page load. The profile choice rule for every other sign-in is untouched.
- **Files likely touched:** `Components/Setup/SetupAdministratorStage.razor` (renamed `SetupProfileStage.razor`), `Pages/SetupPage.razor`, `Services/Integration/DashboardAuthenticationEndpoints.cs` and `src/MediaEngine.Identity/FirstPartyIdentityService.cs` only if the in-page sign-in needs a new path (to be confirmed by a scout), tests in `tests/MediaEngine.Web.Tests` and `tests/MediaEngine.Identity.Tests`.
- **Depends on:** WP1.
- **Size:** Complex (security: sign-in; reviewed for access).
- **Done when:** A2; the existing v2 guardrail test (normal sign-in still goes to the profile choice) passes unchanged.

### WP3 — Tools step
- **What it does:** A real step per D9: the Settings › Tools and requirements tool rows (`ToolRequirementRow` and the page's list) rendered with a `Setup` flag (Local AI rows hidden, v2 D3; Download-now ticks on missing optional tools that start their download on Continue; Check again; Continue always enabled); the start-of-session check feeds it; All set no longer lists tools; `SetupToolRules` wording moves into the shared rows so setup and Settings read from one table.
- **Files likely touched:** `Components/Settings/ToolRequirementsPage.razor` and `Components/Shared/ToolRequirementRow.razor` (shared rows gain the flag; names to be confirmed by a scout), new thin `Components/Setup/SetupToolsStage.razor`, `SetupToolRules.cs` (merged into the shared rows), `Pages/SetupPage.razor`, `Endpoints/SetupEndpoints.cs` (tools step status), tests; `SetupPreflightStage.razor` and the tools part of `SetupReadinessStage.razor` are removed.
- **Depends on:** WP1.
- **Size:** Standard.
- **Done when:** A3; bUnit tests render each tool state and assert one sentence; a ticked optional tool triggers the install action once.

### WP4 — Libraries step from the shared components
- **What it does:** D10: `SetupMediaStage` renders the Libraries redesign overview and Add library with the `Setup` flag; adds the Cards / List switch to the shared overview (Settings gets it too); the Vault step is wired as D11 after it; the guardrail of D14 for this step.
- **Files likely touched:** `Components/Setup/SetupMediaStage.razor` (renamed `SetupLibrariesStage.razor`), `Components/Settings/Libraries/LibrariesOverview.razor(.css)`, `CreateLibraryDialog.razor`, `Components/Setup/SetupVaultOffer.razor` (now a stage), tests and guardrails.
- **Depends on:** WP1; Libraries redesign WP1, WP5, WP7.
- **Size:** Standard.
- **Done when:** A4, A7.

### WP5 — Metadata step from the plugin tiles
- **What it does:** D12: the Plugins v3 tile and list view rendered with the `Setup` flag (Connect button, no switch, no "…"), filtered by the libraries added; the metadata language line; the existing key dialog; Skip for now and Continue as today (the step is "later" until at least one key is saved, v2 behaviour kept).
- **Files likely touched:** `Components/Setup/SetupProvidersStage.razor` (renamed `SetupMetadataStage.razor`), the shared tile component from Plugins v3 WP7/WP9 (`Shared/AppListCard.razor` or its successor), `Shared/Providers/ProviderOnboardingDialog.razor` (unchanged unless the title needs the setup wording), tests.
- **Depends on:** WP1; Plugins v3 WP7, WP9.
- **Size:** Standard.
- **Done when:** A5.

### WP6 — All set and the signed-out state
- **What it does:** D13: seven rows from the Engine's step list, Later links, Needs attention actions, one note while a download runs, the Sign in card when the readiness call is refused for lack of a session (B20), Open my library.
- **Files likely touched:** `Components/Setup/SetupReadinessStage.razor(.css)`, `Pages/SetupPage.razor`, tests.
- **Depends on:** WP1, WP3.
- **Size:** Standard.
- **Done when:** A6.

### WP7 — Frame, narrow layouts, guardrail, docs, batch
- **What it does:** The setup frame restyled on the shared parts (tabs, pills, buttons); D15 layouts; the D14 navigation guardrail across every setup component; the public setup guide in tuvima-docs (`content/products/library/`) rewritten for seven steps; engineering index; screenshots at 1440 and 1280 published as one Artifact page; Local checks appended to `engineering/validation/batch.md`.
- **Files likely touched:** `Pages/SetupPage.razor.css`, `Components/Setup/SetupStepper.razor.css`, `tests/MediaEngine.Web.Tests` guardrails, tuvima-docs, `engineering/README.md`, `engineering/validation/batch.md`.
- **Depends on:** WP2 to WP6.
- **Size:** Mechanical (docs) + Standard (guardrail and layouts).
- **Done when:** A10; the docs build passes; no page describes the old six-entry wizard as current.

**Waves:** Wave 1: WP1. Wave 2: WP2 ∥ WP3. Wave 3: WP4 ∥ WP5 (once their dependencies are built). Wave 4: WP6. Wave 5: WP7.

## 7. Local checks

On SHY-DESKTOP, on a fresh data folder with the small test library (never the real one), at 1440 px wide and again at about 1280 px:

- **L1.** Open `/setup`: seven entries on the left (six if you are not on a desktop); Welcome shows language, region and server name; change the language and check the next screen is in it.
- **L2.** Your profile with "Just my name": Create profile lands on Tools with no sign-in page and no profile choice; the address bar still says `/setup`. Repeat on a wiped folder with "Email and password" and a PIN: recovery codes appear on the same screen, Continue lands on Tools. Sign out and in again later: the profile choice appears as usual.
- **L3.** Tools with FFmpeg already installed and Tesseract not: FFmpeg shows a tick and version; Tesseract shows one sentence and a ticked "Download now"; Continue; on All set a line says the download is still running or finished; Settings › Tools and requirements shows the same states. Remove FFmpeg from the path and repeat: it downloads itself with a progress bar and Continue is still allowed.
- **L4.** Libraries: the table matches Settings › Libraries exactly (same columns, same Add library dialog, same folder pop-up); click every heading, row and area name: nothing leaves setup; switch Cards / List; add a Movies and a Music library; Continue.
- **L5.** Vault: the entry appears after Libraries; Not now removes it and no Vault word is in the page source; on another wiped run choose Set up the Vault, both PINs, add an Adult content library, finish setup and confirm it locks and a Kid's profile never sees it.
- **L6.** Metadata: only TMDB-type and music providers show after L4; MusicBrainz says No key needed; TMDB says Needs a key; Connect with your real key, Test, the pill turns green; switch Cards / List; Continue.
- **L7.** All set: seven rows, ticks and "Later" where you skipped; Open my library opens Home. Open `/setup` in a private window before finishing: "Sign in to finish setup" with a button, not a spinner.
- **L8.** Narrow: resize to 1000 px: steps become a strip above the stage; dialogs centred; nothing clipped. Screenshots at 1440 and 1280 for the batch.
- **L9.** On the real folder `Z:\Development\testing\media\real_media`: run only L4's Add library on the TV folder and confirm the folder pop-up lists names in about a second (Libraries redesign WP6 scope; recorded here so setup is tried on the real drive once).

## 8. Risks and open questions

**Risks**
- WP2 touches sign-in. An in-page sign-in that misbehaves could leave setup's session half made; mitigated by keeping v2 WP1's Engine rule and adding the reload-returns-to-step behaviour (A9) so a refresh always recovers.
- WP4 and WP5 wait on two other drafts (Libraries redesign, Plugins v3). If either slips, setup can ship WP1 to WP3 and WP6 first with today's Libraries and provider screens inside the new frame; the gap is recorded in section 9.
- Renaming the step keys resets any setup in progress on Shy's test machines (pre-release, accepted).
- Filtering providers by the libraries added could hide a source someone wanted (for example SubDL subtitles with only Music added). Mitigation: a quiet "Show all sources" link on the step.

**Open questions:** none. Q1 to Q6 were answered by Shy on 2026-10-30 with the recommended option in each case and are recorded as D16 to D21.

## 9. Gaps and drift (companion notes)

- **First-run setup flow v2 (Built, awaiting Local checks):** D1 to D5 stand. Its WP2 "Your media tools" list moves from All set to the Tools step (D9); its L4 is replaced by L3 here. The v2 packets are listed in section 10.
- **Libraries redesign (Draft):** D13 (setup mode) is adopted as D10; this spec adds the Cards / List switch to the shared overview; the setup frame in its screen 8 is replaced by the frames here.
- **Plugins and Integrations v3 (Draft):** the tile (D15, D19, D23) gains a `Setup` rendering (Connect button, no switch or "…"); Shy's list view for Plugins is the same switch used here.
- **Library setup and add-folder v2 (In progress):** D14 to D21 stand; D17's "last step" becomes a listed optional entry (D11); its packet 9 guardrail is replaced by WP4's. The build thread adds the one-line note to that spec's next version (WP7).
- **Library setup (Watch, Listen, Read, View, Vault) v33 and Library pages v15:** no change beyond what the two drafts above already note.
- **Users, profiles and households:** D3 unchanged; v2 D1 (setup's own session skips the profile choice) stays the only exception.

## 10. Version 2 as built (kept for the record)

Version 2 (2026-10-27, Shy's bugs F1, F2 and F5) is Built, awaiting Local checks, and stands under Version 3:
- **v2 WP1 (Built):** setup's own sign-in skips the profile choice while the household has one profile (D1); a normal sign-in still shows it (guardrail test).
- **v2 WP2 (Built):** one "Your media tools" list with one sentence per tool from `SetupToolRules`, Local AI absent (D2, D3, D5). Version 3 moves that list to the Tools step and merges the wording into the shared Settings rows (WP3).
- **v2 WP3 (Built):** a missing optional AI runtime is one information line in the Engine log, not an error (D4).
- Its Local checks L1 to L5 are in `engineering/validation/batch.md`; Version 3's L2, L3 and L5 cover the same ground after this build and replace them.

## Plain English summary

Setup becomes seven plain steps that read like a checklist on the left: Welcome, Your profile, Tools, Libraries, Vault (optional), Metadata, All set. Each screen asks one thing. Creating your profile no longer bounces you through the sign-in page; it just carries on. Tools get their own step so you can see at a glance that FFmpeg was checked, what is downloading and which optional tools to fetch now, and that list is not repeated at the end. The Libraries step and the Metadata step are literally the Settings screens (the new Libraries table and two-step Add library, and the new plugin tiles with a Cards / List switch) shown inside setup, so there is only one version of each to maintain and nothing in them can throw you out of setup. The Vault stays an optional step for the first administrator only. You answered all six questions with the recommended option and locked it on 2026-10-30.
