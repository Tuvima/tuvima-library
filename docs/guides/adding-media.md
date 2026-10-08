---
title: "How to Add Media to Your Library"
description: "Use governed catalogued sources or a profile's View Personal Space to bring media into Tuvima Library safely."
audience: "user"
category: "guide"
product_area: "library"
tags:
  - "watch-folders"
  - "import"
  - "media"
---
Tuvima Library brings files in through governed sources. Catalogued media can
use watched folders and administrator batch scans. Personal media enters one
View Personal Space per enabled profile and follows a separate local-only path.

## Choose A Folder Strategy

**Watch folder workflow** is best for day-to-day use. Put new files in the watched folder and Tuvima picks them up automatically or during the next scan.

**Batch import workflow** is best for an existing collection. Point Tuvima at a folder, scan it, and work through any review items before adding more.

These scan/review instructions apply to catalogued Read, Watch, and Listen
media. For View, configure one managed root, enable the owning profile, and use
browser upload, managed folder import, or an advanced read-only folder link.
View has no user-facing library picker, and reconciliation is an administrator
recovery/diagnostic action.

For large existing libraries, start with one media lane at a time. It is easier to tune providers and review rules with a smaller batch.

## Configure Folders

1. Open the Dashboard at `http://localhost:5016`.
2. Go to **Settings > Libraries**.
3. For catalogued media, choose an area, presentation, and metadata policy, then add stable sources and an explicit primary destination.
4. Mark each source **Managed by Tuvima** or **Existing library**, then confirm path checks. Existing sources require only read access and can never be modified.
5. Save the settings.
6. For a catalogued source only, start the administrator import scan.

Open **Settings > Ingestion** to monitor progress.

**Being Added Now** and the full media list use responsive cover cards that fill
the available row width. Each card shows its available cover immediately, with
a placeholder while artwork is unavailable. Long titles stay within the card;
hover over a title or contributor to read the full text.

## Supported Formats

| Lane | Media | Formats |
|---|---|---|
| Read | Books | EPUB, PDF |
| Read | Comics | CBZ, CBR |
| Watch | Movies | MKV, MP4, M4V, WEBM, AVI |
| Watch | TV | MKV, MP4, M4V, WEBM, AVI |
| Listen | Music | FLAC, MP3, AAC, M4A, OGG, WAV |
| Listen | Audiobooks | M4B, MP3, M4A |
| View | Images | JPG/JPEG, PNG, WebP, GIF, BMP, TIFF, HEIC/HEIF, AVIF, and supported RAW companions |
| View | Mixed local media | Short video, PDF/Office/text documents, and common audio formats |

For images, home videos, documents, audio notes, lectures, and content that
should never be sent through external matching, enable the profile's View
Personal Space. **Import folder** copies originals into managed storage;
**Link existing folder** indexes an external folder read-only. Multiple sources
do not become multiple browsing destinations. These items bypass catalogue
identity and Review Queue.

MP3, M4A, MP4, MKV, AVI, and WEBM can be ambiguous in catalogued intake. Tuvima
uses folder context, embedded metadata, filename patterns, and classification
logic to decide whether a file is music, audiobook, movie, or TV. If it cannot
decide safely, the catalogued item goes to Review Queue. View keeps the local
asset usable without sending it into that identity workflow.

## What Happens When A File Arrives

The following provider/enrichment stages describe catalogued intake:

1. **Settle:** wait for file activity to stop.
2. **Fingerprint:** compute a stable identity for duplicate detection.
3. **Scan:** read embedded metadata and artwork.
4. **Classify:** resolve media type where needed.
5. **Stage:** register the file safely before promotion.
6. **Stage 3 Retail Match:** call active retail providers for metadata, primary cover/poster evidence, ratings, and bridge IDs.
7. **Stage 4 Wikidata:** use bridge IDs for canonical identity when possible.
8. **Stage 5 Ready:** store core canonical values and managed artwork under `.data/assets`.
9. **Stages 6-8 Enrichment:** expand people, universe relationships, lyrics/subtitles, and deeper artwork.
10. **Settle artwork:** decide whether rich artwork is present, missing, or still pending.
11. **Surface:** show the item only where it is ready and backed by real data.

View stops after the deterministic local steps needed to make the asset usable:
settle, hash, extract available file/capture metadata, retain every source path,
group compound files, and update timeline/search state. It never calls retail
providers or Wikidata. Favorite, Hidden, Archive, Trash, Restore, and Gallery
actions change database organization only and do not modify originals.

## When Items Become Visible

Home, Read, Watch, Listen, Collections, and Search show items that pass the browse readiness gate:

- non-placeholder title
- resolved media type
- settled artwork outcome

Items that do not pass stay visible in operational surfaces such as Ingestion, Activity, or Review Queue.

## Tips For Better Matches

- Keep embedded metadata when possible.
- Include identifiers such as ISBN, ASIN, TMDB ID, MusicBrainz ID, or Comic Vine ID when your tools support them.
- Put mixed file types in folders with clear intent.
- Use provider credentials for services that require them.
- Start with small batches and resolve review items before importing thousands of files.

## Related

- [Your First Library](../tutorials/first-library.md)
- [Supported Media Types and Formats](../reference/media-types.md)
- [How File Ingestion Works](../explanation/how-ingestion-works.md)
- [Troubleshooting](troubleshooting.md)
