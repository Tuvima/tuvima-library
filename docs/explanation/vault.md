---
title: "The Vault and why it has its own folders"
description: "A plain explanation of the Vault in Tuvima Library: what it is, why its folders are kept separate on disk, and what that means for safety, backups, privacy and sharing."
audience: user
category: explanation
product_area: libraries
status: current
tags:
  - "vault"
  - "privacy"
  - "libraries"
---

# The Vault and why it has its own folders

The Vault is a private area of Tuvima Library for material you do not want mixed in with the rest of your collection, such as adult videos. It is switched off by default. While it is off, nothing about it appears anywhere: no row or choice in Settings > Libraries, no search result and no hint that it exists.

This page explains how the Vault is kept apart, and why its folders must never be shared with a normal library. To create a Vault library, see [The Vault area](../guides/library-settings.md#the-vault-area).

## Turning the Vault on

Only an adult profile that an administrator has given Vault mode can open the Vault, and only an administrator can create or change Vault libraries. Vault mode is turned on from Settings and ends on its own after a time you choose, when you sign out, or when you switch profile. If the profile has a PIN, turning it on asks for that PIN. The Vault is a fifth area beside Watch, Listen, Read and View; it is drawn only for an administrator or a profile with Vault access, and for anyone else there is no Vault button, library, count or search result.

## Why Vault folders are separate on disk

A Vault folder holds Vault files and nothing else. Tuvima Library does not allow a Vault folder to share space with a normal library, for four reasons.

- **Safety.** Library decides what a file is by the folder it lives in. A folder that held both would turn ordinary files into Vault files, or the other way round, and each would be treated by the wrong rules. Keeping them apart means a normal library can never organise, rename or file a Vault video, and a Vault video can never land in your Movies.
- **Backups.** A recovery point never holds the Vault's titles, file names, artwork or activity. Because Vault files live in their own folders, you can also leave those folders out of your own file backups and copies, or back them up somewhere more private, without having to sort files by hand. See [Operations and recovery](../guides/operations-and-recovery.md).
- **Privacy.** Anyone looking at your normal folders, and every other app that reads them, sees only normal material. Library keeps Vault names out of its logs, activity list and search results, and shows Vault items only to someone whose Vault mode is on.
- **No accidental sharing.** If you share a library folder with another app, a family member's computer or a network share, the Vault is not in it. A Vault folder that sat inside a normal library folder would be shared along with it.

## What Library refuses

When you add a Vault folder, Library checks the folder first and adds nothing if it fails a check. A Vault folder cannot be:

- **The same as, inside, or around a normal library folder.** You cannot pick a folder that is already a library folder, a folder inside one, or a folder that contains one (a whole drive can hold other libraries' folders, but it still has to pass the next check).
- **A folder that already holds files from your normal libraries.** Library tells you to put the Vault videos in a folder of their own first.

The same rule works the other way: a normal library folder cannot be placed inside or around a Vault folder, because Library will not accept two library folders that overlap.

A Vault folder is added as-is: Library reads the files and never moves, renames or tags them. When a Vault video is matched, only the Stash-box server you set up for the Vault is asked, and nothing else is.

## How a Vault library is made

The Vault is a fifth area beside Watch, Listen, Read and View. A Vault library is created inside it and never converted into or out of it; Adult content exists only there. Each content type has at most one ordinary library and one Vault library, and a Vault library is never listed to a profile that cannot open the Vault. Its folders are added as they are: Library reads them and does not rename, move or tag them.

An administrator can also use **Move to Vault** in a folder's menu, and **Move out of the Vault** in the menu of a folder in a Vault library. See [Moving items in and out](#moving-items-in-and-out).

## Moving items in and out

Move to Vault first shows what it would do, in numbers only: how many titles, how big, where they would go. If the content type has no Vault library yet, it offers **Add a Vault library** and carries on once that exists; if the Vault library has no folder that Library manages, it says so. Starting the move asks for your own profile PIN again.

The move runs in the background and keeps going if you close the window or restart the server. Each title is copied next to its destination under a temporary name, checked byte for byte against the original, and only then does the old record go and the copy take its real name. Your progress, ratings, bookmarks and highlights move with it. A title that cannot be moved safely (more than one file, a link, a file that is missing) is left where it is and counted, never named.

- **A folder Library manages:** the original is removed once its copy is checked.
- **A Read-only folder:** your originals are never touched. The copy is made, and the original stays where it is ("original still in place"); it shows only inside the Vault. If you also want the originals gone, there is a separate switch, off by default, that deletes each original only after its copy's hash is checked again.
- **Move out** goes only to a folder Library manages, never into a Read-only one.

Moves never name a title in the activity list, in logs, or in anything that is sent to other profiles.

## Online lookups and privacy

A Vault library of a kind that normally looks details up (such as Movies) has its own **Look up details online** switch on its Lookup tab. It is off by default, and the reason is privacy: looking something up online means sending its title to an outside service, which is something you may not want for private material, so it is a deliberate choice made library by library.

With the switch off, nothing from that library is sent to any online service. With it on, the titles of that library's items are sent to the lookup services for its kind, and the switch says so in plain words. Adult content is matched only against the Stash-box server you set up for the Vault, as described above.

## Next steps

- [Manage library settings and operations](../guides/library-settings.md)
- [Privacy and local-first](privacy-local-first.md)
- [Operations and recovery](../guides/operations-and-recovery.md)
