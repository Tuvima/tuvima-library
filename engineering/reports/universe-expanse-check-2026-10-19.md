# Universe U-WP0: what really mixes up The Expanse

> **Status:** Current · **Date:** 2026-10-19 · **Spec:** [Universes](../specs/universe.md) (U-WP0) · Read-only check; nothing was changed or re-run.

## What was read

- The live data store has been cleared since the last run. The only data left is the backup taken on 2026-10-18 before the GUID storage reset (`library.db.legacy-text-guid.20261018222218.bak` plus its WAL), copied to a scratch folder and read there.
- Engine logs `src/MediaEngine.Api/logs/tuvima-20261015.log`, `tuvima-20261018.log` and `.tmp/engine.log`.
- The backup holds 27 Works, all Books. The test library (`Z:\Development\testing\media\real_media\tv`) has no Expanse TV show, so **the "novels and TV show merge into one shelf" case cannot be confirmed from real data**. U-WP3 must prove it with a test library instead, as its done-when already says.

## What the data shows (books)

| Title | Shelf it sits on | Number on the shelf | What Wikidata's Series manifest says |
|---|---|---|---|
| Leviathan Wakes … Leviathan Falls (9 novels) | The Expanse | 1 to 9 | Main sequence 1 to 9 |
| Drive | The Expanse | 0.1 | Collected content of Memory's Legion (not linked: no Wikidata match) |
| The Churn | The Expanse | 0.2 | Collected content of Memory's Legion |
| The Butcher of Anderson Station | The Expanse | 0.5 | Not in the manifest |
| The Vital Abyss | The Expanse | 5.5 | Collected content of Memory's Legion |
| Strange Dog | The Expanse | 6.5 | Not in the manifest |
| Gods of Risk | **Memory's Legion** (its own shelf) | 2.5 | Collected content of Memory's Legion |

## Confirmed causes

1. **The shelf ignores the manifest's membership scope.** Novellas the manifest marks as collected content (The Churn, The Vital Abyss) sit inside the numbered run of the main shelf. Nothing produces an "Also in this world" split.
2. **The retail position wins over Wikidata's.** Every shelf number above comes from the retail `series_position` (0.2, 2.5, 5.5 …), not from the manifest's ordinal, so shelf order and manifest order disagree.
3. **Shelf membership depends on processing order.** `CollectionAssignmentService.AssignAsync` treats a work that is already on a "broader" collection as misplaced and moves it to whatever shelf its current claims resolve to ("is assigned to broader collection 'Memory's Legion' … reassigning" in the 18 October log). The Churn and The Vital Abyss were moved from Memory's Legion back to The Expanse; Gods of Risk, with the same Wikidata membership, was left on a separate Memory's Legion shelf. Siblings end up on different shelves, and Gods of Risk's item page says "The Expanse #2.5" while its shelf says Memory's Legion.
4. **Decimal positions are lost from the whole-number column.** `works.ordinal` is empty for every decimal position (0.1, 0.2, 0.5, 2.5, 5.5, 6.5); only `works.ordinal_sort` keeps the value. Anything that reads `ordinal` shows no number for these Works.

Not confirmed, because the data has no second media type: grouping by label text merging two lines that share the name "The Expanse" (books and TV). The code path is still as section 0 of the spec describes; U-WP3 covers it with a test.

## What this means for U-WP3

U-WP3's plan is right, and it should also fix cause 3: shelf placement must come from one rule (Wikidata membership scope, then retail), applied the same way however many times assignment runs, so a re-run can never move a sibling to a different shelf.
