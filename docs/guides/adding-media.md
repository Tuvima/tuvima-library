---
title: "Add media to your library"
description: "Use watched catalogue folders or a profile's View Personal Space to import media without changing protected originals."
audience: "user"
category: "guide"
product_area: "library"
status: current
---

# Add media to your library

Bring files into Tuvima Library from a watched folder or an existing collection. Adding a folder takes a few minutes; processing time grows with the batch.

## Choose how files enter

| What you want | Use |
| --- | --- |
| Add books, comics, movies, TV, music, or audiobooks | A structured library with catalogue matching |
| Keep an existing collection unchanged | An **Existing library** source with read-only access |
| Let Tuvima organize eligible incoming files | A **Managed by Tuvima** source and primary destination |
| Keep photos, home videos, documents, or notes private | A profile's View Personal Space |

Start with one media type and a few files. Large imports are easier to check after providers and permissions work.

## Configure a catalogue folder

1. Open **Settings → Libraries**.
2. Add a library, or open its **Folders** section and choose **Add folder**.
3. Choose its type and server-side path.
4. Select **Existing library** or **Managed by Tuvima**.
5. Check access and confirm the addition.
6. Open **Settings → Operations** and choose **Scan now** for an existing batch.

New files in watched folders are picked up automatically. **Scan now** explicitly starts an extra scan. Operations at `/settings/ingestion` shows current work and bounded batch history without manual refresh.

Existing sources are never modified. Keep **Protect existing files** enabled unless you understand the managed source's write behavior.

## Check supported files

| Lane | Typical formats |
| --- | --- |
| Read: books | EPUB, PDF |
| Read: comics | CBZ, CBR, CB7 |
| Watch: movies and TV | MKV, MP4, M4V, WEBM, AVI |
| Listen: music | FLAC, MP3, AAC, M4A, OGG, WAV |
| Listen: audiobooks | M4B, MP3, M4A |
| View | Images, supported local video, audio, and documents |

See [media types](../reference/media-types.md) for the full format contract. Container support does not guarantee every embedded codec is playable.

MP3, M4A, and video containers can have several meanings. Clear folder context and good embedded metadata help Tuvima choose safely. Uncertain catalogue classification goes to Review Queue.

## Add private media

Configure View's storage root and enable the owning profile. Use **Import folder** to copy into managed storage, or **Link existing folder** for read-only indexing. Browser upload depends on the profile and server policy.

View keeps available local metadata and source paths, groups companion files, and indexes the local timeline. It does not call retail providers or Wikidata and does not enter catalogue identity review.

Favorite, Hidden, Archive, Trash, Restore, and Gallery actions organize View records without rewriting originals. Shared Library contribution transfers are a separate confirmed workflow; see [View Personal Space](view-personal-space.md).

## Follow an import

Tuvima waits for copying to settle, fingerprints the file, reads metadata, finds likely matches, gathers artwork, and organizes eligible managed files.

Catalogue items enter browse surfaces when title, type, and artwork state are settled. A completed file count does not mean every required batch operation is finished. Operations reports provider waits and later work separately.

Open **Settings → Review Queue** when an item needs confirmation. Use [editing items](editing-items.md) for corrections from a detail page.

## Improve matching

- Keep embedded metadata.
- Include known identifiers such as ISBN, TMDB, MusicBrainz, or Comic Vine IDs when available.
- Separate ambiguous file types into folders with clear intent.
- Connect required providers before a large import.
- Resolve a small batch before importing thousands of files.

## Next steps

- [Walk through your first library](../tutorials/first-library.md).
- [Connect metadata providers](configuring-providers.md).
- [Understand ingestion](../explanation/how-ingestion-works.md).
- [Troubleshoot missing files](troubleshooting.md).
