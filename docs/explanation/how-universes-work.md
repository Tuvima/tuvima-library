---
title: "Understand shelves, Collections, and universes"
description: "Learn how related titles form shelves and broader Collections while editions, file placement, and story relationships stay distinct."
audience: user
category: explanation
product_area: concepts
status: current
---

# Understand shelves, Collections, and universes

Learn how Tuvima Library groups related media in about four minutes. Start with a [shelf](../reference/glossary.md#shelf) in Read, Watch, or Listen, then explore broader connections when your library supports them.

## Find related titles in a lane

A book series belongs in Read, a movie series in Watch, and albums or audio series in Listen. Comics use volume and issue wording. TV shows have their own Watch scope, with only episodes you own.

Book, movie, and audiobook series normally need at least two distinct owned works to appear as series cards. A single title remains a normal item. TV shows can appear with one owned episode; music albums have their own grouping rules.

Local or provider metadata can establish a shelf even when Wikidata cannot resolve an identity. A useful lane group does not require a broader universe.

## Understand broader Collections

Automatic Collections need trusted shared series, franchise, or universe evidence. They connect at least two distinct lower-level groups, or a trusted series containing distinct owned titles across multiple lanes.

Owning several Matrix films creates a Watch series shelf. It does not also create a top-level collection just to repeat that shelf. Related Middle-earth book and movie shelves can support a broader collection when trusted relationships connect them.

Several formats of the same work remain variants. An ebook and audiobook alone do not create a collection, nor do same-title formats become separate owned story installments.

## Build your own collections separately

A custom collection can use manual membership or complete dynamic rules. Profile-owned collections are private unless supported sharing policy allows otherwise. Only administrators publish or manage library-wide collections.

Your collections appear in For Me. Accessible library collections enter My List only when you save them. Playlists stay in Listen and use their own queue/edit experience.

Collections also offers a Shelves index and a People list. The Shelves index covers eligible same-media book, movie, and audiobook series. It does not turn albums, TV shows, comic issue groups, or contributor lists into automatic top-level collections.

## Keep files, titles, and story facts distinct

A **work** is an underlying title. An **edition** is a particular release or format. A **media asset** is an actual file. One title can have several editions or files without becoming several story installments.

A **universe** describes a larger creative world. Its graph can connect characters, places, organizations, events, objects, and works. Those story facts do not silently move files or reorder your authored collections.

Applying a different provider match can propose a structural move. The editor previews the old and new paths before Apply. Confirming a universe relationship alone does not change shelf placement.

## Follow the evidence

Relationship surfaces show stored library data and grounded enrichment results. An unavailable graph shows its unavailable state rather than sample characters. Related recommendations need a reason such as a shared series or creator.

Owned counts describe your media. Missing entries may come from a provider manifest, but they are not files you own. Only authoritative totals support a completion target; comic issues use issue identity and owned count.

<details>
<summary>Technical details</summary>

The catalog model is `Library → optional Series → Work → Edition → MediaAsset`. The knowledge graph is `Universe → fictional entities ↔ qualified links to Works`. Authored containers keep user-owned canonical titles and explicit order; these are separate models.

Trusted Wikidata evidence can include P8345 (franchise), P179 (series), and P361 (part of). A shared label, author name, folder, or file format is not enough to merge identities or create a universe rollup. Broader collections require trusted shared relationship rows; local/provider shelf identities can support lane grouping without a QID.

Sequence placement uses `ordinal_sort`, immediate shelf identity, `sequence_total`, `sequence_total_scope`, and child identity keys. MainSequence members have positioned P179/direct P527 evidence. Supplementary covers P361 links; expanded P527 children are CollectedContent; explicit franchise expansion is BroaderContext. Unpositioned members lack ordinal or chain evidence beside a positioned run. Source ordinals, including decimals, retain their meaning rather than being densely renumbered.

Graph appearances can carry role, work context, anchor, narrative-time, date-range, and spoiler qualifiers. Real-world dates, fictional chronology, and editor History are different facts. Organization membership and event participation are distinct projections of qualified relationships.

Stage 2 establishes canonical identity; bounded Stage 3 enriches grounded entities and relationships. Enrichment does not silently realign structural placement or overwrite an authored container's title/order. Consumer Explore is read-only; authorized entity editing uses the shared editor.

Universe grouping lives in the data store and does not create a universe folder hierarchy. Internal `Collection` and `ParentCollection` records have several roles; do not translate every stored Collection into the same user-facing label. The surface determines whether the user sees a series, show, album, or broader collection.

Default series-card thresholds are configured through `lane_group_display.*.minimum_series_items`. Missing-item defaults live in `config/ui/library-preferences.json`; explicit profile/series overrides live in `profile_sequence_preferences` and can be removed to restore inheritance.

See [Universe Graph architecture](../architecture/universe-graph.md) and [the processing pipeline](../architecture/ingestion-identity-enrichment-pipeline.md) for the stored relationship model.

</details>

## Next steps

- [Tour Collections and the media lanes](../guides/dashboard-tour.md)
- [Find your collections in For Me](../guides/for-me.md)
- [Correct a match or structural position](../guides/editing-items.md)
- [Look up product terms](../reference/glossary.md)
