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

The Vault is a private area of Tuvima Library for material you do not want mixed in with the rest of your collection, such as adult videos. It is switched off by default. While it is off, nothing about it appears anywhere: no card in Settings, no choice in Add folder, no search result and no hint that it exists.

This page explains how the Vault is kept apart, and why its folders must never be shared with a normal library. To add a Vault folder, see [Manage library settings and operations](../guides/library-settings.md).

## Turning the Vault on

Only an adult profile that an administrator has given Vault mode can open the Vault, and only an administrator can add or change its folders. Vault mode is turned on from Settings and ends on its own after a time you choose, when you sign out, or when you switch profile. If the profile has a PIN, turning it on asks for that PIN. Adding the first Vault folder asks for the administrator's PIN again and for a confirmation that the folder is for adults only.

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

## Next steps

- [Manage library settings and operations](../guides/library-settings.md)
- [Privacy and local-first](privacy-local-first.md)
- [Operations and recovery](../guides/operations-and-recovery.md)
