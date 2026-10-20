---
title: "Use your Personal Space in View"
description: "Add and browse personal media and documents, organize Galleries, and understand Shared Library access and Places privacy."
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

You can favorite, hide, archive, move to Trash, or restore your own items. Archive removes an item from the normal Photos view. Moving to Trash can be undone, and only [Empty Trash](#move-items-to-trash-and-empty-it) ever removes files from your disk. Browsing and making thumbnails do not change original files.

## Move items to Trash and empty it

**Move to Trash** can be undone. In Photos or Documents, select one or more items, or open one, and choose **Move to Trash**. Phones and tablets can do this too. **Restore** is in the Trash view (**Photos > Trash** or **Documents > Trash**) and brings an item back where it was. While an item is in Trash it is out of Photos and out of your Galleries, and it cannot be a Gallery's cover. Restore puts it back in its Galleries. Moving an item to Trash never changes the file on disk.

Items in a read-only folder cannot be moved to Trash. A folder you have linked to Library is there for looking at, so the button is not shown for them. If you select a mix, the other items go to Trash and Library tells you how many stayed: "3 items are in a read-only folder and weren't moved to Trash." Favorite, Archive, Hide and Galleries still work on them. Items in the Shared Library cannot be moved to Trash either.

The Trash view shows how much it holds, such as **128 items · 4.2 GB**, on every device, so you can see the space Trash is using.

**Empty Trash** deletes the files of everything in your Trash, for photos and documents together. It works only on a computer. On a phone or tablet the Trash view shows its size and "Empty Trash on a computer." Only you can empty your own Trash, from your own Personal Space; household members, administrators and the native apps cannot. You cannot pick items out of Trash to delete. To keep something, restore it first.

On a computer, open **Photos > Trash** or **Documents > Trash** and choose **Empty Trash…**. Library first checks what is in Trash, then shows:

- how many items will be deleted from your disk, split into photos, videos and documents, with the space they use;
- which Galleries will lose items, from A to Z;
- anything that stays in Trash and why: an item waiting to be added to the Shared Library (cancel that contribution first), an item in a read-only folder, or a file Library will not remove safely.

Live Photos and RAW sets count as one item, and all their files go together. Cancel is selected when the dialog opens, and Escape cancels. Only the red **Delete N items from disk** button deletes anything. Deleted files cannot be recovered, and Library has no other copy.

If the Trash changed while the dialog was open, Library shows the new list and deletes nothing; confirm again. If one item cannot be removed, for example because its file is in use, the others go and that item stays in Trash with a note on its tile, such as "Couldn't delete: the file is in use." After a successful run Library says how many items were deleted and how much space was freed.

## Find and file documents

**View > Documents** lists the PDFs, Office files, scans and photos of paper in your Personal Space. Tuvima Library reads their words on your computer, so searching finds what a document says. New documents wait in the **Inbox** with suggested dates, correspondents, types and tags. File them with one tap, or make **Auto-filing rules** so Tuvima Library files them for you.

1. Open **View > Documents**. If the tab is missing, an administrator has not turned on Documents for your profile (child profiles start with it off).
2. Choose **Inbox** to see what is waiting, or **All documents** to browse and search.
3. Use the filters on the left for tags, correspondent, type, date and custom fields. **Save this view** keeps a filter set under a name. Saved views are private to you.
4. Choose **Select** to add tags or set a correspondent, type or date on up to 500 documents at once, or to move them to the **Trash**. **Export list (CSV)** downloads the list you are looking at.

A television can browse and open documents but not change them. A phone can search, read and file from the Inbox, but editing every detail of a document and editing rules need a computer with a mouse or trackpad. Read [Documents in View](../explanation/documents-in-view.md) for the whole picture, and [How documents are filed](../explanation/how-documents-are-filed.md) for the Inbox and rules.

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

A household administrator accepts or declines the batch (so can a server administrator). The transfer checks the shared copy before making it a shared member. Managed originals move only after that check. Linked originals are copied and stay in their source folder. When you send someone else's photo, the Shared Library always gets a copy and their original stays in their own space. Everyone in a household can open and send; a child profile can send only after a household administrator allows it.

## Browse your household

Open the scope picker at the top of Photos, Places or Folders. Your own space stays first, then a **Household** group lists each person in your household by name, then the Shared Library. You can open, search and share from anyone's space in your household, but you cannot edit, hide, move or delete their photos, and their hidden photos stay hidden. People in other households never appear, and none of their photos, counts, map pins or contributions are visible to you.

## See other households as a server administrator

Server administrators have an **Other people** group in the scope picker. It lists every other household, each person in it, and the household's Shared Library. Everything there is read-only: you cannot edit, hide, move, delete or send anything, hidden photos stay hidden, and household administrators and members never see the group. When the administrator screens are locked with a PIN, unlock them first.

Every time you open someone's space or a household's Shared Library, Tuvima records it. The person whose photos you opened sees the date, your name and what you opened under **Settings > Security > Who viewed your photos**, read-only (for a household's Shared Library, the household's own administrator sees it; a server administrator of another household does not). No one else can see it, and it cannot be deleted from there. The same space is recorded at most once an hour.

## Switch View on for a person

People outside your household start with **View** off. Open **Settings > Users and Sign-in** and use the **Can use View** switch on their row when you want them to use it. People in your own household keep whatever you chose when you added them.

## Browse People and Places

People shows named or reviewed people annotations and their sources. If no usable names exist, it explains that state. It does not claim to run face recognition.

Places opens **Tuvima Atlas** from real GPS and place facts. Select a place to browse its media that you can access. Use time, media, and other offered filters to narrow the view.

**Places can make external map requests.** It first tries OpenFreeMap's dark style. That style can load outside map resources. If the request fails, Atlas uses a local country map. Opening Places is not always an offline or network-free action.

Face recognition, object/scene detection, captions, semantic search, and AI memories are planned work. Reading the words in scanned documents and photos of paper is already live (see Documents above). Stored annotations and browse pages do not prove that those AI workers run today.

<details>
<summary>Technical details</summary>

A phone backs its photos up to one person that is chosen for the phone, not to whoever the phone is browsing as. The phone's app asks for that person's PIN when they have one; an administrator can also choose under **Settings > Security > Devices** (**Backs up to**). Until one is chosen, backups from that phone are refused.

Settings > Libraries chooses the managed View root. Current paths use stable `profiles/<profile-id>/sources/<source-id>` folders. Accepted shared originals live in a separate `Shared` tree. Do not move files by hand to match old `View/Profiles` wording.

View uses an internal `personal` library bridge. Local-only/manual personal media skips retail matching, Wikidata, canonical claims, and Review Queue. Each media request and browse query checks View access again.

The map tries `https://tiles.openfreemap.org/styles/dark`. On failure it falls back to `/maps/world-countries.geojson`. See [View architecture](../architecture/view-personal-media.md) for storage and access rules.

</details>

## Next steps

- [Understand privacy and network access](../explanation/privacy-local-first.md)
- [See how Documents work](../explanation/documents-in-view.md)
- [Find your Galleries in For Me](for-me.md)
- [Configure library and View storage](library-settings.md)
- [Check current availability](../product/status.md)
