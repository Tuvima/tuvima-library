---
title: "Manage library settings and operations"
description: "Configure safe library sources, check background work, and find provider, profile, and recovery controls as an administrator."
audience: administrator
category: guide
product_area: ingestion
status: current
---

# Manage library settings and operations

Use Settings in Tuvima Library to add folders and check background work. This five-minute guide helps you choose safe [sources](../reference/glossary.md#library-folder) and find the right admin page.

## Choose how a folder is handled

Libraries is one screen: **Settings > Libraries**. The six media types (Books, Audiobooks, Movies, TV Shows, Music, Comics) are always listed, with the Import folder as a seventh entry. There is no separate Library settings page.

1. Open **Settings > Libraries** with admin access and choose a media type.
2. Choose **Add folder** and pick the folder.
   - Paste or type a path into the full-width path box, then press Enter or **Go**.
   - Single-click a folder to select it, and double-click to open it.
   - Use **Back**, **Forward**, **Up** or the breadcrumbs to move around, and the folder search to find a folder by name.
   - **Selected path** shows what you picked, with a copy button.
   - Media locations are listed before drives. Only folders are listed, the Library's own folders never appear, and **Add** turns on when the folder is valid.
   - Paths are shown as the server sees them. On Docker that means paths inside the container, such as `/library`, not paths on your computer.
   - If a path does not exist, the picker says so in plain words.
3. If the folder already has files, choose **Use as-is** (recommended), **Merge into** a managed folder, or **Let Tuvima manage it**.

Every folder is handled one of two ways:

- **As-is** is read-only. Tuvima never moves, renames, tags, overwrites or deletes its files, so other apps are not affected.
- **Managed** means Tuvima may rename, move and (when the type's tag setting is on) tag the files, including the ones already there. Choose it only for folders no other app depends on.

Each type has one **New media goes here** folder: the managed folder that receives new files. The first managed folder you add becomes it; use a managed folder's row menu to make another the place for new media. There is no priority order.

The folder list is a table with Folder, Handling, New media and Files columns. The row menu offers Make this the place for new media, Organise files now, Merge into, Switch to As-is or Managed, Label and Remove folder. Removing a folder never deletes files.

Keep music album folders intact. Tuvima uses tags or fingerprints to identify music before moving it.

## Advanced settings

Each type has an **Advanced** section with its naming template (**Naming template**, **Apply template**) and **Update tags inside files** (only affects managed folders). The section **File handling for every type** holds the library-wide rules **When the same file arrives twice** (**Keep one copy** or **Keep both**) and **Embed artwork in files**.

## Convert a folder to Managed

Switching an As-is folder to Managed asks for confirmation and shows a preview of the proposed moves and renames. Confirming starts a background organising job.

1. Choose **Switch to Managed** in the folder's row menu.
2. Check the preview: unchanged, moved, renamed, conflicting and blocked counts.
3. Confirm. The row shows progress, and you can pause, resume or cancel.

The job moves sidecar files (subtitles, artwork, notes) with their media. Every move is written to a log you can download. There is no automatic undo. Cancel keeps what was already done.

## Merge an existing folder into a managed folder

Use a merge to bring all the files of one existing folder into a folder Tuvima manages, once. To keep feeding files in over time, use the Import folder instead.

1. Open **Settings > Libraries** and choose the media type.
2. Open the folder's menu and choose **Merge into**. When you add a folder that already has files, you can choose **Merge into** there instead.
3. Choose the managed folder to merge into. The New media folder is chosen for you.
4. Choose **Copy** or, for a folder Tuvima manages, **Move instead of copy**. Copy never changes your folder. Move removes each file from your folder only after its copy has been checked byte for byte, and frees space on that drive.
5. Choose **Preview first** to see what would happen, then **Merge**.

Tuvima names the files the same way as the rest of your library. Exact duplicates follow your "same file twice" setting. Files Tuvima cannot name yet go to a folder called **From merge** inside the target and wait in **Review**.

You can pause, resume or stop a merge. Stopping keeps what was already copied, and the log lists what is still only in your folder. A merge cannot be undone automatically, but you can download the log. When it finishes, the folder reads **Merged into** the target and is no longer scanned, so nothing appears twice. Remove the folder from the list when you are ready; its files stay on disk.

A stopped merge reads "Merge stopped. Nothing was deleted. You can start it again." (a Move says the files already moved stay in the target) and the folder is a normal folder again. Two limits are known: starting it again into a different target, or restarting a Move of files that went to **From merge**, can add a numbered copy; and after a stopped merge of a Managed folder, ordinary scans cannot rename or move the originals that merge listed, to keep them safe.

## Excluded files

When you delete a title from the library, the files that stayed on disk are listed under the folders of that library: files in an as-is folder, the original of a merged file, and any file Tuvima could not remove. Scans skip them, so the deleted title does not come back. The list appears only when there are excluded files, and shows each file, its folder, when it was excluded and who deleted the title.

1. Open **Settings > Libraries** and choose the media type.
2. Find **Excluded files** and check the row for the file.
3. Choose **Add back**. Tuvima scans that one file again, so the title comes back after the scan.

If the file is no longer on disk, **Add back** removes the row and nothing is scanned. Only administrators can add a file back.

## Import folder

The **Import folder** is the seventh entry in the Libraries list. Set it once and drop new media into it, for example from a download tool. Tuvima creates one subfolder per type and files each arrival into that type's New media folder. Files it cannot match go to **Review** with the badge **From the Import folder**. A type with no managed folder leaves its files untouched and says so in one sentence. Use **Check folders for changes** to scan it now.

## Set up View storage

Use **Settings > Libraries** to choose the single managed View root. Use **Settings > Users** for each enabled profile's Personal Space sources and devices.

**Import folder** copies files into managed storage. **Link existing folder** scans an outside folder read-only. Detaching a source does not delete files. Reconcile is an admin repair action.

Browser uploads work today. Saved source and device records do not mean phone backup or device sync is ready. See [View Personal Space](view-personal-space.md).

## Set up Documents

**Settings > Libraries > View** has four Documents sections.

- **Who can see Documents** switches the Documents page on or off for each profile. It is on for everyone except child profiles, which start with it off.
- **Documents** controls text recognition for scanned pages and photos of paper: on or off, **Low** effort for a small server, the languages to read with (English is included, others are an optional download), the reading queue, and **Read all documents again**. If it says **Text recognition is not installed**, install Tesseract (see [configuration](../reference/configuration.md#configocrjson)); everything else keeps working.
- **Organising documents** manages the shared lists of tags, correspondents, document types and custom fields.
- **Auto-filing rules** lists the rules, including the household's Shared Library rules a household administrator can make.

How the reading and filing work is in [Documents in View](../explanation/documents-in-view.md).

## Follow work in Operations

Open **Settings > Library and Ingestion**, or select the header activity icon. The page updates on its own. It shows what is being added now, what was added recently, and anything waiting on a provider or a missing tool.

A checked or visible file does not mean the whole run is done. Matching, richer details, and file moves may still be in progress. Counts come from saved Engine state. Unknown values stay unknown.

1. Check what is being added now and any work that needs help.
2. Use **Check folders for changes** (in the Ingestion page "⋯" menu) for an extra scan of watched folders.
3. Search the list of recently added items or choose a result filter.
4. Select an item to browse its tracks, episodes or parts.
5. Choose **Show older** to load another page of history.

Items are listed under the day they first became Ready. There are no batches to browse, and there is no separate Activity & Audit page.

## Fix review and provider problems

[Review Queue](resolving-reviews.md) holds items that need a human decision. Work still waiting or retrying belongs in Operations. Review reasons include weak matches, duplicates, failed lookups, naming issues, and conflicting details.

**Metadata Providers** shows setup, key state, health, tests, and source priorities where supported. Healthy, Degraded, Offline, Disabled, Missing Configuration, and Unknown mean different things. No health result does not mean a provider is healthy.

These pages do not show secret values. [Set up providers](configuring-providers.md) before retrying a lookup that failed because of setup.

## Find other admin tools

Settings also holds Users, Local AI, network and remote access, backup/recovery, logs, and [Plugins](using-plugins.md). Your role and the Engine's supported actions control which tools you can use.

Developer Tools is for admin use on a development library, when enabled. Reset & Seed clears the catalog and derived intake state, then queues known test files. It keeps users, sources, and access rules. Advanced resets need a separate confirm step.

<details>
<summary>Technical details</summary>

The live page reads `GET /ingestion/queue-progress` and `GET /ingestion/presentation`, which are built from the saved per-file work rows, identity jobs, logs, details, art, people, links and reviews. SignalR events refresh the same numbers. Polling also checks them while work is active or idle.

Catalog sources are in `config/libraries.json`. File layout options also use `config/core.json`. Each source has a stable ID. A destination uses that ID, not its place in the source list. View saves source and device records on their own. Its managed paths use `profiles/<profile-id>/sources/<source-id>` beneath the View root.

Provider files are in `config/providers/*.json`. Keep long-lived keys in ignored `config/secrets/{provider}.json` files. A blank base file does not prove that a key was deleted.

Intake reads the file. Retail Match finds a provider record. Wikidata may add a canonical match. Later jobs add people, links, and art. Jobs can overlap. A valid local or provider sequence can stand even if Wikidata's previous/next chain has gaps. A diagnostic alone need not create a review item.

An outside manifest or expected count does not create owned files. Comic counts and partial totals must not imply a known completion target. More art can arrive after an item is ready to browse.

A group is its structural identity alone: a show, album or series is one group however many times its files were found. A grouped lookup may show a group label until it has per-file results.

A move preview has a short-lived plan fingerprint. The Engine checks the plan, access, rules, and each item before moving it. Some values may be unknown: the last move time, provider limits, or direct phase filters.

The development tools include Reset & Seed Test Library, Rescan All Libraries, View Ingestion, and Advanced resets. Fixture cleanup uses a list of generated files and follows each one to wherever the Library filed it. Routine resets delete only what the Library created; your own files, as-is folders, the Import folder and photos stay, and the result lists the files it kept.

</details>

## Next steps

- [Add media to your library](adding-media.md)
- [Configure metadata providers](configuring-providers.md)
- [Resolve a review item](resolving-reviews.md)
- [Check feature availability](../product/status.md)
