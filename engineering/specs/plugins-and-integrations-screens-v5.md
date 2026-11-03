# Spec: Plugins and Integrations screens (version 5: every plugin page drawn from the plugin's own JSON)

> **Status:** Locked · **Version:** 5 · **Last updated:** 2026-11-03 (locked by Shy, "lock it", with the recommended answer to the open per-plugin log questions) · **Related docs:** Plugins and Integrations screens v4 (`engineering/specs/plugins-and-integrations-screens-v4.md`, Built, awaiting Local checks) · Plugin architecture (`engineering/specs/plugin-architecture.md`, Built) · Design system · Local check of 2 Nov (`engineering/validation/local-check-2026-11-02.md`, the Plugins item) · Mockups: `engineering/design/mockups/plugins-v5.html` 

> **Version 5 change note (2026-11-03):** v4 is built. Shy walked it on 2 Nov and found three things: some tiles had "Configure" and others did not; the health marker was too small and moved around; and the plugin pages are still partly hand-made, one way for each kind of plugin. This version changes the tile footer (a gear on every tile and a bigger fixed tick), makes **every plugin page come from the plugin's own JSON** with no page written for one plugin, lets an administrator **read and edit a plugin's JSON in Settings with checks before saving**, structures and colours "What it provides", groups the All tab by when plugins run, and adds **Restore official plugins**. v4 decisions D31 to D42 stand except where named below. This file adds D43 to D58 and work packets WP22 to WP31.

## 1. Goal

A plugin author describes a plugin once, in JSON, and Library shows it the same way as every other plugin, with no Dashboard code written for that plugin. An administrator can see at a glance whether a plugin works, always knows which control opens its settings, and can read or fix the plugin's file without leaving Library and without being able to save a broken one.

Why this serves the product: **Maintenance** (one page, not one per plugin: today three hand-made cards exist for three plugins), **Extensibility** (a new plugin, including one a person writes for themselves, gets a full settings page for free), **Reliability** (a file is checked before it is saved), **Ease of use** (every tile and every page behave the same).

## 2. Scope

**In**
- Tile and list footer: gear on every tile, larger fixed health tick, "Key missing" pill only when needed (D43, D44).
- The All tab in three headed sections by when plugins run (D45).
- Plugin page: one generator that builds every card from the plugin's JSON; the extended settings vocabulary; the two JSON formats read through one path (D46 to D52).
- "What it provides" as a structured, coloured list (D53).
- Plugin file card: view, edit, check, review, save, reset (D54 to D56).
- Restore official plugins (D57).
- Official plugins that are compiled in describe their settings in a shipped JSON file like the rest (D58).
- A test that fails the build if the Dashboard names a plugin (D52).
- Docs: a "Writing a plugin's settings" guide on the public docs site.

**Out**
- Installing plugins from outside (community plugins stay parked). Editing a plugin's network hosts or permissions (D55).
- Anything on the Integrations side beyond reusing the tile footer (D43).
- Where a plugin is *used* (stays in Ingestion Flow, Lyrics, Subtitles).
- Changing what any plugin does.

## 3. Decisions made

Decisions marked **(proposed)** are my recommendation and are not Shy's yet. The ones with a question in section 8 wait for an answer. Everything else follows Shy's words in the 2 Nov review.

- **D43 — The tile footer (replaces v4 D29's "button becomes the fix" and D30's "one button").** A tile says: logo, name, switch, one sentence, and a footer. Footer, right to left: a **gear button** (every tile, always, opens the plugin's page), then the **health tick**; on the left, a **"Key missing" pill** only when a key is missing (Shy: "key missing pill only when needed"). The gear is always the fix action; when something needs the administrator it turns amber, and the plugin page opens with the card that needs attention open and highlighted. There is no "Configure", "Add key" or "Install tools" text button. Missing tools and Waiting for Local AI are told by the tick's sentence and by the page, not by extra pills (v4 D32 stands: switch locked, purple sparkle logo).
- **D44 — A bigger tick in a fixed place.** The 10 px dot (v4 D29) becomes a 22 px round tick in the same spot on every tile and row: green ✓ working, amber ! needs something from you, red ✕ broken, grey – off. Hover or keyboard focus gives the sentence ("Working · checked 4 minutes ago"). Never colour only. Its place is the bottom left of the tile footer and the gear is bottom right, so each always sits in the same place from tile to tile (Shy, 2026-11-03, Q1 answered B). The "Key missing" pill sits beside the tick. Plugin page header shows the same tick. List view: Health and Settings are two fixed narrow columns at the right (v4 D40 otherwise unchanged).
- **D45 — Group by when plugins run (Shy, 2026-11-03: needs more detail).** Each section gets a one-line explainer, a count and a health summary; a plugin appears once. On the All tab the grid has three headed sections (Getting items in, After import, Accounts & lists) instead of one flat grid. The other tabs show their own single section. Uses the categories of v4 D31 unchanged.
- **D46 — One page, built from the plugin's JSON.** The plugin page is produced by one generator in the Dashboard from a **page model** the Engine builds per plugin. Standard cards come from facts the JSON already holds: **Access** (from the plugin's declared secrets and sign-in), **What it provides**, **Settings**, **Privacy** (from the declared network destinations and what they send), **About**, and the **Plugin file** card. A plugin never supplies page layout, only content. Cards a plugin has nothing for are absent (v4 D33 order stands, Plugin file is last).
- **D47 — One settings vocabulary, extended in place.** Settings stay in the plugin's existing `settings_schema` (nobody learns a second format). It gains the following; each is optional and has a plain default.
  - Control types: **toggle**, **choice** (with labels, not just values), **number** (with unit, minimum, maximum; a slider when the range is small), **text**, **url**, **path**, **secret**, **list of choices**, and **group list** (a repeating group, D49).
  - Wording: `title`, `description` (help under the control), `placeholder`, `unit`, `group` (heading inside Settings), `order`.
  - Behaviour: `advanced` (goes in the collapsed Advanced section), `read_only`, `show_when` (show only while another setting has a given value), `requires` (needs a tool, Local AI or a key; the control is greyed with the reason).
  - Scope: `scope: household` (default) or `profile` (each person sets their own, for example a ListenBrainz token; Shy's household-trust decision stands).
  - Unknown words are ignored and reported in the Plugin file card, never an error on the page.
- **D48 — Secrets and links in the same place.** Today a plugin's keys are described twice: once by the manifest's `secrets`, once by the provider file's `onboarding.credentials`, and the page has separate code for each. Both formats feed one **credentials** list the Access card reads (name, label, help, minimum length, pattern, profile or household, sign-in method). The signup, help, terms and privacy links in `onboarding` feed the Access and Privacy cards the same way.
- **D49 — Repeating groups and row actions.** A **group list** setting declares what one item looks like (its fields), an `item_title`, an `add_label`, which items are `locked` (cannot be removed) and `row_actions` from a fixed list (**test connection**, **remove**, **check now**, **open link**). This replaces the hand-written servers card for StashDB; the default StashDB server is locked in its JSON.
- **D50 — Test and check actions are declared.** A plugin's JSON names the test it supports (**test connection**, **check now**, **dry run**) and the Engine runs it; the page shows the standard button and result. A plugin that declares none shows none.
- **D51 — Provider source files join the same path.** The source files in the providers folder (TMDB, TheTVDB, LRCLIB and the others) and the plugin manifests are two formats today and the page has two code paths (one for sources, one for the rest). The Engine reads either into the same page model; their tunables (weight, rate limit, cache time) appear as Advanced settings through the same vocabulary. Which of the current source-file settings are already shown is to be confirmed in WP22.
- **D52 — The Dashboard may not name a plugin.** A guardrail test fails the build if Dashboard plugin code compares a plugin's id or name to a string, or if a plugin has a Razor component of its own (today `StashBoxSettingsCard` and the id check in the About card are the examples). Fix the code, not the test.
- **D53 — "What it provides" is structured and coloured (Shy: "more structure, uniformity and colour matching the app").** Each of the eleven kinds (Identification, Details, Artwork, People, Series order, Lyrics, Subtitles, Skip markers, Insights, Lists & ratings, Account sync) has one fixed icon and one accent colour from the app's colour tokens, shown as a small card: coloured icon, name, one plain sentence of what it means, and the media types it covers as small chips. The sentence per kind lives once in the Engine list, not in each plugin. Tiles show the coloured icons only; the list view shows icons with names. The "Used in … · Open Ingestion Flow" line stays under the cards.
- **D54 — The Plugin file card.** For plugins defined by a file (the source files, declarative plugins, private uploads) the card shows the file name, "valid" or the count of problems, whether it differs from Tuvima's version, and two buttons: **View file** (read-only, coloured, with line numbers) and **Edit file**. Compiled-in plugins get a read-only **View file** of the shipped JSON that describes them (D58). Editing is for administrators on a desktop with a fine pointer (the same rule as editing items); the Engine's permission check still decides.
- **D55 — Editing is safe by construction.** The editor follows the item editor's pattern: a sticky bar (Cancel, Reset to Tuvima's version, Save changes); checks run as you type; **Save changes stays off until the file is valid**; pressing it shows **Review changes** (what differs, line by line) before anything is written. Checks have two layers: the structure (a published schema file for each plugin kind) and Library's own rules (the same host, permission and range rules the Engine already applies on install). Errors say the line and the reason in plain words; "things to know" (such as a longer cache time) do not block. **Locked fields** cannot be changed in this editor in the first version: id, kind, the provider id, network hosts and permissions; those need the install-time approval flow, so a change cannot quietly widen what a plugin may send out. After saving, the Engine reloads the plugin and runs its check; if that fails, the page says so and offers Reset. The shipped copy of each file is kept apart so Reset, validation baseline and Restore (D57) always have Tuvima's version.
- **D56 — What a saved edit does to the rest.** Keys and settings values are stored apart from the file (secrets folder, data store), so editing or resetting a file never loses them. A removed setting's saved value is kept quietly in case it returns.
- **D57 — Restore official plugins (Shy, 2026-11-03: yes, innocuous and logically placed, possibly a setting).** A quiet row in a Settings page (page to be settled; not on the Plugins tiles), administrators only, opens a short dialog that lists each Tuvima plugin file that is missing, damaged or edited, with "will be restored" or "will be reset", and the count that already match. Confirming restores them. Keys, switches, settings values and Private plugins are untouched. This sits beside v4 D37's quiet repair at start-up (which stays): the link is for the case where the person edited something or wants to see what happens.
- **D59 — Activity is the standard timeline (Shy, 2026-11-03: locked with the recommended answers).** The Activity tab shows the plugin's slice of the app's standard activity timeline, failures first, routine successes collapsed; Check now merges into Test connection; a plugin logging setting caps size per plugin (default 200 entries or 14 days each, whichever comes first); the tab is always shown, with the empty state "Nothing has gone wrong."; Health & Jobs panel dropped.
- **D58 — Compiled-in plugins describe themselves in JSON too (Shy, 2026-11-03: yes where everything can run via JSON).** The plugins written in .NET (ListenBrainz, Commercial Skip, Fandom Lore, Media Segments, the AI feature plugins) build their settings description in C# today. They move to a shipped JSON file beside the code, read through the same path, so the "no page written for one plugin" rule is true for all of them. Their behaviour does not change.

Carried over from the 2 Nov review and already settled by v4, so no new decision: the purple sparkle logo box, the Needs Local AI wait and the Show filter menu (D32, D41). D45 only adds the "Needs Local AI" tick to Show if it is not there yet.

## 4. UI behaviour

- **Tile / list:** see D43 to D45 and mockup sections 1 to 3. Empty, loading and error states of the page are unchanged from v4.
- **Plugin page:** header (logo, name, the tick, one status sentence, the switch), tabs Set up and Activity, cards in the D46 order. When opened from an amber gear, the card that needs attention (usually Access or the Requirements line) is open and highlighted. Settings: Save changes shows Review changes first (unchanged). Advanced is collapsed.
- **Group list:** each row has its title, state, and the row actions; an Add button sits below. A row with a missing key says "Key missing" and shows Add key.
- **Plugin file:** View opens a read-only panel in place; Edit turns the card into the editor with the bar from D55, line numbers, problems on the right and under the text, and Review changes before saving. Reading and editing are desktop only; on phone and tablet the card shows the file name and state, and the line "Editing is available on a computer".
- **Restore official plugins:** a dialog (mockup section 9); empty state "All official plugins match."
- **Phone / tablet / TV:** tiles keep the same footer; the page stacks cards one column; TV and native clients do not show Plugins (v4 stands).
- **Wording:** "Official", "Private", Library, Local AI, StashDB as in v4. No toasts: the saved row glows.

## 5. Data / model changes

- **Plugin page model (Engine → Dashboard):** per plugin, an ordered list of cards and, for Settings, the full control list with current values. Defined in Contracts with fixtures and tests (wire names are a contract).
- **Settings vocabulary:** the extra words in D47 added to the existing settings description; one published schema file per plugin kind, used by the editor's checks and by the docs.
- **Credentials list:** one shape that both the manifest `secrets` and the source-file `onboarding.credentials` map to.
- **Shipped files:** a pristine copy of each shipped plugin file is kept in the install folder (not in the data store); Reset and Restore read from it.
- **New JSON files:** shipped description files for the compiled-in plugins (D58).
- Existing libraries: pre-release, no migration; a reset is acceptable. Saved keys and settings keep working because their names do not change.

## 6. Work packets

Waves: **A** = WP22 ∥ WP23; **B** = WP24 ∥ WP25; **C** = WP26 ∥ WP27; **D** = WP28; **E** = WP29 ∥ WP30; **F** = WP31. Each is built by one cloud thread, two helpers at a time.

### WP22 — Page model and extended settings vocabulary (D46, D47, D48, D49, D50, D51)
- **What it does:** First lists, for every shipped plugin, what the current hand-made code shows that JSON cannot yet say (the gap list, written into the build log). Then adds the D47 words, the credentials list, group lists and declared actions to the settings description; the Engine builds the page model from either file format; a published schema file per kind.
- **Files likely touched:** `src/MediaEngine.Plugins/PluginManifest.cs`, `src/MediaEngine.Api/Services/Plugins/PluginCatalog.cs` and a new page-model builder, `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs`, `src/MediaEngine.Contracts/Plugins/*`, `config/providers/*` (schema file location to be confirmed), tests and wire fixtures.
- **Depends on:** none
- **Size:** Complex
- **Done when:** a test builds the page model for TMDB, LRCLIB, StashDB, Trakt, ListenBrainz and a private declarative plugin and each has Access, Provides, Settings, Privacy and About content from JSON alone; the wire fixture test passes; unknown words are reported, not thrown.

### WP23 — Tile and list footer (D43, D44, D45)
- **What it does:** Gear on every tile and row, 22 px tick in the fixed spot, "Key missing" pill, amber gear with attention card opening, the three sections on the All tab, Health and Settings columns in list view. Removes the text fix buttons.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor` and its CSS, `AppStatusDot` (becomes the tick), tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a bUnit test renders every tile state (working, key missing, tools missing, Local AI wait, broken, off, private) with a gear and a tick in the same place; screenshots at desktop and phone width at the end.

### WP24 — One page generator (D46, D49, D50, D52)
- **What it does:** Replaces `ProviderDetail`, the Stash-box card, the app-details card and the generic settings panel with one generator that draws the cards from the page model, including group lists, secrets, links, declared test buttons and the Review changes flow. Adds the guardrail test.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/Plugins/*`, `PluginSettingsTab.razor`, a new guardrail test in the Web tests.
- **Depends on:** WP22
- **Size:** Complex
- **Done when:** the guardrail test passes; the TMDB, StashDB, Trakt and ListenBrainz pages show everything they showed before (a before/after list in the build log); no component names a plugin.

### WP25 — What it provides (D53)
- **What it does:** The coloured, structured cards; one sentence per kind in the Engine's list; colours from tokens; icons-only on tiles and icons-with-names in the list.
- **Files likely touched:** `Plugins/PluginProvidesLine.razor` and CSS, the Engine's provides list, tokens file if a colour is missing.
- **Depends on:** WP22
- **Size:** Standard
- **Done when:** a test shows all eleven kinds render with their own icon and colour token and sentence; screenshot of a page with several kinds.

### WP26 — Move every shipped plugin to the new vocabulary (D51, D58)
- **What it does:** Rewrites the settings of each source file and each manifest in the new words; adds the shipped JSON for the compiled-in plugins so their settings no longer come from C#; keeps behaviour identical.
- **Files likely touched:** `config/providers/*.json`, the plugin projects (`MediaEngine.Plugin.*`, AI feature plugins), tests.
- **Depends on:** WP22 and WP24
- **Size:** Standard
- **Done when:** a test loads every shipped plugin and its page model has no leftover C#-only settings; the existing plugin tests still pass unchanged.

### WP27 — Plugin file: view and checks (D54, D55 first half)
- **What it does:** The Plugin file card, the read-only coloured viewer, the check service (schema layer plus Library's own rules) with line numbers and plain messages, "differs from Tuvima's version", and the shipped-copy folder.
- **Files likely touched:** a new check service under `src/MediaEngine.Api/Services/Plugins/`, new Engine actions, the card in the Web project, tests.
- **Depends on:** WP22
- **Size:** Complex
- **Done when:** tests prove a bad type, a bad range, a locked-field change and a malformed file each give the right line and message and that a valid file gives none; the card shows state for a shipped file.

### WP28 — Plugin file: edit, review, save, reset (D55, D56)
- **What it does:** The editor with sticky bar, live checks, Review changes, save-then-reload-then-check, Reset to Tuvima's version, and the desktop-only gate.
- **Files likely touched:** the Web card and an editor component, Engine save/reset actions, tests including permission checks.
- **Depends on:** WP27
- **Size:** Complex
- **Done when:** a test cannot save an invalid file; saving a valid one reloads the plugin and keeps its keys and values; Reset restores the shipped file; a non-administrator cannot call the actions; phone width shows no editor.

### WP29 — Restore official plugins (D57)
- **What it does:** The link, the comparison against the shipped copies, the dialog, the restore. Reuses v4's start-up repair.
- **Files likely touched:** Engine plugin file service, Plugins page footer, tests.
- **Depends on:** WP27
- **Size:** Standard
- **Done when:** a test with one deleted and one edited file lists both, restores them, and leaves keys, switches and a private plugin untouched.

### WP30 — Public docs
- **What it does:** A guide in the tuvima-docs repository, "Writing a plugin's settings" (every word of the vocabulary with an example and the screen it makes), updates to the plugin reference and the Plugins page description; the "docs needed" box in the PR.
- **Files likely touched:** `content/products/library/…` in `tuvima-docs` (exact folder to be confirmed from its manifest), the manifest file.
- **Depends on:** WP22
- **Size:** Standard
- **Done when:** the docs site build passes its link and anchor check and the guide lists every vocabulary word the schema file defines.

### WP31 — Close-out
- **What it does:** Full CI-strength build and tests, security review of the editor and the save actions (no sign-off needed from Shy), update `engineering/README.md`, append Local checks to `engineering/validation/batch.md`, archive v4 when its Local checks are done.
- **Depends on:** WP23 to WP30
- **Size:** Standard
- **Done when:** CI is green and the batch rows exist.

## 7. Local checks

- **L1.** Open Plugins on SHY-DESKTOP: every tile has a gear at the bottom right and the tick sits in the same place on all of them; none says "Configure". A plugin missing its key shows "Key missing" and an amber gear; the gear opens its page at the key card. Failure: any tile without a gear, a tick that moves, or a pill on a healthy tile.
- **L2.** All tab shows three headed sections; the Show filter has a Needs Local AI tick.
- **L3.** Open TMDB, StashDB, Trakt and ListenBrainz pages: compare with the 2 Nov pages; nothing lost; StashDB servers can be added, tested and removed (the default cannot be removed).
- **L4.** What it provides shows coloured cards that look like the rest of the app, on a real page with several kinds.
- **L5.** Edit a plugin file: break a number and see the line and message, Save stays off; fix it, Review changes, save; the plugin still works and its key is still there. Reset puts Tuvima's version back.
- **L6.** Try the editor on a phone-sized window: no editing.
- **L7.** Delete one shipped plugin file by hand, press Restore official plugins: the dialog lists it and the plugin comes back with its key and switch unchanged.
- **L8.** Real keys: TMDB and TheTVDB Test connection from the new page.

## 8. Risks and open questions

**Risks**
- **A badly edited file could break a plugin.** Mitigated by checks before save, reload-then-check after save, Reset, and locked fields so a plugin's reach cannot widen. If the reload check fails the page says so at once.
- **The vocabulary could grow without end.** Mitigated by a fixed list in the schema file; a missing control type is a spec change, not a one-off.
- **Rewriting every plugin's settings touches a lot of files at once.** WP26 changes descriptions only and the existing plugin tests must pass unchanged; WP24 keeps a before/after list per page.
- **Two parallel formats (source files and manifests) are bridged, not merged.** Merging them into one file format is a larger job and is left for later; the page does not care.
- **Performance:** the page model is built per plugin on open, from files already in memory; no extra network calls. The editor's checks run in the Engine and are debounced.

**Open questions:** none. Answered by Shy on 2026-11-03: Q1 B (tick left, gear right), Q2 A (every file-based plugin editable; Reset copies back the pristine file that ships with Library, not a git pull), Q3 yes (a quiet Settings row, exact page settled in the build), Q4 more detail (explainers, counts, health summary), Q5 yes (everything that can run from JSON does). Open question left for the build: whether any compiled plugin can become JSON-only (WP26 starts with a short audit).

## 9. Gaps and drift (what v4 built that this version changes)

| v4 said | Seen on 2 Nov | This version |
|---|---|---|
| One dot, text button becomes the fix (D29, D30) | "Configure" missing on some; dot too small | Gear on every tile, 22 px tick in a fixed place (D43, D44) |
| Plugin page: one column, five cards (D33) | Right shape, but built three ways (source files, generic settings, Stash-box) | One generator from JSON (D46 to D52) |
| Provides line from a fixed list (D34) | Text line, plain | Coloured, structured cards (D53) |
| Quiet repair of shipped files (D37) | Fine, but invisible | Kept, plus a manual Restore link (D57, Q3) |

## Plain English summary

Every plugin tile gets the same bottom row: a bigger, fixed health tick and a gear that always opens the plugin's settings. A small "Key missing" label appears only when a key is missing. The Plugins page groups plugins by when they run, and "What it provides" becomes neat coloured cards. The big change is behind the scenes: each plugin's settings page is now drawn automatically from the plugin's own JSON file, so nobody writes special screens for one plugin, and anyone who writes a plugin (including you, for yourself) gets a proper settings page for free. For plugins defined by a file, an administrator on a computer can open that file in Settings, see it coloured, edit it, and Library checks it as you type, points to the exact line of any mistake, will not save a broken file, shows you what will change before it saves, and can put Tuvima's version back at any time. There is also a "Restore official plugins" link for when a file goes missing or gets changed. Keys and settings you have entered are never lost by any of this, and an edit cannot widen where a plugin sends data.
