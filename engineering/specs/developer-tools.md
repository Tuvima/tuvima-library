# Spec: Developer tools as one family (Local AI, Plugins, Integrations, Developer Tools)

> **Status:** In progress · **Version:** 3 (replaces v2; adds the Local AI behaviour review and the Integrations and Developer Tools review; locked by Shy 2026-10-19) · **Last updated:** 2026-10-20 · **Related docs:** [Product overview](../product/overview.md) · [Appendix A: plugin platform review](#appendix-a-plugin-platform-review) · [Appendix B: Local AI review](#appendix-b-local-ai-review) · [Appendix C: Integrations and Developer Tools review](#appendix-c-integrations-and-developer-tools-review) · [Settings revamp](archive/settings-revamp.md) (D35, D43 to D48) · [First-run prerequisites](archive/first-run-prerequisites.md) · [Ingestion and readiness](archive/ingestion-and-readiness.md)

> **Change note (wave 2 build, 2026-10-19, decided by Shy):** (1) WP5: the development data page (reset, seed, rescan) becomes a second development-only sub-page, **Development data**, under Developer Tools beside the Enrichment Tester. (2) WP9 converts only what it names (TVDB, the TMDB and ComicVine hooks, SubDL, LRCLIB); every other place that treats a provider specially by name moves to WP20. (3) WP2: approval also covers widening an integration's permissions, switching it back on while it holds keys, and making it an administrator.

> **Build progress (2026-10-20):** wave 1 (WP0, WP13, WP3, WP32) and wave 2 (WP14, WP2, WP5, WP9, plus WP1) are built. WP0 gained record-and-replay: a first run saves every provider answer and later runs replay them, so before/after comparisons see only our own changes. Two replays of one build still differ where ingestion itself depends on timing (MusicBrainz release choice, TMDB composer choice, which audiobook part carries the match, TV episodes joining their show, cast lookups); a separate fix is in progress, and until it lands those are treated as known noise. The WP9 before/after comparison (generated sample and the real-media sample) shows no difference beyond that noise. WP27 (the Local AI engine room) is built: every AI and text-reading request goes through one queue with the priority person, ingestion, Library background, plugins; plugins have hourly allowances; each request leaves a usage record; readiness is checked for real; the unused "max at once" setting is gone. Its comparison against main shows no ingestion difference beyond the known noise. Wave 3 continues in its own session.

> **Build note (wave 3, 2026-10-20; only item 1 is Shy's decision, items 2 to 4 are how the build carried out existing decisions):** (1) Shy decided: the Install button on Available plugins stays hidden until WP19 adds real installs; the approval step behind it (a preview built from the directory entry) is built and tested now, and one constant switches the button on. (2) Added packet **WP15a** at the Universes spec's request (U-WP9) and for WP26: a plugin's manifest declares each web address it contacts and what it sends; each shows as a "Contacts <address>: <what it sends>" line in the approval statement (inside its fingerprint, so a changed address asks again) and under "Sends data out" on the card; `/settings/plugins?plugin=<id>` opens that plugin's card. This carries out D-B ("where") and D-C ("whether anything leaves the machine"). (3) WP15's limits include the plugin web connection: only declared addresses, with a per-address requests-a-minute cap. (4) WP15 adds two permissions not named in the spec text but implied by D-R: `watch-activity` ("is told what people watch", required for playback events) beside `library-events`, `plugin-records`, `scheduled-work` and `background-work`.

> **Build progress (2026-10-20, wave 3):** WP4, WP6, WP15a and WP15 are built (WP27 merged earlier in #319). Checkpoint: CI-strength build with no warnings, full test suite passing, independent review with no must-fix (its should-fix items were fixed), on-screen check of the Plugins and Available plugins screens at desktop, tablet and phone widths. Not done at this checkpoint, at Shy's request to close out: the before/after ingestion comparison (wave 3 changes no identification or enrichment code; the comparison runs at the wave 4 checkpoint, which covers both waves) and a live on-screen check of the webhook delivery list (covered by component tests). Follow-ups are listed in [the handoff](../handoffs/2026-10-20-developer-tools-waves-4-to-8.md).

## 0. What versions 2 and 3 change

Version 1 settled the family model and left the plugin platform build to a follow-up spec. Shy asked for all of it as one large planned implementation, so version 2 brings the platform build into this spec (WP13 to WP26), organises the work into waves with checkpoints (section 6), and keeps everything else in version 1 unchanged.

Version 3 covers the whole Developer area, not only plugins. It is also the **Local AI behaviour review** (planning item I) that the first-run prerequisites spec deferred. Shy's direction: **Local AI is the engine room** that runs the models and keeps the machine fair, and **plugins are how AI is used**, including the AI features that ship with the Library. It adds decisions D-Q to D-X, packets WP27 to WP32, Appendix B (Local AI review) and Appendix C (Integrations and Developer Tools review); amends D-B, D-C, D-M, WP10, WP12, the waves, section 8 and Appendix A section 8; and changes Settings revamp D43 (the Local AI sub-pages).

## 1. Goal

The four pages in Settings > Developer all extend or connect the Library, but today each has its own on/off switch, its own idea of "healthy", its own approval story and its own log. An administrator has to learn four different ways of answering "is this on, is it working, what is it allowed to do, and when did it last run?". This spec gives them one shared set of answers, and settles whether outside software should connect through plugins.

## 2. Scope

**In scope**
- The whole plugin platform (version 2): the plugin description format with version and platform checks, a secrets store, host services (own data, scheduler, events, long-running work), plugin pages and menu items from the Library's page types, the JSON-only plugin runtime, the separate-program plugin host, one-button install, update and removal with signature checks, the full provider move under Plugins, the WebAssembly tier, and a Live TV readiness record (WP13 to WP22), a general notification service with channel plugins (WP23, WP24), and backup destinations as plugins with scheduling, retention and encryption (WP25, WP26).
- Making every shipped metadata provider fully JSON-defined: TVDB (coded today) becomes a JSON provider, and the TMDB and ComicVine code hooks that are keyed to their names are replaced by general JSON features (WP9).
- One shared "state card" every item in the four areas uses: name, kind, on/off, health, what it is allowed to do (plain words), last activity, last problem.
- One approval rule: nothing new switches on until an administrator has seen, in plain words, what it can do and what it sends out of the machine.
- The answer to Shy's question on outside software and plugins (D-A).
- Making recurring work visible in one place and recorded durably.
- Fixing the gaps found in the code review (section 8).
- Version 3: Local AI as the engine room (abilities, one queue, budgets, usage record) and its Settings page (D-Q, D-S, D-U, WP27, WP30); every AI feature becomes an Official plugin whose results are labelled and shown (D-R, D-T, WP28, WP29); removing AI code that cannot run (D-V); Integrations gain one shared event list, key-expiry warnings and health (D-W, WP31); an administrator check on the development reset actions (D-X, WP32).

**Out of scope**
- Redesigning the Plugins page content beyond the shared state card (settings revamp D44 stays: one page).
- Building Live TV itself (only keeping it possible, D-I).
- Backing up the media files themselves: backups cover the data store and settings only, as today; media stays where the user keeps it.
- OpenID Connect / SSO (belongs to Users and Sign-in).
- The phone and TV apps (they are not Integrations).
- Version 3 directions that are recorded but not built: new AI abilities (transcribe speech, find similar, describe image), an AI reader for hard scanned pages, an AI runtime elsewhere on the home network (Q20), and new Integrations such as OPDS reading feeds, an AI-assistant connection and play-history scrobbling (Q22). Each needs its own spec.

## 3. Decisions made

All decisions below were agreed by Shy on 2026-10-19. Already locked in the Settings revamp and not reopened: Developer group for server administrators only; icons; Local AI keeps AI settings only (D43); Plugins folded into one page (D44); Integrations = credentials and webhooks (D46, D48); only Local AI shows red today (D3).

- **D-A (agreed).** Outside software stays in **Integrations**, not plugins. Plugins are code running inside the server with the server's own trust; outside software is somebody else's code running elsewhere and proving itself with a key. Mixing them would put unlike levels of trust under one label. Reason: Privacy and Reliability. The two share the state card and approval rule (D-B) so they feel like one family. A plugin may later *offer* an integration (for example a plugin that pushes to a notification service) without changing this.
- **D-B (agreed; v3 amended).** One **state card** everywhere: Name · Kind (Plugin, Integration, Tester; plus one card for the Local AI engine itself; AI features are plugins under D-R and show "Uses Local AI") · Switch (on/off) · Health (Working, Off, Needs attention, Broken) · Allowed to (plain list) · Sends data out? (yes/no, where) · Last activity · Last problem. Reason: Uniformity and Maintenance; one component, one wording.
- **D-C (agreed; v3 amended).** One **approval rule**. A new item is off. Turning it on shows a confirmation listing what it can do and whether anything leaves the machine, and the choice is logged with who and when. Applies to: each AI plugin, including the Official ones that ship with the Library (names the abilities it uses and any model download), each plugin (its declared permissions, tool needs and AI permissions; a note that plugins run unsandboxed inside the server), each integration credential (its scopes) and each webhook (its address; private-network addresses already need approval). The Approved Catalog becomes the live Plugin directory (D-F), a list to browse, not a gate. Reason: Privacy; prevent, don't patch.
- **D-D (agreed).** **Health is the same four words** across the family, and "Broken" (red in the menu) means *an item that is switched on cannot do its job*. Today only Local AI can be red; under this rule a broken switched-on plugin or a webhook failing repeatedly also shades its menu entry red, and nothing switched off is ever red. Amber stays reserved for Needs Review. Reason: Uniformity.
- **D-F (agreed).** **A live plugin directory kept on GitHub.** One public list (a single file in a Tuvima GitHub repository) names every known plugin: name, author, what it does, version, permissions it declares, link, and a verified mark where Tuvima has checked it. Anyone submits a plugin by proposing a change to that list; Tuvima reviews and accepts it, so GitHub itself is the submission and review trail. The Library already reads such a list from a GitHub-only address (setting in core.json); this spec makes it a first-class page: Plugins > Available plugins, with "last checked", a Check now button, and a quiet "new plugins available" note (not an attention bubble). Fetching sends nothing about the user or their library, only a plain download; it can be switched off, and the page then works from the last copy saved on the machine. Reason: Extensibility and Privacy. Installing still goes through the approval step (D-C); being listed is not approval. GitHub is a good fit now (free, reviewable, public history); the address is a setting, so it can move later. **Official plugins live in the main tuvima-library repository and ship with the app** (Shy, 2026-10-19). Community plugins move to their own public repository later, suggested name `Tuvima/tuvima-library-plugins`, when the first community submission is ready; until then the directory address points at the main repository. Because the address is a setting, moving it is not a Library change.
- **D-G (agreed).** **Three kinds of plugin, safest first** (Shy asked for non-.NET and JSON-only plugins, cross-platform and safe). (1) Declarative plugins written only in JSON (metadata sources, M3U/XMLTV channel and guide sources, notification routes); (2) service plugins, separate programs in any language that talk to the Library over a published contract, never inside the server; (3) sandboxed WebAssembly plugins later. Community .NET assemblies loaded into the server stop being the way to extend the Library; only Tuvima-signed ones may run in-process. Plugins add pages and menu items only by choosing from the Library's own page types, so the look stays uniform. Reason: Privacy, Reliability, Extensibility. Full analysis: Appendix A.
- **D-H (agreed).** **Verification has its own process** based on common practice (Grafana, VS Code, Obsidian, Home Assistant): Official, Verified, Community (not verified) and Private levels; proven publisher identity, automated checks, signed releases with a parts list, human review of permissions for Verified, and a blocklist that can switch a harmful plugin off everywhere. Shown on the state card and the approval step.
- **D-I (agreed; clarified by Shy 2026-10-19).** **Live TV does not have to be built, it only has to remain possible.** The platform is designed so Live TV can be added later, either as a Library feature on open formats (M3U, XMLTV, HDHomeRun) with its own spec, or with tuner/guide plugins on top of it, and nothing in this spec closes that door. No Live TV work is scheduled. The plugin platform lets others add tuner backends and guide sources. Reason: no media server makes Live TV a plugin, and it needs seams (a channel library type, live stream path) that belong to the Library.
- **D-J (agreed, Shy 2026-10-19: option b).** **Metadata providers join the same model, and the direction is to move them fully under the Plugins page.** Conditions before the move is built (follow-up platform spec): a secrets store plugins can use, first-run setup asked by capability, a warning before removing a source, a capability sheet and Library-set weights, and opt-in re-matching (Appendix A). Until then they stay where they are and share the model: A provider that is only a JSON file (the "config-driven" providers in config/providers) is already a declarative plugin, so it gets the same manifest, state card, permissions (which web addresses it may call, which key it needs) and trust level (Official when it ships with the Library). Providers written in code stay built into the Library as Official first-party plugins. Not changed: the Priority Cascade, the order providers are asked in, and the Wikidata bridge stay core Library behaviour; a provider plugin only supplies data into them, and a plugin can never replace the cascade. The Settings > Metadata Providers page stays where it is (ordering and keys); it simply lists the same items the Plugins page lists, so there is one source of truth. The three plugins that ship today (commercial skip, Fandom lore, media segments) also become Official built-ins. Reason: Uniformity and Maintenance. No provider is rewritten as part of this spec.
- **D-K (agreed).** **Existing plugins are not rewritten, and API keys survive a wiped data store.** The existing plugin contract stays supported for Official plugins, so they keep working without edits. The three plugins that ship today (commercial skip, Fandom lore, media segments) are Tuvima-built, so they may keep running inside the server as Official plugins; they only need a manifest touch-up (trust level, supported Library versions, capability list). Provider API keys today live in files under the config folder (`config/secrets/`), not in the data store, so wiping the database leaves them in place. That stays true: the plugin secrets store must keep keys in the config folder (or an equivalent that survives a database reset), never only in the database. Backups can include or exclude secrets as they do now. Reason: Reliability and Maintenance.
- **D-L (agreed, Shy 2026-10-19).** **TVDB and the name-keyed provider hooks become JSON-defined as part of this work**, so every shipped metadata provider is a declarative plugin and a provider can be swapped by file alone. Risk: TVDB signs in to get a token before each session, so the JSON format must learn that flow; results must not change (WP9).
- **D-M (agreed, Shy 2026-10-19; v3: built on D-S and D-U).** **Every kind of plugin can use the Library's Local AI services.** Under version 3, plugins ask for *abilities* (D-S) rather than "AI roles", and every request goes through the one AI queue (D-U). Today only in-server plugins can (they ask for an AI role such as text or structured output, declared in their manifest). Under the three kinds (D-G), JSON-only plugins can name an AI step in their description (for example "summarise this text"), and separate-program plugins call the same service through a plugin-scoped key. Rules: the plugin must declare which AI roles it uses and the administrator sees this at approval (D-C); the AI always runs on the user's machine and a plugin never gets the model files or a way to send data out through it; if Local AI is off or its tools are missing the plugin is told "AI unavailable" and shows Needs attention instead of failing silently; plugin AI requests wait behind the Library's own work (ingestion) and have time and size limits so a plugin cannot starve the machine. The same idea applies later to other local services (OCR, FFmpeg tools). Reason: Privacy and Performance.
- **D-N (agreed, Shy 2026-10-19).** **The plugin platform is built in this spec, as one large planned implementation**, in waves with a checkpoint (build, tests, ingestion comparison, independent review) after each wave, so the app is never left half-converted. Reason: Reliability; a big change lands as steps that each leave a working app.
- **D-O (agreed, Shy 2026-10-19).** **One notification service.** Today the only message the Library sends is the password-reset email, and webhooks tell outside software about events. Version 2 adds one place that decides *who is told what, and how*. Events (examples: Needs Review items waiting, a backup failed or finished, a plugin switched off or broken, a new plugin available, disk space low, ingestion finished, someone invited) are sent through **channels**: in the Dashboard, email, and channel plugins (a webhook-style channel, an Apprise-style gateway, push services, chat apps). Server administrators choose per event which channel and how quiet (grouped, one per minute at most, off at night, a channel that keeps failing switches itself off and shows Needs attention). Rules: administrator-only events go only to administrators; messages carry as little as possible by default (no titles of what you watch or own leave the machine unless you choose to include them); channel logins live in the secrets store. The password-reset email moves onto this service. Webhooks stay in Integrations for outside software that pulls data in; a webhook can also be chosen as a channel.
- **D-P (agreed, Shy 2026-10-19).** **Backup destinations are plugins.** A backup is still first made on the server (a local folder, as today). A **destination** is an extra place a finished backup is copied to, so one broken disk or a wiped machine does not lose it. Rules:
  1. *What is backed up:* the data store and the non-secret settings, as today; media files are not included. Secrets are excluded unless the person includes them.
  2. *Always encrypted before leaving the machine:* the archive is encrypted with a passphrase the person sets; the Library never stores the passphrase and a destination plugin only ever sees an encrypted file. Losing the passphrase means that copy cannot be restored, and the screen says so plainly.
  3. *What a destination can do:* test the connection, send a backup, list backups held there, fetch one for restore, delete old ones (for retention), report free space. Nothing else.
  4. *Schedule and retention:* a backup schedule (daily or weekly by default) and "keep the last N" are set once for all destinations, and appear in Scheduled tasks. A failed backup or failed copy shows Needs attention and sends a notification.
  5. *Restore:* choose a backup from the local folder or any destination; it is fetched, decrypted, validated and staged using the existing safe restore (applied at the next start, with the current data kept as a pre-restore copy).
  6. *Verification:* after a copy, the Library checks the file arrived intact (size and checksum) and records it; once a month it offers a test restore check of the newest backup (validate only).
  7. *First destinations:* Official built-ins for another folder or a network share; Official plugins for S3-compatible storage and WebDAV (Nextcloud and similar); others by the community through the plugin kinds in D-G.
  Reason: Reliability and Privacy.
- **D-E (agreed).** **Recurring work is recorded durably and listed once.** Local AI automation, plugin jobs, webhook retries and the segment-detection schedule each write a run record (started, finished, result). System > Tools and requirements > Scheduled tasks lists them all with last and next run (settings revamp D22, D42); each setting stays on its own page.

**Version 3 decisions (agreed by Shy 2026-10-19).** The Local AI review behind them is Appendix B; the Integrations and Developer Tools review is Appendix C.

- **D-Q (agreed).** **Local AI is the engine room.** It runs the local models (today one text model in three sizes and an optional speech model), decides which model serves which ability, holds the machine's AI budget and the queue, keeps the privacy promise (AI never sends data out), and records who used AI and how much. Its Settings page has four parts: **Status** (runtime, chosen model, health), **Model and hardware** (size profile and the hardware check), **Budget** (how much of the machine AI may use, quiet hours) and **Usage** (which plugins used which ability, how much, when). Model files and downloads stay on Tools and requirements (settings revamp D43). The old Features, Vocabulary and Automation sub-pages go: AI features appear on the Plugins page with a "Uses Local AI" filter (and are linked from Usage), the vibe vocabulary moves into the vibe tags plugin's settings, and each AI plugin's schedule lives with that plugin and in Scheduled tasks (D-E). Reason: Maintenance and Extensibility; one owner for the machine's AI.
- **D-R (agreed, Q15 and Q17).** **Every AI feature is an Official plugin that ships with the Library**: Smart labelling (reads messy file names), Type helper (decides an unclear media type), Series order, Vibe tags, Description insights (themes, mood, setting, period, audience, pace, content warnings, people, and the short summary; the separate TL;DR switch that controls nothing is folded in), and Taste summary (which gains a switch and a "reads what people watch" permission). All stay off by default and go through the approval step (D-C). Smart labelling and the Type helper run during ingestion through a new ingestion-assist hook point; each already has a non-AI fallback, so the Library works fully with every AI plugin off. The rule this follows: **the Library must work with no AI at all; anything AI-powered is optional and therefore a plugin.** Reason: Extensibility and Uniformity.
- **D-S (agreed).** **Plugins ask for abilities, not models.** Abilities available now: write text (quick, careful or long), pull out structured facts, classify, and read text from images. Abilities recorded for later: transcribe speech, find similar, describe image. The engine room decides which engine serves each ability, so a model can be changed without breaking a plugin; plugins may use structured output, which today only the Library's own features can. Abilities cover **local tools as well as AI**: read text from images is served by Tesseract, and audio and video analysis by FFmpeg. They share the queue, budget, usage record, approval wording and "unavailable" status. Tools are **not** AI for the person: Tesseract and FFmpeg stay on Tools and requirements, keep their own settings, and keep working when Local AI is off (Q23). Reason: Extensibility and Reliability.
- **D-T (agreed, Q16).** **AI results are labelled suggestions, never overwrites.** An AI plugin's output is recorded as coming from that plugin, marked AI, ranked below providers and below anything a person entered, and removed with the plugin. Anything AI computes must be shown somewhere or is not computed: vibes, themes, moods, the short summary and content warnings appear on the item's detail page marked AI, and vibes and themes are browse filters. Today these values are computed and stored but no screen shows them. Reason: Presentation and Privacy; nothing hidden, nothing silently replaced.
- **D-U (agreed).** **One AI queue for everything.** Order: a person using the app, then ingestion, then the Library's background work, then plugins. Each plugin has a request and time budget; going over it shows Needs attention. Ingestion-time AI and plugins join the queue (today they skip it). The "max at once" setting, which no code reads, is removed. Reason: Reliability and Performance.
- **D-V (agreed, Q18).** **AI code that cannot run is removed**: the speech-sync stub and its unregistered background service, the image-AI placeholder in the media-segments plugin (it asks for an ability that does not exist and always shows degraded), and the unused Wikidata tie-break prompt. The abilities stay listed as "later" in the contract and come back with a real feature. Reason: Maintenance; no setting or status should promise what nothing delivers.
- **D-W (agreed, Q19).** **Integrations.** Outside software keeps keys and webhooks (D-A). Additions: **one event list** shared by webhooks, notifications (D-O) and plugin event subscriptions (WP15); keys get a suggested expiry when issued and a key unused for 90 days shows Needs attention; each integration shows the four health words (D-D). Outside software gets **no** Local AI access; the existing "use AI" permission stays unavailable. Reason: Privacy and Maintenance.
- **D-X (agreed, Q21).** **Developer Tools.** The development-build reset, wipe, factory-reset and seed actions require an administrator (today any signed-in user can call them in a development build). Section 8 is corrected. Reason: Privacy and Reliability.

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
- New stored information in version 2: notification routes and channel health; backup destinations, schedule, retention and copy records; plugin records (WP14, WP15, WP19). The backup passphrase is never stored by the Library.
- New stored information in version 3: AI usage records (which plugin used which ability, when, how long, result) and per-plugin budgets (WP27); AI plugin output recorded as coming from that plugin and marked AI, instead of overwriting values (WP28); integration health and the last-used and expiry state that drives key warnings (WP31). Pre-release data may be reset.

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
- **What it does:** Gives JSON-only and separate-program plugins the same Local AI abilities in-server plugins have, behind a plugin-scoped key and the declared AI permission, using the engine room's ability contract, queue and budgets from WP27 and the "AI unavailable" state (D-M, D-S, D-U).
- **Files likely touched:** `PluginExecutionContext.cs` (AI client), AI endpoints, plugin manifest AI permissions, provider JSON format, plugin state card (to be confirmed by Claude Code)
- **Depends on:** WP1, WP2, WP27
- **Size:** Complex
- **Done when:** a JSON-only test plugin using a text role gets a result with Local AI on and a clear "AI unavailable" status with it off; a plugin without the permission is refused; a flood of plugin requests does not slow an ingestion run in a timed test.

### WP13 — Plugin description format v2 and compatibility checks
- **What it does:** Extends the plugin description (manifest) with the plugin kind (JSON, separate program, sandboxed, Official built-in), the Library versions it supports, supported operating systems, a capability sheet (which data and abilities it supplies), and declared permissions; the Library actually checks version and platform before a plugin can be switched on. The existing contract stays supported for Official plugins.
- **Files likely touched:** `src/MediaEngine.Plugins/PluginManifest.cs`, `PluginCatalog.cs`, plugin tests, Contracts types and fixtures (to be confirmed by Claude Code)
- **Depends on:** none
- **Size:** Standard
- **Done when:** a plugin outside its supported version range or on an unsupported operating system cannot be switched on and says why; the three shipped plugins load unchanged.

### WP14 — Plugin secrets store
- **What it does:** A secure store plugins can use for keys and logins, kept in the config folder (survives a data store wipe), scoped so one plugin cannot read another's, never logged, included or excluded in backups as today (D-K). Provider keys move to it without the user re-entering them.
- **Files likely touched:** provider credential service, plugin settings service, backup service (to be confirmed by Claude Code)
- **Depends on:** WP13
- **Size:** Complex
- **Done when:** with the data store deleted, keys still work; a test plugin cannot read another plugin's key; no key appears in logs or API responses.

### WP15 — Plugin host services: own data, scheduler, events, long-running work
- **What it does:** Gives plugins a place to store their own records, register recurring work with run records (WP3), subscribe to Library events (for example item added, ingestion finished), and run always-on background work with start, stop and health. Each behind a declared permission and bounded by limits.
- **Files likely touched:** `src/MediaEngine.Plugins/PluginContracts.cs`, plugin execution context, scheduler services, event publishing, storage schema (to be confirmed by Claude Code)
- **Depends on:** WP3, WP13
- **Size:** Complex
- **Done when:** a test plugin stores and reads its own records across a restart, receives an item-added event, and runs a recurring job whose runs are listed in Scheduled tasks; a plugin exceeding its limits is stopped with a clear status.

### WP16 — Plugin pages and menu items
- **What it does:** A plugin declares pages and menu entries by choosing from the Library's own page types (list, grid, guide timeline, form, detail) and supplying data; the Dashboard renders them in the Library's look, only for users and devices the plugin's permissions allow, and never on phones unless the page type supports it. No plugin ships its own screens.
- **Files likely touched:** Dashboard plugin-page host component, `SettingsNav.cs` or main navigation, Contracts page-description types (to be confirmed by Claude Code)
- **Depends on:** WP15
- **Size:** Complex
- **Done when:** a test plugin adds a menu entry and a list page and a guide-timeline page that look like built-in pages at desktop and TV sizes; a profile without permission does not see them.

### WP17 — JSON-only plugin runtime
- **What it does:** Runs plugins that are only JSON: metadata sources (the WP9 format), channel and guide sources (M3U and XMLTV definitions), notification routes, and AI steps (D-M), with no code. Adds the missing abilities to the format (sign-in-then-token flows, scheduled refresh, field transforms).
- **Files likely touched:** `src/MediaEngine.Providers/Adapters/`, new declarative plugin loader, Contracts, docs schema (to be confirmed by Claude Code)
- **Depends on:** WP9, WP10, WP13
- **Size:** Complex
- **Done when:** a new metadata source and a channel-list source are added as JSON files alone and work; a malformed file is refused with a plain message.

### WP18 — Separate-program plugin host
- **What it does:** Starts and supervises plugins that are separate programs (or connects to one at an address), talking over a published contract (an OpenAPI description) with a key limited to that plugin: health checks, restart on crash, time and memory limits where the operating system allows, and per-operating-system builds or a container option. Permissions are enforced at the Library's side of the door.
- **Files likely touched:** new plugin host service, published contract document, process supervision, Dashboard state card (to be confirmed by Claude Code)
- **Depends on:** WP13, WP14, WP15
- **Size:** Complex
- **Done when:** a sample plugin written in another language (Python or Go) installs, shows healthy, serves a metadata lookup, is restarted after being killed, and cannot call anything beyond its permissions.

### WP19 — Install, update and remove with signature checks
- **What it does:** One-button install from the directory (WP6) with checksum and signature verification (Sigstore-style) for Verified and Official plugins and a clear warning for Community; updates re-ask for approval if permissions grow; removal shows what depends on the plugin ("TV artwork would have no source") and keeps saved metadata.
- **Files likely touched:** plugin catalog and install services, directory client, Dashboard install and remove flows (to be confirmed by Claude Code)
- **Depends on:** WP2, WP6, WP7, WP13
- **Size:** Complex
- **Done when:** a tampered package is refused; an update that adds a permission asks again; removing a plugin that is the only source for a data type warns first.

### WP20 — Providers fully under Plugins (D-J)
- **What it does:** Lists all metadata providers on the Plugins page as plugins (Official built-ins), with setup asked by capability in first run, removal warnings from capability sheets, Library-set weights (a plugin only suggests), and opt-in re-matching when a source is swapped. The Settings > Metadata Providers page remains the place for ordering and keys and shows the same items.
- **Files likely touched:** `PluginSettingsTab.razor`, provider settings panels, first-run provider step, scoring weights loader (to be confirmed by Claude Code)
- **Depends on:** WP9, WP14, WP19
- **Size:** Complex
- **Done when:** swapping Apple for another book source on a test library shows the capability comparison first, keeps existing identifications, and re-matches only when asked; first run asks for "a source for movies and TV artwork" rather than a named service.

### WP21 — WebAssembly plugin tier
- **What it does:** Runs sandboxed code plugins with hard limits on time, memory and access, using an open-source WebAssembly runtime. **Needs a licence check (AGPLv3-compatible) before any tool is added; if the licence does not pass, stop and ask Shy.**
- **Files likely touched:** new sandbox host, `Directory.Packages.props`, licence notices, plugin tests (to be confirmed by Claude Code)
- **Depends on:** WP15, WP18
- **Size:** Complex
- **Done when:** a sample sandboxed plugin runs on Windows, Linux and Docker, is stopped when it exceeds limits, and cannot reach the file system or network except through granted host services.

### WP22 — Live TV readiness record
- **What it does:** Writes the decision record for how Live TV can be added later (a channel library type, a live-stream path into the player, tuner and guide plugins on open formats) and adds a contract test proving the platform has room for it (a plugin can declare a "source" capability and register channels without any tuner or UI). Does not build Live TV.
- **Files likely touched:** `engineering/architecture/decisions/` (new), plugin contract tests (to be confirmed by Claude Code)
- **Depends on:** WP13, WP15, WP16
- **Size:** Standard
- **Done when:** the record exists and the contract test passes; nothing in the Live TV direction needs a platform change.

### WP11 — Full ingestion regression of every media type
- **What it does:** Repeats the WP0 ingestion on the finished build and compares it with the baseline: every media type must identify the same titles, with the same metadata values, artwork, grouping and Needs Review outcomes, and no slower beyond an agreed margin. Differences are listed and each is explained or fixed. Also checks a wiped data store: keys in the config folder still work, and a provider swapped for a JSON equivalent behaves the same.
- **Files likely touched:** the same harness as WP0
- **Depends on:** WP9, WP10, WP17, WP20, WP27, WP28 and every packet that touches ingestion or providers; also run at the end of each wave
- **Size:** Standard
- **Done when:** a report shows zero unexplained differences across all media types; any intended difference is listed and approved by Shy before merge.

### WP12 — Docs
- **What it does:** Updates every document the changes touch so nothing describes the old behaviour: a new "Developer tools" guide (the family, trust levels, approval rule, verification levels, directory); the plugin guides (three plugin kinds, no longer "in-server for everyone"); the provider docs (all providers JSON-defined, how to add one by file, keys in the config folder, capability sheet); security (plugin trust, secrets, blocklist, webhook history); configuration and settings docs (Developer menu, Plugins directory address, new state card); the Local AI docs (version 3: Local AI as the engine room, abilities including tool-served ones, AI features as Official plugins, AI results marked and ranked below providers, the one queue and budgets; and the drift listed in Appendix B section 6); the Integrations docs (shared event list, key expiry, health); the developer code tour; a new decision record for the plugin kinds and Live TV staying possible; the engineering README index and product overview where they mention plugins or providers; the public docs site build.
- **Files likely touched:** `docs/guides/using-plugins.md`, `docs/guides/building-a-plugin.md`, `docs/architecture/security.md`, `docs/architecture/hydration-and-providers.md`, `docs/architecture/provider-onboarding.md`, `docs/architecture/provider-enrichment-strategy.md`, `docs/architecture/configuration.md`, `docs/architecture/settings.md`, `docs/architecture/ai-integration.md`, `docs/architecture/local-ai-model-portfolio.md`, `docs/reference/ai-configuration.md`, `docs/explanation/how-ai-works.md`, `AGENTS.md`, `engineering/architecture/decisions/` (new), `engineering/README.md`, `engineering/product/overview.md`, new guide (to be confirmed by Claude Code)
- **Depends on:** WP1 to WP11 and WP13 to WP32
- **Size:** Standard
- **Done when:** the docs site builds, a search of the docs finds no page that still says plugins all run inside the server, providers are code, TVDB is coded, AI features are switched on under Local AI, or cover-art checking uses AI, and the guides match the screens.

### WP23 — Notification service core
- **What it does:** One service with an event catalogue, per-event routing, grouping and rate limits, quiet hours, channel health (D-O). Built-in channels: in the Dashboard and email (SMTP, settings as today). Moves the password-reset email onto it. Settings page for routes and a Send test button per channel.
- **Files likely touched:** `PasswordResetEmailSender.cs`, new notification service and endpoints, Dashboard notifications page, event catalogue, Contracts and fixtures (to be confirmed by Claude Code)
- **Depends on:** WP1, WP14, WP15
- **Size:** Complex
- **Done when:** a failed backup, a missing tool and a broken plugin each reach an administrator through the chosen channel and not a non-administrator; a channel that fails repeatedly switches itself off and shows Needs attention; password reset still works.

### WP24 — Notification channel plugins
- **What it does:** Lets channel plugins (JSON-only for webhook-style and Apprise-style gateways; separate programs for anything needing code) register as channels with the same routing and limits; a sample JSON channel ships as Official.
- **Files likely touched:** WP17 declarative loader (notification routes), notification service, plugin docs (to be confirmed by Claude Code)
- **Depends on:** WP17, WP23
- **Size:** Standard
- **Done when:** a channel added as a JSON file alone delivers a test message; its login is in the secrets store; it appears in the routing page.

### WP25 — Backup destination contract, schedule, retention, encryption
- **What it does:** Adds the destination plugin contract (test, send, list, fetch, delete, free space), the backup schedule and "keep the last N", passphrase encryption of archives before they leave the machine, intact-copy verification, the monthly restore check, restore from a destination, and a Backup destinations section inside Backup and recovery (D-P). The local folder becomes the first built-in destination.
- **Files likely touched:** `DatabaseBackupService.cs`, `SqliteBackupTarget.cs` (becomes a real interface), `SystemEndpoints.cs`, backup Contracts, Dashboard Backup and recovery page (to be confirmed by Claude Code)
- **Depends on:** WP3, WP13, WP14, WP23
- **Size:** Complex
- **Done when:** a scheduled backup runs, is encrypted, copied to a test destination and verified; the oldest are removed beyond the limit; restore from the destination restores a test library after a data store wipe; a wrong passphrase fails with a clear message; the file the destination holds cannot be opened without the passphrase.

### WP26 — First backup destinations
- **What it does:** Official built-ins for another folder and a network share; Official plugins for S3-compatible storage and WebDAV. Each shows its state card, permissions (the address it contacts) and keeps logins in the secrets store.
- **Files likely touched:** new Official plugin projects, plugin registration, licence notices for any library used (AGPLv3 check) (to be confirmed by Claude Code)
- **Depends on:** WP25
- **Size:** Complex
- **Done when:** a backup copied to a network share and to a test S3-compatible store restores correctly; each destination's address and permissions show at approval.

### WP27 — Local AI engine room (version 3)
- **What it does:** Builds the engine room (D-Q, D-S, D-U): an ability contract (write text quick/careful/long, pull out structured facts, classify, read text from images) that maps each ability to an engine (the text model, Tesseract), so callers never name a model; one queue with the priority order person > ingestion > Library background > plugins, which ingestion-time AI and plugin calls join; per-plugin request and time budgets with Needs attention when exceeded; durable usage records (who, which ability, when, how long, result) using the run records from WP3; real readiness checks in place of the hard-coded "ready"; removes the unread "max at once" setting. Tesseract and FFmpeg stay tools that work with Local AI off.
- **Files likely touched:** `src/MediaEngine.AI/Infrastructure/` (`AiFeatureGate.cs`, `ModelLifecycleManager.cs`, `AiModelSelectionAdvisor.cs`, `AiModelCatalogDefaults.cs`), `src/MediaEngine.Api/Services/BackgroundAiAdmissionController.cs`, `src/MediaEngine.Api/Services/Plugins/PluginAiClient.cs`, `PluginExecutionContext.cs`, `src/MediaEngine.Domain/Enums/AiModelRole.cs`, `config/ai.json`, `AiSettingsValidator.cs`, OCR service entry point, storage schema, AI tests (to be confirmed by Claude Code)
- **Depends on:** WP3, WP13
- **Size:** Complex
- **Done when:** a test shows an ingestion-time AI request is served before a waiting plugin request and a person's request before both; a plugin over its budget is refused with Needs attention; every AI and text-reading request leaves a usage record; with the model files missing the readiness check reports unavailable instead of ready; document text reading still works with Local AI off.

### WP28 — AI features as Official plugins (version 3)
- **What it does:** Moves the AI features behind the plugin contract as Official built-ins (D-R): Smart labelling and Type helper through a new ingestion-assist hook point (their non-AI fallbacks run when the plugin is off); Series order, Vibe tags and Description insights (with the short summary folded in and the dead TL;DR switch removed) as scheduled plugins using the plugin scheduler (WP15); Taste summary with a switch and a "reads what people watch" permission. Their output is recorded as coming from the plugin and marked AI, ranked below providers and people, and removed with the plugin (D-T). Vibe vocabulary moves into the vibe tags plugin's settings. Removes the code in D-V. Adds the missing tests (vibe tagging, description insights, type helper).
- **Files likely touched:** `src/MediaEngine.AI/Features/`, `src/MediaEngine.Api/Services/VibeBatchService.cs`, `SeriesAlignmentBackgroundService.cs`, `DescriptionIntelligenceBatchService.cs`, `src/MediaEngine.Ingestion/IngestionEngine.ProcessingStage.cs`, `src/MediaEngine.Ingestion/Services/MediaTypeResolver.cs`, `src/MediaEngine.Storage/CanonicalValueRepository.cs` (AI writes), `src/MediaEngine.Plugins/PluginContracts.cs`, `src/MediaEngine.Plugin.MediaSegments/MediaSegmentPlugin.cs`, `src/MediaEngine.AI/Whisper/`, `src/MediaEngine.Processors/WhisperSyncService.cs`, `PromptTemplates.cs`, `TuvimaAiServiceCollectionExtensions.cs`, `TuvimaHostedServiceCollectionExtensions.cs`, `config/ai.json`, tests (to be confirmed by Claude Code)
- **Depends on:** WP2, WP15, WP27
- **Size:** Complex
- **Done when:** the ingestion comparison against WP0 shows the same results with all AI plugins off and the expected results with them on; switching off or removing an AI plugin removes its suggestions and leaves provider and person values untouched; the Plugins page lists the six AI plugins with their abilities; no AI switch remains in `config/ai.json`.

### WP29 — Show AI results (version 3)
- **What it does:** Carries vibes, themes, moods, the short summary and content warnings through Contracts to the item's detail page, marked AI, and adds vibes and themes as browse filters (D-T). Follows `docs/product/presentation-rules.md` and the Dashboard visual checks.
- **Files likely touched:** Contracts detail and browse types and fixtures, detail page components, browse filter components, `WorkViewModel.cs`, read services (to be confirmed by Claude Code)
- **Depends on:** WP28
- **Size:** Standard
- **Done when:** an item with AI results shows them on its detail page with an AI mark at desktop, tablet and phone sizes; filtering by a vibe returns matching items; with the plugins off nothing AI shows.

### WP30 — Local AI Settings page (version 3)
- **What it does:** Rebuilds the Local AI page as Status, Model and hardware, Budget and Usage on the Settings frame and state card (D-Q); removes the Features, Vocabulary and Automation sub-pages; Usage links to the AI plugins on the Plugins page.
- **Files likely touched:** `LocalAiSettingsTab.razor`, `AiFeaturesTab.razor`, `AiScheduleTab.razor`, `VibeVocabularyTab.razor`, `SettingsNav.cs`, AI Contracts and fixtures (to be confirmed by Claude Code)
- **Depends on:** Settings revamp frame (its WP2 to WP4), WP1, WP27
- **Size:** Standard
- **Done when:** the Local AI menu tree shows exactly Status, Model and hardware, Budget and Usage; Usage lists each plugin's ability use; no AI feature switch remains on the Local AI page.

### WP31 — Integrations: one event list, key warnings, health (version 3)
- **What it does:** Replaces the webhook-only event list with the one event list shared by webhooks, notifications and plugin event subscriptions (D-W); suggests an expiry when a key is issued and shows Needs attention for keys unused for 90 days or near expiry; gives each integration the four health words, with "failing repeatedly" for webhooks (D-D).
- **Files likely touched:** `src/MediaEngine.Api/Services/Events/ApplicationEventRegistry.cs`, notification event catalogue (WP23), plugin event subscriptions (WP15), `ApplicationAdministrationService.cs`, `ManagedAccessApplications.razor`, `ManagedApplicationWebhooks.razor`, Contracts and fixtures (to be confirmed by Claude Code)
- **Depends on:** WP4, WP15, WP23
- **Size:** Standard
- **Done when:** a new event added once appears for webhooks, notification routes and plugin subscriptions; a key unused for 90 days shows Needs attention; a webhook failing repeatedly shows Broken and shades the menu red.

### WP32 — Administrator check on development reset actions (version 3)
- **What it does:** Requires an administrator for the development-build reset, wipe, factory-reset and seed actions (D-X), matching the real-media actions beside them.
- **Files likely touched:** `src/MediaEngine.Api/DevSupport/DevSeedEndpoints.cs`, `src/MediaEngine.Api/DevSupport/IntegrationTestEndpoints.cs`, Engine tests (to be confirmed by Claude Code)
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** in a development build, a signed-in non-administrator calling a reset or seed action is refused and an administrator succeeds.

### Waves (checkpoint after each: build, tests, ingestion comparison against the WP0 baseline, independent review)
1. **Foundation:** WP0, WP13 (two at once), then WP3, WP32; WP1 as soon as the Settings revamp frame is built.
2. **Trust and safety:** WP14, WP2, then WP5, WP9.
3. **Platform services:** WP15, WP27, then WP4, WP6.
4. **AI and plugin kinds:** WP10, WP28, then WP17, WP18.
5. **Surfaces:** WP7, WP16, then WP29, WP30.
6. **Lifecycle and providers:** WP19, WP20, then WP21.
7. **Notifications, events and backups:** WP23, WP25, then WP24, WP31, then WP26.
8. **Close-out:** WP8, WP22, WP11 (final regression), WP12 (docs).
Each wave runs at most two packets at once; the Settings revamp frame (WP2 to WP4 there) must be built before WP1 and WP30.

## 7. Risks and open questions

- **Risks:** This is a very large change (eight Complex packets). Mitigation: waves with checkpoints (D-N); the ingestion comparison after every wave. The WebAssembly tier depends on a licence check that may fail (WP21); then it stops for Shy's decision and the rest is unaffected. Plugins load in-process and unsandboxed, so the approval note is honesty, not protection; a bad plugin can still do harm once approved. Mitigation: only approved sources, clear wording, audit record. More red shading could alarm people if a webhook fails once; D-D requires repeated failure. Version 3 adds six packets (two Complex). Moving Smart labelling and the Type helper behind a plugin hook touches ingestion; mitigation: the WP0 comparison with AI plugins off and on (WP28). Showing AI results adds Dashboard surfaces that must follow the presentation rules and visual checks (WP29).
- **Q14 (answered: a).** Notifications is a new sub-page under Manage > System, and Backup destinations sit inside the existing Backup and recovery page. This adds one page to the locked Settings menu (settings revamp D35); it takes the Manage > System tree's usual icon rules and the no-duplicate-child rule (D45).
- **Questions:** all answered by Shy on 2026-10-19; none open. Version 3 answers (Shy, 2026-10-19): Q15 all six AI features plus the Taste summary become Official plugins, including the two that run during ingestion (D-R); Q16 AI results show on the detail page marked AI, and vibes and themes are browse filters (D-T); Q17 the Taste summary becomes a plugin with a "reads what people watch" permission, off by default (D-R); Q18 the speech-sync stub and image-AI placeholder are removed until a feature needs them (D-V); Q19 outside software gets no Local AI access (D-W); Q20 an AI runtime elsewhere on the home network is a later direction, home network only, never cloud (not built); Q21 the development reset actions require an administrator, built in wave 1 (D-X, WP32); Q22 OPDS, an AI-assistant connection and scrobbling are recorded as future Integration and plugin directions only; Q23 Tesseract stays the standard text reader, as a tool served through the abilities contract and not switched off with Local AI; an AI reader for handwriting and hard pages is a possible later opt-in plugin (D-S). Summary of answers: Q1 keep Integrations separate with the shared model (D-A); Q2 approval rule for all four areas (D-C); Q3 red for repeatedly failing items (D-D); Q4 hide the Enrichment Tester, it is temporary and goes at beta (WP5); Q5 and Q6 the live directory, official plugins in the main repository and community plugins in a separate repository later (D-F); Q7 and Q10 community listed, with the four trust levels (D-H); Q8 three plugin kinds (D-G); Q9 Live TV only has to remain possible (D-I); Q11 this spec sets direction and the platform build is a follow-up spec; Q12 full provider move as the direction, with conditions (D-J); Q13 existing plugins unchanged and keys stay in the config folder (D-K); version 2: whole platform in one planned implementation (D-N), notification service (D-O), backup destinations (D-P), Q14 placement as above.

## 8. Gaps and drift

- Developer Tools: code has three separate top-level items (including a DevHarness not mentioned in the revamp), not one group with a sub-page.
- Enrichment Tester talks to debug actions mapped only in development builds. *(v3 correction: those debug actions do require an administrator; it is the development reset, wipe, factory-reset and seed actions beside them that accept any signed-in user; see D-X and WP32.)*
- Plugin job state is in memory only; the plugin schedule key is not in the settings model.
- Webhook delivery rows are stored but no screen or action shows them.
- Four separate health models and five separate on/off flags; no approval step for AI features or credentials beyond creation.
- Approved plugin catalog contains no entries.
- *(v3, now confirmed)* AI model files live in the models folder set in the AI settings (text models under `llama/`, the speech model under `whisper/`; an environment setting can move it); plugin enabled state lives in one small settings file per plugin in the library's data folder, default off; the DevHarness's real-media actions check for an administrator but its reset and seed actions do not (D-X).
- *(v3)* Local AI, Integrations and Developer Tools gaps are listed in Appendix B section 6 and Appendix C section 4.

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
| OCR (Tesseract) | Interface, tied to Tesseract | *(v3)* Stays a tool; served to plugins as the "read text from images" ability (D-S, Q23) |
| AI features | Interface plus flags | *(v3, superseded)* The AI runtime stays core as the engine room (D-Q); the AI features become Official plugins (D-R) |
| Backup targets | One internal class, no interface | Good future fit (cloud, NAS) once an interface exists |
| Webhook event list | Hard-coded | Stays core |
| Transcoding, casting | Config plus single implementation | Stays core |
| Naming / organisation templates | Per-library template | Good fit as declarative presets |
| Ratings, OPDS reading feeds, geocoding / map tiles | Not found in code | New plugin territory |

---

## Appendix B: Local AI review

Research date 2026-10-19 (version 3). Facts read from our code by code scouts; inferred points are marked (inferred). This is the Local AI behaviour review (planning item I) the first-run prerequisites spec deferred.

### 1. What runs today

| Part | Today |
|---|---|
| Text model | One local text model in three sizes chosen by a hardware check: Essential (about 0.6 GB), Standard (about 1.3 GB, default), Advanced (about 2.5 GB, needs a passing hardware check). All AI "roles" (quick, careful, scholar, CJK) use the same model file with different limits. |
| Speech model | Optional audio pack (about 1.5 GB), off by default. Nothing in the Library uses it yet. |
| Image, "find similar" | None. A plugin asking for image AI is refused. |
| Where files live | A models folder set in the AI settings; download links are written in code, not in the shared tool-download list. |
| Running requests | One at a time. Background AI waits while someone is using the app, during first run and when the machine is busy; a person's request cancels background work. The "max at once" setting is never read. |
| Ready checks | "Ready" flags are hard-coded to yes; the model state is checked, the runtime is not (inferred). |
| Schedules | Series order 3am, vibe tags 4am, description insights every 15 minutes. Runs leave no record; only per-item results are stored. |
| Plugin access | In-server plugins declare AI roles in their manifest and are checked per call. No shipped plugin uses AI. |

### 2. The AI features

| Feature | What it does | When | Writes | Shown? |
|---|---|---|---|---|
| Smart labelling | Reads messy file names into title, author, year, season, episode | During ingestion (not for audiobooks or music); falls back to filename rules when off | Ingestion's working values | Indirectly (better identification) |
| Type helper | Decides an unclear media type | During ingestion, only when the type is unclear; "unknown" when off | Ingestion's working choice | Indirectly |
| Series order | Suggests a position in a series | Nightly | Overwrites an AI-owned value (person and provider values protected) | Yes, as series position |
| Vibe tags | Picks 3 to 5 mood words from a fixed list | Nightly, newest 200 items with a description | Overwrites an AI-owned list | **No screen shows it** |
| Description insights | Themes, mood, setting, period, audience, pace, content warnings, short summary, people | Every 15 minutes | Overwrites AI-owned values; people are added as records | **No screen shows themes, moods or the short summary** (inferred from search) |
| TL;DR switch | Meant to control short summaries | Never checked | Nothing | The Settings page lists it with the wrong model role |
| Taste summary | One or two sentences about what a profile enjoys | Every time a profile overview is opened; **no switch** | Nothing | Only the genre chart is shown |

Not AI, although near it: cover-art checking (a picture fingerprint), commercial skip and segment detection (FFmpeg), Fandom lore, narration phrases, person biographies.

### 3. Placeholders that cannot run

Speech transcription has no caller and the speech sync is a stub with an unregistered background service; the media-segments plugin's "AI visual check" asks for image AI, which does not exist, so it always reports degraded; a Wikidata tie-break prompt has no caller.

### 4. Text reading (Tesseract) and the View page

PDFs with real text, Word, Excel, PowerPoint, OpenDocument text and RTF are read by built-in readers without Tesseract. Tesseract is used only for scanned PDF pages (page by page: pages with fewer than 25 characters of real text) and for images. When text recognition is on and installed, **every** image indexed in View is read, because a photo of paper cannot be told apart in advance. Without Tesseract, scanned pages and photos stay in View and open normally; only their words are not searchable and word-based document rules do not match. Tesseract is small, fast on ordinary computers, accurate on print and never invents text; an AI model that reads images is better at handwriting and messy photos but is far larger and slower and can invent text, which is harmful for search. Decision: Q23 and D-S.

### 5. The model chosen

Local AI is the engine room (D-Q): it owns runtimes, models, abilities, budget, queue, privacy and the usage record. Everything that uses AI is a plugin (D-R) and asks for abilities (D-S), never a model. Four rules make it hold: plugins ask for abilities; the Library works with no AI; AI results are labelled suggestions (D-T); the engine room owns fairness (D-U). Tools (Tesseract, FFmpeg) share the abilities contract but are not AI for the person.

What this makes possible later (direction only): a spoiler-safe "previously on" recap from a person's progress; natural-language search and smart collections from themes and vibes; "more like this" across formats (needs a find-similar runtime and a licence check); subtitles for videos without them and audiobook and ebook sync (speech); photo search by content (image AI, opt-in); kids' profiles filtered by content warnings; AI suggestions sent to Needs Review for a person to approve; comic panel text and translation; an AI reader for pages Tesseract struggles with. An outside AI assistant asking about the library is an Integration with a read-only key, not a plugin.

### 6. Gaps and drift found

- Ingestion-time AI and plugins skip the background queue (fixed by D-U, WP27).
- The "max at once" setting is never read; settings validation forces it to one (WP27 removes it).
- Ready flags are hard-coded to yes (WP27).
- The TL;DR switch controls nothing and the Settings page shows the wrong role for it (WP28, WP30).
- Taste summary uses AI with no switch on every profile overview (WP28).
- Vibes, themes, moods and the short summary are computed but not shown (WP29).
- AI results overwrite values instead of being recorded as labelled suggestions (WP28).
- No run records for AI schedules (WP3, WP27).
- No direct tests for vibe tagging, description insights or the type helper (WP28).
- Docs drift (WP12): the AI architecture page says every AI call uses grammar-constrained output (it is optional) and that cover-art checking uses AI (it does not); code comments say roles map to files in the AI settings and that the CJK role is a different model.

---

## Appendix C: Integrations and Developer Tools review

Research date 2026-10-19 (version 3). Facts read from our code by a code scout.

### 1. Integrations today

- **Keys:** outside apps get a key shown once; only its fingerprint is stored. 66 permission scopes in 7 presets (media player, monitoring and analytics, home automation, metadata integration, read-only, administrator, custom). Keys can expire, but the Dashboard issues them without expiry; last use is recorded; keys can be revoked, rotated, or the app disabled or deleted. Only administrators (or an app holding the app-management permission) manage them. A "secured account" check on these actions does nothing yet.
- **Webhooks:** per app, signed, for 17 fixed events (playback, library items, ingestion, metadata and review, provider status, plugin jobs, system health). HTTPS only unless local network is allowed; private, loopback and cloud-metadata addresses are guarded. Up to six attempts over about three hours; deliveries older than a day expire and finished ones are pruned after seven days. The Dashboard shows the last status only: no delivery history and no Send test.
- **Other ways in:** a live event feed for apps, device pairing for the phone and TV apps, OpenID Connect and OAuth sign-in, and a plugin-services door for apps. Not present: OPDS reading feeds, Kodi, Jellyfin or Plex-style access, scrobbling, calendar or RSS feeds, an AI-assistant connection.
- **AI:** a "use AI" permission exists for apps but is deliberately marked unavailable.

### 2. Developer Tools today

Three separate menu items (DevHarness, Provider Tester, Enrichment Tester), only in development builds. Provider Tester uses the normal search actions. Enrichment Tester uses debug actions that require an administrator. DevHarness resets, seeds, rescans, verifies real media and factory-resets; its real-media actions require an administrator, but **reset, wipe, factory-reset and seed accept any signed-in user** in a development build (D-X).

### 3. Plugins, notifications and backups (facts behind v2)

Outside plugins are folders with a manifest in the library's data folder, loaded into the Engine with no isolation; enabled state is one small settings file per plugin, default off; plugin job state is in memory; the segment schedule is read from a maintenance setting that is not in the shipped settings file; the plugin directory address points at an empty list. The only outgoing message is the password-reset email (with a test email); there is no notification centre. Backups are manual only, with no schedule or retention, exclude secrets, and restore safely at the next start.

### 4. Gaps and drift found

- No webhook delivery history or Send test (WP4).
- Keys issued with no expiry and no warning for unused keys (WP31).
- Three separate event lists planned (webhooks, notifications, plugin events) (WP31).
- Development reset actions accept any signed-in user (WP32).
- The "secured account" check on key actions does nothing (recorded; belongs to Users and Sign-in).
- Secrets live in two folders: provider keys in one, sign-in, email and service logins in another (WP14 settles one place).
- A development-only localhost sign-in bypass exists, off by default (recorded against CLAUDE.md rule 7; separate follow-up).

---

## Plain English summary

- **One look and one rule.** Local AI, Plugins, Integrations and Developer Tools get the same status card and the same four health words. Turning anything on for the first time shows what it can do and whether anything leaves your machine, and records your answer.
- **Plugins get safer and more varied.** Three kinds: plain JSON files (no code), separate programs in any language, and sandboxed code later. Community plugins no longer run code inside the server. Tuvima's own plugins keep working unchanged.
- **A plugin directory.** A list of known plugins kept on GitHub, labelled Official, Verified, Community or Private, with a blocklist that can switch a bad plugin off. Official plugins live in the main repository; community plugins get their own repository later.
- **Every metadata source becomes a JSON file**, including TVDB, so swapping one source for another is a file change. A full ingestion of every media type before and after proves nothing changed. API keys stay in the config folder, so wiping the database keeps them.
- **Plugins can use Local AI** safely on your machine, with limits so they cannot slow down scanning.
- **Smaller fixes.** Webhooks show delivery history and a Send test button; plugin job history survives restarts; the temporary Enrichment Tester is hidden when it cannot run.
- **A notification service** decides who is told what and how (Dashboard, email, channel plugins), with quiet hours and limits, keeping titles off the wire by default.
- **Backups can go off the machine.** A schedule, keep-the-last-N, encryption with your passphrase before anything leaves, copies to another folder, a network share, S3-compatible storage or WebDAV, and restore from any of them.
- **The whole plugin platform is built here**, in seven waves with a checkpoint after each (build, tests, before/after ingestion comparison, independent review).
- **Live TV** is not being built, and nothing here blocks it.
- **Local AI becomes the engine room** (version 3). It runs the models, keeps the machine fair (your use first, then scanning, then background work, then plugins) and records who used AI. Its page shows Status, Model and hardware, Budget and Usage.
- **Every AI feature becomes a plugin**, including the ones that ship with the Library, so AI is always optional. Plugins ask for what they need done ("summarise", "read text from this image"), never for a particular model.
- **AI results are shown and labelled.** Vibes, themes and short summaries that are worked out today but never shown appear on the detail page marked AI, always below real provider data and anything you typed, and disappear if you remove the plugin.
- **Tesseract stays** as the reader for scanned pages and photos of paper; document search keeps working with AI off.
- **Integrations and Developer Tools get tidied**: one shared list of events, warnings for old or unused keys, health on every connection, and an administrator check on the development reset buttons.
- **Documentation** is updated everywhere it described the old behaviour.

