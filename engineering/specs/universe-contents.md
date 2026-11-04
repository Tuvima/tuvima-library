# Spec: Universe contents (more kinds of things, with pictures and logos)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-04 · **Related docs:** [Product overview](../product/overview.md) · [Universes spec (v4, In progress)](universe.md) · [Universal editor v3](universal-editor-v3.md) · [Plugin architecture](plugin-architecture.md) · [Universe Graph architecture](../../docs/architecture/universe-graph.md)

## 0. Where we are today (read from the code; nothing was run)

- A Universe stores **five kinds of thing** in one table (`fictional_entities`): Character, Location (shown as "Places"), Organization (shown as "Factions"/"organisations"), Event and Object. Each is identified by a Wikidata ID (required and unique), and links to the Works it appears in (with role, time and spoiler information).
- **Pictures:** one image address and one local picture per thing. Characters can also have a portrait and a background through the editor (`CharacterPortrait`, `Background`). **No logo, no gallery, and no pictures for the other kinds.**
- **Local edits:** only name and description can be overridden by hand (`fictional_entity_user_overrides`). A thing cannot be added by hand, because every thing needs a Wikidata ID.
- **Not stored at all:** Species, Concepts, and a Timeline as its own view (appearance rows carry narrative time, but there is no timeline screen).
- Fandom Lore (a plugin, off by default) can add facts but only for Wikidata IDs the Library already holds.

## 1. Goal

A Universe should feel alive, not like a page of text. It can hold the full cast of a story world (characters, places, factions, events, objects, species and concepts), each with its own pictures and logos, drawn from Wikidata, optionally from Fandom Lore, and added or improved by hand.

## 2. Scope

**In scope**
- Two new kinds: **Species** and **Concept**. (Events and Objects already exist in the data; they get pictures, a page section and an editor.)
- A **Timeline** as a view over Events and dated appearances (not a stored kind).
- Pictures per thing: a main picture, a logo (factions, places, objects, species), a background, and an optional small gallery.
- Where each fact and picture comes from (Wikidata, Fandom Lore, manual) and which wins.
- Adding a thing by hand to an existing Universe, and removing or hiding one.
- What the universal editor must support for Universe contents (handed to the "Overhaul the editor" thread).

**Out of scope**
- Where a Universe is found and its icon (the "Make Universes easy to find" thread).
- Creating a whole Universe by hand with no Wikidata evidence (universe.md Q5 stays open there).
- Editing on phones or TV (editing stays desktop-only, `EditorDeviceRule.CanEdit`).
- AI-generated pictures or descriptions.
- Relationship graph redesign (path finder, family tree stay as they are).

## 3. Decisions made

Shy answered all open questions on 2026-11-04 (Q1 to Q6 below, resolved). Items marked **(Shy)** came from Shy's 2026-11-04 steer: Universe editing is always available, and pictures/logos for factions and characters are wanted so a Universe "comes alive".

- **P1 (Shy).** Editing the contents of a Universe is always available (to people allowed to edit) on the Universe's own page, through the same pop-over editor as everything else. Nothing here recreates a management screen.
- **P2.** Seven kinds shown to people: **Characters, Places, Factions, Events, Objects, Species, Concepts**. In the data, "Places" stays Location and "Factions" stays Organization (internal names unchanged; the person only ever sees the friendly names).
- **P3.** **Timeline is a view, not a kind.** It lists Events plus dated first appearances, in story order where known and release order otherwise, with a toggle between the two. Reason: nothing to maintain twice (Maintenance).
- **P4.** **A thing may exist without a Wikidata ID** only when a person adds it by hand to a Universe that already has a Wikidata identity. It carries a local ID and a "Added by you" label, is never overwritten by a provider, and is never sent anywhere (Privacy). Reason: a faction that Wikidata lacks is exactly what a collector wants to add.
- **P5.** **Pictures follow one rule everywhere:** a person's own picture beats a provider picture, a Fandom picture never replaces a Wikidata/Commons picture unless the person chooses it, and nothing is downloaded from a source that is switched off.
- **P6.** **Fandom Lore stays off by default** and only looks up Wikidata IDs the Library already holds (universe.md D5). It may supply descriptions, pictures and relationships for any of the seven kinds, always labelled "Fandom".
- **P8 (Shy, 2026-11-04).** Concepts appear only when Fandom Lore is on, including hand-added ones. No gallery now: each thing has a main picture, a logo and a background. Timeline defaults to story order when enough dates exist, otherwise release order. Phone and TV show all seven kinds read-only. Fandom pictures are saved locally only when the person chooses them.
- **P7.** **Spoiler respect extends to every kind.** An Event, Character or Object whose first appearance is in something you have not reached is hidden or blurred, using the existing spoiler rules (`CharacterSpoilerService` logic generalised, U-WP11).

## 4. UI behaviour

### 4.1 Universe page

| Situation | What the person sees |
|---|---|
| Normal | Sections in a fixed order: Series and Works, then **Characters, Factions, Places, Events, Objects, Species, Concepts, Timeline**. A section appears only if it has something in it. Each is a rail of picture tiles (square for most, logo tile for Factions and Objects). Tiles use the shared tile look. |
| Empty Universe | Only Series and Works show. A quiet line: "Nothing is known about this world yet. Turn on Fandom Lore or add things yourself." (the second half only for people who can edit). |
| Loading / partly ready | Tiles appear as Wikidata fills in; pictures fade in. A small "Still gathering details" note until enrichment finishes. |
| Error | Section shows what is already stored; a note says details could not be refreshed. |
| Phone / TV | Read-only. Same sections, rails only, no editing. |

### 4.2 A thing's page

Picture (or logo) at the top, name, kind, short description with a source label (Wikidata, Fandom, You), "Appears in" (Works you own), related things (members of a faction, species of a character, where an event took place), and gallery. Hidden-by-spoiler details show as "Hidden until you reach it".

### 4.3 Timeline

A horizontal, scrollable strip grouped by era or year. Each stop is an Event or a first appearance with its picture. Toggle: **Story order** (default when enough dates exist) or **Release order**. Empty if fewer than three dated items exist; the section is then not shown.

### 4.4 Editing (desktop only)

Edit in the Universe's "..." menu opens the pop-over editor over the page. For Universe contents it adds:
- A **family tree on the left** with Universe at the top and the seven kinds as branches, so any thing is one click away.
- **Details:** name, kind, description, aliases, and a "belongs to" link (a character to a faction, a faction to a place).
- **Pictures:** main picture, logo, background and gallery. Each slot shows the current picture large, with Replace (from a file, a web address, or provider choices) and Remove. A person's own picture is marked "Yours".
- **Match:** the Wikidata match (or "Not on Wikidata, added by you"), with the usual side-by-side before and after.
- **History:** what changed and from where.
- **Add** at the top of any kind branch: "Add faction" and so on, giving a name and kind; the rest is optional.
- Nothing saves until **Save changes**, which shows **Review changes** first. Provider facts stay read-only; your edits are layered on top.

## 5. Data / model changes

- Allow two more kinds, **Species** and **Concept**, in the entity table's kind rule. Pre-release: a reset is acceptable, no migration (CLAUDE.md Never 6).
- Allow a **local-only ID** for hand-added things, with a flag "added by user"; the Wikidata ID becomes optional for those.
- **Pictures:** one place for all of a thing's pictures with a slot (Portrait, Logo, Background, Gallery), source (Wikidata/Commons, Fandom, You) and chosen-or-not. Reuses the existing artwork asset service and `EntityAsset` slots, extended to every kind; characters' existing portrait and background migrate into it with no behaviour change.
- **Overrides** widen from name and description to also include aliases, the "belongs to" link, and which picture is chosen per slot.
- **Relationships between things** (member of, located in, species of, took place at) are stored as links, with source and confidence, so Fandom and manual links sit beside Wikidata ones.
- **Timeline** needs no new table: it reads Event rows and appearance rows (narrative time and release dates already exist).

### Where each kind comes from

| Kind | Wikidata (main source) | Fandom Lore (optional) | Manual |
|---|---|---|---|
| Character | Fictional character, with appearances in the Works you own | Bio, picture, relations | Yes |
| Place | Fictional location | Description, map, picture | Yes |
| Faction | Fictional organisation, with its logo from Commons where one exists | Description, emblem | Yes (logo is the main reason) |
| Event | Fictional event or battle, with date and participants | Description, order | Yes |
| Object | Fictional object, vehicle, device or document | Description, picture | Yes |
| Species | Fictional species or race | Description, picture | Yes |
| Concept | Little on Wikidata; mostly absent | Lore terms and glossary | Yes |

Most Wikidata coverage is strong for Characters, Places and Factions and thin for Events, Species and Concepts, so those sections will often be empty unless Fandom is on or the person adds things. The exact Wikidata classes and properties used per kind are decided in WP1, with the Tuvima.Wikidata library extended if it lacks a call (see WP2).

## 6. Work packets

### WP1 — Kinds and where Wikidata finds them
- **What it does:** Adds Species and Concept; defines per kind the Wikidata classes/properties; extends the enrichment to fetch Events, Objects and Species with their dates and participants.
- **Files likely touched:** `src/MediaEngine.Domain/Constants/FictionalEntityType.cs`, schema for `fictional_entities`, `src/MediaEngine.Api/Services/UniverseEnrichmentService.cs`, tests (to be confirmed).
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test library shows a Species and an Event in a Universe from stored Wikidata fixtures.

### WP2 — Wikidata library support (only if a gap is found)
- **What it does:** If WP1 shows `Tuvima.Wikidata` cannot return a kind's members or Commons logo, add the call there, with docs, tests and a version bump (that repo's rules).
- **Files likely touched:** `tuvima-wikidata` (to be confirmed), `tuvima-docs`.
- **Depends on:** WP1 findings
- **Size:** Standard
- **Done when:** the new call is covered by offline tests and documented.

### WP3 — Pictures for every kind
- **What it does:** Portrait, Logo, Background and Gallery slots for every kind; source and chosen flag; fetch from Commons/Fandom when allowed; right-sized pictures.
- **Files likely touched:** `ArtworkAssetService.cs`, `EntityAsset.cs`, `ArtworkCanonicalHelper.cs`, storage (to be confirmed).
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** a faction shows a logo tile and a character keeps its existing portrait.

### WP4 — Hand-added things and wider overrides
- **What it does:** Local-ID entities, aliases, "belongs to" link, per-slot picture choice, delete/hide.
- **Files likely touched:** `FictionalEntityRepository.cs`, `FictionalEntityEditSubject.cs` (to be confirmed).
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a hand-added faction survives a re-enrichment and never leaves the machine.

### WP5 — Universe page sections, thing page, Timeline
- **What it does:** Section rails, thing page, Timeline view with story/release toggle, spoiler respect for all kinds.
- **Files likely touched:** `UniversePageReadService.cs`, Dashboard Universe components, `CharacterSpoilerService.cs` (to be confirmed).
- **Depends on:** WP1, WP3
- **Size:** Complex
- **Done when:** the page shows only non-empty sections and hides an unreached Event.

### WP6 — Editor support for Universe contents
- **What it does:** Tree, Details, Pictures, Match, History and Add as in 4.4, inside the pop-over editor.
- **Files likely touched:** edit-session services and editor components (to be confirmed); coordinated with the editor overhaul.
- **Depends on:** WP3, WP4
- **Size:** Complex
- **Done when:** adding a faction with a logo and saving shows Review changes first, then the logo on the page.

### WP7 — Fandom Lore for all kinds, and docs
- **What it does:** Plugin returns data for the seven kinds with "Fandom" labels; public docs explain the why (sources, spoilers, what is yours).
- **Files likely touched:** `src/MediaEngine.Plugin.FandomLore`, `tuvima-docs` (to be confirmed).
- **Depends on:** WP1, WP3
- **Size:** Standard
- **Done when:** with the plugin on, a Concept appears; with it off, none does.

## 7. Local checks

- **L1.** On a real Universe (for example Dune or The Expanse), check which of the seven sections have content from Wikidata alone, and note the thin ones.
- **L2.** Turn on Fandom Lore and see whether pictures and descriptions are good and correctly labelled.
- **L3.** Add a faction with a logo by hand; rescan; it is still there.
- **L4.** Look and feel of the tiles, logo tiles and Timeline.

## 8. Risks and open questions

- **Risks:** Fandom pictures carry licences and variable quality (Reliability, legal). Wikidata has little for Species and Concepts, so the sections may feel empty (Reliability). More pictures mean more storage and slower first load (Performance) — addressed by right-sized pictures and lazy loading.

**Open questions:** none. Resolved by Shy 2026-11-04: Q1 hand-added things allowed (yes); Q2 Concepts only with Fandom; Q3 Fandom pictures saved locally only when chosen; Q4 story order by default when dates suffice; Q5 all seven kinds read-only on phone and TV; Q6 gallery left for later.

## Plain English summary

A Universe today knows about characters, places, factions, events and objects, mostly as text with one picture each. This plan adds species and concepts, a timeline, and proper pictures and logos for every kind, so a faction can have its emblem and a character a portrait. Information comes from Wikidata first, optionally Fandom Lore (off unless you turn it on), and from you: you can add things the sources miss, and your own pictures and wording always win. Editing is always available on the Universe's own page through the same pop-over editor, and nothing is saved until you review the changes.
