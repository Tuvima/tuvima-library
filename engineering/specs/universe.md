# Spec: Universes (explore, series grouping, helper apps, clean-up)

> **Status:** In progress (U-WP0 to U-WP3, U-WP5, U-WP7, U-WP10 and U-WP12 built) · **Version:** 4 · **Last updated:** 2026-10-20 · **Related docs:** [Product overview](../product/overview.md) · [Item Detail spec (archived, D11/D16/D27)](archive/item-detail.md) · [Universe Graph architecture](../../docs/architecture/universe-graph.md) · [How universes work](../../docs/explanation/how-universes-work.md) · [Collections architecture](../../docs/architecture/collections.md) · [Developer tools (v3, In progress)](developer-tools.md) · [Ingestion and readiness](ingestion-and-readiness.md) · [Settings revamp](archive/settings-revamp.md) · Bug list item 23 (The Expanse)

## 0. Where we are today (verified by reading the code, nothing was run)

Read this first; it explains why the plan looks the way it does.

- **Two different things are both called "universe".** (a) A *Universe shelf*: a stored grouping of Series, shown on Collections as "Shared universes" and served to the helper apps. (b) A *Universe graph*: a Wikidata-based web of characters, places, organizations, events and the Works they appear in, shown only on the Chronicle Explorer page. They are linked only by a shared Wikidata ID.
- **The graph screen exists but nobody can find it.** `/universe/{id}/explore` has a path finder, family tree, cast comparison and adaptation tree, and works. But no menu, button or link in the Dashboard leads to it. The one place that did (a "Universe" tab on item pages) was deliberately removed.
- **The enrichment behind it runs.** The Engine fills the graph in the background and a Fandom lore plugin can add extra facts, but the plugin is off by default and nothing tells the user it exists.
- **Helper apps show only the shelf, never the graph.** Android, iPhone/iPad and the TV web app list a Universe's Series. Roku shows Collections only. No helper app can show characters, cast, adaptations or paths.
- **Series grouping is the weak link (bug 23).** Shelves are grouped by label text, numbered from a whole-number cast that drops decimals (1.5 becomes 1), and the shelf ignores Wikidata's own "main sequence vs supplementary" classification that the item page already respects. So the shelf and the item page can disagree, and two lines that share a label merge.
- **A lot of Universe code has no caller** (section 5 and the dead-code list in U-WP1).

## 1. Goal

The long-term idea (Shy, 2026-10-19): a Universe is the backbone of a **companion** that knows what you're consuming (TV, a book, an audiobook) and gives you spoiler-safe context and connections, with AI as an enhancement and never the foundation. See section 4A.

A Universe becomes a place you can actually use: you can find it from your Library, see the Series, Works and formats that belong to it in the right order, explore who and what connects them, and see the same thing on your phone or TV, with every fact coming from one trusted source and nothing shown that the Library cannot back up. Series stop mixing (The Expanse novels and the TV show become separate, correctly numbered Series inside one Universe).

## 2. Scope

**In scope**
- A Universe page that is reachable (entry points, what it shows, empty states).
- Series grouping and numbering fix (bug 23) plus rules for how Series sit inside a Universe.
- A companion follow-along and a reliable-first feature menu (section 4A), built in three layers: Universe tools, no-AI companion, experimental AI.
- A "Characters and connections" experience, returning in a limited, evidence-backed form (Q2).
- Helper apps (Android, iPhone/iPad, TV web, Roku): what each shows, and the Engine actions they need.
- How data from retail providers, Wikidata and the optional lore plugin is reconciled for Universes.
- What is missing to make Universes useful as a tool (section 8).
- Removal or deprecation of unused Universe code, config and docs (U-WP1, list in section 5).

**Out of scope**
- Characters and places on item pages (stays removed; Item Detail D11/D16 stand).
- A Universe tab, Series tab or Related tab on item pages (never; Shy's rule).
- Editing the graph on phones (editing is desktop-only).
- Creating a Universe by hand with no Wikidata evidence (open question Q5).
- New media types; the Photos index (it never joins the Work/Edition graph).

## 3. Decisions made

Carried in from earlier decisions (not reopened):
- **D1.** No Universe, Series or Related tab on item pages. A Universe is reached from the Library, not from inside an item.
- **D2.** Vocabulary: Universe (ParentCollection) contains Series (Collection) contain Works. Formats of one Work are Editions, never separate installments.
- **D3.** Prevent, don't patch: a Series is built from trusted evidence, so a shelf can't mix lines. We don't build a "fix the mixed shelf" screen.
- **D4.** Uniformity: the shelf, the item page and the helper apps use one source for order and count.
- **D5.** Privacy first: nothing about the user's library is sent out. Fetching public Wikidata/Fandom data is allowed; sending the user's list of titles is not (the Fandom plugin must only look up QIDs the Library already holds).
- **D6.** Characters stay off item pages. **Decided by Shy 2026-10-19 (Q2):** characters return only as a read-only list on the Universe page, each with a visible source and spoiler respect. No portraits or relationship graph.

New decisions need Shy (section 7). Nothing in section 4 onwards is decided until then.

## 4. UI behaviour (proposed)

### 4.1 Finding a Universe
| Where | What the person sees |
|---|---|
| Collections page (desktop/tablet) | A "Universes" group (already exists as "Shared universes"); tiles open the Universe page. Renamed to match vocabulary. |
| Phone | Collections is reached from My Library (3-tab dock stays Home, Search, My Library). A Universe appears as a tile, never a tab. |
| Search | Universe results already exist; they open the Universe page. |
| Item page | One quiet line under the hero "Part of the Dune Universe" linking to it (not a tab). Only if the Library holds 2+ Series or formats in it (Q4). |

### 4.2 The Universe page
A single page, not a management screen. Sections in this order, each hidden if empty:
1. **Hero**: name, cover stack, one line of totals ("3 Series, 14 Works you own, 2 formats").
2. **Series**: each Series as its own shelf, in correct order, showing owned vs known (missing marked, not hidden if the user chose that in preferences). Novels, TV and film lines are separate Series even when they share a name (D3).
3. **Standalone Works** that belong to the Universe but to no Series.
4. **Connections** (desktop and tablet; a simple list on phone): the existing explorer, reduced to what is backed by evidence: Cast (people across formats), Adaptations (book to film to show), Characters (Q2). Path finder and family tree stay desktop-only unless Shy wants them elsewhere (Q3).
5. **Edit Universe** (desktop admins only, inside Editing Mode style, no separate management screen).

| Situation | What the person sees |
|---|---|
| Normal | Sections above. |
| Empty graph (not enriched yet) | Series and Works still show; Connections says "Still learning about this Universe" with a quiet progress note, never sample data. |
| Loading | Hero and Series first; Connections loads after. |
| Error | "Can't reach the Engine" with retry; Series from cache if available. |
| Partly ready | A small "Enrichment running" indicator in the same blue/green style as the ingestion indicator (ingestion spec). |
| Lore plugin off | Connections still works from Wikidata; a line "Add Fandom details" appears for admins only, linking to the plugin settings. |

### 4.3 Series inside a Universe (bug 23)
- A Series card shows its own numbering (1, 1.5, 2 …). Decimal positions are kept and displayed as the source gives them.
- Only "main sequence" members count toward numbering and "x of y". Supplementary, collected content and broader-context items appear in a separate "Also in this world" row under the Series, never inside the numbered run.
- If a title matches two lines, it goes in the one the Wikidata classification says; if neither is trusted, it goes to Needs Review rather than guessing (D3).

### 4.4 Helper apps (phone, TV, Roku)
| App | Today | Proposed |
|---|---|---|
| Android phone/TV, iPhone/iPad, TV web | Universe lists Series | Same hero + Series + "Also in this world", plus a read-only Cast list and Adaptations list. No graph canvas. |
| Roku | Collections row only | Universe tile opens a list of Series (no connections). |
| All | No characters | Characters only if Q2 says yes, as a plain read-only list. |
Helpers never edit. The Engine keeps its permission check, and Universes the person can't see return "not found", as today.

## 4A. The companion idea and a reliable-first feature menu (added v2, from Shy's 2026-10-19 steer)

**Shy's goal.** Your phone (or any screen) knows what you are watching, reading or listening to and where you are in it, and gives you spoiler-safe context: who someone is, how they connect to others, where else you have met them. TV is the headline case, not the only one. AI makes it richer but is an enhancement; the base must work without it.

### 4A.1 What already exists that this builds on (verified by reading code; nothing run)
- **The Engine already knows what a profile is playing and where.** One live state per profile (device, client, playing/paused, position, duration) and a record per play; clients send a heartbeat every 10 to 15 seconds (Dashboard 10 s; Roku, Android TV, Android phone, iOS 15 s). A phone on the same profile can read it. Gaps: only one state per profile (two TVs in one profile overwrite each other); no list of "what's playing in the house" for ordinary profiles; the Engine does not know what a Chromecast receiver is playing (the sender does).
- **Characters, their actors and where they appear are stored**, with a role, narrative-time and "spoiler for" qualifiers, plus typed relationships (parent, spouse, ally, opponent, member of, based on, and more). "Which works does this character appear in" already works for works you can see. Not stored: which episode, chapter or minute a character first appears. Two useful Wikidata properties (a character's friends/enemies and notable-work links) are not read at all.
- **Subtitles** are downloaded from SubDL and turned into a standard format, but the Engine does not read their text lines, and embedded subtitles are only noted by language. **Speech-to-text exists only as an unused stub, which the Developer tools spec removes (D-V).** The local AI is off by default and its smallest model is chosen on machines with under 8 GB of memory.
- **Chapters** are read from video files. Intro and credits markers are guessed by a simple rule. Whether chapter times are stored: unverified.
- The Wikidata helper library has no character-specific tools; it can fetch any claim on any item, so character support is built in the Library itself.

### 4A.2 Three layers (each works without the next)
**Layer 1 — Universe tools (no AI, no playback needed).** Built from data we already hold or can fetch from Wikidata.
1. **"Also appears in" (the Sinestro moment).** Open any character, actor or place and see every Work in your Library that shares it: the HBO series, the comics you own, an audiobook. Reliable because it matches the same Wikidata item, never a name. If a Work has no character data on Wikidata, we say so rather than guess.
2. **Spoiler-safe who's who.** Cast and characters for the Work, hidden or blurred beyond your progress where we know it, limited to "no plot details" where we don't.
3. **Start here / next in this world.** Reading and watching order across formats, using the Series fix (U-WP3).
4. **Connections map.** Family, allies, enemies, organisations, adaptations (existing explorer, trimmed).
5. **Timeline.** Story-time versus release-time for a Universe, where Wikidata gives story dates.
6. **Actor lens.** "Who plays this?" and "what else of theirs do you own?" (people data exists).
7. **Compare adaptations.** Book versus film versus show: cast, creators, differences in length (exists, needs a tidy page).
8. **Universe progress.** "You've watched 4 of 9 in the main run", respecting the Series scope rules.
9. **Previously on.** Before an episode, the last owned episode's synopsis (retail or Wikidata text, no AI).

**Layer 2 — Companion follow-along (no AI).** The phone shows a "Now playing" card; tapping opens the companion for that Work at your position.
- **Position**: from the Engine's live player state (TV, Dashboard, phone) with a 10 to 15 second delay, or **manual** ("I'm at episode 3 / chapter 12") when nothing is playing or the source isn't ours (e.g. a Chromecast). Manual mode is first-class, not a fallback, so it works for books and audiobooks too.
- **Context at your position**: the episode or chapter title and synopsis, the cast and characters for that episode (TV episodes carry their own cast on Wikidata and retail providers), what has been introduced so far, and everything from Layer 1 for anything you tap.
- **Spoiler control** per profile: "up to where I am" (default) or "whole Work".
- Works for: video (episode/film-chapter level), books (chapter), audiobooks (chapter), comics (issue). Music gets credits and "more from this world".
- **Honest limits**: minute-by-minute facts ("they are in Mordor now") need scene data we don't have from any reliable source; Layer 2 stops at episode/chapter level.

**Layer 3 — AI enhancements (experimental, off by default, built ahead of time off-peak).**
- **Subtitle reading.** Parse subtitle lines into time-stamped text (a prerequisite, not AI). Then plain name matching: when "Frodo", "Mordor" or an alias appears in a window of subtitles, tag that window with that character or place. This is deterministic and checkable, not an AI guess.
- **Per-chapter recaps.** A local model summarises subtitle windows overnight; summaries are stored with the subtitle lines they came from, labelled "AI-generated", and character names are checked against the graph. The phone only ever reads stored results; nothing is inferred live.
- **Who is speaking.** Not promised. Subtitles rarely name speakers, and speaker identification needs audio analysis that isn't in the Library today (the speech-to-text piece is an unused stub that the Developer tools spec removes, and no speaker-separation tool is present). Treat as a research spike (U-WP15), not a feature.
- **Hardware reality.** The Local AI is off by default and the small model picked for low-memory machines is unlikely to write good recaps; this layer would need an "advanced" setup and long overnight runs per film. Spike first, measure, then decide.

### 4A.3 What the companion screen looks like (phone)
| Situation | What the person sees |
|---|---|
| Something playing on this profile | A "Now playing" card on Home (e.g. "The Fellowship of the Ring, on Living Room TV") with a Companion button |
| Open the companion | Title, your position, "Who's here" (characters up to your position), "Where you've seen them" (other Works you own), "Previously", and a link to the Universe page |
| Nothing playing | A "Companion" button on every item page's menu; pick your episode/chapter by hand |
| Spoiler level | A small switch at the top: "Up to here" / "Everything" |
| Data thin or unavailable | "No character details for this one yet" with what we do have (cast, synopsis); never invented content |
| AI layer on | Extra "Recap" and "In this scene" cards, each marked AI-generated with its source lines |
| Desktop | Same companion as a side panel beside the player is a later option (Q14) |

### 4A.4 Data needed for the companion
- Episode- and chapter-level appearance: take cast from TV episode items and chapter data; add an "first seen at" marker on character-to-Work links where Wikidata or retail data gives it; otherwise leave empty (work-level only).
- A companion session: lets a phone follow the right device even when several devices play in one profile (needs more than one live state per profile; today there is one).
- Subtitle text index (Layer 3 prerequisite) and, optionally, chapter times stored.
- Read the two unused Wikidata character properties for allies/enemies.
- Privacy: the phone only sees what **your own profile** is playing. Whether others in the household can see it is a decision (Q12). Nothing is sent outside the machine (D5); Wikidata lookups use item IDs only.


## 5. Data / model changes

- **Series identity.** Group by Wikidata ID plus media type, falling back to the retail label only when no ID exists. Today grouping is by label text and shelf lookup by ID across all media types.
- **Ordering.** One ordering source for shelf, item page and helper apps: the Wikidata manifest position first, the work's own position second, decimals preserved. Today the work's own number wins and is cut to a whole number.
- **Membership scope.** The shelf reads the manifest's scope (main sequence, supplementary, collected, broader, unpositioned). Today it ignores it.
- **One Universe record.** The Universe shelf and the graph root are tied to one stored record by Wikidata ID, instead of two records joined by coincidence.
- **Alignment rules (where sources disagree).** Retail providers win for display text and cover art; Wikidata wins for identity, Series membership and order; the lore plugin only adds supplementary facts and never moves a Work (matches "story facts do not silently move files"). Audiobooks have no Wikidata fallback today (Apple only); this plan adds one.
- **Series position when a Work has several series statements.** Store each (series ID, position) pair instead of one number.
- **Pre-release:** reset and re-ingest is acceptable (CLAUDE.md rule 6). No migration.

### Unused or half-finished Universe code (from the audit; each needs a decision, Q7)
Safe to remove (High confidence, no callers):
- Engine actions `/universe/{id}/health`, `/universe/{id}/cross-media` (plus the service method behind it), and their Dashboard wrapper and wire types.
- Dashboard methods `GetUniverseCandidatesAsync`, `GetUniverseUnlinkedAsync`, their view models, and `UIOrchestratorService.GetUniverseAsync`.
- The "lore delta discovered" live listener (the Engine never sends it; the banner works from a page-load check).
- The no-op `ScanUniversesAsync` library-scan stub, its result type and the always-zero `UniversesUpserted` field.
- Config keys no code reads: `lore_delta_check_on_explorer_open`, `universe_title_search_auto_accept`, `universe_xml_write_debounce_seconds`, `minimum_universe_work_count`, plus `canon_discrepancy_detection` and `era_actor_resolution` if confirmed unread.
- A second copy of eight Universe data shapes in the Dashboard (`ChronicleExplorerDtos.cs`), which breaks the rule that the Contracts project alone owns wire types. Move to Contracts and delete the copies.
Needs your call (might be future features):
- `/universes` and root `/universe/{id}` (the helper apps use `/api/v1/universe/{id}` instead).
- `deep-enrich` action (docs wrongly say Chronicle Explorer triggers it).
- Candidate accept / reject / batch-accept actions (a "suggest Universe membership" idea never built).
- Character portrait, a Universe's character list, `/library/assets/{id}` and the enrichment trigger action.
Docs to correct: `universe-graph.md` (wrong parameter names, a live event that never fires, a setting that does nothing, missing actions), `hydration-and-providers.md` (deep-enrich claim), and the two docs that describe different "universe detail" handlers.
Guardrail and fixture tests that pin dead code will be edited with a stated reason; wire fixtures refreshed with `tools/Update-ContractFixtures.ps1`.

## 6. Work packets (sizes and files to be confirmed by Claude Code)

### U-WP1 — Clean-up of unused Universe code and docs
- **What it does:** remove the High-confidence items above, correct the docs; apply Shy's answers for the "needs a call" items.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs`, `LibraryEndpoints.cs`, `CharacterEndpoints.cs`, `src/MediaEngine.Web/Services/Integration/*`, `config/hydration.json`, `src/MediaEngine.Domain/Configuration/HydrationSettings.cs`, `docs/architecture/universe-graph.md`, guardrail and fixture tests.
- **Depends on:** Q7.
- **Size:** Standard.
- **Done when:** build has 0 warnings, all tests pass, wire fixtures refreshed, and a search finds no reference to the removed names.

### U-WP2 — Move the Dashboard's second set of Universe data shapes into Contracts
- **What it does:** Chronicle Explorer uses the shared wire types; local copies deleted.
- **Files likely touched:** `src/MediaEngine.Web/Models/ViewDTOs/ChronicleExplorerDtos.cs`, `src/MediaEngine.Contracts/*`, Contracts tests.
- **Depends on:** none (can run with U-WP1).
- **Size:** Standard.
- **Done when:** the explorer renders unchanged and a guardrail test shows no Dashboard JSON copy of these shapes.
- **Status:** Built (2026-10-19, branch `claude/universe-wave2-int`).

### U-WP3 — Fix Series identity, scope and ordering (bug 23)
- **What it does:** group by ID plus media type; read membership scope; keep decimals; one ordering rule shared by shelf and item page; "Also in this world" row.
- **Files likely touched:** `CollectionBrowseReadService.cs`, `CollectionCatalogReadService.cs`, `CollectionAssignmentService.cs`, `WikidataSeriesManifestHydrationService.cs`, `DetailCompositionOrchestrator.Sequence*.cs`, Dashboard Series shelf components.
- **Depends on:** U-WP0 (Expanse data check, below).
- **Size:** Complex.
- **Done when:** a test library with Expanse novels, novellas and TV shows three separate Series, numbered correctly, decimals intact, shelf and item page agreeing.
- **Status:** Built (2026-10-19, branch `claude/universe-wave2-int`; data store upgrade step 0012). Existing libraries need a data store reset and re-ingest (section 7): shelves made before this step have no media type, so they are not reused and can be left behind beside the new ones.

### U-WP0 — Confirm the real Expanse mix-up
- **What it does:** read-only check on Shy's actual data to confirm which of the code paths in section 0 causes bug 23 (so U-WP3 fixes the real cause, not a guess).
- **Files likely touched:** none (reads the Engine log and data store).
- **Depends on:** none. **Size:** Mechanical.
- **Done when:** a short note names the confirmed cause.

### U-WP4 — Universe record and alignment rules
- **What it does:** one stored Universe tied to the Wikidata ID; store several (series, position) pairs per Work; audiobook Wikidata fallback; written precedence rules in the data-store rules.
- **Files likely touched:** `NarrativeRootResolver.cs`, `ReconciliationAdapter.ClaimMapping.cs`, `config/pipeline-priority-defaults.json`, storage schema + repositories.
- **Depends on:** U-WP3, DT-WP9 (priority changes via DT-WP20 weights). **Size:** Complex.
- **Done when:** a book, its audiobook and its TV adaptation land in one Universe with three correct Series.

### U-WP5 — Universe page
- **What it does:** the page in 4.2, entry points in 4.1, empty and partly-ready states, the "Part of …" line on item pages.
- **Files likely touched:** `ChronicleExplorer.razor` and `Components/Universe/*`, `Collections.razor`, `CollectionsPage.razor`, `ShellNavigation.cs`, item detail hero.
- **Depends on:** U-WP2, U-WP3. **Size:** Complex.
- **Done when:** from Collections you can open a Universe, see Series in order, open Connections, and every empty state renders at phone, tablet, desktop and TV widths (visual checks per `src/MediaEngine.Web/CLAUDE.md`).
- **Status:** Built (2026-10-20, branch `claude/universe-wp5-page`). Page at `/universe/{qid}` reads one Engine action (`/universe/{qid}/overview`) built on the same read as the helper apps. Works linked straight to a Universe (no Series) show as Standalone and count toward the totals and the "Part of" format test, reading "Series or formats in it" literally. The "show missing" rule now lives in one shared place used by the page and the Series panel. Edit Universe opens in today's `EditingDrawer`; when the Details overlay and page-based Editing Mode spec (Locked v2, D3 "no drawer anywhere") replaces the drawer with its banner and section slot (its WP3), the Universe page's Edit moves with it.

### U-WP6 — Evidence-backed Connections (Cast, Adaptations, Characters per Q2)
- **Depends on:** U-WP5, Q2. **Size:** Complex.
- **Done when:** every character or link shown carries a visible source, and an unenriched Universe shows the "still learning" state with no sample data.

### U-WP7 — Engine actions for helper apps
- **What it does:** extend `/api/v1/universe/{id}` with "Also in this world", Cast and Adaptations; keep profile visibility rules.
- **Files likely touched:** `CollectionClientEndpoints.cs`, `UniverseClientReadService.cs`, Contracts v1 types and tests.
- **Depends on:** U-WP3. **Size:** Standard.
- **Done when:** a profile that can't see the Universe gets "not found" and one that can gets the new sections; Contracts tests pass.
- **Status:** Built (2026-10-20, PR 289). For U-WP8: a Series whose only visible items are "Also in this world" members returns an empty numbered run, and the run shelf's see-all route may list Also members too.

### U-WP8 — Helper app screens (Android, Apple, TV web, Roku)
- **Depends on:** U-WP7. **Size:** Standard (one packet per client, at most two at a time).
- **Done when:** each app shows the 4.4 layout against a test Engine; Roku lists Series.

### U-WP9 — Lore plugin alignment with the Developer tools family
- **What it does:** Official manifest touch-up (DT D-K), declare what it contacts, route Fandom requests through the shared rate-limited client, send only QIDs already in the Library (D5), link from the Universe page to its Plugins state card. No private switch or health display.
- **Depends on:** U-WP5, DT-WP1, DT-WP2. **Size:** Standard.
- **Done when:** an admin can turn it on from one place and a test proves no title list leaves the machine.

### U-WP10 — "Also appears in" across Works you own
- **What it does:** from any character, actor or place, list every owned Work that shares it, grouped by format; honest empty state when Wikidata has no character data for a Work.
- **Files likely touched:** `FictionalEntityRepository.cs`, `UniverseGraphQueryService.cs`, `SharedEntityEditorEndpoints.cs` appearance logic, Universe page components, Contracts.
- **Depends on:** U-WP5. **Size:** Standard.
- **Done when:** with an HBO Lanterns-style series and a comic that share a character QID in a test library, the character page lists both.
- **Status:** Built (2026-10-20, PR 294). Per Shy there is no character page: the "Also appears in" panel opens from the Universe page's Cast rows (and later from the Characters list, U-WP6).

### U-WP11 — Character appearance data and spoiler rules
- **What it does:** read the two unused Wikidata character properties; add "first seen at" (episode/chapter) where sources give it; one spoiler rule used by every surface.
- **Files likely touched:** `RelationshipPopulationService.cs`, `RecursiveFictionalEntityService.cs`, `config/providers/wikidata_reconciliation.json`, storage schema, tests.
- **Depends on:** U-WP4. **Size:** Complex.
- **Done when:** a test show hides characters first seen after the viewer's episode and labels unknown ones "no plot details".

### U-WP12 — Companion session service
- **What it does:** let a phone follow one chosen device of its own profile, allow several live states per profile, add manual position, expose it under `/api/v1`.
- **Files likely touched:** `PlayerService.cs`, `PlayerEndpoints.cs`, player_sessions schema, Contracts v1, tests.
- **Depends on:** none. **Size:** Complex.
- **Done when:** two devices on one profile no longer overwrite each other and a phone reads the chosen one within about one heartbeat.
- **Status:** Built (2026-10-20, PR 286; data store upgrade step 0013). The shared player and its queue are unchanged; each device now keeps its own live state beside it, and `/api/v1/companion` lets a phone follow one device of its own profile or hold a manual position.

### U-WP13 — Phone companion screen (Dashboard first, then native apps)
- **Depends on:** U-WP10, U-WP11, U-WP12. **Size:** Complex.
- **Done when:** the 4A.3 table works at phone width for a film, an episode, a book chapter and an audiobook chapter, including manual mode and the spoiler switch.

### U-WP14 — Subtitle text index
- **What it does:** parse subtitle files into time-stamped lines; extract embedded subtitles; store chapter times; match names and aliases to graph entities (plain matching, no AI).
- **Depends on:** U-WP11. **Size:** Complex.
- **Done when:** reads stored normalised subtitle tracks regardless of source; a test film's subtitle file yields "Frodo" and "Mordor" tags at the right time windows.

### U-WP15 — AI feasibility spike (research only)
- **What it does:** try off-peak recaps and speaker identification on three real titles; report quality, time per title and hardware needed. No shipped feature. Would run as an Official plugin asking for abilities (section 6A); speaker identification needs a new ability first.
- **Depends on:** U-WP14, DT-WP15, DT-WP27, DT-WP28. **Size:** Standard.
- **Done when:** a written report with samples and a go / no-go per feature, reviewed by Shy.

## 6A. Landing alongside the Developer tools spec (v3, status In progress) and the other Locked specs

The Developer tools spec (cited as DT, its packets as DT-WP#) is large, Locked, and being built in waves with an ingestion comparison after each. Packets in this document are cited as U-WP# so the two numbering schemes never mix. This section was checked against DT **version 3** (which added Local AI as the "engine room" and made every AI feature an Official plugin).

**What lines up, no change needed**
- Wikidata stays core; plugins only supply data into the Priority Cascade (DT D-J). The lore plugin adds supplementary facts and never moves a Work.
- Fandom lore stays an in-server Official plugin, only getting a manifest touch-up (DT D-K). New items start off and need an approval that says what leaves the machine (DT D-C): same as our Q8 and D5.
- AI results are labelled suggestions ranked below providers and people, shown somewhere or not computed at all (DT D-T). Our alignment rule (retail wins display, Wikidata wins identity and order, lore/AI only supplement) is the same ladder.

**What changes in this spec because of it**
1. **U-WP9 (lore plugin) shrinks.** The switch, health display and approval come from DT's state card and approval step (DT-WP1, DT-WP2). U-WP9 only: the Official manifest touch-up, declaring what it contacts (Wikidata and Fandom, item IDs only), moving its Wikidata calls onto the shared rate-limited client, and linking the Universe page's "Add Fandom details" line to its Plugins page card.
2. **AI recaps are an Official plugin, not a Local AI feature.** Under DT D-R and D-S, anything AI-powered is a plugin that asks for abilities (write text quick, careful or long; pull out structured facts; classify), never a model. So "Universe recaps" would be an Official plugin using the plugin scheduler (DT-WP15) and the shared queue (DT-WP27), where a person's request goes first, then ingestion, then the Library's background work, then plugins. It appears on the Plugins page under "Uses Local AI", off by default, with approval, run records and usage records, and its output is marked AI (DT D-T). The subtitle text index and name tagging (U-WP14) are not AI and stay core.
3. **Speaker identification cannot rely on today's code.** DT D-V removes the speech-sync stub and its Whisper service, and lists "transcribe speech" only as a later ability. The U-WP15 spike therefore treats "who is speaking" as research that would need a new ability added through DT's contract first; it is not promised, and nothing in this plan builds on the removed stub.
4. **No new Settings page.** The lore plugin and the recaps plugin live on the Plugins page; Local AI keeps Status, Model and hardware, Budget and Usage (DT D-Q); the per-profile spoiler level lives in the profile's own preferences. The phone companion is a Library surface, not an Integration.
5. **The Universe page and companion are core, not plugin pages.** DT-WP16 lets plugins add pages from the Library's page types, but no plugin can alter Universe structure or Series order.
6. **Subtitle sources become declarative plugins later.** U-WP14 reads the Library's stored, normalised subtitle tracks, never a specific source.
7. **Playback events.** The companion reads the Engine's existing playback state; if the unified event list in DT D-W adds playback events, the companion uses that list rather than inventing its own.

**Other Locked specs**
- **Ingestion and readiness (v4).** "Ready" includes "placed in its show, album or series". U-WP3 changes how Series identity and membership are computed, so its done-when must include that the Ready rule and the display-query guardrail still pass, and the TV "each file is its own show" fix (bug 22) stays owned by that spec; U-WP3 does not re-fix it.
- **Settings revamp (v1.1).** Menu and look are unchanged by this plan.

**Sequencing so the specs do not collide** (ingestion is the shared risk: DT-WP0 captures an ingestion baseline and every DT wave compares against it; DT-WP28 also changes ingestion by adding the ingestion-assist hook)
- **U-WP1 (clean-up) and U-WP2 (Contracts copy):** safe any time except alongside a DT packet touching the same files (DT-WP5 and DT-WP32 touch the development-only actions and tester tabs; DT-WP9 touches provider JSON; DT-WP28 and DT-WP30 touch AI settings and `config/ai.json`). Never run in parallel with those.
- **U-WP3 and U-WP4 (Series identity, Universe record, alignment):** change what ingestion produces, so they land **between DT waves, with the baseline re-captured** immediately before and after, and not in the same window as DT-WP28. U-WP4's change to `config/pipeline-priority-defaults.json` waits for DT-WP9 and is then expressed through the Library-set weights (DT-WP20), not by editing the file in parallel.
- **U-WP5 to U-WP8, U-WP10:** UI and read-only Engine actions; safe alongside DT.
- **U-WP11 to U-WP13 (companion):** do not depend on DT, except that U-WP12 adds playback-state storage, which goes through the numbered data-store upgrade scripts like any other change.
- **U-WP14 (subtitle index):** independent of DT. **U-WP15 (AI spike) and any recaps plugin:** wait for DT-WP15 (plugin host services), DT-WP27 (engine room and queue) and DT-WP28 (AI plugin pattern).
- Recommended order across both: DT waves 1 to 2, then U-WP1 to U-WP4, then DT waves 3 and 4 alongside U-WP5 to U-WP14, then U-WP15 after DT wave 4.

## 6B. Before starting any packet: re-check for collisions (Shy, 2026-10-19)

Before Claude Code starts the first packet, and again before each wave:
1. Re-read `engineering/specs/developer-tools.md` (version and status), `engineering/specs/ingestion-and-readiness.md`, `engineering/specs/settings-revamp.md` and `engineering/README.md` on main for changes since this spec was locked.
2. List which DT packets and waves are merged, in progress or not started, and check the sequencing rules in 6A against that. If a conflicting packet is in flight, wait or ask Shy; do not run in parallel.
3. Search main for changes to the files this packet lists (`git log` on those paths since the spec's date). Anything touching them from another spec is a collision to report before starting.
4. If the Developer tools spec has a newer version that changes plugin, AI or ingestion behaviour, stop and ask Shy before building U-WP9, U-WP14, U-WP15 or anything touching ingestion.
5. Record the result in one line at the top of the packet's pull request.

## 7. Risks and open questions

**Answers (Shy, 2026-10-19): Q1 to Q15 all go with the recommendation (★); Q2 chose (b).** Q7 detail: delete the High-confidence items now; delete deep-enrich, candidate accept/reject, character portraits and the asset action; keep `/universes` one release then remove.

**Further decisions (Shy, 2026-10-19, while building U-WP5 and U-WP10):** Edit Universe reuses the existing Universe editor, shown in-line beside the Universe page in the same side drawer Editing Mode uses (not a pop-up), desktop administrators only. "Also appears in" is a panel on the Universe page, not a character page.

**Landing alongside the locked Developer tools spec:** see section 6A. That spec (`engineering/specs/developer-tools.md`, Locked v2, 2026-10-19) owns the plugin platform and the shared state card, approval rule, health words and run records; this spec defines no plugin mechanics.

**Risks**
- U-WP3 changes how shelves are grouped, so existing libraries need a re-ingest (acceptable pre-release). Performance: grouping by ID is cheaper than by text, and the known slow library list (separate item) is unaffected until that investigation.
- Wikidata data for fiction is uneven. Some Universes will have thin graphs; the "still learning" state matters.
- Removing endpoints could break an external script we can't see (Q7).

**Open questions (recommendation marked ★)**
- **Q1. Where does a Universe live in the menus?** (a) Inside Collections as a "Universes" group ★ (b) its own top-level item (c) only via search. ★ keeps the 3-tab phone dock and avoids a new menu.
- **Q2. DECIDED (b), 2026-10-19.** Do characters come back? (a) No, only Cast (people) and Adaptations (b) a read-only Characters list on the Universe page only, each with a visible source ★ (c) full graph with portraits. ★ gives the "who is who" value without the old maintenance load.
- **Q3. Path finder and family tree:** (a) keep on desktop only ★ (b) remove (c) bring to tablet. ★ they are heavy and rarely useful on small screens.
- **Q4. "Part of …" line on item pages:** (a) yes, only when 2+ Series or formats are owned ★ (b) never (c) always.
- **Q5. Can a user create a Universe by hand?** (a) No, evidence only ★ (b) yes, admins only. ★ avoids a second source of truth (D3).
- **Q6. Missing entries (known but not owned):** (a) respect the existing preference ★ (b) always show (c) never show.
- **Q7. Unused code:** (a) delete High-confidence items now and ask on the rest ★ (b) delete everything unused (c) deprecate only. For the "needs a call" list I recommend: delete `deep-enrich`, candidate accept/reject, character portraits and the asset action; keep `/universes` one release, then remove.
- **Q8. Lore plugin:** (a) keep, off by default, admin-visible ★ (b) turn on by default (c) remove. ★ it is the only source for non-Wikidata lore, but privacy-neutral only when it sends QIDs only.
- **Q9. Roku:** (a) Series list only ★ (b) nothing beyond Collections.
- **Q10. Spoiler default for the companion:** (a) up to my position ★ (b) whole Work. Per-profile switch either way.
- **Q11. Build order for the companion:** (a) Layer 1 first, then Layer 2, then the AI spike ★ (b) Layer 2 first (needs Layer 1 data anyway) (c) AI spike in parallel.
- **Q12. Can other household members see what you're playing?** (a) No, only your own profile ★ (b) admins can (c) a per-profile switch.
- **Q13. Where first?** (a) Dashboard on phone first, native apps after ★ (b) native apps first. ★ one codebase to prove it; native apps already read player state.
- **Q14. Desktop side panel beside the player:** (a) later ★ (b) now (c) never.
- **Q15. AI layer:** (a) spike only, decide after measuring ★ (b) build recaps now (c) drop AI from this plan.

## 8. What is missing to make Universes a real, useful tool

1. A way to get there (today: typed address only).
2. Correct, consistent Series order (bug 23).
3. A reading list / watch order across formats ("start here") for a Universe (not in this draft; suggest a follow-up spec once U-WP3 lands).
4. Visible "what's still missing" per Universe, tied to the user's missing-items preference.
5. A clear enrichment state, so empty never looks broken.
6. Helper app parity.
7. Spoiler control for Characters if Q2 = (b) or (c): the data model already carries spoiler qualifiers; the page must honour them.

## 9. Gaps and drift

- **Docs vs code:** see section 5 (wrong parameters, a live event that never fires, a setting that does nothing).
- **Duplicate property map:** Dashboard `WikidataPropertyDefaults.cs` copies `config/providers/wikidata_reconciliation.json`; drift unverified.
- **Not verified in this draft:** whether the Fandom plugin is enabled on any real install; which Engine code serves `library/universe-candidates` (the audit found it in `LibraryEndpoints.cs`); the real Expanse data (U-WP0); the default Series source for books.

## Plain English summary

Today a Universe is half a feature: there is a good explorer page that nobody can reach, a shelf that groups by label and can mix up The Expanse's lines, helper apps that only list Series, and a pile of Universe code that nothing calls. This plan makes a Universe a page you open from Collections: your Series in the right order (novels, TV and film kept apart), an "Also in this world" row for extras, and evidence-backed Cast, Adaptations and optionally Characters. Helper apps get the same simple version. One set of rules decides which source wins, and the unused code and wrong docs are cleaned out first. Beyond that, the Universe becomes the backbone of a companion: Layer 1 gives useful, reliable tools such as "this character also appears in your comics"; Layer 2 lets your phone follow what you're watching or reading, at episode or chapter level, spoiler-safe; Layer 3 (AI recaps, name spotting in subtitles) is an experiment we measure before promising. "Who is speaking" is not promised. I need your decisions on the fifteen questions above, mainly Q1, Q7, Q10 and Q11.
