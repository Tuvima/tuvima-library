# Spec: Developer tools as one family (Local AI, Plugins, Integrations, Developer Tools)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-10-19 · **Related docs:** [Product overview](../product/overview.md) · [Appendix A: plugin platform review](#appendix-a-plugin-platform-review) · [Settings revamp](settings-revamp.md) (D35, D43 to D48) · [First-run prerequisites](archive/first-run-prerequisites.md) · [Ingestion and readiness](ingestion-and-readiness.md)

## 1. Goal

The four pages in Settings > Developer all extend or connect the Library, but today each has its own on/off switch, its own idea of "healthy", its own approval story and its own log. An administrator has to learn four different ways of answering "is this on, is it working, what is it allowed to do, and when did it last run?". This spec gives them one shared set of answers, and settles whether outside software should connect through plugins.

## 2. Scope

**In scope**
- Making every shipped metadata provider fully JSON-defined: TVDB (coded today) becomes a JSON provider, and the TMDB and ComicVine code hooks that are keyed to their names are replaced by general JSON features (WP9).
- One shared "state card" every item in the four areas uses: name, kind, on/off, health, what it is allowed to do (plain words), last activity, last problem.
- One approval rule: nothing new switches on until an administrator has seen, in plain words, what it can do and what it sends out of the machine.
- The answer to Shy's question on outside software and plugins (D-A).
- Making recurring work visible in one place and recorded durably.
- Fixing the gaps found in the code review (section 8).

**Out of scope**
- Redesigning the Plugins page content beyond the shared state card (settings revamp D44 stays: one page).
- Building the new plugin platform itself (tiers, host services, contributions, signing, sandbox): this spec records the direction (D-G); the build is a follow-up spec. Live TV is its own spec (D-I).
- OpenID Connect / SSO (belongs to Users and Sign-in).
- The phone and TV apps (they are not Integrations).

## 3. Decisions made

All decisions below were agreed by Shy on 2026-10-19. Already locked in the Settings revamp and not reopened: Developer group for server administrators only; icons; Local AI keeps AI settings only (D43); Plugins folded into one page (D44); Integrations = credentials and webhooks (D46, D48); only Local AI shows red today (D3).

- **D-A (agreed).** Outside software stays in **Integrations**, not plugins. Plugins are code running inside the server with the server's own trust; outside software is somebody else's code running elsewhere and proving itself with a key. Mixing them would put unlike levels of trust under one label. Reason: Privacy and Reliability. The two share the state card and approval rule (D-B) so they feel like one family. A plugin may later *offer* an integration (for example a plugin that pushes to a notification service) without changing this.
- **D-B (agreed).** One **state card** everywhere: Name · Kind (AI feature, Plugin, Integration, Tester) · Switch (on/off) · Health (Working, Off, Needs attention, Broken) · Allowed to (plain list) · Sends data out? (yes/no, where) · Last activity · Last problem. Reason: Uniformity and Maintenance; one component, one wording.
- **D-C (agreed).** One **approval rule**. A new item is off. Turning it on shows a confirmation listing what it can do and whether anything leaves the machine, and the choice is logged with who and when. Applies to: each Local AI feature (names the model download), each plugin (its declared permissions, tool needs and AI permissions; a note that plugins run unsandboxed inside the server), each integration credential (its scopes) and each webhook (its address; private-network addresses already need approval). The Approved Catalog becomes the live Plugin directory (D-F), a list to browse, not a gate. Reason: Privacy; prevent, don't patch.
- **D-D (agreed).** **Health is the same four words** across the family, and "Broken" (red in the menu) means *an item that is switched on cannot do its job*. Today only Local AI can be red; under this rule a broken switched-on plugin or a webhook failing repeatedly also shades its menu entry red, and nothing switched off is ever red. Amber stays reserved for Needs Review. Reason: Uniformity.
- **D-F (agreed).** **A live plugin directory kept on GitHub.** One public list (a single file in a Tuvima GitHub repository) names every known plugin: name, author, what it does, version, permissions it declares, link, and a verified mark where Tuvima has checked it. Anyone submits a plugin by proposing a change to that list; Tuvima reviews and accepts it, so GitHub itself is the submission and review trail. The Library already reads such a list from a GitHub-only address (setting in core.json); this spec makes it a first-class page: Plugins > Available plugins, with "last checked", a Check now button, and a quiet "new plugins available" note (not an attention bubble). Fetching sends nothing about the user or their library, only a plain download; it can be switched off, and the page then works from the last copy saved on the machine. Reason: Extensibility and Privacy. Installing still goes through the approval step (D-C); being listed is not approval. GitHub is a good fit now (free, reviewable, public history); the address is a setting, so it can move later. **Official plugins live in the main tuvima-library repository and ship with the app** (Shy, 2026-10-19). Community plugins move to their own public repository later, suggested name `Tuvima/tuvima-library-plugins`, when the first community submission is ready; until then the directory address points at the main repository. Because the address is a setting, moving it is not a Library change.
- **D-G (agreed).** **Three kinds of plugin, safest first** (Shy asked for non-.NET and JSON-only plugins, cross-platform and safe). (1) Declarative plugins written only in JSON (metadata sources, M3U/XMLTV channel and guide sources, notification routes); (2) service plugins, separate programs in any language that talk to the Library over a published contract, never inside the server; (3) sandboxed WebAssembly plugins later. Community .NET assemblies loaded into the server stop being the way to extend the Library; only Tuvima-signed ones may run in-process. Plugins add pages and menu items only by choosing from the Library's own page types, so the look stays uniform. Reason: Privacy, Reliability, Extensibility. Full analysis: Appendix A.
- **D-H (agreed).** **Verification has its own process** based on common practice (Grafana, VS Code, Obsidian, Home Assistant): Official, Verified, Community (not verified) and Private levels; proven publisher identity, automated checks, signed releases with a parts list, human review of permissions for Verified, and a blocklist that can switch a harmful plugin off everywhere. Shown on the state card and the approval step.
- **D-I (agreed; clarified by Shy 2026-10-19).** **Live TV does not have to be built, it only has to remain possible.** The platform is designed so Live TV can be added later, either as a Library feature on open formats (M3U, XMLTV, HDHomeRun) with its own spec, or with tuner/guide plugins on top of it, and nothing in this spec closes that door. No Live TV work is scheduled. The plugin platform lets others add tuner backends and guide sources. Reason: no media server makes Live TV a plugin, and it needs seams (a channel library type, live stream path) that belong to the Library.
- **D-J (agreed, Shy 2026-10-19: option b).** **Metadata providers join the same model, and the direction is to move them fully under the Plugins page.** Conditions before the move is built (follow-up platform spec): a secrets store plugins can use, first-run setup asked by capability, a warning before removing a source, a capability sheet and Library-set weights, and opt-in re-matching (Appendix A). Until then they stay where they are and share the model: A provider that is only a JSON file (the "config-driven" providers in config/providers) is already a declarative plugin, so it gets the same manifest, state card, permissions (which web addresses it may call, which key it needs) and trust level (Official when it ships with the Library). Providers written in code stay built into the Library as Official first-party plugins. Not changed: the Priority Cascade, the order providers are asked in, and the Wikidata bridge stay core Library behaviour; a provider plugin only supplies data into them, and a plugin can never replace the cascade. The Settings > Metadata Providers page stays where it is (ordering and keys); it simply lists the same items the Plugins page lists, so there is one source of truth. The three plugins that ship today (commercial skip, Fandom lore, media segments) also become Official built-ins. Reason: Uniformity and Maintenance. No provider is rewritten as part of this spec.
- **D-K (agreed).** **Existing plugins are not rewritten, and API keys survive a wiped data store.** The existing plugin contract stays supported for Official plugins, so they keep working without edits. The three plugins that ship today (commercial skip, Fandom lore, media segments) are Tuvima-built, so they may keep running inside the server as Official plugins; they only need a manifest touch-up (trust level, supported Library versions, capability list). Provider API keys today live in files under the config folder (`config/secrets/`), not in the data store, so wiping the database leaves them in place. That stays true: the plugin secrets store must keep keys in the config folder (or an equivalent that survives a database reset), never only in the database. Backups can include or exclude secrets as they do now. Reason: Reliability and Maintenance.
- **D-L (agreed, Shy 2026-10-19).** **TVDB and the name-keyed provider hooks become JSON-defined as part of this work**, so every shipped metadata provider is a declarative plugin and a provider can be swapped by file alone. Risk: TVDB signs in to get a token before each session, so the JSON format must learn that flow; results must not change (WP9).
- **D-M (agreed, Shy 2026-10-19).** **Every kind of plugin can use the Library's Local AI services.** Today only in-server plugins can (they ask for an AI role such as text or structured output, declared in their manifest). Under the three kinds (D-G), JSON-only plugins can name an AI step in their description (for example "summarise this text"), and separate-program plugins call the same service through a plugin-scoped key. Rules: the plugin must declare which AI roles it uses and the administrator sees this at approval (D-C); the AI always runs on the user's machine and a plugin never gets the model files or a way to send data out through it; if Local AI is off or its tools are missing the plugin is told "AI unavailable" and shows Needs attention instead of failing silently; plugin AI requests wait behind the Library's own work (ingestion) and have time and size limits so a plugin cannot starve the machine. The same idea applies later to other local services (OCR, FFmpeg tools). Reason: Privacy and Performance.
- **D-E (agreed).** **Recurring work is recorded durably and listed once.** Local AI automation, plugin jobs, webhook retries and the segment-detection schedule each write a run record (started, finished, result). System > Tools and requirements > Scheduled tasks lists them all with last and next run (settings revamp D22, D42); each setting stays on its own page.

## 4. UI behaviour

Administrator only; none of this appears on phones or for non-administrators.

| Situation | What the person sees |
|---|---|
| Normal | Each item shows its state card. The Developer menu entry shows no shading when everything switched on is working. |
| Empty | Plugins: "No plugins installed" with a link to the guide. Integrations: "No outside software connected." Nothing is created for the person. |
| Loading | Cards show a quiet placeholder; health fills in as each check returns. |
| Error | A failed health check shows "Needs attention" with the last problem in plain words and one button (Retry, Open settings, Download). |
| Switching on | A short confirmation: what it can do, whether data leaves the machine, then Turn on. Cancel leaves it off. |
| Partly ready | Local AI with tools missing keeps the permanent notice (revamp D9). A plugin that needs a tool not yet installed shows "Needs attention: needs FFmpeg" with a link to Tools and requirements. |
| Developer Tools | The Enrichment Tester appears only when the running Engine supports it (see section 8); otherwise it is absent, not greyed out. It is a temporary quick-testing tool and is removed at beta. |
| Webhook problems | Each webhook shows its recent deliveries (time, result, attempt) and a "Send test" button. |

## 5. Data / model changes

- A small shared "run record" and "approval record" in the data store (who switched what on, when, what they were shown). Pre-release data may be reset.
- Plugin job state moves from memory to the data store so it survives a restart.
- No change to the credential or webhook secret storage.

## 6. Work packets

### WP0 — Baseline ingestion of every media type (before any provider change)
- **What it does:** Runs a full ingestion of a sample library covering every media type (Books as EPUB and PDF, audiobooks, TV, Movies, Music, Comics, Personal/Custom, Photos) on the current build, and saves the results as the "before" record: identified titles and IDs, metadata values per field, artwork chosen, Universe and Series grouping, Needs Review items and counts, and timings. Must include items that exercise every provider (Apple, TMDB, TVDB, ComicVine, MusicBrainz, SubDL, LRCLIB, Wikidata).
- **Files likely touched:** existing real-media harness and test fixtures; a stored baseline file under `.tmp/` (not committed) (to be confirmed by Claude Code)
- **Depends on:** none
- **Size:** Standard
- **Done when:** a baseline file exists listing every sample item per media type with its identified values, and a second run of the same unchanged build produces the same result (proving the baseline is stable).

### WP1 — Shared state card and health words
- **What it does:** One component and one health vocabulary (D-B, D-D), used first by Local AI and Plugins.
- **Files likely touched:** new shared `App*` component, `LocalAiSettingsTab.razor`, `PluginSettingsTab.razor` (to be confirmed by Claude Code)
- **Depends on:** Settings revamp WP2 to WP4 (frame and attention state)
- **Size:** Standard
- **Done when:** Local AI features and installed plugins render the same card; a test shows the four health words map from each area's existing status.

### WP2 — Approval step and record
- **What it does:** Confirmation on first switch-on for AI features, plugins, credentials and webhooks (D-C); writes an approval record.
- **Files likely touched:** Engine plugin, AI and application endpoints, new storage table (to be confirmed by Claude Code)
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** switching on a plugin shows its declared permissions and nothing turns on until confirmed; the record shows who and when.

### WP3 — Durable run records and Scheduled tasks list
- **What it does:** Plugin job state and AI/segment schedules write durable run records (D-E); the Tools and requirements list reads them. The plugin segment schedule becomes a proper setting instead of an unlisted key.
- **Files likely touched:** `PluginJobStateService.cs`, `PluginScheduledSegmentService.cs`, `MaintenanceSettings.cs`, AI batch services (to be confirmed by Claude Code)
- **Depends on:** none (list display depends on revamp WP for Tools and requirements)
- **Size:** Standard
- **Done when:** restarting the Engine keeps plugin job history; Scheduled tasks lists AI, plugin and webhook work with last and next run.

### WP4 — Integrations: delivery history and test
- **What it does:** Exposes the webhook delivery log already stored, with a Send test button, and shows a credential's last use and expiry on its card.
- **Files likely touched:** `ApplicationWebhookEndpoints.cs`, `ManagedApplicationWebhooks.razor`, `ManagedAccessApplications.razor`, Contracts types and fixtures
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a failing webhook shows its last six attempts with results; Send test records a delivery.

### WP5 — Developer Tools made honest
- **What it does:** Gather the three tester items into the group the revamp describes (opens on Provider Tester, sub-page Enrichment Tester); hide the Enrichment Tester when the Engine does not offer its debug actions; remove the separate on/off flags for testers in favour of the one admin-only rule.
- **Files likely touched:** `SettingsNav.cs`, `Settings.razor`, `ResolvedUISettingsViewModel.cs`, `DebugEndpoints.cs` mapping (to be confirmed by Claude Code)
- **Depends on:** none
- **Size:** Standard
- **Done when:** a release Engine shows no Enrichment Tester; a development Engine shows it; the menu matches revamp D35.

### WP6 — Live plugin directory
- **What it does:** Plugins > Available plugins reads the GitHub list (D-F): fetch with a saved local copy, last-checked time, Check now, off switch, verified / not verified labels, "new plugins available" note, Install leading to the approval step. Defines the list's format and a short "submit your plugin" guide.
- **Files likely touched:** `ApprovedPluginCatalogService.cs`, `config/core.json`, `PluginSettingsTab.razor`, `docs/reference/approved-plugins.json` (to be confirmed by Claude Code)
- **Depends on:** WP1, WP2
- **Size:** Standard
- **Done when:** with the Engine offline the page shows the saved copy and its date; with the switch off no request is made; a list entry marked not verified shows that label and the warning before install.

### WP7 — Verification and blocklist
- **What it does:** Shows trust levels (D-H) on cards and the approval step; checks the blocklist and switches blocked plugins off with a reason; checks checksum and the supported Library versions before install.
- **Files likely touched:** `ApprovedPluginCatalogService.cs`, `PluginCatalog.cs`, plugin manifest model, Contracts (to be confirmed by Claude Code)
- **Depends on:** WP6
- **Size:** Standard
- **Done when:** a blocklisted plugin is switched off and says why; a plugin outside its supported version range cannot be switched on; the level shows on its card.

### WP8 — Community plugins repository set-up (when the first community plugin is ready)
- **What it does:** Creates the community plugins repository with schema, one-folder-per-plugin layout, submission guide and template, automated checks, generated index and blocklist (Appendix A section 5).
- **Files likely touched:** new repository `Tuvima/tuvima-library-plugins`; `config/core.json` address (to be confirmed by Claude Code)
- **Depends on:** none (WP6 reads it; not needed for first release because official plugins ship in the main repository)
- **Size:** Standard
- **Done when:** a test submission with a bad manifest fails the checks and a good one appears in the generated index.

### WP9 — Make all shipped providers declarative
- **What it does:** Converts TVDB from a coded provider to a JSON-defined one, and removes the TMDB and ComicVine special code hooks by adding the missing general abilities to the JSON format (for example sign-in-then-token flows, which TVDB needs, and the extra lookups TMDB and ComicVine do). Behaviour and results must not change: the same items identify and enrich the same way. Also covers the subtitle and lyrics sources (SubDL, LRCLIB) so they too need no name-picked code.
- **Files likely touched:** `src/MediaEngine.Providers/Adapters/` (config-driven adapter and the name-keyed enrichment hooks, TVDB provider), `config/providers/tvdb.json`, `tmdb.json`, `comicvine.json`, `subdl.json`, `lrclib.json`, provider tests (to be confirmed by Claude Code)
- **Depends on:** none
- **Size:** Complex
- **Done when:** a before/after comparison on the same sample library gives the same identified titles and metadata values for TVDB, TMDB and ComicVine items; no provider code refers to a provider by name; a new provider can be added by a JSON file alone.

### WP10 — Plugin access to Local AI
- **What it does:** Gives JSON-only and separate-program plugins the same Local AI roles in-server plugins have, behind a plugin-scoped key and the declared AI permission; adds queueing, limits and the "AI unavailable" state (D-M).
- **Files likely touched:** `PluginExecutionContext.cs` (AI client), AI endpoints, plugin manifest AI permissions, provider JSON format, plugin state card (to be confirmed by Claude Code)
- **Depends on:** WP1, WP2
- **Size:** Complex
- **Done when:** a JSON-only test plugin using a text role gets a result with Local AI on and a clear "AI unavailable" status with it off; a plugin without the permission is refused; a flood of plugin requests does not slow an ingestion run in a timed test.

### WP11 — Full ingestion regression of every media type
- **What it does:** Repeats the WP0 ingestion on the finished build and compares it with the baseline: every media type must identify the same titles, with the same metadata values, artwork, grouping and Needs Review outcomes, and no slower beyond an agreed margin. Differences are listed and each is explained or fixed. Also checks a wiped data store: keys in the config folder still work, and a provider swapped for a JSON equivalent behaves the same.
- **Files likely touched:** the same harness as WP0
- **Depends on:** WP9, WP10 and every packet that touches ingestion or providers
- **Size:** Standard
- **Done when:** a report shows zero unexplained differences across all media types; any intended difference is listed and approved by Shy before merge.

### WP12 — Docs
- **What it does:** Updates every document the changes touch so nothing describes the old behaviour: a new "Developer tools" guide (the family, trust levels, approval rule, verification levels, directory); the plugin guides (three plugin kinds, no longer "in-server for everyone"); the provider docs (all providers JSON-defined, how to add one by file, keys in the config folder, capability sheet); security (plugin trust, secrets, blocklist, webhook history); configuration and settings docs (Developer menu, Plugins directory address, new state card); the Local AI docs (plugins can use Local AI); the developer code tour; a new decision record for the plugin kinds and Live TV staying possible; the engineering README index and product overview where they mention plugins or providers; the public docs site build.
- **Files likely touched:** `docs/guides/using-plugins.md`, `docs/guides/building-a-plugin.md`, `docs/architecture/security.md`, `docs/architecture/hydration-and-providers.md`, `docs/architecture/provider-onboarding.md`, `docs/architecture/provider-enrichment-strategy.md`, `docs/architecture/configuration.md`, `docs/architecture/settings.md`, `docs/architecture/ai-integration.md`, `AGENTS.md`, `engineering/architecture/decisions/` (new), `engineering/README.md`, `engineering/product/overview.md`, new guide (to be confirmed by Claude Code)
- **Depends on:** WP1 to WP11
- **Size:** Standard
- **Done when:** the docs site builds, a search of the docs finds no page that still says plugins all run inside the server, providers are code, or TVDB is coded, and the guides match the screens.

## 7. Risks and open questions

- **Risks:** Plugins load in-process and unsandboxed, so the approval note is honesty, not protection; a bad plugin can still do harm once approved. Mitigation: only approved sources, clear wording, audit record. More red shading could alarm people if a webhook fails once; D-D requires repeated failure.
- **Questions:** all answered by Shy on 2026-10-19; none open. Summary of answers: Q1 keep Integrations separate with the shared model (D-A); Q2 approval rule for all four areas (D-C); Q3 red for repeatedly failing items (D-D); Q4 hide the Enrichment Tester, it is temporary and goes at beta (WP5); Q5 and Q6 the live directory, official plugins in the main repository and community plugins in a separate repository later (D-F); Q7 and Q10 community listed, with the four trust levels (D-H); Q8 three plugin kinds (D-G); Q9 Live TV only has to remain possible (D-I); Q11 this spec sets direction and the platform build is a follow-up spec; Q12 full provider move as the direction, with conditions (D-J); Q13 existing plugins unchanged and keys stay in the config folder (D-K).

## 8. Gaps and drift

- Developer Tools: code has three separate top-level items (including a DevHarness not mentioned in the revamp), not one group with a sub-page.
- Enrichment Tester talks to debug actions mapped only in development builds, and those have no access checks.
- Plugin job state is in memory only; the plugin schedule key is not in the settings model.
- Webhook delivery rows are stored but no screen or action shows them.
- Four separate health models and five separate on/off flags; no approval step for AI features or credentials beyond creation.
- Approved plugin catalog contains no entries.
- Not confirmed (ask Claude Code): where AI model files live, how plugin enabled state persists beyond its settings file, whether the DevHarness has its own access check.

---

## Appendix A: plugin platform review


Research date 2026-10-19. Facts from our code were read by a code scout; competitor facts were read from public docs (sources listed). Items marked (unverified) were not confirmed.

## 1. Can a Live TV plugin be built on today's model?

**No.** Today a plugin is a .NET file the server loads inside itself and calls at fixed moments: detecting skippable segments (credits, ads), supplying Universe lore, reporting health, and describing its settings. Live TV needs several things that do not exist yet.

| Live TV needs | Today |
|---|---|
| Its own pages (guide grid, channel list, recordings) | Only a settings form generated from a list of fields |
| Its own Engine actions (tune, record, list channels) | None; plugins cannot add routes |
| A new kind of thing to watch (a channel, a live stream) | None; plugins get one file at a time |
| Feeding a live stream into the existing player/streaming path | None; streaming is internal |
| Always-on background work (hold a tuner, record at 8pm) | Only scheduled passes, and the job slot has nothing using it |
| Hearing about events (a recording ended, a title was added) | None |
| Its own saved data (channels, schedule, recordings) | Temporary files only |
| Safe storage for logins / keys | None |
| Finding devices on the network | None |
| Compatibility and packaging checks | Version and platform fields are written in the manifest but never checked; no install button, no package signature |

Safety today: plugins run inside the server with full trust, unsandboxed (our own docs say so). Permissions are asked per call, but a plugin is ordinary code and can bypass them. Plugin job history is lost on restart.

## 2. What the others do

| Server | How plugins work | Safe? | Finding plugins | Live TV |
|---|---|---|---|---|
| Jellyfin | .NET plugins loaded into the server, built against a specific server version | No sandbox (unverified); only a download checksum | Catalog is a JSON file anywhere; users can add or remove catalogs | Built in: HDHomeRun and M3U tuners, guide from XMLTV or Schedules Direct, recording |
| Plex | Old plugin system removed. New: "custom metadata providers", a web address the server calls | Fully separate programs | User pastes an address; no store | Built in; free to watch, DVR is paid |
| Emby | .NET plugins, catalog in the dashboard, restart needed | No review process described | Built-in catalog | Built in, plus plugins bridging DVR apps (NextPVR, TVHeadend, etc.) |
| Kavita | No plugins. OPDS reading feeds, API, OpenID sign-in | n/a | n/a | n/a |
| Audiobookshelf | No plugins. Metadata providers are outside web services following a published spec; notifications go through Apprise; OpenID sign-in | Fully separate; community ones explicitly "not security-checked" | User pastes address | n/a |

Lesson: nobody makes Live TV a plugin that draws its own screens. Everyone builds Live TV into the server using open formats (M3U channel lists, XMLTV guide data, HDHomeRun tuners), and uses plugins only to bridge extra tuner backends. The old plugin servers (Jellyfin, Emby) are powerful but tied to server versions and fully trusted; the newer approach (Plex, Audiobookshelf) is safer and simpler because plugins are separate programs behind a published contract.

## 3. Industry practices worth borrowing

- **A manifest that says everything before any code runs** (VS Code, Home Assistant): what it adds, what it may do, which Library versions it works with.
- **Compatibility ranges and checksums per version** (Jellyfin).
- **Quality tiers and discovery keys** (Home Assistant: bronze to platinum; tuner discovery by network announcement).
- **Signing levels** (Grafana: unsigned plugins refused by default; community and commercial levels after review; private plugins signed by their owner).
- **Publisher identity proof** (VS Code: domain ownership and track record; Obsidian: linked GitHub account).
- **Automated submission checks** (Obsidian): a plugin cannot be listed until the checks pass.
- **Signed releases and a parts list** (Sigstore signing and an SBOM): cheap and standard.
- **Run untrusted code sandboxed**: WebAssembly (Extism; licence needs the AGPLv3 check) or a separate process talking over a published contract (HashiCorp's go-plugin pattern).
- **Open formats where possible**: M3U, XMLTV, HDHomeRun, Apprise, OpenAPI.

## 4. Proposed plugin model: three tiers, safest first

1. **Declarative plugins (JSON only).** No code. A manifest describes: a metadata source (we already have this for providers: config-driven JSON), a channel list or guide source (M3U, XMLTV), a notification route, extra fields or views. Safest, same on every operating system, easiest to write. This is where most plugins should live.
2. **Service plugins (separate program, any language).** The Library starts the program (or is given its address) and talks to it over a published contract, like Plex and Audiobookshelf providers. It runs outside the server, so a crash or bad code cannot take the Library down, and it only sees what a scoped key lets it. Covers a tuner bridge, a Python metadata scraper, a Go tool. Cross-platform if it ships builds per system or runs as a container.
3. **Sandboxed code plugins (WebAssembly), later.** Small code that needs to run inside, with hard limits on time, memory and access. Deferred until needed.

First-party .NET plugins (like the current built-ins) stay possible but only for Tuvima-signed plugins. Community .NET assemblies loaded into the server are retired as the way to extend the Library, because they cannot be made safe.

What every tier gets from the Library (the "host services", each behind a permission): its own saved data and secrets store, a scheduler, event subscriptions, HTTP, tools, AI, and (new) **contributions**: a plugin declares pages and menu items from a fixed set of Library page types (list, grid, guide timeline, form, detail), filled with its data, so a plugin never ships arbitrary screens and the look stays uniform.

### Live TV under this model
Honest recommendation: **Live TV itself is a Library feature, planned as its own spec**, built on open formats (M3U, XMLTV, HDHomeRun discovery), with its own library type, channel and guide pages and a path into the existing streaming pipeline, as Jellyfin and Plex do. The plugin platform then lets others add tuner backends and guide sources (tier 1 for a playlist or guide file, tier 2 for a hardware bridge). Making the whole of Live TV a plugin would force us to build every platform seam first and would be slower, less safe and less uniform.

## 5. The plugin repository

**Name suggestion:** `Tuvima/tuvima-library-plugins` (clear, searchable, matches the main repo).

**Structure:** one folder per plugin with a manifest file (`plugins/<id>/plugin.json`), so each submission is a small, reviewable proposed change; a schema file for validation; submission guide and template; a generated `index.json` (what the Library downloads); a `blocklist.json` for withdrawn plugins.

**How it is managed:**
- Anyone proposes a plugin by opening a proposed change; automated checks run (manifest valid, version range, licence AGPLv3-compatible or clearly stated, permissions declared, release has checksum, signed release, parts list). Nothing lists until checks pass.
- Code owners (Tuvima) review anything that asks for risky permissions.
- The index is regenerated and published automatically; the Library downloads one small file.
- **Blocklist:** if a plugin turns out harmful, it is added to the blocklist; the Library checks it and switches that plugin off with a visible reason. This is the safety net for everything above.
- Advanced users may add other repositories (Jellyfin style); Tuvima's is the default, and removing it stops all calls. Nothing about the user is sent.

## 6. Verified vs unverified (own process, based on common standards)

| Level | Meaning | How it earns it |
|---|---|---|
| Official | Built by Tuvima | Signed with Tuvima's key |
| Verified | Publisher proven, plugin passed review | Publisher proves identity (GitHub account plus domain, as Obsidian and VS Code); automated checks pass; signed release and parts list; permissions reviewed by Tuvima; declares supported Library versions; re-checked on every new version |
| Community (not verified) | Listed, automated checks passed only | Same automated checks; no human review; install shows a clear warning and the full permission list |
| Private / sideloaded | Installed by hand | Disabled until the administrator approves the permission list; always labelled |

Revoking verification or blocklisting is always possible. Tiers show on the state card (D-B) and in the approval step (D-C).

## 7. Gaps summary

Missing in our app compared with others: install from catalog with one button; compatibility check; signed/checksummed packages; out-of-process or sandboxed running; own pages and routes; background services and events; plugin data and secrets storage; media source / live stream seam; tuner and guide support (Live TV) entirely; repository management and blocklist; durable job history; the JSON-only plugin tier beyond metadata providers; OpenID sign-in is planned elsewhere. Present in ours and worth keeping: permission list per plugin, tool requirements with per-platform checked downloads, AI permission gate, settings schema forms, health checks.

## Sources
Jellyfin plugins, plugin repositories and Live TV docs (jellyfin.org); Plex custom metadata providers announcement and DVR FAQ (forums.plex.tv, support.plex.tv); Emby plugin and Live TV plugin pages (emby.media); Kavita OPDS wiki; Audiobookshelf custom metadata providers and notifications docs; Home Assistant integration manifest docs; Grafana plugin signing; VS Code publishing and verified publishers; Obsidian plugin submission; Extism; HashiCorp go-plugin.

## 8. What else in the app is a good plugin candidate (code scan, 2026-10-19; coupling judged from file structure, not tested)

| Feature | Today | Plugin fit |
|---|---|---|
| Metadata providers (Apple, TMDB, ComicVine, MusicBrainz) | JSON-defined, with a few name-specific code hooks for TMDB and ComicVine | Best fit; the name-specific hooks must go first |
| TVDB, Wikidata | Coded | TVDB later; Wikidata stays core (authority for the cascade) |
| Subtitle and lyrics sources (SubDL, LRCLIB) | JSON plus a coded class chosen by name | Good fit; make the format declarative so a new source needs no code |
| Notifications / email | Password-reset email only, one fixed class | Good fit (many destinations, Apprise-style) after email becomes a general notification service |
| Sign-in providers (OIDC / OAuth) | Config, closed two-kind switch | Belongs in Users and Sign-in, keep core |
| File format handlers (EPUB, PDF, comics, audio, video) | Coded, registered in code | Tight today; a later fit for new formats via separate programs; core formats stay built in |
| OCR (Tesseract) | Interface, tied to Tesseract | Swappable engine, good service-plugin fit |
| AI features | Interface plus flags | Stays core (local, privacy); an alternative runtime could be a service plugin later |
| Backup targets | One internal class, no interface | Good future fit (cloud, NAS) once an interface exists |
| Webhook event list | Hard-coded | Stays core |
| Transcoding, casting | Config plus single implementation | Stays core |
| Naming / organisation templates | Per-library template | Good fit as declarative presets |
| Ratings, OPDS reading feeds, geocoding / map tiles | Not found in code | New plugin territory |

---

## Plain English summary

- **One look and one rule.** Local AI, Plugins, Integrations and Developer Tools get the same status card and the same four health words. Turning anything on for the first time shows what it can do and whether anything leaves your machine, and records your answer.
- **Plugins get safer and more varied.** Three kinds: plain JSON files (no code), separate programs in any language, and sandboxed code later. Community plugins no longer run code inside the server. Tuvima's own plugins keep working unchanged.
- **A plugin directory.** A list of known plugins kept on GitHub, labelled Official, Verified, Community or Private, with a blocklist that can switch a bad plugin off. Official plugins live in the main repository; community plugins get their own repository later.
- **Every metadata source becomes a JSON file**, including TVDB, so swapping one source for another is a file change. A full ingestion of every media type before and after proves nothing changed. API keys stay in the config folder, so wiping the database keeps them.
- **Plugins can use Local AI** safely on your machine, with limits so they cannot slow down scanning.
- **Smaller fixes.** Webhooks show delivery history and a Send test button; plugin job history survives restarts; the temporary Enrichment Tester is hidden when it cannot run.
- **Live TV** is not being built, and nothing here blocks it.
- **Documentation** is updated everywhere it described the old behaviour.

