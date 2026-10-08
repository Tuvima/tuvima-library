---
title: "Use your Personal Space in View"
description: "Add and browse personal media, organize Galleries, and understand Shared Library access and Places privacy."
audience: user
category: guide
product_area: view
status: early-access
---

# Use your Personal Space in View

Use View in Tuvima Library for your photos, videos, and other local media. This five-minute guide explains your [Personal Space](../reference/glossary.md#personal-space), sharing, and current limits.

## Choose a space

Each enabled profile owns one Personal Space. Several folders or devices can feed it. They do not become separate libraries.

**Mine** shows your personal media that you can access. **Shared Library** holds accepted household media in separate storage. Shared access does not open another profile's whole Personal Space.

Your admin controls View access. Check the scope selector before you share or sort items. It tells you whose media you are browsing.

## Add files

For a browser upload:

1. Open **View > Photos** and select **Mine**.
2. Use the upload action and choose your files.
3. Wait for the upload and local file checks to finish.
4. Find the items in Photos or Folders.

To add a folder source, ask an admin to open your View sources in **Settings > Users**. **Import folder** copies files into managed storage. **Link existing folder** scans an outside folder read-only. It leaves the original files where they are.

Detaching a source does not delete its files. Source and device records exist today. Phone backup, device sync, and auto-upload clients are not complete products yet.

## Browse Photos and Folders

Photos groups items by date and offers search and filters. Open an item in the viewer. Select several items for the bulk actions shown.

Folders keeps each source's real folder tree. Breadcrumbs show where you are. Use include-descendants to search child folders too. Private folder pins give you shortcuts.

You can favorite, hide, archive, trash, or restore your own items. Archive removes an item from the normal Photos view. Trash can be restored. Browsing and making thumbnails do not change original files.

## Make Galleries

1. Open **View > Galleries**.
2. Create a manual Gallery or a rule-driven Smart Gallery.
3. Add items to a manual Gallery, or set rules for a Smart Gallery. Save your changes.
4. To share, choose allowed profiles and the access each can have.

A Gallery groups media. It is not a new file source. Deleting a Gallery does not delete its media. Profile sharing follows access rules. Public-link sharing is not ready.

## Send items to Shared Library

1. Select owned items in **Mine** and open the contribution action.
2. Check the preview and target shared folder or timeline.
3. Submit the batch if you have that right.
4. Follow it in **Shared Library > Contributions**.

A curator with review rights accepts or declines the batch. The transfer checks the shared copy before making it a shared member. Managed originals move only after that check. Linked originals are copied and stay in their source folder. Submission and review need separate rights.

## Browse People and Places

People shows named or reviewed people annotations and their sources. If no usable names exist, it explains that state. It does not claim to run face recognition.

Places opens **Tuvima Atlas** from real GPS and place facts. Select a place to browse its media that you can access. Use time, media, and other offered filters to narrow the view.

**Places can make external map requests.** It first tries OpenFreeMap's dark style. That style can load outside map resources. If the request fails, Atlas uses a local country map. Opening Places is not always an offline or network-free action.

Face recognition, object/scene detection, OCR, captions, semantic search, and AI memories are planned work. Stored annotations and browse pages do not prove that those AI workers run today.

<details>
<summary>Technical details</summary>

Settings > Libraries chooses the managed View root. Current paths use stable `profiles/<profile-id>/sources/<source-id>` folders. Accepted shared originals live in a separate `Shared` tree. Do not move files by hand to match old `View/Profiles` wording.

View uses an internal `personal` library bridge. Local-only/manual personal media skips retail matching, Wikidata, canonical claims, and Review Queue. Each media request and browse query checks View access again.

The map tries `https://tiles.openfreemap.org/styles/dark`. On failure it falls back to `/maps/world-countries.geojson`. See [View architecture](../architecture/view-personal-media.md) for storage and access rules.

</details>

## Next steps

- [Understand privacy and network access](../explanation/privacy-local-first.md)
- [Find your Galleries in For Me](for-me.md)
- [Configure library and View storage](library-settings.md)
- [Check current availability](../product/status.md)
