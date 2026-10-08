---
title: "Correct an item"
description: "Edit an item's appearance, artwork, provider match, and personal notes from its existing detail page."
audience: user
category: guide
product_area: editing
status: current
---

# Correct an item

Fix a title, cover, or wrong match in Tuvima Library from the item's own page. Most small changes take a few minutes. Some lookup work may finish later.

## Open the editor

1. Open the item's [detail page](../reference/glossary.md#detail-page).
2. Select **Edit** if your role allows it.
3. Choose the section you need.
4. Save or discard your changes before switching to another item.

Normal edits and Review Queue use the same editor. The item and your access rights control which sections appear.

## Change text and personal notes

In **Details**, use Appearance to change the display title, description, tagline, sort title, or genres. My Library holds your own notes and local tags.

Genre and tag fields suggest values already used in your library. Type a full value, then press Enter or choose a suggestion. You can type spaces without saving the tag too soon.

People, dates, language, runtime, and source ratings come from the file or provider. To fix a wrong source match, use Matching. These facts are not all fields you can type over.

## Choose artwork

Open **Artwork** to choose or add images. The top of the editor shows the item's main image. [Artwork types](../reference/artwork-types.md) explains covers, backgrounds, logos, and other image roles.

For shelves that support edits, you can change the description and add a custom cover. You can also keep the stack of member art that Tuvima builds. A shelf cover belongs to the shelf. It does not replace the covers of its members.

## Fix a wrong match

1. Open **Matching**, then use **Retail Match** to find the right provider entry.
2. Compare the proposed match with your file.
3. If its parent or place in a series changes, check **Previous Path → Target Path**.
4. Choose **Apply** to save that match.
5. Check the new art, then follow later work in **History** or Operations.

The provider match and main artwork change first. Wikidata links and richer details may arrive later. Art you uploaded stays available.

Episodes and tracks have **Parent & position** in Matching. Moving one has its own preview and confirm step. A normal text edit does not move it to a different show, season, or album.

## Change chapter titles and check past changes

Use **Chapters** to change an audiobook chapter's display title. Its times and boundaries still come from the file and cannot be edited here. Tuvima does not rename source chapters on its own.

**History** shows changes to matches, details, artwork, and file intake. **Files** shows facts about the physical file. If newer changes conflict with yours, the editor keeps your input and offers reload. It does not write over the newer changes.

## Edit a universe or story entity

Universe Explore is for browsing, not editing. If your role allows it, open universe and entity tools in the shared editor from a supported media item.

Within that editor, you can switch between a universe and its characters, places, groups, events, or objects. Details and Artwork changes apply to the chosen target. Graph facts stay read-only. An entity edit does not reorder a media shelf.

## Resolve blocked items

Use [Review Queue](resolving-reviews.md) when Tuvima asks for a human decision. For a normal title, cover, or note change, open Edit on the item.

<details>
<summary>Technical details</summary>

The saved display fields are `title`, `description`, `tagline`, `sort_title`, and `genre`. They save with your profile's notes and tags. Each save checks the record's revision. If it has changed, the editor reports a conflict and keeps your draft.

A catalog edit does not grant file-write access. Existing/read-only sources stay protected. Moving files or writing tags still needs the source's permission and policy checks. See [inline media editing](../architecture/inline-media-editing.md) for the storage and matching rules.

</details>

## Next steps

- [Resolve an item that needs review](resolving-reviews.md)
- [Understand how metadata sources are chosen](../explanation/how-scoring-works.md)
- [Understand library source safety](library-settings.md)
