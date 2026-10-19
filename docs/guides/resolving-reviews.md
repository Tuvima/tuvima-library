---
title: "Resolve review items"
description: "Confirm uncertain media matches, retry a failed write-back, and tell Needs Review decisions apart from routine editing."
audience: user
category: guide
product_area: review
status: current
---

# Resolve review items

Use [Needs Review](../reference/glossary.md#needs-review) in Tuvima Library when an item needs your help. Allow a few minutes per item. Comparing several matches may take longer.

Only server administrators can see and resolve Needs Review. Household administrators and Kids profiles do not see the Needs Review menu entry, its count, or the list. Apps that a server administrator has given review permission can also list and resolve items.

## Open an item

1. Open the account menu and choose **Needs Review**, or open **Settings**, then **Library and Ingestion**, then **View all** under Recently added. The **Needs Review** list is pinned at the top, oldest first.
2. Select an item and read why it needs help.
3. Choose **Review** to open the shared editor with that reason.
4. Check the file facts and offered matches before acting.

The reason may be a weak match, missing title, failed lookup or tag write, unclear Wikidata match, or missing ID. The reason and Engine state decide which actions you can use.

## Choose an action

- Confirm or fix the match when the facts support it.
- Apply a better match. Check any proposed parent or position change first.
- Fix fields that blocked the file.
- Choose **Retry write-back** when writing the details into the file failed and you have fixed the cause, such as a read-only file.
- **Dismiss** an item that no longer needs help.
- Choose **Keep provider match** when it is offered and the item can work without a universe link.
- For a film that looks like a TV title, choose **Move to TV** or **Keep as film**.

If an action does not work, a short message says so and the item stays in the list. If the list itself cannot load, you see an error with **Retry** rather than an empty list.

After saving, check the item or **Library activity** below the list for the result. Later jobs may still add details after the review is resolved.

## Edit normal mistakes on the item

If you find a wrong title, cover, note, or match while browsing, open **Edit** there. This works for albums, episodes, books, comics, and movies where supported. You do not need to create a review item first.

Needs Review is for decisions you can act on. Background work, provider waits, and retries stay in Operations until they need your help. A missing Wikidata ID alone need not keep a ready item out of browse.

<details>
<summary>Technical details</summary>

Review and normal edits share one media editor. Review adds the pending reason and allowed actions. Confirm, retry write-back, dismiss, keep provider match, move to TV, and matching actions call the Engine, which checks that the person is a server administrator (or the app has review permission). A local Dashboard flag does not replace saved job state.

See [inline editing](../architecture/inline-media-editing.md) and [file intake](../explanation/how-ingestion-works.md) for save and browse-readiness rules.

</details>

## Next steps

- [Correct an item from its detail page](editing-items.md)
- [Check Operations and provider health](library-settings.md)
- [Understand matching and enrichment](../explanation/how-hydration-works.md)
