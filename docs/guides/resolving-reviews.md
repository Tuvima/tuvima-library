---
title: "Resolve review items"
description: "Confirm uncertain media matches, retry fixed problems, and distinguish Review Queue decisions from routine editing."
audience: user
category: guide
product_area: review
status: current
---

# Resolve review items

Use [Review Queue](../reference/glossary.md#review-queue) in Tuvima Library when an item needs your help. Allow a few minutes per item. Comparing several matches may take longer.

## Open an item

1. Open **Settings**, then **Review Queue**.
2. Select an item and read why it needs help.
3. Choose **Review** to open the shared editor with that reason.
4. Check the file facts and offered matches before acting.

The reason may be a weak match, missing title, failed lookup or tag write, unclear Wikidata match, or missing ID. The reason and Engine state decide which actions you can use.

## Choose an action

- Confirm or fix the match when the facts support it.
- Apply a better match. Check any proposed parent or position change first.
- Fix fields that blocked the file.
- Retry a failed step after fixing its cause, such as a missing provider key.
- Dismiss an item that no longer needs help.
- Skip universe/QID matching when that action is offered and the item can work without the link.

After saving, check the item or Operations for the result. Later jobs may still add details after the review is resolved.

## Edit normal mistakes on the item

If you find a wrong title, cover, note, or match while browsing, open **Edit** there. This works for albums, episodes, books, comics, and movies where supported. You do not need to create a review item first.

Review Queue is for decisions you can act on. Background work, provider waits, and retries stay in Operations until they need your help. A missing Wikidata ID alone need not keep a ready item out of browse.

<details>
<summary>Technical details</summary>

Review and normal edits share one media editor. Review adds the pending reason and allowed actions. Confirm, retry, dismiss, skip, and matching actions call the Engine. A local Dashboard flag does not replace saved job state.

See [inline editing](../architecture/inline-media-editing.md) and [file intake](../explanation/how-ingestion-works.md) for save and browse-readiness rules.

</details>

## Next steps

- [Correct an item from its detail page](editing-items.md)
- [Check Operations and provider health](library-settings.md)
- [Understand matching and enrichment](../explanation/how-hydration-works.md)
