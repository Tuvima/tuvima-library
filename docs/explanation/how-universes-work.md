---
title: "How Universes and Series Work"
description: "Understand the grouping model that connects books, films, audio, and related media."
audience: "user"
category: "explanation"
product_area: "concepts"
tags:
  - "universes"
  - "series"
  - "grouping"
---
Most media software organizes by format: books in one app, movies in another, music somewhere else. Tuvima Library organizes by *story*. The same creative world - whether you own it as a novel, a film, an audiobook, a graphic novel, or a music soundtrack - lives together in one place. That place is a **Universe**.

This page explains how the grouping model works, what the terminology means, and how the Engine figures out which items belong together.

Current serving rule: Universe, Series, graph, people, entity, and related-media surfaces are conservative. They show only sections backed by stored library data or local enrichment output. If Tuvima has not found real graph relationships yet, Chronicle Explorer shows an unavailable state instead of sample nodes. Related media shelves require a grounded reason such as Same Series, Same Creator, or shared metadata; they are not filled with random items.

---

## The Idea Behind Universes

Consider Dune. You might own Frank Herbert's original novels as EPUBs. You might have Denis Villeneuve's film adaptations as MKVs. You might have the audiobook narrations as M4Bs. You might have the graphic novel adaptations as CBZ files. These are all separate files in separate formats. A folder-based organizer would scatter them across Books, Videos, Audio, and Comics sections with no indication they're related. A Universe brings them forward as one creative world - exactly as they belong.

This is the **Presentation** philosophy at the core of Tuvima Library. The stories already exist on your hard drive, fragmented. The Library's job is to find them, understand them, and surface the result as something coherent.

---

## Three Related Models, Not One Tall Hierarchy

The library has three connected but distinct ways to describe media. They answer different questions, so a graph relationship must not silently move a work between authored containers or structural shelves.

**Structural placement** answers “where does this title belong in my media organization?” A Series can group Works; a Work can have Editions; Editions are represented by one or more Media Assets (files). A Universe may provide a broader cross-media context, but it is not a required parent row above every Series or Work.

```
Structural/catalogue:  Library → Series (optional) → Work → Edition → Media Asset
Knowledge graph:      Universe → fictional entities ↔ qualified links to Works
Authored containers:  user-owned canonical title and explicit member/order choices
```

- **Work** - a single title, regardless of how many files or formats you own.
- **Edition** - a specific version of a Work, such as theatrical and director's cuts.
- **Media Asset** - an actual file for an Edition.
- **Series** - a lane-level structural shelf, such as Dune novels or Dune films.
- **Authored container** - a user-authored grouping whose canonical title and member order remain authoritative; enrichment does not rename or reorder it.
- **Universe and fictional entities** - knowledge-graph identities connected to visible Works by scoped, sourced facts, not by implicit file or shelf containment.

Retail matching answers which provider record identifies an edition/work and may also propose structural placement. When a selected candidate would move an item to another parent or position, the editor shows the previous and target paths before Apply. This is separate from Universe identity: confirming a Wikidata/Universe relationship does not itself change the Work's authored title, Series order, or media placement.

---

## Shelves vs Collections

Tuvima shows immediate ordered groups as **shelves** inside Read, Watch, and Listen. A book series belongs in Read, a movie series belongs in Watch, and a music album belongs in Listen. A shelf is useful even when it is the only related group you own.

The Collections page is stricter. It shows broader creative worlds only when they connect multiple shelves. Owning only the Matrix films creates a Watch shelf for the Matrix film series, not a top-level Matrix collection. Owning Middle-earth books plus film trilogies can create a Middle-earth collection because it connects several shelves.

Multiple formats of the same work do not create a collection. An ebook and audiobook for the same title are variants of one Work. A standalone story with a book and film adaptation, such as The Martian, does not become a Collections tile unless the library also contains a broader series/shelf structure that justifies a rollup.

---

## How Grouping Happens Automatically

Universes and Series are resolved automatically at metadata-scoring time. The Engine doesn't need you to manually assign items to groups - it figures out the relationships from the metadata.

The primary signals are Wikidata relationship properties:

- **P8345 (media franchise)** - directly identifies the franchise a work belongs to
- **P179 (part of series)** - links a work to its series
- **P361 (part of)** - broader membership (e.g., a spin-off that's part of a larger franchise)

When the Engine successfully identifies a work and fetches its Wikidata properties, it gets these relationship values as QIDs (Wikidata identifiers). It then looks up those QIDs to find their labels. A shared franchise or universe QID is a rollup candidate; it becomes a visible Collection only when it connects multiple shelves instead of merely duplicating one lane-level series.

Secondary signals also contribute: shared author, shared narrative roots, shared characters detected by the Universe Graph. Shared author alone doesn't necessarily create a Universe - an author might write in completely unrelated genres - but combined with shared series membership or franchise identifiers, the grouping becomes clear.

This happens after the retail and Wikidata identity gates. Stage 1 retail must first provide a safe match and bridge IDs; Stage 2 Wikidata resolves the canonical QID; Stage 3 then expands narrative roots, fictional entities, people, relationships, and additional artwork. If Stage 2 cannot find a QID, the item can remain usable with retail data, but universe graph enrichment waits for a canonical identity.

**Importantly, Universes and Series have no presence on the filesystem.** Your files are organized by media type and title. The Universe grouping exists only in the data store and is resolved at query time. This means the grouping can change as the Engine learns more - a standalone novel might later be recognized as part of a franchise when new metadata arrives.

---

## Series Are Flexible Containers

A Series is not limited to numbered sequences. It's any meaningful grouping of related Works.

Some Series are obvious sequential collections: the Dune novels in publication order, the MCU films in release order, the Discworld subseries by character arc. But Series can also be:

- **Adaptation clusters**: all film adaptations of a specific novel
- **Spin-off works**: tie-in novellas and short stories connected to a main series
- **Thematic collections**: all standalone novels by an author set in the same fictional universe
- **Cross-media narratives**: a book shelf connected to a separate television or film adaptation shelf

What defines a Series is **shared contextual metadata** - the same Wikidata series QID, the same franchise membership, the same author+universe combination. Not a shared file format, not a shared folder location.

Within a series, relationship scope still matters. Main numbered works remain the
main sequence, while P361-linked short fiction appears as supplementary material.
An ebook and audiobook of the same title are formats of one Work, not two franchise
members. Adaptations remain separate lane shelves and can converge under a broader
franchise or fictional-universe collection when a trusted shared relationship
connects them. A standalone work such as *The Hobbit* stays standalone at the
series level while its fictional-universe relationship can still connect it to
Tolkien's legendarium.

---

## Universes Are Optional

Not everything belongs to a franchise. A standalone novel with no series membership, no adaptations, and no franchise connections lives directly under the Library - no Universe wrapper needed.

Only when the Engine discovers a broader relationship shared by multiple shelves does it promote those shelves under a common Collection. A single Series that belongs to no larger visible grouping stays in its Read, Watch, or Listen lane.

This also means the grouping can evolve. A standalone shelf might later become part of a Collection when another related shelf appears in your library, or when Wikidata gains new relationship data that connects multiple shelves.

---

## The Terminology Explained

You'll notice the user-facing names and the internal code names are different. This is intentional - it decouples the user experience from the implementation, so the code can evolve without changing the product language.

| What you see in the Dashboard | What the code calls it | Why different |
|---|---|---|
| Universe | ParentCollection | The code predates the Universe concept; renaming internally would risk data store migrations |
| Series | Collection | Same history |
| Work | Work | Same in both |
| Edition | Edition | Same in both |
| Media Asset | MediaAsset | Minor formatting difference only |

When reading code or architecture documentation, Collection = Series and ParentCollection = Universe. When writing anything user-facing - UI labels, documentation, error messages - always use Universe and Series.

---

## The Universe Graph: Beyond Simple Grouping

Grouping items together is the first layer. The Universe Graph is the second - a richer map of relationships *within* and *across* Universes.

The Universe Graph tracks:

- **Characters** - fictional entities that appear across multiple works (a character appearing in both novels and their film adaptations)
- **Locations/Places** - fictional places that recur across media
- **Organizations/Groups** - factions, families, orders, and their membership
- **Events** - story-world events and their participants
- **Objects/Artifacts** - notable fictional objects
- **Narrative relationships** - which works are sequels, prequels, spin-offs, adaptations of each other

This graph powers features beyond simple browsing. It's what enables "which actor played this character in which adaptation?" It's what connects a graphic novel adaptation to the novel that inspired it. It's the infrastructure for the Chronicle Explorer - the visual graph at `/universe/{QID}/explore` where you can navigate these relationships interactively.

For now, Universe Graph data comes from Wikidata properties. The Tuvima.Wikidata.Graph module handles in-memory graph queries over this data so you can ask relationship questions without a network connection. Organization membership and Event participation are distinct kinds of graph projection; neither replaces the broader relationship list.

Entity appearances link an entity to a particular Work and can carry role, work context, anchor, narrative-time, date-range, and spoiler-for-work qualifiers. Those qualifiers keep a fact scoped to the adaptation, installment, or time span where it applies. Real-world dates (such as release or birth dates), in-universe narrative time, and editor History (when Tuvima changed a record) are separate timelines.

Stage 2 establishes canonical identity for a matched Work. Bounded Stage 3 can then enrich grounded Universes, entities, qualified relationships, and artwork. It does not overwrite authored-container canonical titles/order or silently realign structural placement; any placement change must be an explicit Retail Match preview and Apply.

---

For technical details about the Universe Graph schema, the relationship model, SPARQL query patterns, and the Chronicle Engine's temporal qualifier system - see the [architecture deep-dive](../architecture/universe-graph.md).

## Related

- [Universe Graph](../architecture/universe-graph.md)
- [Ingestion, Identity, and Enrichment Pipeline](../architecture/ingestion-identity-enrichment-pipeline.md)
- [Glossary](../reference/glossary.md)
- [Your First Library](../tutorials/first-library.md)
