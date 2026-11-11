# Spec: Plugins and Integrations screens (version 4: dumb plugins, calm tiles, one page per plugin)

> **Status:** Built, awaiting your Local checks · **Version:** 4 · **Last updated:** 2026-11-01 (locked by Shy, 17:51Z: "agree on all questions", every recommended option, 1A to 8A) · **Related docs:** Plugins and Integrations screens v3 (`engineering/specs/archive/plugins-and-integrations-screens-v3.md`, Built) · v2 (`engineering/specs/plugins-and-integrations-screens.md`, Built) · Plugin architecture (`engineering/specs/plugin-architecture.md`, Built; C22 dependency map) · One shared look (Built) · First-run setup v3 (Built; reuses the plugin tiles) · Design system · Mockups: https://claude.ai/artifact/5X8AkwbXFzX4vfXDhhYavh

> **Version 4 change note (2026-11-01):** version 3 is built and, seen on screen on 2026-11-01, Shy found the tiles and the plugin page cluttered, confusing and busy. This version keeps the v3 structure (tiles only, Configure opens the plugin's own page, Cards / List, tabs are navigation) and changes what a tile and a page *say*. The guiding principle is Shy's: **plugins are dumb.** The Plugins page is where you set a plugin up, get its access working and tested, and read what it provides to Library in one standard way. *Where* a plugin is used, and what it targets, is shown where it is used (Ingestion Flow, Lyrics, Subtitles), not on the plugin. This file adds decisions D29 to D42 and work packets WP14 to WP21; v3 D15 to D28 stand except where a decision below replaces one (named in each case).

## 1. Goal

Settings › Platform › **Plugins** shows a calm grid: one tile per plugin with its logo, its name, a coloured status dot, one standard "Provides" line, a two-line description and one button. Three tabs group plugins by *when Library uses them*: **Getting items in**, **After import**, **Accounts & lists**. A plugin's page is one short column: the access card (key or account) with Test, what it provides, its settings, what leaves this machine, and an Activity tab. Plugins that need Local AI say so and cannot be switched on until Local AI is ready. Official plugins and the integration the Tuvima apps use cannot be removed. A Movies or TV library never imports files while no source can identify them.

## 2. Fable review of what is on screen today (2026-11-01, from the code on `main`)

### 2a. The tile
A tile today carries, top to bottom: logo, name, switch; two lines of description; a pill row with the status pill ("Working"), a category pill ("Metadata"), up to two media pills and "+N", and an Official pill; then a footer with Configure and a "..." menu (Check health, What it can access, Activity, Remove). That is up to **six pills and two action controls** on one tile, with three of them (category, media, Official) repeating what the tab, the Ingestion Flow and the Tuvima mark already say.

| Problem | Why it matters | Fix in this version |
|---|---|---|
| "Working" pill on every healthy tile | The healthy state is the normal state; a pill for it is noise 15 times over. | A coloured dot (D29). |
| Category pill | The tab you clicked already says it; on All it is the only pill that varies, and it reads as a label, not information. | Removed; categories are tabs only (D31). |
| Media pills ("Movies", "TV", "+3") | Says what the plugin targets. Shy's rule: a plugin is dumb; targeting lives where it is used. | Removed from tiles and list (D30). The plugin page has one line "Used in … · Open Ingestion Flow". |
| Official pill | A pill for the common case. | Nothing on the tile (replaces v3 D28). "Official" lives in the About card and in the Show filter menu; private uploads show a small grey "Private" word. |
| "..." menu beside Configure | Everything in it is on the plugin's page one click away. Two action controls for one destination. | Removed from tiles and rows (D30). |
| Description clipped at two lines, no way to read it | Two of eleven source descriptions are longer than two lines ("Stash-box" is 3+). | Tooltip with the full text, and every description rewritten to one standard sentence (D35). |
| Provider logos missing on Plugins, present in first-run setup | Root cause: the Plugins page asks the Engine for an *uploaded* icon (`/settings/providers/{name}/icon`, served only when an administrator has uploaded one, and rendered as a Dashboard-relative address), while setup uses the catalogue's bundled icon path (`images/providers/tmdb.svg`). Nobody has uploaded icons, so Plugins shows initials. | Use the catalogue icon path everywhere, with the uploaded icon as an override (D36). Small, mechanical. |
| Amber reasons as pills ("Needs a key", "Waiting for Local AI") | Correct information in the wrong shape: it tells you what is wrong but the button still says Configure. | The footer button *is* the fix: "Add key", "Set up Local AI", "Install tools" (D29). |

### 2b. The plugin page
Header: logo, name, description, "Turned on by … on …", then **five pills** (category, media, Official, status), the switch and a "..." menu. Tabs: Overview · Settings · Access · Activity. A provider's Overview is **five cards** (About, What it supplies, Where it is used, What leaves this machine, Compare with another source); its Settings tab is three more (Your key, Lookup, Advanced connection) plus a Save bar. Access is a fourth tab of facts (Who can use it, Works on, Vault items, Limited to, Needs, Needed by).

| Problem | Fix |
|---|---|
| Four tabs for what is one short page of facts and one form. The person must guess whether a key lives under Overview, Settings or Access (today: the Overview *shows* the key card only when the key is missing; otherwise it is under Settings). | Two tabs: **Set up** (everything you can change or must do) and **Activity** (D33). |
| "What it supplies" is free text per source (field names like `original_release_date`), so no two pages read alike. | One standard vocabulary of what a plugin provides, with icons: Details, Artwork, People, Series order, Identification, Lyrics, Subtitles, Skip markers, Insights, Lists & ratings, Account sync (D34). Shown identically on the tile and the page. |
| "Compare with another source" | A read-only tool that lists field names two sources share. It answers an ordering question ("which source do I put first?") on a page that is not about ordering. Removed (3A). |
| "Where it is used" card | Targeting detail on the plugin. Shrinks to one line with a link to the Ingestion Flow (D30). |
| Access tab | "Who can use it" and "Limited to libraries" are settings; "Vault items" is privacy; "Needs / Needed by" is status. Each moves to the card it belongs to (D33). |
| Header pills | Replaced by the dot and one status sentence ("Working · checked 4 minutes ago", "Needs a key", "Waiting for Local AI"). |
| "..." in the header | Its items (Check health, Access, Activity, Remove) are on the page. Removed. Remove, where allowed, is a quiet link at the bottom of Set up (D37). |

### 2c. Key dialog on setup versus the plugin page
Both already open the same dialog (`ProviderOnboardingDialog`: title "Connect TMDB", fields, Test, Done). What differs is the frame around it: setup shows a tile with a "Connect" / "Review key" button and a "Needs a key" / "No key needed" pill; the plugin page shows a "Your key" card with an explanation paragraph and a filled "Add your key" button. Same dialog, two vocabularies. D34 makes one **Access card** that both places render, with the same words.

### 2d. Behaviour gaps Shy found
- **No source, files still imported.** Creating a Movies or TV library with no TMDB or TheTVDB key saves the folder and the next scan adds every file as an unidentified item. Nothing warns. D38 holds import until a source can identify that kind.
- **AI plugins look switchable while Local AI is off.** The Engine already knows ("Waiting for Local AI" from the dependency map, architecture C22) but the tile shows it as a pill and the switch is live. D32.
- **Anything can be deleted.** The integration detail page offers Delete on every application, including the one the Tuvima apps use (where that record is first created is a build-time check). Official plugins that are compiled in already hide Remove; the source files in `config/providers/*.json` can be deleted on disk and then vanish from the page. D37.

## 3. Scope

**In scope**
- Tile and list row content (D29, D30, D35, D36); categories (D31); the Needs Local AI rule (D32); the plugin page (D33); the shared Access card and the "Provides" vocabulary (D34); StashDB wording; the import gate (D38); the removal safety rules (D37); Integrations tiles and page follow the same dot and button rules (D39).
- First-run setup's Metadata step renders the same Access card, so the words match.

**Out of scope**
- What plugins can do, permissions, approval, the Priority Cascade and provider order (the Ingestion Flow is unchanged).
- Community plugins, directory, signing, encrypted keys (parked).
- Phone and TV (Platform pages stay desktop and tablet).
- Any new provider or plugin.

## 4. Decisions made

Shy locked this version on 2026-11-01 (17:51Z) with the recommended answer to every question: 1A three categories by use, 2A dot plus fix button, 3A remove Compare, 4A Set up and Activity tabs, 5A hold import until a source works, 6a-A the Tuvima apps integration is not deletable, 6b-A official plugins switch off only, 6c-A the Engine repairs shipped files quietly, 7A StashDB, 8A mark only Private. Shy's three remarks during review are recorded in D30, D32, D41 and D42.

- **D29 — Status dot, not status pill (replaces v3 D19).** Every tile, row and page header shows one 10 px dot at the top left of the logo: green (working), amber (needs something from you), red (broken), grey (off or always on). The dot has an icon-free tooltip with the sentence ("Working · checked 4 minutes ago", "Needs a key", "Waiting for Local AI", "Requires Comskip or FFmpeg", "Off", "Always on"). When the dot is amber or red the tile's one button becomes the fix: **Add key**, **Set up Local AI**, **Install tools**, **Retry**; when green or grey it is **Configure**. Status is never colour-only: the dot carries a small glyph (tick, exclamation, cross, dash) for colour-blind readers and the sentence is in the tooltip and on the page.
- **D30 — A tile says five things and no more.** Logo with dot; name and the switch (no Official mark, no AI mark beside the name: Shy, 2026-11-01, "the icons feel overwhelming"; Official appears only in the About card and as a tick in the Show filter menu, Private uploads show a small grey "Private" word); one **Provides** line (D34); description clamped to two lines with the full text as a tooltip; one button. No category pill, no media pills, no Official pill, no "..." (replaces the pill row in v3 D23 for Plugins and the "..." rule in the design system's list-page paragraph). "Works on" leaves the list columns too. Where a plugin is used is one line on its page with a link to the Ingestion Flow.
- **D31 — Three categories named for when Library uses the plugin (replaces the four code categories on the page).** Tabs: **All · Getting items in · After import · Accounts & lists.**
  - *Getting items in* (used while identifying files and filling in their first details): TMDB, TheTVDB, Comic Vine, MusicBrainz, Apple, Open Library, Wikidata, Local files, StashDB, YouTube, Filename and type helper (AI).
  - *After import* (adds to items already in the Library): LRCLIB, SubDL, Commercial Skip and the skip-marker detectors, Series order (AI), Description insights (AI), Vibe tags (AI), Smart labelling (AI), Fandom Lore, Taste summary (AI), MDBList (ratings and web lists).
  - *Accounts & lists* (connect a person's outside account): Trakt, ListenBrainz.
  Internally the category word on each plugin becomes one of `intake`, `enrichment`, `accounts`; the old words map once (Metadata → intake; Media processing and Utilities → enrichment; Services → accounts) and each plugin's file is corrected where the mapping is wrong (MDBList, YouTube). Pre-release: no migration.
- **D32 — Plugins that need Local AI say so and wait for it.** A plugin whose requirements include Local AI has no brand logo, so its logo box *is* the mark: a purple sparkle on a tinted box instead of initials, and its Provides line ends with "runs on Local AI". No extra icon beside the name (Shy, 2026-11-01). While Local AI is not ready (off, no model, or the needed ability missing) the dot is amber with "Waiting for Local AI", the switch is locked, and the button is **Set up Local AI** (opens Settings › Platform › Local AI). Once Local AI is ready the plugin stays off until someone switches it on; nothing turns on by itself. Uses the dependency map (architecture C22), no new rule in the Engine.
- **D41 — One filter menu with ticks replaces the Official Tuvima slider (Shy, 2026-11-01).** The filter row is Search · **Status** (tick boxes: On, Off, Needs a key, Missing tools, Waiting for Local AI, Broken; Shy, 2026-11-01: "we can also add filters for Missing tools") · **Show** (tick boxes: Official, Private, AI enabled) · Sort · Cards / List. Every menu allows one or several ticks at once; the menu label reads the ticked words ("Status: Needs a key, Missing tools"). The word is "Official", never "Official Tuvima" (Tuvima is the brand, Library the product). "Clear filters" counts a Show tick as a filter (v3 D20 unchanged). The same menu shape is used on Integrations where it has ticks.
- **D42 — Only the exception is marked (Shy, 2026-11-01, 8A).** Official plugins carry no mark, tint or edge on cards or rows. A Private upload's card has a grey dashed border and its list row a grey "Private" word, so the one thing not from Tuvima is the thing that looks different. AI-enabled plugins are told apart by their purple sparkle logo box (D32) and nothing else.
- **D33 — The plugin page is one short column with two tabs.** Header: logo with dot, name, one status sentence, the switch. Tabs **Set up** (default) and **Activity**. Set up is at most five cards, in this order, each absent when it has nothing to say:
  1. **Access** — the shared card (D34): "No key needed" with a tick; or the key state, **Connect** / **Replace key**, **Test connection**, "Get a key at tmdb.org"; for account plugins (Trakt, ListenBrainz) the per-profile link rows.
  2. **What it provides** — the standard icon list, then one line "Used in: Movies and TV, 1st of 3 sources · Open Ingestion Flow" (that line is the only place targeting appears).
  3. **Settings** — the plugin's own fields; Who can use it; Libraries (All · Change); Advanced collapsed. Save changes with Review changes first (unchanged from v3 D18).
  4. **Privacy** — "What leaves this machine" in one or two sentences, "Used for Vault items: follows the Vault's online-lookup setting" where relevant, links to the service's terms and privacy pages.
  5. **About** — version and "what changed" line, Publisher (Tuvima (Official) or Private), "Turned on by Shy on 12 October", Needs and Needed by as two short lines, and (private uploads only) the quiet **Remove this plugin** link with its preview counts.
  Activity: **Check now**, then the dated list of what it did and every failure with its cause (unchanged). Compare is removed (3A).
- **D34 — One Access card and one Provides vocabulary, shared.** The Access card is one component rendered by the plugin page and the first-run Metadata step, with the same words: **Connect** (no key yet, filled button), **Replace key** and **Test connection** (key saved), "No key needed" (tick, no button). The dialog is the one that exists ("Connect TMDB"). The Provides line is built from a fixed list the Engine returns per plugin: Identification, Details, Artwork, People, Series order, Lyrics, Subtitles, Skip markers, Insights, Lists & ratings, Account sync. A plugin's file names which ones it provides; the Dashboard never composes the sentence from field names.
- **D35 — Descriptions are one sentence, same shape, and StashDB is StashDB.** Every plugin's description is rewritten as one sentence of at most 90 characters starting with what it does ("Finds films and shows and fills in their details and posters." for TMDB). The tile and the row show that sentence; the tooltip shows the longer paragraph (which moves to the page's About card). The source `stashbox` is displayed as **StashDB** everywhere (tile, page, Ingestion Flow, Vault settings, docs); "Stash-box" appears only in the About card as "StashDB uses the Stash-box format". v3 D25's privacy wording stands.
- **D36 — Logos come from the catalogue path.** Tiles, rows and pages use the bundled brand icon the catalogue already serves to first-run setup; an uploaded icon (the Engine action) overrides it when present. Fixes the missing logos.
- **D37 — What cannot be removed.** Official (compiled-in) plugins have no Remove anywhere; they can only be switched off. Shipped source files (`config/providers/*.json`) are repaired quietly when the Engine starts if one is missing or unreadable, keeping the person's key and settings (they live in `config/secrets` and the data store, not in the shipped file); the Engine logs one line. The integration the Tuvima apps use cannot be deleted: its page offers **Disable** and **Revoke keys** only, with a one-line note saying why. Private uploads keep Remove. (Confirmed by Shy, 6a to 6c, 2026-11-01; the quiet repair replaces the "Restore button" idea raised in the local check thread.)
- **D38 — No source, no import.** A library kind whose files can only be identified by an outside source (Movies and TV: TMDB or TheTVDB; Comics: Comic Vine) is **held** while no such source is switched on and working: the Create library dialog shows an amber line "Nothing can identify films yet. Connect TMDB or TheTVDB first." with a **Connect a source** button; the library can still be created, but its folders show "Waiting for a source" (amber) on the Libraries page and no file is added until a source works, then import starts by itself. Music (MusicBrainz, no key), Books (Open Library and Local files), Audiobooks, Photos and Videos (Local files) are never held. Mixed folders are held only for the held kinds.
- **D39 — Integrations follow the same rules.** Integration tiles get the dot (Enabled green, Disabled grey, Delivery failed red), name, one line, keys count and last used, one button (**Manage**, or **Fix delivery** when red); no kind or access pills, no "...". The integration page keeps its tabs (they are real forms) but drops header pills for the dot and sentence.
- **D40 — List view columns (replaces v3 D27's column list for Plugins).** Dot and logo · Name · Provides · Status sentence · Switch · one button. 56 px rows, nothing wraps at 1280.

## 5. UI behaviour

### 5a. Walkthrough
An administrator opens Settings › Platform › Plugins. Title, one line, **Upload plugin**. Summary strip: 21 installed (purple) · 17 on (green) · 3 need attention (amber). Search, Status (ticks), Show (Official · Private · AI enabled), Sort, Cards / List. Tabs **All 21 · Getting items in 11 · After import 8 · Accounts & lists 2**.

The grid: the TMDB tile has a green dot on its logo, "TMDB", the switch on; "Provides: Identification · Details · Artwork · People"; "Finds films and shows and fills in their details and posters."; **Configure**. The SubDL tile has an amber dot; "Provides: Subtitles"; its button says **Add key**. The Description insights tile has the purple sparkle box as its logo, an amber dot ("Waiting for Local AI"), a locked switch and the button **Set up Local AI**. Commercial Skip: amber dot, **Install tools**. Wikidata: grey dot ("Always on"), no switch, **Configure**.

Hovering the SubDL description shows the full paragraph. Clicking **Add key** opens the SubDL page on Set up with the Access card first: "No key yet. SubDL needs a free key." **Connect** · "Get a key at subdl.com". Connect opens the same dialog first-run setup opens; after Test succeeds the card reads "Key saved · tested just now", the dot turns green and the switch unlocks.

Configure on TMDB opens its page: header (logo with green dot, TMDB, "Working · checked 4 minutes ago", switch), tabs Set up · Activity. Cards: Access (Key saved · Replace key · Test connection), What it provides (four icons; "Used in: Movies and TV, 1st of 3 sources · Open Ingestion Flow"), Settings (Language: Follow household · Who can use it: Administrators · Libraries: All · Advanced), Privacy (one paragraph, two links), About (version, Official, turned on by, Needs: nothing, Needed by: Fandom Lore).

In Settings › Libraries, Shy creates a Movies library before any key exists. The dialog's last step shows the amber line and **Connect a source**; Create still works. On the Libraries page the folder reads "Waiting for a source" with the same button; nothing is imported. After a TMDB key is tested, the scan starts and the row turns green.

### 5b. States
| Situation | What the person sees |
|---|---|
| Healthy plugin | Green dot, Configure. |
| Needs a key | Amber dot, switch locked, **Add key**; page opens on the Access card. |
| Needs Local AI, AI not ready | Sparkle mark, amber dot "Waiting for Local AI", switch locked, **Set up Local AI**. |
| Needs a tool (Comskip, FFmpeg) | Amber dot with the tool named in the tooltip, **Install tools** (opens Tools & requirements). |
| Broken | Red dot with the cause, **Retry**; the page's status sentence gives the cause and Activity has the failure. |
| Off / Always on | Grey dot, Configure; Always on has no switch. |
| Loading | Skeleton tiles; dots grey with "Checking…" tooltips. |
| Empty tab, no filters | "Nothing in this category yet." |
| No results with a filter | "No plugins match" and Clear filters; tab kept (v3 D20). |
| Create Movies library, no source | Amber line and **Connect a source**; create still allowed. |
| Library folder waiting | "Waiting for a source" on the Libraries page; nothing imported; starts by itself when a source works. |
| Delete on the Tuvima apps integration | No Delete; Disable and Revoke keys, with one line explaining. |

### 5c. Devices
Desktop and tablet (unchanged from v3 D26): three columns at 1200 px of content and above, two below; plugin page one column under 980 px. Phones read tiles, cannot switch or configure (unchanged).

### 5d. Accessibility
The dot is never the only signal: glyph inside, sentence in the tooltip and in the page header, `aria-label` on the dot ("Status: needs a key"). Every button has its plugin's name in its accessible label ("Add key for SubDL"). Tooltips open on focus as well as hover.

## 6. Data / model changes
- Plugin summary (Engine → Dashboard): `category` takes the three new words; adds `provides` (list from the fixed vocabulary), `requiresLocalAi` (true when the requirement map lists a Local AI ability), `shortDescription`, `longDescription`, `iconPath` (catalogue path, overridden by an uploaded icon), `removable` (false for official and compiled-in). Contracts tests and wire fixtures change (Never rule 3). No stored data changes; pre-release, no migration.
- Provider and plugin files: each gets `provides`, a one-sentence description and the long paragraph; `stashbox.json` display name becomes StashDB.
- Managed applications: a `protected` flag on the record the Tuvima apps use (where it is created is a build-time check; if no single record exists, the flag goes on the application type the apps pair with).
- Library import: a folder state **Waiting for a source** and the rule in D38, evaluated from the same source health the Plugins page shows.

## 7. Work packets
Built in the cloud, validated locally in one batch. Waves: (WP14 ∥ WP15) → (WP16 ∥ WP17) → (WP18 ∥ WP19) → WP20 → WP21.

### WP14 — Status dot and one-button tile (D29, D30, D36)
- Shared `AppStatusDot` (tone, glyph, tooltip, aria); the Status and Show tick-box filter menus replacing the Status select and the Official switch (D41; `AppSearchFilterRow` gains a multi-tick menu); the Private dashed border (D42); `AppStateCard` and the Plugins list row lose the pill row and "..." and gain the dot, Provides line, description tooltip and single fix-or-Configure button; logos from the catalogue path with uploaded override; design-system page updated; guardrail that no Plugins tile renders a pill.
- Files likely touched: `Components/Shared/AppStateCard.razor(.css)`, new `Components/Shared/AppStatusDot.razor(.css)`, `Components/Settings/PluginSettingsTab.razor`, `Services/Integration/EngineApiClient.Settings.cs` (icon path), `engineering/design/design-system.md`, tests in `MediaEngine.Web.Tests`.
- Depends on: none · Size: Standard
- Done when: at 1280 a row of three tiles shows no pill, every logo appears, SubDL without a key shows an amber dot and **Add key**, hover on a long description shows the full text.

### WP15 — Provides vocabulary, descriptions, categories, StashDB (D31, D34, D35)
- Engine returns `provides`, short and long descriptions and the new category words; every shipped plugin and provider file filled in; `stashbox` displayed as StashDB; Contracts and fixtures.
- Files likely touched: `config/providers/*.json`, `Services/Plugins/AiFeatures/AiFeaturePlugins.cs`, `Services/Plugins/Trakt/TraktPlugin.cs`, `Services/Plugins/Sources/ProviderListings.cs`, `MediaEngine.Contracts/Plugins/*`, contract fixtures, `PluginCategories.cs`.
- Depends on: none · Size: Standard
- Done when: the three tabs count 11 / 8 / 2 with the shipped set; no description over 90 characters; "Stash-box" appears nowhere a person reads except the About card.

### WP16 — Needs Local AI (D32)
- `requiresLocalAi` from the dependency map; sparkle mark; switch lock with reason; **Set up Local AI** button; page status sentence.
- Depends on: WP14 · Size: Standard
- Done when: with Local AI off, Description insights, Series order, Vibe tags, Smart labelling, Filename helper and Taste summary show the sparkle box, the amber dot and a locked switch; with Local AI ready they stay off until switched on.

### WP17 — Plugin page as one column (D33, D34)
- New page layout: header with dot and sentence, Set up and Activity tabs, the five cards; the shared `ProviderAccessCard` used here and in `Setup/SetupMetadataStage.razor`; Access tab content moved into Settings, Privacy and About; Compare removed; header "..." removed.
- Files likely touched: `Components/Settings/PluginSettingsTab.razor` (detail part split into `Components/Settings/Plugins/PluginPage.razor`), `Plugins/ProviderDetail.razor`, `Plugins/ProviderCompareCard.razor` (deleted or moved), new `Components/Shared/Providers/ProviderAccessCard.razor`, `Setup/SetupMetadataStage.razor`, tests.
- Depends on: WP14, WP15 · Size: Complex
- Done when: every action reachable today is reachable on Set up or Activity; the Access card on setup and on the page use the same words; a plugin with nothing to set shows no Settings card.

### WP18 — Removal safety (D37)
- No Remove for official plugins anywhere; shipped provider files repaired at Engine start (one log line, keys and settings untouched); `protected` on the Tuvima apps integration and the Disable / Revoke-only page.
- Files likely touched: `Services/Plugins/Sources/*` (file repair), `Endpoints/PluginEndpoints.cs`, managed application storage and `Components/Settings/ManagedAccessApplications.razor`, tests.
- Depends on: none · Size: Standard
- Done when: deleting `config/providers/tmdb.json` and restarting brings it back with the saved key still working; the Tuvima apps integration page has no Delete; a private upload still has Remove.

### WP19 — Integrations follow (D39)
- Dot and single button on integration tiles and rows; header pills replaced on the integration page.
- Depends on: WP14 · Size: Mechanical
- Done when: Integrations tiles show no pill; a failing webhook shows a red dot and **Fix delivery**.

### WP20 — No source, no import (D38)
- Folder state Waiting for a source; the hold rule per library kind from live source health; Create library amber line and **Connect a source**; Libraries page row state; import starts by itself when a source works.
- Files likely touched: library folder scanning services and folder state in the Engine, `Components/Settings/Libraries/CreateLibraryDialog.razor`, `LibraryFolderTable.razor`, Contracts and fixtures, tests.
- Depends on: WP15 (source health shape) · Size: Complex
- Done when: a Movies folder added with no TMDB or TheTVDB key imports nothing and shows the state; adding and testing a TMDB key starts the scan without a restart; a Music folder is never held.

### WP21 — Docs, batch list, visual proof
- tuvima-docs: Using Plugins, Configuring providers (the Library's `PublicDocsDriftTests` read it, so update the test in the same pair), Libraries guide (the hold); engineering index; screenshots at 1440 and 1280 as one Artifact page; Local checks appended to `engineering/validation/batch.md`.
- Depends on: WP17, WP18, WP19, WP20 · Size: Mechanical

## 8. Local checks (go to `engineering/validation/batch.md`)
1. Plugins at 1440 and 1280 on the real library: every logo shows; no pills on tiles or rows; dots and tooltips read right for TMDB (green), SubDL without a key (amber, Add key), Description insights with Local AI off (sparkle, amber, locked switch), Commercial Skip with tools missing (Install tools), Wikidata (grey, no switch); the three tabs count correctly; hover shows full descriptions.
2. TMDB page: two tabs; the Access card words match the first-run Metadata step's card word for word; Replace key and Test connection with the real key; Save shows Review changes; Compare is gone.
3. StashDB: name everywhere, About card mentions the Stash-box format, Vault lookup with the real key.
4. Delete `config/providers/tmdb.json`, restart: it is back, the key still works, one log line.
5. The Tuvima apps integration has no Delete; Disable and Revoke keys work.
6. Create a Movies library with no key: amber line, Connect a source, nothing imported, "Waiting for a source"; add and test a TMDB key: the scan starts by itself.

## 9. Risks and open questions
- **Holding import (D38) changes a behaviour people may rely on**: today a folder with no source still shows its files (unidentified). The hold is the "prevent, don't patch" choice; the cost is that a person who deliberately wants an unmatched Movies folder must connect a source first. Shy chose the hold (5A).
- **Three categories are a judgement.** Some plugins straddle (MDBList supplies ratings during import *and* web lists for Home). The rule "where does it do most of its work" places each; Shy approved the names (1A).
- **Quiet file repair (D37)** differs from the "Restore button" idea in the local check thread. A button asks the person to notice and fix damage to files they never edit; the repair is the Engine's job. Shy agreed (6c-A).
- **Compare removal** loses a small tool; nothing else uses its Engine action.
- Open questions: none. Shy answered Q1 to Q8 with the recommended options on 2026-11-01.

## 10. Gaps and drift (version 3 as built, 2026-11-01)
| Spec said | Built / seen | This version |
|---|---|---|
| One status pill per tile (D19) | Correct, but it is one of up to six pills | Dot and one button (D29, D30) |
| Providers' own logos (D15) | Initials: the page asks for uploaded icons, setup for bundled ones | Catalogue path everywhere (D36) |
| Overview · Settings · Access · Activity (D17) | Built; five Overview cards and a fourth tab of facts | Set up · Activity, five cards (D33) |
| Compare on the provider's Overview (D16) | Built | Removed (3A) |
| Stash-box wording (D25) | Built | Name becomes StashDB; wording kept (D35) |
| Cards / List with status, category, works on (D27) | Built | Columns without category and works on (D40) |

## Plain English summary
The Plugins page stops shouting. Each tile shows a logo with a small coloured dot, the name, the switch, one line saying what the plugin provides, a short description you can hover to read in full, and one button: Configure when all is well, or the fix when it is not ("Add key", "Set up Local AI", "Install tools"). The pills, the "..." menu and the "what it targets" chips go; where a plugin is used stays on the Ingestion Flow. The tabs become three plain groups: Getting items in, After import, Accounts & lists. A plugin's page becomes one short column with a Set up tab (key or account with Test, what it provides, settings, privacy, about) and an Activity tab, and the key card uses the same words as first-run setup. Plugins that need Local AI say so and cannot be switched on until it works. Official plugins and the integration the Tuvima apps use cannot be removed, and the Engine quietly repairs its own shipped files. A Movies or TV library no longer imports files while nothing can identify them. Built in the cloud, checked on your PC in one batch.
