---
title: "Understand which metadata wins"
description: "Learn how provider priorities choose metadata, how personal fields differ from source facts, and how presentation corrections persist."
audience: user
category: explanation
product_area: scoring
status: current
---

# Understand which metadata wins

Learn why Tuvima Library chooses one title, author, or year when sources disagree. This three-minute explanation also shows how to correct an item's appearance without confusing it with provider identity.

## Keep the source evidence

A file, provider, or Wikidata record can each supply a [claim](../reference/glossary.md#claim): a value with its source and confidence. Earlier claims remain available when a later claim supersedes them.

For example, an audiobook file may name an author differently from a catalog. Tuvima keeps the evidence and chooses a value using a defined order.

## Choose source values in order

1. For supported personal fields, an explicit user lock wins.
2. Configured priorities choose preferred providers for particular fields.
3. Wikidata supplies the default authority when an eligible claim exists.
4. Otherwise, the highest-confidence eligible claim wins.

The author rule can preserve an explicitly credited pen name. A stronger file or user-source author claim can beat the Wikidata author claim.

## Correct appearance or identity

Open **Edit** on the detail page to change supported display fields such as title, description, tagline, sort title, and genres. These durable presentation overrides are a separate mechanism from provider claim scoring.

Use **Matching** to correct a provider identity. Provider-managed people, dates, languages, runtime, and ratings are not a free-form editing form. Your personal notes and tags belong to your profile.

## Change priorities for a wider problem

Administrators can prefer different providers for specific fields. For example, a provider may supply better descriptions or artwork for one media type.

Priorities affect source selection across relevant items. Prefer an item-specific correction when only one title is wrong, and inspect the source history before changing broad policy.

<details>
<summary>Technical details</summary>

`PriorityCascadeEngine` resolves groups of claims by field. Tier A honors user locks only for `rating`, `media_type`, and `custom_tags`. This describes the scoring mechanism; it does not imply that normal editing offers a Change Type action.

Per-media priorities in `config/pipelines.json` are checked before global `config/field_priorities.json`. Enabled provider definitions resolve configured provider names. If a priority list has no usable claim, resolution falls through to the next tier. Priorities reload on scoring calls.

Wikidata claims win by default. Among its claims, confidence and recency select the result. For `author`, a stronger file-source or user-source claim can win; an arbitrary retail author claim does not receive that exception.

Without Wikidata or a configured priority winner, highest confidence wins. Tied fallback claims prefer the earliest claim, preserving the first source author rather than the last inserted. Overall confidence averages field confidence, then applies folder-category priors and media-specific confidence floors.

The current cascade does not apply the legacy field-count scaling, close-score conflict marking, or 90-day claim decay algorithm. Those options still exist in scoring configuration types; their presence does not prove this implementation uses them. Current cascade field results set `IsConflicted` to false. Identity matching and review can still detect actionable ambiguity through their own checks.

The shared editor saves presentation overrides for `title`, `description`, `tagline`, `sort_title`, and `genre` with profile notes/tags under an optimistic revision. Those writes are distinct from Tier A claim locks. A stale revision preserves input and requires explicit reload.

See [scoring architecture](../architecture/scoring-and-cascade.md) for implementation context and [inline editing](../architecture/inline-media-editing.md) for presentation overrides. The source-selection behavior above follows the current Priority Cascade implementation.

</details>

## Next steps

- [Correct an item](../guides/editing-items.md)
- [Configure metadata providers](../guides/configuring-providers.md)
- [Understand matching and enrichment](how-hydration-works.md)
