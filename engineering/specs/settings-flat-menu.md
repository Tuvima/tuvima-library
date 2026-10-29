# Spec: Settings as a flat menu with page tabs

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-10-29 · **Related docs:** [Product overview](../product/overview.md) · [Settings and top navigation fixes](settings-and-top-navigation.md) · [Users, profiles and households](users-profiles-households.md) · [Plugins and Integrations screens](plugins-and-integrations-screens.md) · [Library pages](library-pages.md) · [Presentation rules](../../docs/product/presentation-rules.md)

Covers bug-log item F90 and leans on F84, F85, F86, F87, F88, F89 and F76 (see section 8 for how). Locked by Shy on 2026-10-29. Shy's own brief is the source; every product choice below is traced to it or flagged as a question.

## 0. Product walkthrough (read this first)

**What you see today.** Settings is a three-level tree in a side menu: groups (Personal, Server, Manage, Developer) with expanding parents (Library and Ingestion, Network and Remote Access, Streaming, Users and Sign-in, System, Local AI...) and children under those (Metadata Providers has Ingestion Flow under it). Related things are scattered: Integrations sits under Developer but lives inside the Users page, Scheduled tasks hides inside Tools and requirements, a person's Playback, Listening, Reading and Home preferences are four separate pages away from their profile, and the Logs page has its own tabs inside a tab.

**What you will see after this change.**
1. The Settings menu is one flat list with no expanding rows (the shape in your screenshot): **Personal** (Profile, Manage Profiles, Security & Devices), **Server** (Library & Ingestion, Network & Remote Access, Streaming, Users & Households), **Platform** (Local AI, Plugins, Integrations), **System** (System) and, in development builds only, **Developer Tools**.
2. Each page that has related areas shows one row of underlined tabs under its title (same look everywhere). A page with a single purpose has no tabs.
3. Every tab has its own address, so a link such as "Network > Remote Access" or "System > Scheduled Tasks" opens exactly that tab.
4. Nothing is removed. Every existing setting, permission and administrator/household rule keeps working; it just lives in one predictable place.
5. On a phone the page picker stays a single list in the same order, and tabs scroll sideways.

**What stays the same.** Who can see which page (administrator, household administrator, ordinary profile, Kids profile limited to Profile), desktop-only pages staying desktop-only, the universal editor and "Settings is configuration, never a correction workspace", the attention banner, breadcrumbs, the Vault rules, and the content of the pages themselves.

**Representative journeys.**
- Administrator opens **Library & Ingestion**: lands on today's library summary and live ingestion dashboard (tab Overview), then clicks **Libraries** for folders, paths and rules, or **Ingestion Flow** to see which provider runs at each stage.
- Administrator opens **System > Scheduled Tasks**, sees every task with its status, schedule, last and next run and a link to the setting that controls it.
- A household administrator opens **Manage Profiles**, sees a Netflix-style row of profiles, and opens a child's profile without switching into it; its tabs hold details, preferences (Playback, Listening, Reading, Subtitles, Home) and safety (PIN, restrictions).
- Someone on a phone opens **Settings**, picks "Security & Devices" from the page picker, and swipes between Sign-in and Devices & Sessions.

## 1. Goal

Make Settings fast to scan and hard to get lost in: one level of menu, tabs inside pages, no duplicate settings, and every capability still where an administrator or household member can reach it.

## 2. Scope

**In scope**
- A flat menu in the groups above, replacing the grouped, expandable tree (desktop sidebar, phone page picker, breadcrumb, Settings search and the attention banner links).
- One shared, route-driven underlined tab strip used by every multi-tab Settings page (built on the existing tab styling; see F84).
- New pages: Manage Profiles regrouped into profile tabs with drag-to-reorder, Security & Devices, Users & Households (Integrations moved out), Platform > Integrations as its own page, System with five tabs, Developer Tools with three tabs.
- Moving Scheduled Tasks out of Tools and requirements into System > Scheduled Tasks, with status, schedule, last/next run, run history and a link to each task's setting.
- Updating internal links, address aliases, breadcrumbs, the Settings search, public docs and tests to match.

**Out of scope**
- Restyling cards, tabs, filter bars and pills across Plugins, Integrations and Libraries (F84; this spec reuses whatever tab look F84 settles).
- Moving provider configuration onto plugin detail pages and making provider order editable (F85, F89; see D15 and D16).
- Redesigning Profile, Taste and Activity content (F86), the top-bar profile menu (F87), the "Who's watching" screen (F88), and dropping per-profile library access (F83).
- Any change to what a setting does, to permissions, or to data. No new Engine behaviour except the small run-history read in WP6 (D17).

## 3. Decisions made

- **D1.** Settings has one menu level: group labels plus pages. No expandable rows, no sub-menus. (Brief.)
- **D2.** Related areas become tabs inside the page; a page with one purpose has none. Tabs are links with their own addresses (`/settings/<page>/<tab>`), not hidden client-only panels, so deep links, back/forward and breadcrumbs work. (Brief: "directly accessible through links and routes.")
- **D3.** Menu content: Personal = Profile, Manage Profiles, Security & Devices; Server = Library & Ingestion, Network & Remote Access, Streaming, Users & Households; Platform = Local AI, Plugins, Integrations; System = System; Developer Tools only in development environments. Server, Platform and System are administrator-only as today; Users & Households keeps the household-administrator exception already built for Households. (Brief and screenshot.)
- **D4.** Order inside a page puts what people open most first: Library & Ingestion opens on Overview (library summary plus live ingestion dashboard, exactly today's page), then Libraries, then Ingestion Flow.
- **D5.** Metadata providers are managed as Plugins. Ingestion Flow owns provider order and stages. The separate "Metadata Providers" menu entry goes away; its page stays reachable from the provider plugins' Configure action until F85/F89 move its settings (D16).
- **D6.** Integrations leaves Users and becomes its own Platform page at `/settings/integrations`; Plugins stays at `/settings/plugins`. Both keep their own pages and existing behaviour.
- **D7.** Logs keeps its four views (Events, Technical log, Retention, Export) but they are shown as a segmented control inside the Logs tab, not a second row of tabs. Only one tab row per page.
- **D8.** Scheduled Tasks is a real tab of System; the card no longer appears in Tools and requirements. System > Configuration holds Tools and requirements and Notifications as stacked sections (headed groups, not tabs).
- **D9.** Old addresses keep working inside the app through the existing alias list (for example `/settings/providers` and `/settings/security/devices`), so links in notices, emails and docs do not die. Pre-release data is not touched.
- **D11.** (Shy, 2026-10-29) The first Personal page is named **Profile** everywhere (Settings menu, top-bar menu, breadcrumb). Its tabs keep today's addresses. It also serves as the page where an administrator reaches other people's profiles' settings via Manage Profiles.
- **D12.** (Shy, 2026-10-29) A Kids profile can open **Profile** (Overview, Activity, Taste) and nothing else in Settings; every other page stays blocked for them. This changes today's rule that a Kids profile never reaches Settings, so the menu shown to a Kids profile lists only Profile, and every other address still redirects them Home.
- **D13.** (Shy, 2026-10-29) Manage Profiles lets an administrator **drag profiles to reorder** them. The order is shared with the "Who's watching" screen and the top-bar profile menu (F87, F88). Edit on a profile takes you to that profile's page, as on Netflix.
- **D14.** (Shy, 2026-10-29) With a flat menu, text is a little larger and clearer: menu rows, group labels, tab labels and page titles each go up one step on the design-system type scale, with stronger contrast for group labels, and the same on phone. The exact sizes come from `engineering/design/design-system.md`; tab text size is set once in the shared tab component (F84) so every page matches.
- **D15.** (Shy, 2026-10-29) Ingestion Flow tab ships with today's content; editing the provider order is a separate spec ("Plan provider settings and editable ingestion").
- **D16.** (Shy, 2026-10-29) The Metadata Providers page leaves the menu but stays reachable from each provider plugin's Configure button until that separate spec moves its settings onto the plugin pages.
- **D17.** (Shy, 2026-10-29) Scheduled Tasks shows recent run history, using a small new Engine read of the stored task runs.
- **D18.** (Shy, 2026-10-29) Old addresses forward to the new place through the alias list.
- **D19.** (Shy, 2026-10-29) The Apps page (get the phone/TV app, address, QR code) is not a tab: a small, unobtrusive link at the bottom of the Profile page opens it, keeping its current address.
- **D10.** Mobile availability stays per page as today (Full, SummaryOnly, DesktopOnly); this spec adds no new phone capability. Tab rows scroll sideways on narrow screens.

## 4. UI behaviour

### 4.1 Menu and shell

| Situation | What the person sees |
|---|---|
| Desktop | Left menu: four labelled groups of flat rows with icons (screenshot), a search icon at the top, purple highlight on the open page. Developer Tools group appears only in a development build. |
| Tablet | Same menu, narrower. |
| Phone | The existing "Settings page" picker, listing the same groups and pages; pages marked DesktopOnly show the existing "Use a larger screen" notice. |
| Open a tab | Title, then the underlined tab row, then the tab's content; breadcrumb reads Settings > Page > Tab. |
| Not allowed (not an administrator) | Server, Platform and System are not listed and their addresses show the existing "not allowed" state. Kids profiles see only Profile (D12). |
| Engine unreachable | The existing read-only banner, per page, unchanged. |

### 4.2 Page-by-page map (every existing destination)

| New page (tabs) | Built from (existing) | Notes |
|---|---|---|
| **Profile** (Overview, Activity, Taste) | Overview (`OverviewPage`), Activity (`ActivityPage`), Taste (`OverviewTastePage`) | Rich personal pages kept as they are (F86 redesigns the content later). "Edit profile" opens the person's own page in Manage Profiles. Apps page: small link at the bottom of the page (D19). |
| **Manage Profiles** (list; each profile has tabs Profile, Preferences, Access & safety) | Profiles list (`ProfilesTab`), profile detail (`ProfileDetailPage`), Playback, Listening, Reading, Home, Subtitles pages (they already take a profile id) | Netflix-style list with drag-to-reorder (D13). Administrators can edit any permitted profile without switching into it. **Profile**: details, roles, sign-in status, activity link. **Preferences**: Playback, Listening, Reading, Subtitles, Home as stacked sections or sub-sections of one tab (no nested tab row). **Access & safety**: Libraries and lanes (until F83 changes it), Administrator PIN, restrictions, delete profile. "Preferences" summary page retired (its rows become the Preferences tab). |
| **Security & Devices** (Sign-in, Devices & Sessions) | `SecuritySettingsPage` (password, email, passkeys, two-step, sign out elsewhere), `DevicesTab`, `SignedInSessionsSection`, `PairedDevicesSection` | One person's own account. "Who viewed your photos" row stays where it is today. Server-wide sign-in methods are not here (see Users & Households). |
| **Library & Ingestion** (Overview, Libraries, Ingestion Flow) | Overview = `LibrarySummary` + `IngestionTasksTab` (live dashboard). Libraries = `LibrariesTab` (folders, paths, rules, Vault libraries, write schedule, excluded files, recycle bin). Ingestion Flow = the Ingestion Flow surface of `MetadataSettingsPage` | Recently Added and Needs Review stay off-menu pages (opened from the attention banner and row links) under this page's breadcrumb. Metadata Providers list: see D5 and D16. |
| **Network & Remote Access** (Overview, Local Network, Remote Access, Advanced) | `NetworkRemoteAccessSettings` and its panels | Already tab-shaped; only the address and tab style change. |
| **Streaming** (Hardware Acceleration, Optimized Copies, Diagnostics) | `PlaybackDeliverySettingsTab` (`EncodeSettingsTab`, `StreamingLimitsSettingsTab`, `OfflineDownloadsTab`) | Playback tool status (`PlaybackToolsStatusCard`) shows once, here under Diagnostics, and is removed from Tools and requirements to avoid showing it twice. |
| **Users & Households** (Users, Sign-in Methods, Households) | `UsersAccessSettingsTab` (Users, Sign-in Methods), `HouseholdsTab` | Keeps the profile-owned permissions model and the household-administrator exception. Integrations leaves this page. |
| **Local AI** (Status, Models & Hardware, Budget, Usage) | `LocalAiSettingsTab` | Unchanged content. The "Local AI models" card in Tools and requirements becomes a link to the Models tab so the model list lives in one place. |
| **Plugins** (no new tabs) | `PluginSettingsTab` (its own category tabs stay; F84 restyles) | Provider plugins get a Configure link to the provider's settings until F85/F89. |
| **Integrations** (no new tabs) | Integrations part of `UsersAccessSettingsTab` (`ManagedAccessApplications`, webhooks, invite and import helpers) | Same functions, own page and address. |
| **System** (Overview, Scheduled Tasks, Logs, Backups, Configuration) | Overview = `SystemSummaryPage`. Scheduled Tasks = `ScheduledTasksCard`. Logs = `LogsPage`. Backups = `BackupRecoveryPanel` (+ destinations, recovery). Configuration = `ToolsRequirementsTab` (Tools, minus the moved cards) + `NotificationsPanel` | Scheduled Tasks and Logs are described in 4.3. |
| **Developer Tools** (Provider Tester, Enrichment Tester, Development data) | `ProviderTesterToolTab`, `EnrichmentTesterToolTab`, `DevHarnessTab` | Development builds only; the page stays desktop-only. |

### 4.3 System tabs

| Tab | What the person sees |
|---|---|
| Overview | Today's server, processor and memory, and storage panels. |
| Scheduled Tasks | One row per task: name, plain-English status (Running, Waiting, Needs attention), schedule, last result and time, next run, and a link to the setting that controls it (for example Enrichment refresh to Library & Ingestion). Today's card already shows schedule, last run and next run; recent run history (last runs with result) is added in WP6 (D17). Empty: "No scheduled tasks yet". Error: existing "unavailable" state. |
| Logs | Events, Technical log, Retention, Export as a segmented control. |
| Backups | Today's backup status, create, restore, destinations and recovery content. |
| Configuration | Two headed groups on one page: **Tools and requirements** and **Notifications**. |

## 5. Data / model changes

One small addition: a saved display order for profiles (D13), shared by Manage Profiles, "Who's watching" and the top-bar menu; new profiles go to the end. Otherwise none to stored data. The Dashboard's menu description (`SettingsNav`) changes shape from a three-level tree to groups of flat pages with tab lists; address aliases are updated. One small Engine read for recent scheduled-task runs may be added (the run history is already stored in the Engine's task-run table); any wire change goes through Contracts tests and fixtures. Existing libraries are unaffected.

## 6. Work packets

Built in the cloud and validated locally as one batch. Waves by "Depends on"; at most two packets at once.

### WP1 — Flat menu model, route-driven tab strip, shell
- **What it does:** Replaces the tree in the Settings menu description with groups of flat pages, each with an optional tab list; puts the shared tab component from the one-shared-look plan (F84, packet 1: one flat underlined tab, `AppTabs` / `AppCountTabs`) on every page, with no new tab style. Settings needs two things beyond icon, label and count: each tab can be a link to its own address (route-linked), and the row scrolls sideways on a phone; updates the desktop menu, phone page picker, breadcrumb, Settings search, attention-banner links and the alias list; routes `/settings/<page>/<tab>`; applies the larger type (D14) to the menu rows, group labels and page titles.
- **Files likely touched:** `src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs`, `Services/Navigation/SettingsMenuBuilder.cs`, `Components/Pages/Settings.razor`, `Components/Settings/Frame/*`, new `SettingsPageTabs` component, `Components/Shared` (menu shell), `tests/MediaEngine.Web.Tests/SettingsNavTests.cs`, `SettingsMenuTreeTests.cs`.
- **Depends on:** F84 packet 1 (tab consolidation) merged first
- **Size:** Complex
- **Done when:** menu tests show exactly the groups and pages in D3 with no children; an unknown tab address falls back to the page's first tab; every old address in the alias list resolves; phone picker lists the same pages.

### WP2 — Personal: Profile, Manage Profiles, Security & Devices
- **What it does:** Wires Overview, Activity and Taste as Profile tabs; makes Manage Profiles the Netflix-style list with per-profile tabs (Profile, Preferences, Access & safety) reusing the Playback, Listening, Reading, Subtitles and Home pages with their profile id; combines Security, Devices and Sessions into Security & Devices; retires the Preferences summary page.
- **Files likely touched:** `Components/Settings/ProfilesTab.razor`, `ProfileDetailPage.razor`, `SecuritySettingsPage.razor`, `DevicesTab.razor`, `PreferencesSummaryPage.razor`, `Components/Settings/Overview/*`, matching tests.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** an administrator edits another profile's Playback preference from Manage Profiles without switching profile and the saved value appears in that profile; a non-administrator cannot open another profile's tabs; password, passkey, two-step, devices and sessions all remain reachable.

### WP3 — Library & Ingestion
- **What it does:** One page with Overview (existing summary and live dashboard, unchanged), Libraries (existing Libraries page and its sub-pages) and Ingestion Flow (existing surface); removes the Metadata Providers menu entry per D5 and keeps Recently Added and Needs Review reachable under it.
- **Files likely touched:** `Components/Settings/IngestionTasksTab.razor`, `LibrariesTab.razor`, `MetadataSettingsPage.razor`, `MetadataAdminShell.razor`, `LibrarySummary.razor`, tests (`LibrarySettingsFlowTests`, `MetadataSettingsCompositionTests`).
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** `/settings/ingestion` opens Overview with the live dashboard; `/settings/libraries` and its folder, Vault and rule sub-pages open under the Libraries tab; `/settings/providers` still opens (alias) and no provider setting is lost.

### WP4 — Network & Remote Access, Streaming
- **What it does:** Applies the shared tab strip to both pages; moves the playback tool status card to Streaming > Diagnostics.
- **Files likely touched:** `NetworkRemoteAccessSettings.razor`, `PlaybackDeliverySettingsTab.razor`, `PlaybackToolsStatusCard.razor`, `NetworkSettingsUiTests.cs`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** each tab opens by address and shows today's content; the playback tool card appears once.

### WP5 — Users & Households, Integrations, Local AI, Plugins
- **What it does:** Splits Integrations out of the Users page into its own page; makes Users, Sign-in Methods and Households tabs of Users & Households (Households keeps its household-administrator gate); applies the tab strip to Local AI; leaves Plugins as is with the Configure link for provider plugins.
- **Files likely touched:** `UsersAccessSettingsTab.razor`, `HouseholdsTab.razor`, `ManagedAccessApplications.razor`, `LocalAiSettingsTab.razor`, `PluginSettingsTab.razor`, `IntegrationHealthTests.cs`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** Integrations opens at `/settings/integrations` with every action it had; a household administrator who is not the server administrator sees only Users & Households > Households (as today).

### WP6 — System
- **What it does:** Builds the five-tab System page; moves `ScheduledTasksCard` out of Tools and requirements into the Scheduled Tasks tab and adds status wording, the setting link per task and recent-run history (small Engine read if needed, D17); shows Logs' four views as a segmented control; merges Tools and requirements and Notifications into the Configuration tab and replaces the duplicate Local AI models card with a link.
- **Files likely touched:** `SystemSummaryPage.razor`, `ScheduledTasksCard.razor`, `ScheduledTaskText.cs`, `ToolsRequirementsTab.razor`, `LogsPage.razor`, `NotificationsPanel.razor`, `BackupRecoveryPanel.razor`, `src/MediaEngine.Contracts/System/ScheduledTaskContracts.cs` and its Engine action and fixtures (only if history is added), tests.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** Scheduled Tasks no longer appears in Configuration; the tab lists every task the card listed before plus last runs; Logs, Backups and Configuration tabs open by address with today's content.

### WP7 — Developer Tools
- **What it does:** One Developer Tools page with three tabs; hidden entirely outside development environments.
- **Files likely touched:** `SettingsNav.cs`, the tester tab components, `DevHarnessSettingsTests.cs`.
- **Depends on:** WP1
- **Size:** Mechanical
- **Done when:** a production-mode test shows no Developer Tools entry; a development-mode test shows three tabs.

### WP8 — Links, docs and sweep
- **What it does:** Updates every in-app `/settings/...` link to the new addresses (about 120 for Libraries alone; aliases keep stragglers working), the public docs pages in `tuvima-docs` (presentation rules and the Settings architecture page, plus a doc note for the four drift-checked pages if their wording changes), `docs/architecture/settings.md`, the spec index and the Local checks batch.
- **Files likely touched:** Razor and C# files with settings links, `docs/product/presentation-rules.md`, `docs/architecture/settings.md`, `engineering/README.md`, `engineering/validation/batch.md`; companion docs PR in `tuvima-docs`.
- **Depends on:** WP2, WP3, WP4, WP5, WP6, WP7
- **Size:** Standard
- **Done when:** a test fails if any in-app settings link does not resolve to a page and tab; a text search finds no description of the old tree as current.

Waves: **Wave 1** WP1. **Wave 2** WP2 + WP3. **Wave 3** WP4 + WP5. **Wave 4** WP6 + WP7. **Wave 5** WP8. Opus review of WP1 and WP2 (permissions) and a final review of the whole.

## 7. Local checks

- **L1.** Desktop: menu shows the four groups (plus Developer Tools in a dev build), no expanding rows; open every page and every tab by clicking and by typing its address; breadcrumb is right each time.
- **L10.** Menu and tab text reads comfortably at your normal distance on desktop and phone, with no wrapping or cut-off labels.
- **L2.** Phone-width window and a real phone: page picker lists the same pages, tab rows scroll, desktop-only pages show the larger-screen notice.
- **L3.** As administrator, household administrator, ordinary profile and a Kids profile: the right pages appear, hidden pages stay blocked by address.
- **L8.** As a Kids profile: Settings shows only Profile with its three tabs; typing any other settings address sends you Home.
- **L9.** Drag profiles into a new order in Manage Profiles; "Who's watching" and the top-bar menu show the same order after a refresh.
- **L4.** Manage Profiles on your real household: edit another profile's Playback and PIN without switching in; saved values stick.
- **L5.** System > Scheduled Tasks on the real library: status, schedule, next run and history look right; each setting link goes to the right place.
- **L6.** Library & Ingestion on real media (`Z:\Development\testing\media\real_media`): Overview still shows the live dashboard during an ingestion; Libraries and Ingestion Flow tabs look the same as before.
- **L7.** Old bookmarks (`/settings/providers`, `/settings/security/devices`, `/settings/access/applications`) still land in sensible places.

## 8. Risks and open questions

- **Risks:**
  - *Overlap with F84.* F84 restyles tabs and cards centrally. WP1 should use the F84 tab look; if F84 lands first, WP1 reuses it, otherwise F84 restyles the one strip WP1 creates (Maintenance).
  - *Overlap with F85/F89.* Removing the Metadata Providers menu entry before provider settings move onto plugin pages would strand settings; D5 and D16 prevent that.
  - *Overlap with F83.* The profile "Access & safety" tab shows per-profile library access as it is built today; F83 will remove parts of it later.
  - *Lots of links.* About 120 library links alone; the alias list plus a link-resolution test (WP8) is the guard (Reliability).
  - *Scheduled-task history* may need a new Engine read (small Contracts change).
- **Gaps in the brief found by reading the code** (none removes a capability):
  - Ingestion Flow is read-only today, but the brief says it "controls provider ordering and processing stages" (D15).
  - The Overview "Apps" page (get the phone/TV app and QR code) has no home in the brief (D19).
  - Tools and requirements repeats the playback tool status and the Local AI model list that Streaming and Local AI already show; the spec shows each once.
  - Run history for Scheduled Tasks is stored but only last and next run are shown today (D17).

**Open questions:** none. Q1 to Q6 were answered by Shy on 2026-10-29 and are recorded as D11 to D19.

## Plain English summary

Settings becomes one simple list: Personal, Server, Platform, System (and Developer Tools only while building). Each page that covers several related things gets one row of tabs, so nothing is buried two menus deep. The system page gains a Scheduled Tasks tab that shows what runs and when. Every setting, permission and household rule you have today stays; they are just grouped where you would look for them. It is built in the cloud in five waves and checked on your PC in one batch.
