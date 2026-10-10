---
title: "Your first library"
description: "Add a safe source folder, import a small media batch, and find the result in the correct library lane."
audience: "user"
category: "tutorial"
product_area: "library"
status: current
---

# Your first library

Add a folder to Tuvima Library and check a small batch before importing everything. Folder setup takes a few minutes; matching time depends on your files and providers.

Start with a running server and an administrator account. See [getting started](getting-started.md) if you have not completed setup.

## Choose the right destination

Use a structured library for books, comics, movies, TV shows, music, or audiobooks. These files can receive catalogue metadata.

Use [View Personal Space](../guides/view-personal-space.md) for private photos, home videos, documents, or audio notes. View does not send files through retail matching or Wikidata.

## Add a structured library

1. Open **Settings → Libraries** and choose **Add library**.
2. In **Choose type**, select Books, Comics, Movies, TV Shows, Music, or Audiobooks.
3. In **Add folders**, choose an approved storage location or enter a server-side path.
4. Choose **Existing library** for originals that must stay unchanged. Choose **Managed by Tuvima** only for folders Tuvima may organize.
5. Check read/write access and any overlap warnings.
6. Review the settings and confirm the final action.

The path belongs to the server running the Engine. For Docker, use the path inside the container, such as `/library`, rather than its host path.

**Protect existing files** starts on. Existing-library sources always stay read-only. Saving a naming template does not reorganize existing files. A filename collision preserves the existing file and gives the incoming file a unique name.

## Import a small batch

1. Use a few supported files from one media type.
2. Copy them into a managed source, or attach an existing read-only folder containing them.
3. Open **Settings → Operations** and choose **Check folders for changes** (in the Ingestion page "⋯" menu).
4. Keep Operations open to see active work and recent batch outcomes.

Operations uses `/settings/ingestion`; it refreshes automatically. File scanning can finish while matching, artwork, or organization still runs.

Tuvima reads file metadata, identifies likely matches, gathers artwork, and organizes eligible managed files. Some files need human confirmation. Optional provider work can continue after an item becomes browsable.

## Check the result

| Media | Where to browse |
| --- | --- |
| Books and comics | Read |
| Movies and owned TV episodes | Watch |
| Music and audiobooks | Listen |
| Personal local media | View |
| Cross-library results | Search |

Home shows populated discovery shelves. For Me keeps the active profile's Continue, My List, Favorites, and owned collections or playlists.

Catalogue browsing requires a real title, resolved media type, and a settled artwork outcome. A missing image can be a settled outcome; an unresolved match should not look confirmed.

If a file needs attention, open **Settings → Review Queue**. Read the reason and use the shared editor to confirm or correct it.

## Add personal media to View

1. Configure the single View storage root in **Settings → Libraries**.
2. Enable View for the intended profile.
3. In profile source management, use **Import folder** to copy into managed storage, or **Link existing folder** to index originals read-only.
4. Open **View** with that profile. Browser upload is available only when its policy permits it.

Every enabled profile has one Personal Space. Sources and devices feed that space rather than creating separate libraries. A populated View root cannot be relocated through settings.

Managed uploads use `View/Profiles/<profile-label>/Timeline/<year>/<month>/`. Managed mixed folders use `Folders/<folder-label>` and retain their hierarchy. Labels remain stable when display names change.

Opening the Shared Library and sending to it are separate permissions (both on by default, except a child profile cannot send until a household administrator allows it). A household administrator reviews contributions, which remain pending until reviewed. Accepted managed files move only after verified Shared copies exist; linked originals are copied and retained.

## Adjust folders later

Open the library to manage Folders, Organization, File Handling, and Advanced Settings.

**Add folder** saves at its final confirmation. Detaching requires confirmation and leaves files on disk. If the detached primary still serves managed folders, choose a replacement primary. Names and custom templates use their own Apply action.

## Next steps

- [Add more media safely](../guides/adding-media.md).
- [Connect providers](../guides/configuring-providers.md).
- [Resolve review items](../guides/resolving-reviews.md).
- [Create a recovery point](../guides/operations-and-recovery.md).
