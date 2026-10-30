# Spec: Plugins and Integrations screens (version 3: just tiles, own pages, one shared look)

> **Status:** Built, awaiting Local checks (PR #553; docs PR tuvima-docs #26) · **Version:** 3 · **Last updated:** 2026-10-30 (locked by Shy, 05:58Z, with the default answers 1A, 2A, 3A, 4A) · **Related docs:** [Plugins and Integrations screens v2](../plugins-and-integrations-screens.md) (Built, awaiting Local checks) · [One shared look](../one-shared-look.md) (Built) · [Plugin architecture](../plugin-architecture.md) (Built) · [Provider settings on plugin pages and editable Ingestion Flow](provider-settings-and-editable-ingestion.md) (Built) · [Developer tools](../developer-tools.md) D-J and WP20 · Design system · Follow-ups F73, F78, F84, F85, F89 · Mockups: https://claude.ai/artifact/8Fn4j4LB9zn4CgYfgX1uzg

> **Version 3 change note (2026-10-30):** version 2 is built but, seen on screen, the Plugins page is not what the spec describes. This version keeps every version 2 decision (D1 to D14) and adds: metadata providers become tiles under the Metadata tab; nothing sits under the grid; Configure and Manage open the item's own page; one status pill with the reason on it; coloured counts; the active tab reads like the top bar; and "Clear filters" never appears for a tab. This version lives beside version 2 (`plugins-and-integrations-screens.md`, Built, awaiting Local checks), whose decisions D1 to D14 and packets WP1 to WP6 stand unchanged; this file adds D15 to D28 and WP7 to WP13.

## 1. Goal

Settings › Platform › **Plugins** shows one thing only: a grid of tiles, one per plugin, where metadata providers (Apple, Comic Vine, TMDB, TheTVDB, MusicBrainz, LRCLIB, SubDL, Stash-box, YouTube, Wikidata, Local files) are tiles like any other plugin. Pressing Configure opens that plugin's own page, with the breadcrumb following, and everything about the plugin, including a provider's key, lives there. **Integrations** has the same shell, and Manage opens the integration's own page. The tab, pill, summary strip and card are defined once and used by Plugins, Integrations and Libraries, so a fix in one place fixes every page (Uniformity, Maintenance).

## 2. Scope

**In scope**
- Plugins page: providers as tiles; remove the Metadata sources box, the Available plugins box, the Plugin details box and the inline Configure panel from under the grid.
- Plugin's own page at `/settings/plugins/{id}` with Overview, Settings, Access and Activity tabs, for plugins and providers alike; provider settings (enabled, language strategy, key, Test connection, servers, advanced connection) move into its Settings tab; the separate Metadata Providers list and `…/providers/{name}` pages go away. The Ingestion Flow tab stays where it is (Library & Ingestion).
- Integrations: Manage opens `/settings/integrations/{id}` with Overview, Events or Access, Keys or Signing secret, Deliveries, Activity; the side drawer goes.
- Shared parts fixed centrally: active tab (white text, purple underline), coloured summary counts, one pill set including media-colour pills, the "Clear filters" rule, status pill moved into the pill row so names never truncate at 1440 and 1280.
- Stash-box wording (F78): tile line and page explanation rewritten, with a link to StashDB and what Library does with the key.
- Root-cause fixes listed in section 3.

**Out of scope**
- What plugins, providers or integrations can do; permissions; approval; the Priority Cascade and provider order (Ingestion Flow is unchanged).
- Community plugins, the plugin directory, signing, encrypted keys (parked). The "Available plugins" plumbing stays in the code, hidden.
- Phone and TV (Platform pages stay desktop and tablet only).
- First-run setup: it reuses the plugin tiles and pages built here, but its own overhaul is a separate spec (thread "Redesign first-run setup").
- The New integration wizard (built in v2, unchanged).

## 3. What is wrong today (found in the code, 2026-10-30)

1. **Providers are not plugins on the page.** The grid is fed by the plugin list only. Providers are rendered by a separate component, `MetadataSourcesPanel`, as a text list ("Supplies: …", "Sends searches to …") with a Compare tool, placed under the grid. So the Metadata tab holds nothing and the bottom of the page is a wall of text (F85, F84). The Developer tools spec D-J set the direction (providers fully under Plugins) but WP20 built only this list.
2. **Three more boxes under the grid:** "Available plugins" (community list; Install is switched off), "Plugin details" (a plugin picker that defaults to a plugin, hence "Commercial Skip at the bottom", with Health & Jobs, Capabilities and a Danger zone), and the Configure panel, which opens in place on the same page instead of its own page.
3. **"Clear filters" after clicking a tab.** `PluginSettingsTab.razor` defines `Narrowed` as search, Status, Media, Official **or the selected category tab**; `ManagedAccessApplications.razor` does the same with the kind tab. Clicking Metadata therefore shows Clear filters, and Clear filters also throws you back to All.
4. **Active tab colour.** The one shared tab (`.tl-tab.tl-tab-active` in `wwwroot/app.css`) uses accent-coloured text and underline. The top bar uses white text with the accent underline (`.layout-shell__nav-link--active`). Shy wants the tab to match the bar.
5. **Counts have no colour.** `AppSummaryStrip` renders every value in the same colour.
6. **Three pill looks** on cards: `AppStatusBadge`, `AppChip` (integrations) and a hand-styled `app-list-card__kind` span. Media types are not shown on tiles at all.
7. **Tile top row is overfull.** Status pill plus switch beside the name truncates names like "Commercial Skip" to "Comm…" in a three-column grid at 1440.
8. **Provider settings live elsewhere:** `/settings/metadata/providers/{name}` (section Providers) with "Provider Configuration" twice and a raw timestamp (F89); Configure on a source links there.
9. **The expanding facts inside a tile** ("View what it can access") make one tile taller than its row.

## 4. Decisions made

Version 2 decisions D1 to D14 stand. Shy locked the following on 2026-10-30, taking the recommended answer to each of the four questions (core sources shown as "Always on" tiles; Manage opens a page; Available plugins removed while parked; Compare moves to the provider's Overview).

- **D15 (Shy, 2026-10-30).** **Every metadata provider is a tile** in the Plugins grid, category Metadata, with its own logo in its brand colour, media pills, Official Tuvima, a status pill and (where it can be switched) the switch. Wikidata and Local files are core sources: they show "Always on" and no switch. Reason: one list, one truth (Uniformity).
- **D16 (Shy, 2026-10-30).** **Nothing under the grid.** The Metadata sources, Available plugins and Plugin details boxes and the inline Configure panel are removed from the Plugins page. "What it supplies" and Compare move to the provider's Overview tab; health, jobs, capabilities and Remove move to the plugin's page; Available plugins returns when community plugins are reopened.
- **D17 (Shy, 2026-10-30).** **Configure opens the plugin's own page** at `/settings/plugins/{id}`, breadcrumb Settings › Plugins › Name, tabs Overview · Settings · Access · Activity. A plugin without settings hides the Settings tab. The header carries logo, name, one line, pills, status pill, switch and "...". No forwarding from the old provider addresses (Shy's standing rule).
- **D18 (Shy, 2026-10-30).** **Provider settings live in the Settings tab** of the provider's page: a key card (plain explanation, Get a key link, Replace key, Test connection with a readable "checked N minutes ago"), Lookup (enabled, language strategy, provider-specific switches such as the Stash-box fingerprint), Servers where the provider supports them, and a collapsed Advanced connection. Saving uses Save changes with Review changes first; nothing applies before Save. The Metadata Providers list page and section go away; Ingestion Flow stays under Library & Ingestion and the provider's Overview links to it.
- **D19 (Shy, 2026-10-30).** **One status pill per tile, first in the pill row**, never beside the name. Words: Working (green), Off (grey), Always on (grey), Broken (red), or the amber reason itself: "Requires Comskip or FFmpeg", "Needs a key", "Waiting for Local AI". Never two amber pills on one tile; the full sentence is the pill's tooltip and the page's Overview says what to do. Only the switch sits beside the name.
- **D20 (Shy, 2026-10-30).** **Tabs are navigation, not a filter.** "Clear filters" shows only while search, a filter menu or the Official switch narrows the list; clearing keeps the tab you are on. An empty tab with no filter set says "Nothing in this category yet" and offers no button. Same on Integrations and any page using the shared row.
- **D21 (Shy, 2026-10-30).** **Active tab = white text + purple underline**, like the top bar; inactive grey, hover white; counts small and grey (lighter when active). Set once in `.tl-tab`, so Libraries, Settings pages, Logs and Browse follow.
- **D22 (Shy, 2026-10-30).** **Summary counts carry colour by meaning:** total in accent, switched on in green, needs attention in amber and grey when zero (with a tick icon). Set once in `AppSummaryStrip` through a tone on each item.
- **D23 (Shy, 2026-10-30).** **One pill set:** status tones (ok, warn, bad, mute), category and Official or Private, and media pills in the design-system media colours (books, audiobooks, comics, movies, TV, music, videos, photos). `AppListCard` and the Integrations card use it; the hand-styled kind span and `AppChip` on cards go.
- **D24 (Shy, 2026-10-30).** **Manage on an integration opens its own page** at `/settings/integrations/{id}` with the same shell (header, tabs); the side drawer is removed. Reason: every Platform page behaves the same way, and your rule that single-item editing has no drawers.
- **D25 (Shy, 2026-10-30).** **Stash-box wording (F78).** Tile: "Details, covers, performers and studios for videos in your Vault, from StashDB." Page: what Stash-box and StashDB are, a link to stashdb.org, "Library asks it only about videos in your Vault", "the key is kept on this machine and sent only to stashdb.org so it knows the request is yours; the video's title is sent to find a match; a fingerprint of the file only if you switch that on; without a key nothing is sent". A "Get a key at stashdb.org" button goes to stashdb.org/register.
- **D27 (Shy, 2026-10-30, asked for a list view).** **Cards or List.** The same Cards / List switch the audiobook library uses sits at the right of the filter row on Plugins and Integrations. List is one table row per item: logo, name with the Tuvima icon when Official, one line of description, status pill, category, works on (two media pills and "+N"), the switch, Configure and "...". Integrations list: status, kind, access, keys, last used, Manage. Search, filters and tabs apply to both views; the choice is remembered per person on this device. Row height and columns follow the design-system list rules (56 px rows, 16 px gap, text left, numbers right, "..." for rare actions).
- **D28 (Shy, 2026-10-30).** **Official Tuvima shows the real Tuvima icon**, not a generic shield: the gold mark already served at `wwwroot/images/tuvima-icon.svg` (source `assets/images/tuvima-icon.svg`), 14 px in the Official pill on tiles and the filter switch, 16 px beside the name in the list and in the page header. Private uploads keep a plain grey pill. The v2 "shield on Official" icon rule in the design system is replaced by this.
- **D26 (Shy, 2026-10-30).** **Grid columns:** three at 1200 px of content and above, two below, one on phone; every tile in a row the same height; paging only above 24 (v2 D10 as amended by One shared look D6).

## 5. UI behaviour

### 5a. Walkthrough

An administrator opens Settings › Platform › Plugins. Title once, one line under it, **Upload plugin** on the right. The summary strip: 17 installed (purple), 15 switched on (green), 2 need attention (amber). Search, Status, Media, Sort, the Official Tuvima switch. Tabs All 17 · Metadata 11 · Media processing 2 · Services 1 · Utilities 3; the active tab is white with a purple underline. The grid: Apple, Comic Vine, Commercial Skip, Description insights, Fandom Lore, Local files, LRCLIB, MusicBrainz, Series order, Skip markers, Stash-box, SubDL, Taste summary, TheTVDB, TMDB, Wikidata, YouTube. Each tile: logo, name, switch; two-line description; status pill, category pill, media pills, Official; Configure and "...". Under the grid only "Showing 17 of 17 plugins".

Clicking Metadata narrows to the 11 providers; no Clear filters. Typing "music" narrows to Apple, LRCLIB, MusicBrainz and shows Clear filters; pressing it clears the search and stays on Metadata.

Configure on Stash-box opens its page. Breadcrumb Settings › Plugins › Stash-box. Header with the SB logo, name, line, pills, Connected, switch, "...". Tabs Overview · Settings · Access · Activity. Settings shows the key card, Lookup, Servers and Advanced connection; a Save bar at the bottom is disabled until something changes and shows Review changes before saving. Overview explains the source, lists what it supplies, where it is used (Vault videos · Primary, link to Ingestion Flow) and what leaves this machine. Access shows Who can use it, Works on, Vault items, Limited to with Change, Needs and Needed by. Activity shows Check now and the dated list of what it did and every failure with its cause.

Configure on Commercial Skip opens the same shell. Overview leads with the amber "Requires Comskip or FFmpeg" panel (Open Tools & requirements, Check again), then What it does, the version note, Needs and Needed by, and Removing this plugin with its preview counts.

Integrations: the same shell. Tiles show a status pill (Enabled, Disabled, Delivery failed), kind and access pills, active keys and last used. Manage opens the integration's page: for a webhook, Overview leads with the failing-deliveries panel (what Discord answered, Change address, Send test), a deliveries table, Details, the Deliver events switch and Delete.

### 5b. States

| Situation | What the person sees |
|---|---|
| Normal | As above. |
| Empty tab, no filters | "Nothing in this category yet", no button. |
| No results with a filter or search | "No plugins match" and Clear filters; the current tab stays. |
| Loading | Skeleton strip and equal-height tile skeletons; status pills say "Checking…" until health returns. |
| Provider without a key | Amber "Needs a key" pill; Overview leads with the key card; the switch is off and disabled with the reason. |
| Provider that needs no key (Apple, MusicBrainz, LRCLIB, YouTube) | No key card; Settings shows Lookup and Advanced connection only. |
| Core source (Wikidata, Local files) | "Always on", no switch; Settings tab hidden if there is nothing to set. |
| Old provider address | Not found (no forwarding). |
| Not administrator / phone | Pages absent, as today. |

### 5c. Devices
Desktop and tablet. Three columns at 1440 and 1280 (content ≥ 1200 px), two at 1100 and tablet; the filter row wraps; detail pages go to one column under 980 px of content.

### 5d. Accessibility
Tabs, switches and menus keyboard-reachable with visible focus; status never colour-only (icon and word); the switch has the plugin's name as its label; the breadcrumb is a nav landmark.

## 6. Data / model changes

- **Plugin list includes providers.** The Engine's plugin summary gains a `kind` (plugin or provider) and, for providers, logo, accent colour, media types from `can_handle`, whether a key is required and present, and the connection state. Providers keep their own ids (for example `stashbox`); the Plugins page lists both in one response. `MediaEngine.Contracts` and the wire fixtures change accordingly (Never rule 3).
- **Provider settings wire.** Today's provider settings actions (enabled, language strategy, timeout, interval, concurrency, endpoints, key replace, test connection, servers) are reused unchanged by the plugin page; no new storage. Pre-release: no migration.
- **Routes.** `/settings/plugins/{id}` and `/settings/integrations/{id}` are new Settings detail routes; `SettingsNav` drops the Providers list route and keeps `/settings/metadata/ingestion-flow`.
- No stored data changes.

## 7. Work packets

Built in the cloud, validated locally in one batch. Waves: (WP7 ∥ WP8) → (WP9 ∥ WP10) → (WP11 ∥ WP12) → WP13.

### WP7 — Shared parts fixed once
- **What it does:** Cards / List switch wired to the shared list page (D27, reusing the existing view switch and `AppTable`); active tab white text and purple underline; `AppSummaryStrip` item tone (accent, ok, warn, mute); one pill set with media colours in `AppStatusBadge` (or a renamed `AppPill`) and removal of the kind span and `AppChip` from cards; `AppListCard` moves the status pill into the pill row; `AppSearchFilterRow` documents that tabs never count; design-system page and `DesignSystemPreview` updated; a guardrail test that no page styles its own pill.
- **Files likely touched:** `wwwroot/app.css`, `Shared/AppSummaryStrip.razor(.css)`, `Shared/AppListCard.razor(.css)`, `Shared/AppStatusBadge.razor`, `Shared/AppStateCard.razor`, `Pages/DesignSystemPreview.razor`, `engineering/design/design-system.md`, `tests/MediaEngine.Web.Tests/DeveloperPageSharedPartsTests.cs`
- **Depends on:** none · **Size:** Standard
- **Done when:** Plugins, Integrations and a library page show the white-and-purple active tab; six tiles of mixed content are the same height with no truncated name at 1280; summary values carry tones.

### WP8 — Tabs are not filters
- **What it does:** `Narrowed` and `ClearFilters` on Plugins and Integrations exclude the tab; empty-tab wording; tests.
- **Files likely touched:** `Settings/PluginSettingsTab.razor`, `Settings/ManagedAccessApplications.razor`, `tests/MediaEngine.Web.Tests/PluginsListPageTests.cs`, `ManagedAccessApplicationsTests.cs`
- **Depends on:** none · **Size:** Mechanical
- **Done when:** clicking any tab never shows Clear filters; Clear filters keeps the tab; the existing tests are updated and a new one covers both.

### WP9 — Providers as tiles
- **What it does:** The Engine lists providers in the plugin summary with `kind`, logo, colour, media types, key state and connection state; the Plugins page renders them as tiles under Metadata with "Always on" for core sources; the Metadata sources, Available plugins and Plugin details boxes are removed from the page; counts include providers; wire fixtures updated.
- **Files likely touched:** `Endpoints/PluginEndpoints.cs`, provider inventory services, `Contracts/Plugins/*`, `Settings/PluginSettingsTab.razor`, `Settings/MetadataSourcesPanel.razor` (deleted), `Settings/AvailablePluginsPanel.razor` (no longer rendered), contract fixtures, tests
- **Depends on:** WP7 · **Size:** Complex
- **Done when:** with the shipped providers and plugins the grid shows 17 tiles, Metadata 11, nothing under the grid; Wikidata and Local files have no switch; SubDL without a key shows "Needs a key".

### WP10 — Plugin's own page
- **What it does:** New route and page component with breadcrumb, header, Overview · Settings · Access · Activity; moves the v2 detail sections (Access, Needs, Needed by, Activity, Check now, version note, removal preview) into the tabs; Configure navigates to it; "..." items open the matching tab; inline Configure panel removed.
- **Files likely touched:** new `Settings/Plugins/PluginDetailPage.razor(.css)`, `Pages/Settings.razor`, `Models/ViewDTOs/SettingsNav.cs`, `Settings/PluginSettingsTab.razor`, tests
- **Depends on:** WP7 · **Size:** Complex
- **Done when:** every action reachable today is reachable on the page; reload and Back work; a plugin without settings hides the Settings tab.

### WP11 — Provider settings on the plugin page
- **What it does:** The provider Settings tab (key card with explanation and Get a key link, Replace key, Test connection with readable time, Lookup, Servers, Advanced connection, Save with Review changes) and Overview (About, What it supplies, Compare, Used in with Ingestion Flow link, What leaves this machine), built from today's `MetadataSettingsPage` controls and actions; the Providers list page and `…/providers/{name}` routes are removed; Stash-box wording (D25); readable "last checked".
- **Files likely touched:** `Settings/MetadataSettingsPage.razor` (split: Ingestion Flow stays, provider detail moves), `Settings/Plugins/*`, `config/providers/stashbox.json` (intro text), `Models/ViewDTOs/SettingsNav.cs`, `Services/Navigation/SettingsMenuBuilder.cs`, tests
- **Depends on:** WP9, WP10 · **Size:** Complex
- **Done when:** Stash-box, TMDB and Apple pages show the right cards; replacing a key and Test connection work from the page; the old provider address is not found; Ingestion Flow still opens.

### WP12 — Integration's own page
- **What it does:** Route and page for an integration (Overview, Events or Access, Keys or Signing secret, Deliveries, Activity) built from the drawer's forms and the webhooks component; Manage navigates to it; the drawer is removed.
- **Files likely touched:** new `Settings/Integrations/IntegrationDetailPage.razor(.css)`, `Settings/ManagedAccessApplications.razor`, `Settings/ManagedApplicationWebhooks.razor`, `Pages/Settings.razor`, `SettingsNav.cs`, tests
- **Depends on:** WP7 · **Size:** Complex
- **Done when:** every drawer action (edit, enable, revoke, delete, webhooks, Send test) works on the page; a failing webhook's page leads with the failure panel.

### WP13 — Docs, batch list, visual proof
- **What it does:** Using Plugins, Configuring providers and Integrations guides in tuvima-docs (the Library's `PublicDocsDriftTests` read `configuring-providers.md`, so update the test in the same pair of changes); engineering index; screenshots at 1440 and 1280 in light and dark published as one Artifact page; Local checks appended to `engineering/validation/batch.md`.
- **Depends on:** WP11, WP12 · **Size:** Mechanical

## 8. Local checks (go to `engineering/validation/batch.md`)
1. Plugins at 1440 and 1280 on the real library: 17 tiles, List view shows the same 17 as rows with nothing clipped and the choice remembered after reload, nothing under the grid, Metadata 11, names never truncated, every tile in a row the same height, active tab white with purple underline, counts coloured; clicking tabs never shows Clear filters; search "music" shows it and clearing keeps the tab.
2. Configure on Stash-box, TMDB, Apple and Commercial Skip: breadcrumb follows, tabs work, key replace and Test connection on Stash-box with your real key, readable "checked N minutes ago", Save shows Review changes; with Comskip and FFmpeg really missing the Commercial Skip Overview leads with the amber panel and the tile pill says "Requires Comskip or FFmpeg".
3. The old `…/metadata/providers/apple_api` address is not found; Ingestion Flow still opens from Library & Ingestion.
4. Integrations: Manage opens the page; a webhook pointed at `http://127.0.0.1:9/hook` shows Delivery failed on its tile and leads with the failure panel on its page; Send test, Revoke keys and Delete work from the page.
5. Libraries overview and a library page: the tabs look identical to Plugins (white text, purple underline).
6. Compare against the target image on Shy's PC (F73).

## 9. Risks and open questions

- **Developer tools D-J conditions.** D-J set conditions before providers move "fully" under Plugins (a secrets store, first-run by capability, a warning before removing a source, capability sheet, opt-in re-matching). This spec moves only how providers are *shown and configured*; keys stay where they are, built-in providers cannot be removed, order stays on the Ingestion Flow. So none of the conditions is triggered. Recorded here so the drift is explicit.
- **One Engine response for two kinds.** Providers and plugins are different objects in the Engine. The plugin summary gains a `kind` rather than merging the two models, so later work (per-library limits, dependencies) stays per kind.
- **Deleting the Providers list page** removes a place nothing else links to once Configure goes to the plugin page; the only risk is an old bookmark, and no forwarding is your rule.
- **Open questions:** none. Shy answered Q1 to Q4 with the recommended options on 2026-10-30 (recorded in D15, D16 and D24).

## 10. Gaps and drift (version 2 as built, 2026-10-30)

| Spec said | Built | This version |
|---|---|---|
| One card list with Metadata tab | Providers in a separate text box under the grid; Metadata tab empty | Providers as tiles (D15) |
| Cards, pager, nothing else | Four boxes under the grid | Removed (D16) |
| WP3 "Plugin detail (Configure)" page with back link and breadcrumb | Opens in place under the grid | Own page (D17) |
| Provider settings on the plugin page (provider-settings spec D1) | Separate `…/providers/{name}` pages with code names | Settings tab on the plugin page (D18) |
| "No results" shows Clear filters | Also shown after clicking a tab | Tabs never count (D20) |
| One pill set (One shared look D4) | Three pill looks, no media pills | One set with media colours (D23) |

## Plain English summary

The Plugins page becomes a clean grid of tiles and nothing else: the metadata providers (Apple, TMDB, Stash-box and the rest) become tiles under the Metadata tab, and the text boxes under the grid go away. Configure opens the plugin's own page with the breadcrumb following, and that page holds everything, including a provider's key and Test connection. One status pill says plainly what is wrong ("Needs a key", "Requires Comskip or FFmpeg"), the counts at the top are coloured, the active tab is white with a purple underline like the top bar, and "Clear filters" only appears when you actually filtered. Integrations get the same shell and Manage opens a page instead of a side drawer. The tab, pill and card are fixed once in the shared parts, so Libraries and the Settings pages follow. Built in the cloud, checked on your PC at 1440 and 1280 in one batch.
