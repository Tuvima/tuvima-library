---
title: "Manage library settings and operations"
description: "Configure safe library sources, check background work, and find provider, profile, and recovery controls as an administrator."
audience: administrator
category: guide
product_area: ingestion
status: current
---

# Manage library settings and operations

Use Settings in Tuvima Library to create libraries, add folders and check background work. This five-minute guide helps you choose safe [sources](../reference/glossary.md#library-folder) and find the right admin page.

## Find your way around Libraries

Libraries is **Settings > Libraries**, in two levels. Each has its own address, so Back and a reload keep your place, and the page title and breadcrumb above it show where you are.

1. **The overview** has four tabs, **Watch**, **Listen**, **Read** and **View**. The tabs filter the list: pick one and the table under it shows only that area's libraries, one row per library. A row has the library's name with its icon, its content type, who can see it (**Standard** or **Selected profiles**), its folder and title counts ("3 folders · 1,284 titles", or "Nothing added yet") and a status: **Healthy**, **Needs review** (with the number of files; it opens Needs Review), **Empty**, **Folder offline** (the folders at fault in a tooltip) or **Scanning**. The whole row opens the library, and a chevron and a highlight on hover show that it does. Search and a status filter sit above the table, and an area with nothing in it says "No Watch libraries yet" with one **Add library** button. A Vault-protected library appears, with a lock, only while Vault mode is on for you and your profile may use it; otherwise there is no row and no hint. Under the table is a line for the [Import folder](#import-folder) (it reads "set" or "not set").
2. **A library page** (for example Movies) shows the library's content type, who can see it, its counts and status, and **Scan now**, **Add folder** and **Add files**. Its tabs are **Folders** (the table of folders), **Access**, **File handling** and **Activity**, plus **Lookup** for a library that has lookup settings (a YouTube library, or a Vault-protected library of a kind that looks details up). Each tab is described below. **Scan now** scans this library's folders only (folders with scanning paused or offline are left alone). An amber count and a short **Paused** note on the Folders tab mean a new folder is waiting for you (see [the first check](#the-first-check-of-a-new-folder)).

New libraries start from **Add library** on the overview (see [Create a library](#create-a-library)). First-run setup shows this same table in a simpler form (see [First-run setup](#first-run-setup)).

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

A kind that looks details up has its icon tile in its area's colour; a kind that does not has a quiet grey tile. Kinds that look nothing up (Videos, Other audio and Other reading) can hold several named libraries, up to five each (for example Lectures and Family recordings): each is its own row. The other kinds hold one library each, and you add more folders to it instead. In Watch, the Videos page in the lanes gathers the YouTube and Videos libraries: with more than one it shows a chip for each library to filter by, and a profile that cannot see a library sees neither its row nor its chip. Photos, Personal videos and Documents open the same page shape as any library (see [View libraries](#view-libraries)). Older addresses (a group such as `/settings/libraries/watch`, or a type such as `/settings/libraries/watch/movies`) still work and land on the matching tab or library. On a phone the rows, counts and status are read-only, with a note to use a desktop or tablet to change folders.

## Create a library

Choose **Add library** (on the overview, or in the empty state of an area) with admin access. One short dialog opens; nothing is created until you press **Create**, and **Cancel** creates nothing.

1. **Media area.** Watch, Listen or Read. (View's three kinds always exist; see [View libraries](#view-libraries).)
2. **Name.** Up to 80 characters. It is filled in for you from the content type ("Movies", then "Movies 2" if that name is taken) and must be new within its area, so two rows can always be told apart.
3. **Content type.** Each kind is a row with an (i) that says what belongs there. A kind that holds one library is greyed out once you have it, with "Add a folder to it instead". A kind that holds several (Videos, Other audio, Other reading) stops at five.
4. **Access.** **Standard** (everyone who can use the area) or **Selected profiles** (only the profiles you tick afterwards on the library's Access tab; the dialog warns that until then only administrators see it). A third choice, **Vault-protected**, appears only for an administrator whose Vault mode is on (see [The Vault](../explanation/vault.md)). For anyone else the dialog holds no Vault word.
5. **First folder.** The folder picker sits in the dialog. The address bar shows the folder as clickable names, like Windows Explorer: click a name to go there, or the small arrow after it to jump into a folder; click the empty end to type or paste a full path, then press Enter. Single-click selects a folder and double-click opens it. **Back**, **Forward**, **Up** and the folder search help you find one, and media locations are listed before drives. As soon as you pick a folder, Library checks it and says so in plain words: how many media files it found (counting stops at a cap, so a huge folder never freezes the dialog), and a refusal if the folder is the same as, inside or around another library's folder, cannot be read, or does not have enough free space to be used. Create stays off until the folder passes. A short line says how subfolders are treated in this area (in Watch they give structure: a folder of several videos is a series).
   - A Watch folder that looks like untouched phone footage (for example `DCIM` with `IMG_` files) gets a hint with **Add to View instead**.
   - Paths are shown as the server sees them. On Docker that means paths inside the container, such as `/library`, not paths on your computer.
   - If a path does not exist, the picker says so in plain words.

**Create** saves the library with that folder, starts a scan and opens the new library's page. You are not asked anything else. The folder is **Read-only** (As-is) unless the kind is one Library normally manages, in which case it is **Managed**; you can switch it later from the folder's menu (see [Choose how a folder is handled](#choose-how-a-folder-is-handled)). Tooltips (the (i) marks) show on top of the dialog.

The kind decides what is looked up. A YouTube library looks videos up on YouTube by their ID (only the ID is sent); the Videos, Other audio and Other reading kinds never look anything up and never contact an outside service. There is no YouTube switch. Removing a folder never touches the files.

## Add a folder to a library

Open the library and choose **Add folder** in its header. The folder picker for this library's content type opens; pick a folder and press **Add**. The new row is highlighted on the Folders tab and one line confirms it. Nothing else is asked, and it is added to this library whatever its kind. A folder inside, around or equal to another library's folder is refused with a plain message.

Every folder row has a **Suitable for limited profiles** switch (not on a Vault folder). Unrated items from that folder, such as saved videos or audiobooks with no rating, are then shown to profiles with a rating limit, whatever their "allow unrated" setting says; rated items still follow each profile's limit, and only a blank rating, NR, Not Rated or Unrated counts as unrated. Without the switch, a limited profile sees an unrated item only if that profile allows unrated. It saves straight away.

Every library has a **Recycle bin** switch (on for Videos), on its **File handling** tab. With it on, a deleted file is kept in Library's own data folder instead of being removed straight away. Settings lists what is waiting there; **Put back** returns a file to where it was (nothing is ever overwritten) and **Empty recycle bin** removes everything for good.

## First-run setup

First-run setup's Libraries step shows the same table and the same **Add library** dialog as Settings, in a simple mode: Watch, Listen and Read only, Standard access only (no Access choice), no Vault, no Import folder line, and rows that do not open a page (a library's page belongs to Settings). A note reminds you that Photos and personal videos, the Import folder and each library's own settings are added after setup, in **Settings > Libraries**. **Skip for now** still defers the step.

## View libraries

View's three kinds (Photos, Personal videos and Documents) always exist, so the View tab has no **Add library** button. Each opens a page of the same shape as any library: **Folders**, **Access** and **File handling**. The Access tab keeps View's own controls (each profile's View switch and the Shared Library) in place of Standard and Selected profiles, and the File handling tab explains how View stores files. Each folder has a **Show in Photos timeline** switch; turn it off to keep that folder's photos out of the timeline while they stay browsable. There is no separate View Folders page: View's folders are managed here. See [View Personal Space](view-personal-space.md).

## Choose how a folder is handled

Every folder is handled one of two ways:

- **Read-only** means Tuvima never moves, renames, tags, overwrites or deletes its files, so other apps are not affected.
- **Managed** means Tuvima may rename, move and (when the type's tag setting is on) tag the files, including the ones already there. Choose it only for folders no other app depends on.

Each library has one **Import destination**: the managed folder that receives new files (files you add with **Add files** and files filed from the Import folder). The first managed folder you add becomes it. There is no priority order, and an As-is folder can never be it.

The Folders tab is described [below](#the-folders-tab). Each folder's icon shows how Library treats it: a plain folder for Managed (full access) and a folder with a lock for Read-only; hover it for the words. The row menu offers Override file handling for this folder, Pause or Resume scanning, Change what this folder is, Organise files now, Merge into, Switch to Read-only or Managed, Label and Remove folder. Removing a folder never deletes files.

Keep music album folders intact. Tuvima uses tags or fingerprints to identify music before moving it.

### The first check of a new folder

A folder that you add to a kind that looks details up gets a first check before Library changes anything in it. While the first files are identified (50 by default, or all of them if there are fewer) the row reads "Checking the first files…", and in a Managed folder **nothing is renamed, moved or tagged yet**; files still appear in Library straight away.

- If most of those files match, the check passes. In a Managed folder the moves and tags that were held are queued together and done in the next run (see [When changes reach your files](#when-changes-reach-your-files)).
- If more than half do not match, the folder is paused. Its row shows an amber line, "Most files in this folder aren't matching: 212 of 300. This is often the wrong kind or the wrong folder.", and the library's page shows a **Paused** note. Nothing has been touched, so every choice leaves your files exactly as they were: **Keep going** carries on and leaves the files that do not match as Unmatched (they are tried again later), [**Change what this folder is**](#change-what-this-folder-is) picks a better kind, and **Remove the folder** takes it out of Library without touching the files.

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

**Where to find it.** Open the folder's "..." menu on the library's Folders tab and choose **Change what this folder is…**. A folder that has paused on its first check offers the same choice beside **Keep going** and **Remove the folder**. It is not offered while a job is running on the folder, for Vault folders, in first-run setup, or where folders are read-only (phones and portrait tablets).

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

### Choose who sees one library

Open the library and pick its **Access** tab. There are three choices:

- **Standard** is the rule above: everyone with the area sees the library.
- **Selected profiles** shows the library only to the profiles you tick, and to administrators (who always see everything). Each profile shows its picture; Kids profiles are marked. Every other limit still applies on top: a profile also needs the area, must not have the library hidden, and keeps its content limit and the household ceiling.
- **Vault-protected** is chosen when a library is made and can never be switched on or off here. It is drawn only for an administrator whose Vault mode is on; for anyone else the tab has no Vault word. A library that is already Vault-protected shows this choice, read-only, with a note that it cannot be changed. See [Vault-protected libraries](#vault-protected-libraries).

Switching a Standard library to **Selected profiles** starts with nobody ticked, and the tab warns that only administrators will see it until you tick someone. A new profile starts with no Selected libraries. Nothing is saved until you press **Save changes** in the bar at the bottom (**Discard** puts the saved choice back, and leaving with unsaved changes asks first). Switching a library between Standard and Selected clears its ticked profiles, and a profile that gains or loses a library signs in again to refresh what it can see. Only administrators can change this, and on a phone the tab shows the choice but cannot change it. You can also pick **Selected profiles** when you add a library; the same warning appears and you tick profiles on its Access tab afterwards.

## Vault-protected libraries

A library can be made **Vault-protected** when you create it (see [Create a library](#create-a-library)). There is no separate Vault section: it is simply a library whose Access is Vault-protected, and each content type can have one ordinary library and one Vault library. The choice is only ever drawn for an administrator whose Vault mode is on. With Vault mode off, for a Kids profile and for everyone else, there is no row, no choice and no hint that the Vault exists. For why Vault folders are kept apart on disk, see [The Vault](../explanation/vault.md).

- **Creating one.** Pick **Vault-protected** under Access in the Add library dialog. The new library is named like "Private Movies" beside "Movies" and its first folder is added as it is (read-only); Library never moves or renames files in it.
- **Add as a Vault folder.** On an ordinary library, **Add folder** also offers **Add as a Vault folder** to an administrator with Vault mode on. The folder joins the Vault library of that content type, which Library makes first if there is none.
- **Move to Vault and Move out.** An administrator with Vault mode on sees **Move to Vault** in an ordinary folder's "..." menu, and **Move out of the Vault** in a Vault folder's menu. Each first shows what it would do in numbers (titles, size, destination, never a title) and says plainly if there is no Vault library of that content type yet (with **Add a Vault library**) or if the Vault library has no folder Library manages. Starting asks for your own PIN, then runs in the background and keeps going if you close the window. Progress, ratings and bookmarks move with each item. From a Read-only folder the originals stay in place unless you tick the separate "Also remove the originals" switch; Move out goes only to a folder Library manages. See [Moving items in and out](../explanation/vault.md#moving-items-in-and-out).
- **Who can open a Vault library.** Nothing is open by default. On a profile's page under **Settings > Profiles**, an administrator ticks the Vault sections (Vault libraries) that adult profile may open; a Kids or content-limited profile never gets one, and an administrator opens every section.
- **On its page.** A Vault library has no Scan now or Add files. Its Activity tab points to the Vault, where its activity is listed, never here.
- **Lookup.** A Vault library of a kind that looks details up has a **Look up details online** switch on its Lookup tab, off by default. Off, nothing from that library is sent to any online service. On, the titles of its items are sent to the lookup services for its kind, and the tab says so in plain words.

**Adult content** is a Watch content type for adult videos. Its Vault tick is on by default; unticked, it becomes an ordinary Watch library whose access is Selected profiles, never Standard. A folder cannot be changed into or out of Adult content afterwards.

## Choose how Library treats a library's files

Open the library and pick its **File handling** tab. It has four cards, each with one (i) on its heading and an **Example** line that shows what the choice does:

- **Organization**: **Preserve original names** (the default; every file and folder keeps its name), **Library standard** or **Custom pattern**. A custom pattern is checked by the Library server as you type, and a pattern it does not accept shows its error under the field and cannot be saved.
- **File changes**: two switches, both off for a new library. **Rename or move files automatically** lets Library rename and move the files in all of the library's folders that can be changed (off means they are left read-only). **Write metadata into media files** writes titles, people and artwork into the files themselves; it is separate from renaming and waits for a regular pass over your files (see [when changes reach your files](#when-changes-reach-your-files)). Both switches are greyed out, with the reason, for a Vault-protected library and for a type Library never changes.
- **Duplicates**: **Keep one copy** (Library compares what is inside the files, so a renamed copy counts) or **Keep both**. **Use server default** removes the library's own choice, and the tab says "Using the server default (Keep one copy)" while it follows the server.
- **Deleted files**: the **Use Recycle bin** switch (on for Videos, off for the other types).

Nothing is saved until you press **Save changes** in the bar at the bottom (**Discard** puts the saved values back, and leaving with unsaved changes asks first). Turning **Rename or move files automatically** on for a library that already has files shows how many files will be renamed or moved; when you press **Save changes**, Library asks the same question as converting a folder (it can take hours and cannot be undone automatically, though a full log of every move is kept) and starts the visible background job only after you confirm. Turning it off asks first and stops any organising that is running; files already renamed keep their new names. A folder can still override duplicates and metadata from its menu on the **Folders** tab. On a phone the tab shows the values but cannot change them.
## See what happened in one library

Open the library and pick its **Activity** tab. It lists what has happened in this library only, newest first, each line with a time, an icon whose colour says the kind, and a plain sentence: titles added ("Added Dune."), items that need review ("Arrival needs review", with a **Needs review** link to [Review Queue](resolving-reviews.md)), files that could not be taken in, the first check of a folder, renames and merges, and folders whose kind was changed. Nothing new is stored; the tab is a filtered view of events the Library already records, so a line appears only for the library its files belong to. It never shows another library's events, and it is shown to administrators only. A library with no events yet says "Nothing has happened in this library yet". If the Library server cannot be reached the tab says so and offers **Try again**.

## Add your own YouTube key

A library of the YouTube kind also has a **Lookup** tab. YouTube lookup works without a key: it finds a video's title, channel and thumbnail from the video ID, and the tab says **Using the shared key**. A free key of your own adds the description and upload date, and then says **Using your key**. Only video IDs are sent to YouTube, never your files.

To get a key, open **How do I get a key?** on the tab:

1. Sign in to the [Google Cloud console](https://console.cloud.google.com/apis/credentials) with a Google account and create a project. Any name will do.
2. Open YouTube Data API v3 for that project and choose **Enable**.
3. Open Credentials, choose **Create credentials**, then **API key**.
4. Copy the key, paste it into the field on the Lookup tab and choose **Save**.

**Save** checks the key with YouTube before keeping it, and **Test key** checks it without saving. The key is kept on your server in the secrets store, never in the data store, and is never shown again or written to a log: the field only says that a key is saved. **Remove** deletes it and goes back to the shared key. Only administrators can change the key, and on a phone the tab shows the state and the steps but cannot change anything.

## Advanced settings

Each type has an **Advanced** section with its naming template (**Naming template**, **Apply template**) and **Save changes into the files** (only affects managed folders; it is the one setting the editor reads, so the editor's Review changes dialog shows it as "Files will be updated: On" or "Off" with a link back here). The section **File handling for every type** holds the library-wide rules **When the same file arrives twice** (**Keep one copy** or **Keep both**) and **Embed artwork in files**.

### When changes reach your files

Library saves every change straight away, so what you see in Library is always up to date. Writing those changes into the files themselves (tags in music, books, comics and videos, embedded artwork, and the profile name in View files) waits for one to-do list. Library works through that list in one pass on a schedule: every **60 minutes** by default. You can change the interval in **Settings > Libraries > Advanced** ("Write changes into files every [60] minutes", from 1 to 1,440 minutes).

A later change to the same file replaces the earlier one, so each file is written once per run with its final values.

The panel shows:

- **N files waiting · Last run 10:00 · Next run 11:00**, or **All files are up to date** when the list is empty.
- **Writing 240 of 1,200 files…** while a run is going.
- **3 files couldn't be written**, with the list of files and the reason for each.
- **Write now** (administrators, on a desktop) starts a run at once. Phones show the status only.

If ExifTool is missing, the panel says **Photos, videos and PDFs in View are waiting for ExifTool**, with a link to **Settings > Tools and requirements**. With **Save changes into the files** (or write-back) turned off, no tags are queued and the schedule is hidden; filing moves still wait for their run.

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

1. Open **Settings > Libraries** and open the library.
2. On the Folders tab, open the folder's menu and choose **Merge into**.
3. Choose the managed folder to merge into. The import destination is chosen for you.
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

The **Import folder** is the line under the table on the Libraries overview. Set it once and drop new media into it, for example from a download tool. Tuvima creates one subfolder per type and files each arrival into that type's import destination. Files it cannot match go to **Review** with the badge **From the Import folder**. A type with no managed folder leaves its files untouched and says so in one sentence. Use **Check folders for changes** to scan it now.

### The Folders tab

The Folders tab is a table with one row per folder: **Folder** (its short name, with the full path under it), **Titles** (how many titles the catalogue holds from that folder), **Access** (Accessible, Folder offline or Needs access, always in words), **Scan** (a switch; off pauses scanning of just that folder) and **Import destination**. Exactly one managed folder is the import destination; an As-is folder cannot be one, and the (i) beside it says why. An offline folder gets an amber warning row under it, and a folder that is being scanned shows a small spinner. The "..." menu on a row has **Override file handling for this folder** (a small dialog where the duplicate rule and writing tags each default to "Use library setting"), **Pause scanning** or **Resume scanning**, **Change what this folder is** and **Remove folder**. On a phone the table is read-only.

**Add folder** (page header) opens the folder picker for this library's content type and adds the folder to this library. The new row is highlighted and one line confirms it; nothing else is asked.

**Add files** (page header, not on phones) copies files from the server into this library's import destination, so it is disabled with "Choose an import folder first" until the library has one. The picker lists server files, greys out the ones this library cannot take, and says for example "2 videos selected, 7.8 GB". Before anything is copied the Engine checks free space and refuses with "Not enough space: N needed, M free". The originals are only read, never moved or changed; a file already in the library is skipped when the duplicate rule is Keep one copy ("Already in your library"), and copied under a numbered name with Keep both. The summary reads "Copied N. Already in your library: N. Could not be read: N." Only administrators can do this, and every file must sit inside an approved storage location.

### Add files by dropping them

A library page's **Add files** button copies files that are already on the server (see [The Folders tab](#the-folders-tab)). You can also send files from your computer.

On a library's page you can drop files from your computer onto it (not on phones or tablets, where libraries are read-only). The files are sent to the server in pieces and land in the Import folder under that type, where Library files them on its next run. If a file does not fit the page (a film dropped on Music), one dialog asks which kind it is for, with the usual kind cards; a kind with several libraries also asks which library. A file name already in use gets a number added, never an overwrite. A file above `large_upload_warning_bytes` (50 GB by default) first shows an amber warning that large uploads can fail and that copying straight into the Import folder on the server is more reliable, with that folder's path and **Copy path**; **Upload anyway** goes ahead and **Cancel** drops just those files. Each file shows a progress bar with **Cancel**, which deletes the partly sent file; uploads keep going while you move between Libraries pages, if the connection drops the upload carries on from the missing pieces, and after a reload choosing the same file again resumes it. A refusal says why in one sentence and links to the fix, for example **Set an Import folder first**. Adding files to the Vault by dropping is not available yet, and photos go through View's own upload. Only administrators can add files this way, and an administrator can turn it off with `allow_drag_and_drop`.

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
