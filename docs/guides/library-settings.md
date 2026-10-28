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

## Find your way around Libraries

Libraries is **Settings > Libraries**, in two levels. Each has its own address, so Back and a reload keep your place, and the page title and breadcrumb above it show where you are.

1. **The overview** has four tabs, **Watch**, **Listen**, **Read** and **View**. The tabs filter the list: pick one and the table under it shows only that area's libraries, one row per library. A row has the library's name with its icon, its content type, who can see it (**Standard** or **Selected profiles**), its folder and title counts ("3 folders · 1,284 titles", or "Nothing added yet") and a status: **Healthy**, **Needs review** (with the number of files; it opens Needs Review), **Empty**, **Folder offline** (the folders at fault in a tooltip) or **Scanning**. The whole row opens the library, and a chevron and a highlight on hover show that it does. Search and a status filter sit above the table, and an area with nothing in it says "No Watch libraries yet" with one **Add library** button. A Vault-protected library appears, with a lock, only while Vault mode is on for you and your profile may use it; otherwise there is no row and no hint. Under the table is a line for the [Import folder](#import-folder) (it reads "set" or "not set").
2. **A library page** (for example Movies) shows the library's content type, who can see it, its counts and status, and **Scan now**, **Add folder** and **Add files**. Its tabs are **Folders** (the table of folders), **Access**, **File handling** and **Activity**, plus **Lookup** for a kind that has lookup settings (YouTube). Access, File handling, Activity and Lookup are being built and say "Coming in the next step" for now. **Scan now** scans this library's folders only (folders with scanning paused or offline are left alone). An amber count and a short **Paused** note on the Folders tab mean a new folder is waiting for you (see [the first check](#the-first-check-of-a-new-folder)).

The kinds are:

| Area | Kinds | Looks details up? |
|---|---|---|
| Watch | Movies, TV Shows, YouTube | Yes |
| Watch | Videos | No: courses, talks and downloads, shown by folder layout and file name |
| Listen | Music, Audiobooks | Yes |
| Listen | Other audio | No: recordings and lectures, shown by folder and the tags inside the files |
| Read | Books, Comics | Yes |
| Read | Other reading | No: loose books and documents, shown by folder and the details inside the files |
| View | Photos, Personal videos, Documents | No (Documents are read on your computer with text recognition) |

A kind that looks details up has its icon tile in its area's colour; a kind that does not has a quiet grey tile. Several libraries can share one kind (for example Home videos and Other videos): each is its own row. Photos, Personal videos and Documents keep their folders on View's own pages, reached from the View tab. Older addresses (a group such as `/settings/libraries/watch`, or a type such as `/settings/libraries/watch/movies`) still work and land on the matching tab or library. On a phone the rows, counts and status are read-only, with a note to use a desktop or tablet to change folders.

## Add a folder

Open **Settings > Libraries** with admin access and choose **+ Add folder**: on the front door, on a group's page, or **Add Folder** on a type's page. A short dialog opens. Nothing is created until the last step, **Back** keeps what you chose, and **Cancel** at any step creates nothing.

1. **What is it?** Pick Watch, Listen, Read or View (Vault joins them only when it may be shown), then the kind. Each kind is a card with its coloured or grey tile and an (i) that says what belongs there. Opened from a group page, this step starts on that group; opened from a type page, the step is skipped.
   - Choosing a View kind takes you to View's own add-folder form, which keeps View's settings in one place. (First-run setup is the exception; see below.)
   - Choosing the Vault goes straight to the folder step; see [The Vault](../explanation/vault.md).
2. **Library** (only for Videos, Other audio and Other reading). These kinds can hold several libraries, each with a name you choose ("Lectures", "Family recordings"), up to five per kind. Pick an existing library or **New custom library** and type its name. Each named library is its own row on the group page and its own tab when you browse. The other kinds are one library each.
3. **Folder.** Pick the folder.
   - The address bar shows the folder as clickable names, like Windows Explorer. Click a name to go there, or the small arrow after it to jump to a folder inside it. Click the empty end of the bar to type or paste a full path instead, then press Enter (Esc puts the names back).
   - Single-click a folder to select it, and double-click to open it.
   - Use **Back**, **Forward**, **Up** and the folder search to find a folder by name.
   - **Selected path** shows what you picked, with a copy button.
   - Media locations are listed before drives. Only folders are listed, the Library's own folders never appear, and **Next** turns on when the folder is valid.
   - A Watch folder that looks like untouched phone footage (for example `DCIM` with `IMG_` files) gets a hint with **Add to View instead**. Nothing is saved until the last step.
   - Paths are shown as the server sees them. On Docker that means paths inside the container, such as `/library`, not paths on your computer.
   - If a path does not exist, the picker says so in plain words.
4. **How.** If the folder already has files, choose **Read-only** (recommended), **Merge into your managed folder**, or **Let Library manage it**. An empty folder only asks whether new media should go there. **Add folder** saves it; the dialog then offers **Go to** the kind or **Add another**.

The kind decides what is looked up. Choosing YouTube looks videos up on YouTube by their ID (only the ID is sent); the Videos, Other audio and Other reading kinds never look anything up and never contact an outside service. There is no YouTube switch. Removing a folder never touches the files.

Every folder row has a **Suitable for limited profiles** switch (not on a Vault folder). Unrated items from that folder, such as saved videos or audiobooks with no rating, are then shown to profiles with a rating limit, whatever their "allow unrated" setting says; rated items still follow each profile's limit, and only a blank rating, NR, Not Rated or Unrated counts as unrated. Without the switch, a limited profile sees an unrated item only if that profile allows unrated. It saves straight away.

Every library has a **Recycle bin** switch (on for Videos). With it on, a deleted file is kept in Library's own data folder instead of being removed straight away. Settings lists what is waiting there; **Put back** returns a file to where it was (nothing is ever overwritten) and **Empty recycle bin** removes everything for good.

First-run setup asks the same thing in its own words, "What do you have?": tick Watch, Listen, Read or View, then add a folder for each with this same dialog. The Vault is never offered there. View folders can be added once the administrator is signed in, to the household's shared library (the default) or to the administrator's own space; until then View says to add them after setup, in **Settings > Libraries > View**. **Skip for now** still defers the step.

## Choose how a folder is handled

Every folder is handled one of two ways:

- **Read-only** means Tuvima never moves, renames, tags, overwrites or deletes its files, so other apps are not affected.
- **Managed** means Tuvima may rename, move and (when the type's tag setting is on) tag the files, including the ones already there. Choose it only for folders no other app depends on.

Each type has one **New media goes here** folder: the managed folder that receives new files. The first managed folder you add becomes it; use a managed folder's row menu to make another the place for new media. There is no priority order.

The folder list is a table with Folder, New media and Files columns. Each folder's icon shows how Library treats it: a plain folder for Managed (full access) and a folder with a lock for Read-only; hover it for the words. The row menu offers Make this the place for new media, Organise files now, Merge into, Change what this folder is, Switch to Read-only or Managed, Label and Remove folder. Removing a folder never deletes files.

Keep music album folders intact. Tuvima uses tags or fingerprints to identify music before moving it.

### The first check of a new folder

A folder that you add to a kind that looks details up gets a first check before Library changes anything in it. While the first files are identified (50 by default, or all of them if there are fewer) the row reads "Checking the first files…", and in a Managed folder **nothing is renamed, moved or tagged yet**; files still appear in Library straight away.

- If most of those files match, the check passes. In a Managed folder the moves and tags that were held are queued together and done in the next run (see [When changes reach your files](#when-changes-reach-your-files)).
- If more than half do not match, the folder is paused. Its row shows an amber line, "Most files in this folder aren't matching: 212 of 300. This is often the wrong kind or the wrong folder.", and the library's row on the group page shows a **Paused** note. Nothing has been touched, so every choice leaves your files exactly as they were: **Keep going** carries on and leaves the files that do not match as Unmatched (they are tried again later), [**Change what this folder is**](#change-what-this-folder-is) picks a better kind, and **Remove the folder** takes it out of Library without touching the files.

Plain kinds (Videos, Other audio, Other reading), the Vault and folders that were already there never get a first check. The first check appears for administrators and not in first-run setup. How many files are checked, and what share may fail, are settings.

## Unmatched files

A file Library cannot match stays exactly where it is and is marked **Unmatched**. It is tried again on its own, by default every 7 days and whenever a provider is turned on or updated, and nobody has to do anything.

- **Where they live.** Unmatched files never appear in the Watch, Listen or Read lanes, on Home, in shelves or in collections, because Library does not know what they are. Each library tab in a lane has an **Unmatched** filter. Anyone who can see that library can use it to browse and play those files, listed flat under their file names and the tags inside the files. A profile with a content limit follows its usual "allow unrated" setting for them.
- **Search.** Search shows Unmatched files in a separate **Unmatched** group, found by file name and tags, never mixed into the matched results.
- **Retry now and Keep all as they are.** A library with Unmatched files shows an amber count on its row, with **Retry now** (look them up again now) and **Keep all as they are** (it asks first). Only administrators can use them.
- **Keep as is.** On one Unmatched file's page, an administrator can choose **Keep as is** from the "..." menu (it asks first). Library stops retrying that file, and it stays under the Unmatched filter, marked **Kept**, and is not counted in the Unmatched count. **Look up again** clears the mark and tries again. A file that really belongs in the lanes as it is should go into Videos, Other audio or Other reading instead.
- **When a match arrives.** If a file is identified later and filed, its watching progress, bookmarks and other personal state go with it.

Items the Engine found but is unsure about are different: they wait in [Review](resolving-reviews.md) for a person to choose.

## Change what this folder is

If a folder is the wrong kind (home videos added as Movies, recordings added as Music), you can change it without removing and adding it again.

**Where to find it.** Open the folder's "..." menu on its type page and choose **Change what this folder is…**. A folder that has paused on its first check offers the same choice beside **Keep going** and **Remove the folder**. It is not offered while a job is running on the folder, for Vault folders, in first-run setup, or where folders are read-only (phones and portrait tablets).

**Same area only.** The dialog first asks "What is this folder?" and offers the other kinds of the folder's own area: from Movies you can pick TV Shows, YouTube or Videos, never a Listen kind, and never the kind it already is. The cards show the same coloured or grey tile, Metadata icons and (i) as elsewhere. A kind that holds several named libraries also asks which library, or **New custom library**; the new library is created only when you confirm.

**What changes, before anything moves.** The next step lists the impact in plain lines, worked out by the Engine, and nothing is sent until you press **Change folder**:

- **Files moved and re-filed.** "N files will move to Videos." In a Managed folder their files are re-filed into the new kind's folder pattern in the next run, not at once, because different kinds never share one folder root (a Movies folder mixed with home videos confuses other apps too).
- **Details dropped for a plain kind.** Changing to Videos, Other audio or Other reading removes the details Library looked up online for these files (titles, descriptions, artwork and outside ids). They then show as they are, by file name and the tags inside the files. Your own edits, watching progress, ratings and bookmarks stay.
- **The first check again for a lookup kind.** Changing to a kind that looks things up identifies the files again under that kind and runs [the first check](#the-first-check-of-a-new-folder) as if the folder were new: nothing is re-filed until it passes, and if most do not match, it pauses with the usual choices.
- **Other apps lose the files.** For a Managed folder: other apps that point at these files (another player, a playlist, a backup) will lose them when they move.
- **Who will not see the files.** "Sam won't see these 40 files." names each profile who has the library the files move into hidden (up to five names, then "and N others"). If Library could not check this, it says so and you can still go on. See [Who sees a library](#who-sees-a-library).

If the Engine refuses the change, the dialog shows its one sentence and only **Close**. Press **Cancel** at any point to leave everything as it was.

**Read-only folders move nothing.** A Read-only folder changes kind without a single file being moved or renamed; only the library it belongs to, what is looked up and what is shown change. Dropped details and the first check still apply as above.

**While it runs.** The folder's row shows the job's progress and message the way Convert and Merge do, with Pause, Resume and Cancel. If it finishes with problems (for example two titles that both carry your own artwork or details, which Library will not merge for you), the row says which; fix them in Editing Mode and start again, or change back.

**Changing back.** You can change a folder back to the kind it was at any time. Details dropped for a plain kind are not restored: changing back to a lookup kind identifies the files again, and your own edits were never touched.

## Who sees a library

Everyone with a library's area (Read, Watch, Listen or View) sees it, including a library you have just added; nothing needs ticking. To keep one from a person, open their page under **Settings > Profiles**, find the area, and untick the library in its **Libraries** list. It saves at once and says "Hidden from {name}"; the list shows only when the area has more than one library, administrators always see everything, and the Vault keeps its own rules and never appears in the list. Moving a folder or changing its kind tells you who will stop seeing its files before you confirm. See [Set up a household](households.md) for who may change this.

## Advanced settings

Each type has an **Advanced** section with its naming template (**Naming template**, **Apply template**) and **Update tags inside files** (only affects managed folders). The section **File handling for every type** holds the library-wide rules **When the same file arrives twice** (**Keep one copy** or **Keep both**) and **Embed artwork in files**.

### When changes reach your files

Library saves every change straight away, so what you see in Library is always up to date. Writing those changes into the files themselves (tags in music, books, comics and videos, embedded artwork, and the profile name in View files) waits for one to-do list. Library works through that list in one pass on a schedule: every **60 minutes** by default. You can change the interval in **Settings > Libraries > Advanced** ("Write changes into files every [60] minutes", from 1 to 1,440 minutes).

A later change to the same file replaces the earlier one, so each file is written once per run with its final values.

The panel shows:

- **N files waiting · Last run 10:00 · Next run 11:00**, or **All files are up to date** when the list is empty.
- **Writing 240 of 1,200 files…** while a run is going.
- **3 files couldn't be written**, with the list of files and the reason for each.
- **Write now** (administrators, on a desktop) starts a run at once. Phones show the status only.

If ExifTool is missing, the panel says **Photos, videos and PDFs in View are waiting for ExifTool**, with a link to **Settings > Tools and requirements**. With **Update tags inside files** (or write-back) turned off, no tags are queued and the schedule is hidden; filing moves still wait for their run.

- A file that is busy, on an unplugged drive or read-only stays on the list for the next run.
- A file that can't hold tags is **kept in Library only** and is not retried.
- Restarting the Engine loses nothing. The list is kept.
- A file you copy out of Library before the next run may not carry the latest tags yet. This is the accepted cost of batching.

**Filing moves wait too.** When Library files a file on its own (a newly identified file going into its folder's pattern, a file leaving the Import folder, or a folder re-filed after [Change what this folder is](#change-what-this-folder-is)), the move joins the same list and is done in the next run, before that run's tags; **Write now** does it at once. Until then the file stays where it is and stays playable. If a file is in use when its turn comes (being played, or held by another program), it is left untouched and tried again in a later run. A new Managed folder queues nothing until its [first check](#the-first-check-of-a-new-folder) passes.

**Moving a title** yourself to another library, in Editing Mode, moves its file straight away, so folders always match Library. Only its tags wait for the run. A move interrupted by a crash or power cut is finished or undone at the next start, so the file is never in two places or in none. If the drive is unplugged, the move waits until the drive is back. See [how libraries and intake work](../architecture/library-model-and-intake.md).

If a file was edited in another app after its write was queued, Library reads it again first. Your own locked fields still win.

The editor's **Files** card shows **Changes waiting to be written to the file · next run 11:00**, or the reason the last write failed.

## Convert a folder to Managed

Switching a Read-only folder to Managed asks for confirmation and shows a preview of the proposed moves and renames. Confirming starts a background organising job.

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

When you delete a title from the library, the files that stayed on disk are listed under the folders of that library: files in a read-only folder, the original of a merged file, and any file Tuvima could not remove. Scans skip them, so the deleted title does not come back. The list appears only when there are excluded files, and shows each file, its folder, when it was excluded and who deleted the title.

1. Open **Settings > Libraries** and choose the media type.
2. Find **Excluded files** and check the row for the file.
3. Choose **Add back**. Tuvima scans that one file again, so the title comes back after the scan.

If the file is no longer on disk, **Add back** removes the row and nothing is scanned. Only administrators can add a file back.

## Import folder

The **Import folder** is the line under the table on the Libraries overview. Set it once and drop new media into it, for example from a download tool. Tuvima creates one subfolder per type and files each arrival into that type's New media folder. Files it cannot match go to **Review** with the badge **From the Import folder**. A type with no managed folder leaves its files untouched and says so in one sentence. Use **Check folders for changes** to scan it now.

### The Folders tab

The Folders tab is a table with one row per folder: **Folder** (its short name, with the full path under it), **Titles** (how many titles the catalogue holds from that folder), **Access** (Accessible, Folder offline or Needs access, always in words), **Scan** (a switch; off pauses scanning of just that folder) and **Import destination**. Exactly one managed folder is the import destination; an As-is folder cannot be one, and the (i) beside it says why. An offline folder gets an amber warning row under it, and a folder that is being scanned shows a small spinner. The "..." menu on a row has **Override file handling for this folder** (a small dialog where the duplicate rule and writing tags each default to "Use library setting"), **Pause scanning** or **Resume scanning**, **Change what this folder is** and **Remove folder**. On a phone the table is read-only.

**Add folder** (page header) opens the folder picker for this library's content type and adds the folder to this library. The new row is highlighted and one line confirms it; nothing else is asked.

**Add files** (page header, not on phones) copies files from the server into this library's import destination, so it is disabled with "Choose an import folder first" until the library has one. The picker lists server files, greys out the ones this library cannot take, and says for example "2 videos selected, 7.8 GB". Before anything is copied the Engine checks free space and refuses with "Not enough space: N needed, M free". The originals are only read, never moved or changed; a file already in the library is skipped when the duplicate rule is Keep one copy ("Already in your library"), and copied under a numbered name with Keep both. The summary reads "Copied N. Already in your library: N. Could not be read: N." Only administrators can do this, and every file must sit inside an approved storage location.

### Add files by dropping them

Library pages have their own **Add files** that copies server files (see [The Folders tab](#the-folders-tab)); the dropping flow below belongs to the older Watch, Listen and Read pages.

On a Watch, Listen or Read page, or on a type's own page, you can drop files from your computer onto the list, or choose **Add files** beside **Add folder** (a touch screen or a pointer that cannot hover gets the button only; phones and tablets get neither, since libraries are read-only there). The files are sent to the server in pieces and land in the Import folder under that type, where Library files them on its next run. If a file does not fit the page (a film dropped on Music), one dialog asks which kind it is for, with the usual kind cards; a kind with several libraries also asks which library. A file name already in use gets a number added, never an overwrite. A file above `large_upload_warning_bytes` (50 GB by default) first shows an amber warning that large uploads can fail and that copying straight into the Import folder on the server is more reliable, with that folder's path and **Copy path**; **Upload anyway** goes ahead and **Cancel** drops just those files. Each file shows a progress bar with **Cancel**, which deletes the partly sent file; uploads keep going while you move between Libraries pages, if the connection drops the upload carries on from the missing pieces, and after a reload choosing the same file again resumes it. A refusal says why in one sentence and links to the fix, for example **Set an Import folder first**. Adding files to the Vault by dropping is not available yet, and photos go through View's own upload. Only administrators can add files this way, and an administrator can turn it off with `allow_drag_and_drop`.

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

The development tools include Reset & Seed Test Library, Rescan All Libraries, View Ingestion, and Advanced resets. Fixture cleanup uses a list of generated files and follows each one to wherever the Library filed it. Routine resets delete only what the Library created; your own files, read-only folders, the Import folder and photos stay, and the result lists the files it kept.

</details>

## Next steps

- [Add media to your library](adding-media.md)
- [Configure metadata providers](configuring-providers.md)
- [Resolve a review item](resolving-reviews.md)
- [Check feature availability](../product/status.md)
