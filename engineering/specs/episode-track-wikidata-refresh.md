# Spec: Refresh an episode or track right after linking it to Wikidata

> **Status:** Draft · **Version:** 1 · **Last updated:** 2026-11-10 · **Related docs:** [Product overview](../product/overview.md)

## 1. Goal

When you link an episode or a track to a Wikidata entry in the editor, its own details should refresh at once, the way a movie or a book already does, instead of waiting for the next scheduled refresh or scan.

## 2. Scope

**In scope**
- Saving a Wikidata link on an episode or track queues a refresh for that item.
- The show or album facts stay exactly as they are.

**Out of scope**
- Movies, books and comics (already refresh at once).
- Changing which facts belong to the show or album versus the item.
- Any new screen or control.

## 3. Decisions made

- **D1.** Show and album facts are never rewritten by this refresh. Reason: a show or album is shared by many items, and one item must not overwrite it.
- **D2.** The refresh uses the same queue and the same background job as today's refresh for movies and books. Reason: one path is easier to maintain and behaves the same everywhere.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Link an episode or track in Match, then Save changes | Same as today. The item's own details update within moments instead of at the next scan. |
| Link a movie or book | Unchanged. |
| Provider down | The link is still saved. The refresh runs on the next scheduled pass, and a log entry records the miss. |

## 5. Data / model changes

None. No new data is stored and no reset is needed.

## 6. Work packets

### WP1 — Queue a refresh for parent-scoped items
- **What it does:** In the Wikidata link writer's refresh step, stop skipping an episode or track whose link belongs to its show or album. Queue the refresh for the item's own record, and leave the show or album record untouched.
- **Files likely touched:** `src/MediaEngine.Api/Services/Matching/WikidataIdentityWriter.cs` (the `QueueRefreshAsync` check on `storedOnOwnWork`), `tests/MediaEngine.Api.Tests/`.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test links an episode and a track and shows a refresh is queued for each. A second test shows the show and album records are unchanged. A third test shows a movie still queues exactly one refresh.

## 7. Local checks

- **L1.** On a small test library with a TV show and an album, link one episode and one track to Wikidata in the editor. Within a minute their own details (description, date) should update without a scan. Failure: they stay empty until a scan, or the show or album changes.

## 8. Risks and open questions

- **Risks:** one extra Wikidata request per saved link on TV and music. A refresh on an item whose show is still being matched could run too early.
- **Open questions:**
  - **Q1.** If the show itself has no Wikidata link yet, refresh the episode anyway? A: yes, with what the episode's own link provides (recommended). B: wait until the show is linked.
