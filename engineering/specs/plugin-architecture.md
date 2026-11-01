# Spec: Plugin architecture (one complete set of controls, applied to every plugin once)

> **Status:** Built, awaiting Local checks (see engineering/validation/batch.md) · **Version:** 1 · **Last updated:** 2026-10-27 (locked by Shy) · **Related docs:** [Plugins and integrations screens](plugins-and-integrations-screens.md) (Locked, being built; this spec adds to it, it does not change it) · [Developer tools](developer-tools.md) (D-C approval, D-J providers under Plugins, WP16 page audience, WP20) · Music player and queue spec (its WP5b lyrics-audience packet shrinks to setting LRCLIB to Everyone once PA1 exists) · [Vault sections](vault-sections-and-move.md) · [Encrypted plugin keys](encrypted-plugin-keys.md) (parked)

## 1. Goal

**One pass, not many (Shy, 2026-10-27):** every control a plugin could need is built in this one spec, and every existing plugin and source is updated exactly once, so nothing has to be revisited plugin by plugin later. Give every built-in plugin and metadata source the same set of declared controls, so the Library (not each feature) decides who may use it, where it may work, and what it may touch. Today each plugin is powerful in what it can *do* (permissions, network list, AI abilities), but says little about *who* may use it, *which libraries* it applies to, and *how it treats Vault and kids' profiles*. This spec closes those gaps once, for everything, instead of one plugin at a time.

## 2. Scope

**In scope (built-in plugins and sources only)**
- A declared **audience** on every plugin and source (the same three words plugin pages already use: administrators, adults, everyone).
- Declared **where it works** (media types, and optionally "can be limited to chosen libraries").
- **Vault and private-space behaviour** declared once and enforced by the Engine.
- **Category and media types** on every plugin and source (already required by the Plugins screens spec, so this is shared).
- One audit of the 11 plugins and 11 sources so each states these correctly.
- Every gap in section 5 is **fixed in this spec**, nothing deferred: per-library limits, dependencies, activity, resource rules, failure behaviour, data lifecycle, update notes, a Check now test, one settings form, and clear data-leaving labels.

**Out of scope**
- Community plugins, the directory, signing, encrypted plugin keys (parked).
- Changing the look of the Plugins page (Locked spec; this spec adds fields it shows).
- New plugin features.

## 3. Decisions made

None yet. Defaults below are proposed; Shy's answers are in section 7.

- **D1 (proposed).** **One audience vocabulary everywhere:** Administrators, Adults, Everyone (including child profiles). This is already how plugin pages work (Developer tools WP16), so one idea, not two. Default for anything that does not say: Administrators.
- **D2 (proposed).** **Audience is about *using* a feature, never about *configuring* it.** Switching a plugin or source on or off, its keys and its settings stay administrator-only always. Audience only decides who may *trigger or see* what it provides (for example, lyrics lookup for any profile).
- **D3 (proposed).** **A plugin or source cannot widen its own audience after approval.** The audience is part of what the administrator approves (the existing approval statement), so changing it asks for approval again, like a new permission.
- **D4 (corrected, 2026-10-27 after Shy's question).** **Vault rule: the Vault section decides, plugins only declare what they would send.** Each plugin and source declares whether it **sends data outside the machine** (derived from its network list). **Local-only plugins** (skip markers, the Local AI plugins, Local files, Taste summary) **may work on Vault items by default**, under the central access service, because nothing leaves the machine. **Plugins and sources that send titles or other details outside** (TMDB, TVDB, Wikidata, ComicVine, Apple, MusicBrainz, YouTube, LRCLIB, SubDL, Fandom) **may work on a Vault item only when that Vault section has "Look up details online" switched on** (the existing Vault sections decision D34, default off, with its privacy note); **Stash-box is the Vault's own source and stays allowed**. So moving a movie into the Vault does not stop it getting local details; online details follow the section's own setting, which the administrator controls once for the whole section. Reason: Privacy without losing metadata.
- **D6 (proposed).** **Per-library limits are built now.** An administrator can limit a plugin or source to chosen libraries (default: all libraries it works on). Vault libraries are never included unless the Vault rule (D4) allows it.
- **D7 (proposed).** **Failure never blocks the Library.** Every plugin and source declares what happens when it is down, slow or over its limit: the item is skipped, retried later and shown as Needs attention; ingestion, playback and browsing never wait on a plugin.
- **D8 (proposed).** **Plain data-leaving label on every item:** "Nothing leaves this machine", "Sends titles and years to X", or "Sends your key and searches to X". Derived from the network list and permissions, never typed by hand.
- **D9 (proposed).** **One settings form:** every plugin and source shows its settings through the same schema-driven form (labels, units, ranges, reset to default).
- **D10 (proposed).** **Every plugin keeps an Activity list** (what it did, when, how many items changed, and every failure with its cause, see C17), reusing the existing job and History records, shown on its detail page.
- **D11 (proposed).** **Shared resource rules:** each plugin declares how heavy it is (light, medium, heavy); heavy ones run when the machine is idle, after ingestion, one at a time.
- **D12 (proposed).** **Data lifecycle is stated:** each plugin declares what it stores (its records, saved output such as skip markers or lore) and what removal does with it (keep, or remove and refresh). Backups include plugin settings and records, not keys.
- **D13 (proposed).** **Update notes:** a version change carries a one-line "what changed". Changes to audience, network, permissions or Vault use re-ask for approval; a harmless change does not.
- **D14 (Shy, 2026-10-27).** **Plugin consolidations agreed:** Intro, Credits and Recap become **Skip markers** (with the chapter-name detector, C19); Smart labelling and Type helper become **Smart filing**, hidden from the Plugins page until the ingestion hook exists; Vibe tags and Description insights become **Description insights**; Commercial Skip, Series order, Taste summary and Fandom Lore stay. 11 plugins become 7. New sources (books, artwork, ratings, subtitles) are added later one by one.
- **D15 (Shy, 2026-10-27).** **Fandom Lore is kept, brought fully up to this spec (audience, display slots, tools, activity, failure reasons) and stays off by default.** It is expected to prove useful with the companion app later, and is looped into the Universe planning that is still to come (its display slots in particular: character and place blocks on item pages).
- **D16 (Shy, 2026-10-27).** **Removal keeps a plugin's Library output by default** (skip markers, lore, tags become ordinary data); the removal screen shows counts and lets the person choose remove for that removal.
- **D17 (Shy, 2026-10-27).** **Everything in section 5b is in this build:** Tier 1 (C1 to C12, C17 to C23, including per-library limits, display slots, dependency map and clean removal), Tier 2 (C13 to C16) and C4 plugin actions on items and C7 per-profile switch. ListenBrainz (PA6) is the first sharing plugin; Trakt, Last.fm, Hardcover and a Goodreads CSV import are separate follow-up specs after terms checks. Skip detectors, Fandom Lore and Taste summary are open to everyone (Fandom stays off by default). Audience, Vault, per-library, failure, tools, dependency and removal rules are as written in this spec.
- **D18 (Shy, 2026-10-27).** **A per-profile "Plugin actions" restriction list is built now (C26)**, because there are several plugins and an administrator will want to restrict a profile ad hoc.
- **D5 (proposed).** **Plugins and sources share one declaration block**, so there is no second format to maintain (today a plugin uses a manifest in C# and a source uses a provider JSON file with different fields). Both feed the same Plugins page.

## 4. UI behaviour

### 4a. Walkthrough

On a plugin or source's detail page (Locked spec WP3), a short **Access** section is added under "What it can access", showing three plain labels with info (i) tooltips: **Who can use it** (Administrators / Adults / Everyone), **Works on** (e.g. Music, TV; "All libraries" or "Chosen libraries"), and **Vault items** (Local only / Allowed with section lookup on / Not used). Nothing is editable except by changing the plugin; the administrator sees it at approval. A plugin cannot appear to a profile that is not in its audience, and the Engine refuses the action even if someone calls it directly.

Example: LRCLIB shows "Who can use it: Everyone", "Works on: Music", "Vault items: Only if the section's online lookup is on". Taste summary shows "Who can use it: Everyone" (each profile sees only its own), Stash-box shows "Administrators" and "Vault items: Allowed (Vault's own source)".

### 4b. States

| Situation | What the person sees |
|---|---|
| Plugin states nothing | Administrators only, Works on All, Vault follows the section setting; the page says "Set by default" |
| Audience changed by an update | Plugin switches off and asks for approval again, listing the change |
| Profile not in audience calls an action | Refused like a missing page; nothing about the plugin is revealed |
| Library limit set | Plugin skips other libraries; its card says "Limited to: Music" |
| Vault item reaches a plugin without the Vault right | The item is skipped and a line in the plugin's activity says so |

### 4c. Devices

Same as the Plugins pages: desktop and tablet for the page; the *effect* (who can use a feature) applies on every device including phone and TV.

## 5. Audit: findings and recommended fixes

**What exists and is good:** permissions, a network list with plain sentences, secrets declared by name, AI abilities, tool requirements, approval that re-asks when anything changes, compatibility checks, health, jobs, events, plugin pages with an audience.

**The 11 plugins:** Commercial Skip, Intro Skip, Credits Detection, Recap Detection, Fandom Lore, Smart labelling, Type helper, Series order, Vibe tags, Description insights, Taste summary. **The 11 sources:** Apple, ComicVine, Local files, LRCLIB, MusicBrainz, Stash-box, SubDL, TMDB, TVDB, Wikidata, YouTube.

| # | Gap found | Why it matters | Proposed |
|---|---|---|---|
| G1 | **No plugin or source declares who can use it.** Lyrics lookup is blocked for ordinary profiles because the Engine assumes administrators. | Features that are not admin functions are locked behind admin | Audience (D1-D3) on both |
| G2 | **Two formats:** plugins are C# manifests, sources are provider JSON with a different field set. Plugins have no "works on" field; sources have `can_handle`. | Every new rule has to be built twice and can drift (this is the same drift the music spec worried about) | One shared declaration block (D5); the page already merges them |
| G3 | **Plugins do not declare whether they send data out, so the Vault rule cannot be applied to them as a group.** The Vault sections spec already limits lookups for Vault items, but only in the provider code paths. | A new or merged plugin (for example Fandom, a sharing plugin) could send Vault titles outside | Vault rule (D4), checked by the Engine |
| G4 | **No per-library scope.** A plugin is either on for the whole Library or off. | Households want commercial detection on TV recordings only, or lore only for the Books library | "Works on" media types now; "limit to chosen libraries" later (Q3) |
| G5 | **Taste summary reads a person's watching** and has one household-wide on/off. | One profile's activity is summarised for a profile page; kids' profiles and privacy | Audience Adults, plus the summary never appears on another profile's page (verify at build) |
| G6 | **Metadata gaps in the source files:** Apple, Stash-box and YouTube have no `ui_metadata` (no category, accent, chips); Local files has no terms/privacy/help links; LRCLIB has no terms or privacy link; no source or plugin has a "last checked" or "what changed" line. | The redesigned cards need category and media types; users see blanks | Fill in as part of the audit packet |
| G7 | **Plugin dependencies are not declared.** Series order and Description insights depend on Local AI being on, Vibe tags on its vocabulary, Intro/Credits on FFmpeg. Only tools are declared, not "needs plugin X" or "needs Local AI". | A plugin looks Working when its upstream is off | Dependency map and order (C22) feeding "Waiting for X" and Needs attention |
| G8 | **Removing a plugin does not say what happens to its output** (suggestions, skip segments, lore, AI labels). | Surprise data loss or leftovers | Clean removal with a preview and a keep-or-remove choice per plugin (C23) |
| G9 | **Activity per plugin is thin:** jobs have records, but "what did this plugin change, and when" is not a single place. | Trust and debugging | One Activity list per plugin on its detail page (Later) |
| G10 | **Backups:** plugin settings and plugin records are in scope, plugin *keys* wait on the parked key work. | Restore may lose keys silently | Backup page says so (copy only, no code) |
| G11 | **No resource budget for built-in code plugins** (limits exist for host services but segment detectors run FFmpeg/Comskip with a timeout only). | A big library scan can slow the machine | Later: shared "run when idle" rule with the engine-room queue |
| G12 | **Source switch-off and plugin switch-off are two flows.** | Uniformity | The Plugins page screens spec already merges the list; this spec merges the *rules* |

**All of G1 to G12 are fixed in this spec** (D1 to D13). Further gaps found on review:

| # | Further gap | Proposed |
|---|---|---|
| G13 | **No "Check now" test.** The only way to know a source works is to wait for ingestion. | A Check now button runs the existing health check plus a harmless sample (one lookup for a source, a tool check for a detector) and reports in plain words. |
| G14 | **Settings are inconsistent:** Commercial Skip has a long list without a form, Fandom has a schema, most AI plugins have none. | One schema-driven form (D9). |
| G15 | **No simple answer to "what does this send out?"** It exists only as a technical list. | The three-label rule (D8). |
| G16 | **Sources have a weight, plugins none, and neither shows its place in the order of trust** (Priority Cascade). | Detail page shows "Where it ranks"; plugins that supply fields declare which. |
| G17 | **No per-plugin say on notifications:** a broken plugin shows red, but administrators are told by a global rule. | Each item marks itself "tell me" or "quiet" (default quiet for background helpers), using the existing plugin-broken notification. |

### 5a. Proposed audience for every existing item

| Item | Who can use it | Works on | Vault items |
|---|---|---|---|
| Commercial Skip, Intro Skip, Credits, Recap (skip markers) | Everyone (Shy, 2026-10-27; they only produce skip buttons for what a person can already watch) | TV, Movies | Allowed (local only) |
| Fandom Lore | Everyone (Shy, 2026-10-27); setting it up stays Administrators | Books, Comics, TV, Movies | Not used (sends to the internet) |
| Smart labelling, Type helper, Series order, Vibe tags, Description insights | Administrators (they run in the background; nobody "uses" them directly) | All | Allowed (Local AI, local only) |
| Taste summary | Everyone, but each profile only ever sees its own summary (Shy, 2026-10-27) | All | Allowed (local only, follows that profile's Vault access) |
| LRCLIB | Everyone | Music | Only if the section's online lookup is on |
| MusicBrainz, Wikidata, Apple | Administrators (matching/enrichment, background) | their media types | Only if the section's online lookup is on |
| Local files | Administrators | All | Allowed (local only) |
| TMDB, TVDB, ComicVine, YouTube | Administrators | their media types | Only if the section's online lookup is on |
| SubDL | Everyone (subtitle lookup on demand, like lyrics) | TV, Movies | Only if the section's online lookup is on |
| Stash-box | Administrators | Video (Vault) | **Allowed (the Vault's own source)** |

### 5b. Extra capabilities (all included by Shy, D17)

Tier 1 (C1 to C12, C17 to C21) is recommended in this pass; Tier 2 is useful and cheap once Tier 1 exists; Tier 3 is only needed for community plugins or later products.

**Tier 1 (recommended)**
- **C1 One output contract.** Everything a plugin produces (a field, a tag, a skip marker, a lore entry, a lyrics file, a **hero slide suggestion**, a **hero detail line**) is recorded the same way: what, source plugin, confidence, when. This reuses the append-only claims and the Priority Cascade, so plugins can never overwrite what a person locked.
- **C2 Suggest or apply.** Each plugin chooses per output: apply automatically above the Library's confidence line (the 90% auto-accept line), otherwise send to **Needs Review**. One rule, not each plugin's own.
- **C3 Undo everything a plugin did.** Because of C1, a "Remove what this plugin added" action can clear one plugin's output without touching anything else.
- **C4 Plugin actions on items.** A plugin can add actions to an item's "..." menu (Look up lyrics, Find subtitles, Re-scan intro), each with its own audience, desktop-only if it edits. This is what lyrics needs, built once. **Actions are bound to what a plugin supplies, never to its name:** an action is declared as "find lyrics" or "find subtitles" (or "re-scan intro"), shown in the slot it names (item menu, player toolbar, a panel), and appears only when at least one switched-on plugin or source supplies that thing, the person is in its audience (D1), and the device rule allows it (editing stays desktop-only). Pressing it runs the supplying sources in their order (C9). So LRCLIB's "Look up lyrics" is not hard-coded; a second lyrics source would be used by the same button, and with every lyrics source off the button is simply not there. The Library keeps owning the lyrics panel itself (it is a core concept); the plugin supplies the data and the action. **Player buttons (Shy, 2026-10-27):** Lyrics is a **core button that is always there** in the music player (it is a Library concept, not a plugin's). It shows the song's lyrics from any source, including a `.lrc` beside the song. If the song has none, pressing it **asks the switched-on lyrics sources to find them** and shows a short "Looking for lyrics" state; if nothing is found it says so plainly; if no lyrics source is switched on it says "No lyrics source is switched on" (administrators also get a link to the Plugins page). Today the button is a fixed item in the player's list and always shows, and the lookup action is not wired to it (read from `PlaybackFullPlayer.razor`, not run).

**Keeping player buttons uncluttered (all devices, especially phone).** Plugins cannot add loose buttons to the player. The player has a fixed set of **core buttons** (Lyrics, Queue, Output, and for video Subtitles and Skip; owned by the Library) and **one overflow ("...") menu**. Every plugin action goes into the overflow menu by default, grouped and ordered by a declared priority. A plugin may ask for a core-button position only for a Library-defined action type (lyrics, subtitles, skip), never for a custom button, so there is never more than the Library's own fixed set on the bar. Phone and TV show the core buttons that fit and move the rest into the overflow; five plugin actions therefore add five rows in a menu, not five buttons. A profile can hide or reorder its own overflow entries (C7). Skip buttons stay contextual (they appear only during a marker) and are not counted against the bar.

**Local files come first (confirmed from the code, not run):** when a song is processed, a `.lrc` (then `.txt`) beside it is imported before any online source is asked, kept as the preferred, "user-owned" track, and an online source never replaces it; subtitles likewise (`.vtt`, `.srt`, `.ass`). The architecture keeps this as a rule for all text-track sources: **a person's own file beats any plugin or source**, and a plugin can only fill what is missing.

**Saving found lyrics, and writeback.** Lyrics found by a source are always saved in the Library's own managed text-track folder (as today). **If writeback is enabled for the library** (the existing writeback settings and batching rules apply, not a new switch), they are also put next to the file: **a separate `.lrc` file beside the song (the default, Shy 2026-10-27; never alters the audio file)** or, only if tag writeback is on and the format supports it, **inside the file's own tags**. **With writeback off, nothing is written to the person's folders**; the managed copy is the only one, and (Shy's note) it lives in the Library's own data folder, which backups leave out because it can be found again, so after a restore the lyrics are looked up again (or re-read from any `.lrc` that exists). Plain-text lyrics write as `.txt`/unsynced tag, synced as `.lrc`/synced tag. The same capability serves subtitles (a `.vtt`/`.srt` beside the video). A plugin only declares that its output is a text track and what kind; the Library does the saving and writeback, so no plugin writes to the person's folders itself. Original media files stay protected unless the library's tag writeback is switched on. Writeback of lyrics is **not built today** (the write-back settings and code have no lyrics handling), so it is a new packet (PA7).
- **C5 Try on one item.** Before switching a plugin on, an administrator can run it on one chosen item and see the result without saving.
- **C6 Output carries a content rating.** Anything a plugin supplies that could be unsuitable (lore, summaries, subtitles, descriptions) is tagged so child and restricted profiles filter it, like the item itself.
- **C7 Per-profile switch for "Everyone" features.** A profile can turn off, for itself, a feature that the household has on (hide skip buttons, hide AI labels, hide taste summary). The household switch stays administrator-only.
- **C8 Outbound-requests receipt.** Each plugin and source shows what it actually contacted in the last days (hosts, counts, last time), so "nothing leaves this machine" is checkable, not just promised. Includes a usage counter for keyed services so TMDB-style quotas are visible.
- **C9 Source order for each field, visible.** The detail page and the Providers screen show which plugin or source wins for each field and why, and an administrator can reorder where the Library allows it.
- **C10 Automatic failover and circuit breaker for every plugin.** After repeated failures a plugin pauses itself and retries gently; the next source in the order takes over. Today only plugin host services do this.
- **C11 Languages.** Each plugin declares the languages it supports, and follows the profile's language where it can (subtitles, descriptions, lyrics).
- **C17 Failure reasons, not just "failed".** Every plugin and source failure is recorded durably with a cause from one fixed list, so you can tell a broken plugin from everything else: **the plugin itself** (crash or bug), **the outside service** (unreachable, refused, rate-limited, key rejected or expired), **a missing tool** (FFmpeg, Comskip), **Local AI** (off, busy, over its hourly allowance), **the item** (file unreadable or unsupported), **a limit** (storage, records, requests), **not allowed** (audience, Vault, permission), **a dependency is off**, and **the Library itself** (data store or disk). Each entry holds the plugin and version, the item, a plain sentence, and a technical detail line (redacted of keys and personal paths). It feeds the Activity list (D10), Needs attention wording and the plugin-broken notification, and survives restarts. Plugins write to the Engine log through one plugin logger tagged with the plugin id, so an administrator can filter the log by plugin and export it.
- **C18 One Tools catalogue for every plugin.** A plugin only names the tool it needs (for example `comskip`, `ffmpeg`). One catalogue (extending `config/tool_downloads.json`, which today only covers FFmpeg, Tesseract and ExifTool) holds the pinned download address, checksum, licence, size and platforms for each tool. Settings → Tools lists every tool any plugin needs with Download, Repair and "Use my own copy". The plugin approval shows the tool, its licence and size ("Downloads Comskip, about 3 MB, GPL-2.0"); the download only happens after approval, comes from an approved address only, must match its checksum, and lands in the Library's own tools folder, never in the repo or build output. Plugins may no longer carry their own download addresses. **Today's gap:** Commercial Skip lists Comskip but no pinned download, so its "auto install" can never work; it only works if Comskip is already on the machine. The same catalogue entry fixes this. **Build must first** confirm a Comskip build we may download (source, licence and checksum) and run the licence check; if none is acceptable the plugin keeps working through FFmpeg and says so.
- **C21 Display slots: where a plugin's output is shown.** A plugin declares which Library surfaces it contributes to, from a short fixed list, and supplies data only; the Library draws it with its own components (no plugin-made screens, so look and feel stay uniform and nothing can run in the browser). Slots: **Home hero** (a line or badge on a slide, such as "Intro skippable", "New lore available", or a short AI reason "Because you finished Dune"), **Home hero slide candidates** (a plugin may nominate an item for the hero with a reason and a strength; the Home spec's rules R1 to R3 still decide, so a plugin can suggest but never reshuffle or override "continue first"), **item details hero** (chips and one-line facts), **item details section** (a titled block, such as Lore, Mood and themes, Taste), **tile/card badge** (small icon with tooltip), **player toolbar and panels** (skip buttons already built; also the lyrics and subtitle panels' "Look up now" and "Add a file" actions), **item "..." menu** (C4), **Settings overview** (a status line, already built for health). Every contribution goes through the same rules as any plugin output: the plugin's audience (D1), content rating (C6), the person's own switch (C7), the "AI" label when it came from Local AI, the source plugin shown on hover, and Vault exclusion (D4). Contributions are plain data with a size limit, so a plugin cannot slow the hero; the hero shows what is ready and never waits on a plugin (D7). Coordinates with the Home page redesign spec, which owns the hero's selection rules and layout; this spec only adds the slot and the data shape.
- **C22 Dependency map and order (Shy, 2026-10-27).** `requires` (G7) becomes a real map the Engine understands, not a note. A plugin lists what it needs: other plugins (with a version range), Local AI abilities, tools (C18), sources, and host services. Each is marked **required** or **optional** (optional ones only improve results). The Engine then: (1) **refuses cycles** when it loads the catalogue; (2) **works out the order**: switching a plugin on shows one approval listing the plugin and every missing dependency, and turns them on dependencies-first; (3) **starts plugins in dependency order** when the Engine starts, so a plugin never runs before what it needs; (4) shows **"Waiting for X"** instead of a false Working when a required dependency is off, missing or the wrong version; (5) when you switch off or remove something others depend on, the removal screen lists the dependants and offers "also switch them off" (default) or cancel; it never silently breaks them; (6) an optional dependency being off only adds a gentle note. Shown on the detail page as "Needs" and "Needed by" lists. Built-in examples: Description insights and Series order need Local AI; Skip markers need FFmpeg (and Commercial Skip needs Comskip or FFmpeg); Smart filing needs Local AI.
- **C23 Clean removal (Shy, 2026-10-27).** Removing a plugin removes what belongs to it, and says so first. Each plugin declares what it stores (D12), and the Engine cleans each part: its settings and schedule overrides, its scheduled jobs and event positions, its own records, personal and household credentials (C13), its approval record, its Activity history (kept for 30 days as a removal note, then pruned), downloaded tools **only if no other plugin needs them** (C18), and cached files. The plugin's **output in the Library** (skip markers, lore, tags, suggestions) follows the plugin's declared choice shown on the removal screen: **keep** (becomes ordinary data, no longer refreshed) or **remove** (uses C3 undo, only that plugin's output). The screen shows counts ("1,240 skip markers, 3 MB of records, Comskip 3 MB"), a default per plugin, and a final Remove button. A leftover sweep at Engine start removes any orphaned data from a plugin no longer present. Media files are never touched. Today (read from the code, not tested) removal deletes the plugin's files and its secrets only, so records, saved output and settings can be left behind.
- **C24 "Not right? Find another" for lyrics and subtitles (Shy, 2026-10-27).** In the lyrics panel and the subtitle menu, a person can ask for a different result when the one playing is bad. The Engine already keeps several candidate tracks per song or video (each with its source, a confidence and a duration-match score) and can switch the preferred one; what is missing is a way to see and use them. The panel shows the alternatives (source, how well it matches the length, synced or plain, language, and the first lines as a preview), lets the person pick one, and has **"This one is wrong"**, which marks that candidate as rejected for this item so it is never picked again, then asks the sources for more (a search returning several matches, not just the single best one, where the source supports it; to be confirmed per source). A person's own file (`.lrc`, `.srt`) stays on the list and is never auto-replaced. **Keep it simple (Shy, 2026-10-27): the latest chosen is the one that counts.** The list of alternatives is fetched live from the sources when the panel opens, not stored. The Library keeps only the **current** track and the **one before it** (so "Go back" works once), plus a small "don't pick this again" note for each track marked wrong. Older ones are removed from the Library's own store. A person's own files are never removed. **In the person's folder there is only ever one file per language** (for example `Song.lrc`, `Movie.en.srt`): when the choice changes and writeback is on, that one file is replaced, so the folder never fills up. Writeback never overwrites a file it did not write itself; if the person's own file is there, it is left alone and used (their file wins). **The choice is Library-wide** (Shy, 2026-10-27): it is the item's data, the same for everyone, and any person who can use lookup may change it without an administrator, because a wrong subtitle is not destructive and several people correcting or confirming a result makes it better; it is recorded with who and when (C25), and "Go back to the previous one" is always offered. Allowed for everyone who can use lookup (D1), on every device, because it changes nothing about metadata or files unless writeback is on, in which case the sidecar is rewritten by the usual writeback rules. Subtitles use the same list and the same wrong-result button, with language and hearing-impaired shown.
- **C25 People-action log: the foundation (Shy, 2026-10-27).** Whenever a person does something with a plugin or source that changes shared Library data, the Engine writes one standard record to the existing Activity ledger (System → Logs → Events): **who** (profile), **what** (for example "chose lyrics", "marked subtitles wrong", "looked up lyrics", "added a lyrics file", "linked an account", "switched a plugin off"), **which item**, **which plugin or source**, a short **before and after** (for example the previous and new track), **device** and **time**. It gets its own category, "People's actions", visible to administrators only. It records only changes to shared Library data; it never records what a person watches, reads or listens to (that stays private). Kept for 12 months by default, adjustable. This spec builds only the record shape, the category, writing the records for every plugin action in this spec, and a simple filter by person, plugin and action; richer review (summaries, alerts, undo from the log, exports) is a **separate, later spec** that builds on this foundation. Today an Activity ledger and Events categories exist; whether each entry records the acting profile is not confirmed and is a build-time check.
- **C26 Restrict a profile's plugin actions, ad hoc (Shy, 2026-10-27; built in this spec).** On a profile's page an administrator sees a short **Plugin actions** list: every action that plugins and sources offer to people (for example "Correct lyrics and subtitles", "Look up lyrics or subtitles", and each later plugin action), each **on by default** with a switch. Switching one off makes that profile **read-only for that action**: it keeps using what is there (the current lyrics and subtitles, skip buttons), the buttons for changing things are simply not offered, and the Engine refuses the action if called directly. The list is built from the actions the plugins declare (C4), so a new plugin's actions appear automatically; nothing is hard-coded per plugin. Decided by the central access service. Every switch is logged (C25). A typical use: the People-action log shows a profile putting bad lyrics or subtitles on purpose, and an administrator switches off "Correct lyrics and subtitles" for that profile only. **Not planned (Shy, 2026-10-27):** an "undo everything this profile changed" action; judged overkill. The per-item "Go back to the previous one" (C24) is enough, because the previous choice is kept.
- **C12 Photos are explicit.** "Works on" lists Photos separately; Photos stay a separate index and default to not used.

**Tier 2 (recommended if cheap once Tier 1 exists)**
- **C13 Per-profile connections.** A plugin can ask each profile to link its own account (for example Trakt or ListenBrainz scrobbling), stored per profile, encrypted, with a clear consent step and removal. Needed by the starter integrations named in the Plugins screens spec (D6). A credential may declare an approve-on-another-device (device code) sign-in (`sign_in` in the manifest: host, code, token and refresh paths, and the household secrets holding the app's client id and secret); the person approves on another device, the token is stored as that profile's own credential and renewed by the Engine. Built for Trakt (the Trakt plugin, `tuvima.trakt`).
  **How credentials are split (Shy, 2026-10-27):** a plugin declares each secret as **household** (default; entered once by an administrator in the plugin's settings, such as an app key or client id; today's behaviour) or **personal** (entered by each profile for itself, such as a ListenBrainz token). Personal ones are entered on the profile's own page in a **Connected accounts** section (Settings → that profile), by the profile itself, after a consent step that says exactly what will be sent and where. Administrators see only "Linked" or "Not linked" for other profiles and can remove a link, never read or set it. A plugin can only read the personal credential of the profile whose event it is handling; it cannot list profiles or read another's. Personal credentials are stored encrypted with the same protection as other secrets, are left out of backups (the profile relinks after a restore), and are deleted when the profile or the plugin is removed. Switching the household switch off stops all sending, whatever is linked. The approval step stays household-level; linking is the person's own consent.
- **C14 Plugin-raised notifications.** A plugin can raise its own events through the existing notification service (for example "TMDB key is about to expire").
- **C15 Import and export of a plugin's settings** as a small file, for backups and moving between machines (keys excluded).
- **C16 Browse facets from plugins.** A plugin can contribute a browse filter (Vibes already do this by hand), declared once.

**Tier 3 (not now)**
- Plugin-to-plugin calls, plugin dependencies on other plugins' data (only declared `requires` for now), a plugin store ratings and reviews, translating plugin text, sandboxed code uploads, new file-format handlers, and signed updates. All wait on community plugins.

### 5c. Review of the existing list, and comparison with other media managers

**Evidence for "is it used?"** (read from the code, nothing run): the four skip plugins feed the player's Skip Intro / Credits / Recap / Commercial buttons; Series order feeds series pages; Vibe tags feed Browse filters; Description insights and Taste summary have result screens (detail page AI results, Settings → Overview → Taste); Fandom Lore feeds the Universe page. **Smart labelling and Type helper are not connected to anything yet** (the ingestion hook they need, Developer tools WP28 part B, is not built), so today their switches do nothing.

| Today (11) | Verdict | Why |
|---|---|---|
| Intro Skip, Credits Detection, Recap Detection | **Merge into one "Skip markers"** with a switch per marker type | Same tools, same setting, same player buttons; three cards for one idea. Jellyfin and Plex treat these as one native feature (Jellyfin's Media Segments, Plex's intro/credits markers) |
| Commercial Skip | **Keep separate**, off by default, labelled "for recordings" | Heavy, needs Comskip, only useful for recorded TV; the others are table stakes |
| Fandom Lore | **Keep** | Unique to the Library (Universe pages); neither Jellyfin nor Plex has an equivalent |
| Smart labelling + Type helper | **Merge into one "Smart filing"**; hide from the Plugins page until the ingestion hook exists | Same job (help when a name or type is unclear), same AI role; a switch that does nothing misleads |
| Series order | **Keep** | In use; Plex and Jellyfin rely on external tools (Kometa, collection plugins) for order |
| Vibe tags + Description insights | **Merge into one "Description insights"** (vibe words become a setting) | Both read descriptions to produce mood words; two AI passes over the same text |
| Taste summary | **Keep**, off by default | Cheap, personal, only reads that profile's own activity |

Result: 11 plugins become 7. Pre-release, so the old plugin ids are simply replaced.

**Sources (11):** all stay. None is redundant; Local files and Wikidata are core. Notes: LRCLIB and SubDL are the two "on-demand for everyone" sources; Stash-box is the only Vault source.

**What other media managers offer that we lack** (from general knowledge of the products plus the sources linked in the thread; not tested here):
- **Jellyfin:** Media Segments as one shared typed list that several plugins feed (we have the same idea); a chapter-name based intro detector (cheap and accurate when chapters are labelled); Playback Reporting; Trakt sync; OpenSubtitles; Webhook; many metadata and image sources (fanart, AudioDB, OMDb). Everything is a plugin from a catalogue.
- **Plex:** native intro and credits markers; commercial detection for DVR; per-library choice of metadata agents; sonic "similar songs"; external tools for stats (Tautulli) and collections (Kometa).
- **Audiobookshelf:** no plugin system, but a **custom metadata provider** format (a web address and optional token that follow a published request format) which let the community add many book sources; chapter lookup (Audnexus); bookmarks, sleep timer, notifications via Apprise.

**Where we are already ahead:** Universe and Series (collections without plugins), editions and variants (Jellyfin's Merge Versions), Needs Review, per-source Priority Cascade, Vault and profile rules, and plugin approval.

**Real gaps this review adds to the spec**
- **C19 Chapter-name detector** inside Skip markers: use labels already in the file ("Intro", "Credits") before heavier analysis. Cheap, accurate, and a person can still correct a marker (locks from C1).
- **C20 Per-library source choice** (Plex-style): an administrator can switch a source on or off for a chosen library (included by D6); the order of trust stays global.
- **Candidate new sources, to be added later each as its own small source file (they need no change to existing plugins, so waiting does not mean doing it twice):** books and audiobooks (Open Library, Hardcover, Audnexus chapters and narrators), artwork (Fanart.tv), ratings (OMDb), subtitles (OpenSubtitles), music images (TheAudioDB), play-history sharing (Trakt, ListenBrainz, already in the Plugins screens spec D6). Each needs its own terms and licence check.

### 5d. Sharing and sync plugins (scrobbling) and other helper plugins worth shipping

**What the architecture must offer them** (all already in Tier 1/2): watch-activity events (exists), per-profile connections (C13), a connection method that is either "paste a token" or "approve on another device" (OAuth device code; added to C13), a retry queue so a plugin that is offline never loses or delays a play, the outbound-requests receipt (C8), and the Activity list with failure reasons (C17). Nothing is shared unless that profile links its own account and the household switch is on; nothing from Vault or private profiles is ever sent.

| Candidate | Fit | Notes (web facts not fully verified; each needs its own terms check) |
|---|---|---|
| **ListenBrainz** (music play history) | Best first one | Open, free, a user pastes a personal token; matches MusicBrainz IDs the Library already stores. **Proposed as the proof-of-architecture plugin in this spec (PA6).** |
| **Trakt** (movie and TV history, watchlist) | Good, own follow-up | Built (device-code sign-in; Trakt plugin). Needed an approve-on-device sign-in; search results show Trakt has changed limits, VIP-only features and doubled VIP prices in 2025, so check current terms before committing |
| **Last.fm** (music scrobbling) | Maybe | Still exists with a scrobble API, but needs an app key per install or a shared key, which needs its terms checked; fewer people use it than ListenBrainz. Do after ListenBrainz if wanted |
| **Goodreads** | **Not possible as live sync** | Goodreads stopped giving out developer keys in December 2020 and has no current public API. Offer a one-time **import from a Goodreads or StoryGraph CSV export** instead (local, private) |
| **Hardcover** (reading status, ratings) | Possible | Newer Goodreads alternative with a published API; confirm terms and that it covers reading status before specifying |
| Notification channels (Discord/Slack webhook, ntfy, email) | Already planned | Developer tools WP24 |
| OPDS feed, Home Assistant | Integrations, not plugins | Plugins screens spec D6 |
| OpenSubtitles, Fanart.tv, OMDb, TheAudioDB, Open Library, Audnexus | New sources | Each a small source file later; none needs existing plugins touched |

## 6. Data / model changes

- One shared **declaration block** read from both a plugin's description and a source's file: `audience`, `works_on` (media types), `libraries` (all, or chosen by the administrator), the Vault behaviour (worked out from the network list, not typed: local-only, or follows the Vault section's online-lookup setting), `requires`, `category`, `weight_class` (light/medium/heavy), `stores` (what it keeps and what removal does), `on_failure`, `notify` (tell me or quiet) and `changelog` (one line per version). Plugins that state nothing default to administrators and all media, and follow the Vault section's setting if they send data out. Pre-release data: no migration; a reset is acceptable.
- The approval statement includes audience and Vault so changes ask again (D3).
- The Plugins summary and Sources overview return these fields for the Access section.

## 7. Work packets

Built in the cloud and validated locally as one batch. Waves: PA1 → PA2b → (PA2 ∥ PA3) → (PA4 ∥ PA6) → PA7 → PA5.

### PA1 — Shared declaration block
- **What it does:** Adds `audience`, `works_on`, `vault`, `requires`, `category` to the plugin description and the provider file format, one validator, default rules, approval statement and returned summary fields. This replaces music-spec WP5b's provider-only version (WP5b shrinks to "set LRCLIB to Everyone" if this lands first).
- **Files likely touched:** `PluginManifest.cs`, `config/schemas/providers/provider.schema.json`, `ProviderDeclarativeValidator*.cs`, `ApprovalStatementBuilder.cs`, `src/MediaEngine.Contracts/Plugins/*`, wire fixtures (to be confirmed)
- **Depends on:** none · **Size:** Complex (touches approval; security review before merge)
- **Done when:** a plugin or source with no declaration is administrators-only; changing the audience re-asks for approval; Contracts tests and fixtures pass.

### PA2 — Engine enforces audience and Vault rule
- **What it does:** One check used by plugin and source actions (lyrics and subtitle refresh/import, Fandom lore, plugin lookups): refuses a caller outside the audience like a missing page; skips Vault items for items without the Vault right. Uses the central access service.
- **Depends on:** PA1 · **Size:** Complex (security review)
- **Done when:** an ordinary profile can refresh lyrics from LRCLIB, cannot refresh from an administrators-only source; a Vault item is never sent to a plugin without the Vault right (tests with a spy).

### PA2b — Shared runtime behaviour
- **What it does:** Builds the parts every plugin shares so none implements them itself, including the display slots (C21), the dependency map and order (C22) and clean removal (C23): per-library limits (D6), failure behaviour (D7), idle-time scheduling for heavy plugins (D11), the Activity list (D10), the People-action log record shape and category (C25), the data-leaving label (D8), the update-note and re-approval rule (D13) and Check now (G13).
- **Files likely touched:** `PluginCatalog.cs`, `PluginJobScheduler.cs`, `PluginHostStatus.cs`, `ProviderCapabilityCatalogue.cs`, scheduled-task descriptors (to be confirmed)
- **Depends on:** PA1 · **Size:** Complex
- **Done when:** a plugin limited to one library only processes that library; a source returning errors is skipped and shown Needs attention while ingestion still finishes; a heavy plugin does not run during ingestion; Check now returns a plain-words result for each of the 22 items.

### PA3 — Audit pass for all plugins and sources
- **What it does:** Applies the merges in 5c (11 plugins become 7), sets the table in 5a, fills the metadata gaps (G6), adds `requires` (G7), weight class, `stores`, `on_failure`, `notify` and a changelog line for each of the 22 items, and gives every plugin a settings schema (D9). Each plugin is edited once, here.
- **Files likely touched:** the six AI plugin manifests, the five code plugins, `config/providers/*.json` (to be confirmed)
- **Depends on:** PA1, PA2b · **Size:** Standard
- **Done when:** every item states audience, works on, Vault, weight, storage, failure and notify values and has a settings form; no source lacks category or links; guardrail test fails if a new plugin or source omits them.

### PA4 — Access, Activity and Check now on the detail page
- **What it does:** Adds the Access section (three labels with tooltips), the data-leaving label, "Limited to" libraries, "Where it ranks", the Activity list, Check now, the update note and the Needs / Needed by lists (C22) and the removal preview (C23) to the detail page built by the Locked screens spec WP3.
- **Depends on:** PA2, PA2b, PA3, and Locked spec WP3 built · **Size:** Complex
- **Done when:** LRCLIB shows Everyone/Music/Not used; removing Intro Skip says its saved skip markers are kept or removed.

### PA6 — ListenBrainz sharing plugin (proof of the architecture)
- **What it does:** A built-in plugin that, for each profile that links its own ListenBrainz token (C13), sends finished plays of music to ListenBrainz with MusicBrainz IDs, queues and retries when offline, and shows its Activity and failure reasons. Off by default; approval lists exactly what is sent and to where.
- **Files likely touched:** new `MediaEngine.Plugin.ListenBrainz` project, per-profile connection storage, tests (to be confirmed)
- **Depends on:** PA2b (per-profile connections, retry queue, receipts), PA3 · **Size:** Standard
- **Done when:** a profile that links a token has its plays appear on ListenBrainz; a profile that has not linked sends nothing; a Vault play is never sent; going offline queues and later delivers; removing the link deletes the stored token.

### PA7 — Player action slots, lyrics find, choose another, and writeback
- **What it does:** Builds the player's core-button-plus-overflow structure for plugin actions (priority, grouping, per-profile hide and reorder); makes the Lyrics button always present and wired to "find lyrics" with its four states (looking, found, not found, no source on); adds the managed-folder-plus-optional-writeback step for text tracks (`.lrc` or `.vtt` sidecar by default; embedded tags only where the library allows tag writeback), following the existing writeback rules and batching.
- **Files likely touched:** `PlaybackFullPlayer.razor`, `ListenPlayerPopupPage.razor`, `StreamEndpoints.cs`, `TextTrackEnrichmentWorker.cs`, the write-back services and `config/writeback*.json`, tests (to be confirmed; must be checked against the Batched writeback spec before building)
- **Also does:** the "Not right? Find another" list and rejection memory (C24) for lyrics and subtitles, written to the People-action log (C25).
- **Depends on:** PA2b, PA4 · **Size:** Complex (touches the person's files: independent review before merge)
- **Done when:** with 5 plugin actions the phone player shows its core buttons plus one overflow menu, nothing wider; pressing Lyrics on a song with none finds and shows them; with writeback on a `.lrc` appears beside the file, with writeback off nothing is written; the audio file is unchanged unless tag writeback is on.

### PA5 — Docs, tests and Local checks
- **Depends on:** PA4 · **Size:** Mechanical
- **Done when:** docs build passes; Local checks appended to the batch list.

## Local checks (go to `engineering/validation/batch.md`)
00. Switch on Description insights with Local AI off: it shows "Waiting for Local AI"; turn on Local AI and the plugin follows. Remove a plugin with saved output: the removal screen shows counts, keeps output by default, and leaves no records behind. On a second profile link a ListenBrainz token on the profile page, play a song, see it on ListenBrainz; an administrator sees "Linked" but cannot read the token.
0. On the real library, limit Commercial Skip to the TV library and confirm Movies are untouched; block LRCLIB's internet and confirm songs still play and the source shows Needs attention; press Check now on each of the 22 items.
1. Sign in as an ordinary profile: lyrics lookup works; a subtitle lookup on an adult-rated item respects the profile's rating; an administrators-only source refuses.
2. Sign in as a child profile: Taste summary shows only that child's own summary, and Fandom Lore shows only lore for items the child can see.
3. Put a file in a Vault library and run a scan with a source switched on: confirm nothing is sent outside (watch the Engine log) unless Stash-box.
4. Plugin detail pages show the Access section at desktop and tablet widths, light and dark.

## 8. Risks and open questions

No open questions as of 2026-10-27; ready for Shy to lock.

- **Overlap with the Locked Plugins screens spec** (WP2, WP3). This spec only adds fields and a section; it needs that spec's list and detail built first (PA4) and a Version 2 of it that names the Access section. No Locked meaning changes without Shy.
- **Overlap with the Vault sections spec** (central access service). PA2 must use it, not make a second rule.
- **Risk:** changing approval statements asks every existing approval again after the update (acceptable pre-release).
- **Added capabilities (section 5b)** touch the Needs Review rules, profile settings and the notification service; each must use the existing central services, not make its own.
- **Unverified:** whether SubDL results already honour age ratings, and whether the Taste summary can ever show on another profile's page; both are build-time checks in PA2.

## 9. Gaps and drift

- Music spec WP5b adds a provider-only version of audience; this spec supersedes it (proposal: shrink WP5b to setting LRCLIB's value once PA1 exists).
- Developer tools WP16's page audience (administrators/adults/everyone) is reused rather than re-invented.

## Plain English summary

Every plugin and source will say who can use it (administrators, adults or everyone), which kinds of media it works on, and whether it may touch Vault items. The Engine, not each feature, enforces this. The same pass adds limits to chosen libraries, a clear "what leaves this machine" label, an Activity list, a Check now test button, dependency warnings, quiet handling when something is down, heavy work only when the machine is idle, and update notes. Each existing plugin is edited once, in one audit (lyrics and subtitle lookup for everyone, background tools for administrators, taste summary for adults, Stash-box the Vault's own source; online sources work on Vault items only when that section's lookup is on). Plugin settings, keys and switching on or off always stay administrator-only. We also list smaller gaps: plugins that depend on others, what removal leaves behind, and missing links and labels on several sources.
