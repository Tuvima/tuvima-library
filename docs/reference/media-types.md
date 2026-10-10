---
title: "Supported Media Types and Formats"
description: "See which media types, file formats, processors, and enrichment paths Tuvima Library currently supports."
audience: administrator
category: "reference"
product_area: "media"
tags:
  - "formats"
  - "media-types"
  - "processors"
status: current
---

# Supported Media Types and Formats

## In this page

See which media types, file formats, processors, and enrichment paths Tuvima Library currently supports.

## Where this lives in the code

- `config/media_types.json`
- `src/MediaEngine.Processors/Processors`
- `src/MediaEngine.Ingestion/DependencyInjection/MediaEngineIngestionServiceCollectionExtensions.cs`

Six media types are supported today. Each type has a processor path, supported file extensions, and configured providers. Ambiguous formats such as PDF, MP3, M4A, MP4, MKV, AVI, and WEBM are resolved through configured library folder context, metadata, filename patterns, heuristics, and Local AI where available.

Provider stages are strict: Stage 1 provider metadata uses active configured providers (MusicBrainz then Apple for music, Apple for books/audiobooks, TMDB for movies/TV, Comic Vine for comics); Stage 2 Wikidata only runs from safe Stage 1 bridge IDs; Stage 3 enrichment adds universe data, TMDB movie/TV artwork, lyrics, subtitles, people, and relationships.

---

## Books (EPUB, PDF)

**Processor:** `EpubProcessor` - priority 100

### Supported formats

| Extension | Format |
|---|---|
| `.epub` | Electronic Publication (EPUB 2 and EPUB 3) |
| `.pdf` | Portable Document Format |

### Extracted metadata

| Field | Confidence | Notes |
|---|---|---|
| `title` | 0.95 | From OPF `<dc:title>` |
| `author` | 0.95 | From OPF `<dc:creator>` |
| `publisher` | 0.95 | From OPF `<dc:publisher>` |
| `language` | 0.95 | From OPF `<dc:language>` (BCP-47) |
| `description` | 0.95 | From OPF `<dc:description>` |
| `date` | 0.95 | From OPF `<dc:date>` |
| `isbn` | 1.0 | From OPF `<dc:identifier>` where scheme = ISBN |
| `word_count` | 1.0 | Computed from content documents |
| `cover_image` | - | Extracted from EPUB manifest cover item |

### Providers

Provider order during Stage 1:

1. Apple API - ISBN lookup and title search, cover art and ratings
2. Wikidata - Stage 2, QID resolution via ISBN or Apple bridge IDs

### Ambiguity resolution

EPUB is strong book evidence. PDF defaults to Books unless the file is inside a single-type Comics library folder, where the folder/media hint wins and the item is routed as Comics.

### Organization template

```
Books/{Title} ({Qid})/{Title}.epub
```

---

## Audiobooks (M4B, MP3)

**Processor:** `AudioProcessor` - priority 95

### Supported formats

| Extension | Format | Ambiguity |
|---|---|---|
| `.m4b` | MPEG-4 Audio Book | Unambiguous - always audiobook |
| `.mp3` | MPEG Audio Layer 3 | Ambiguous - may be music or audiobook |

### Extracted metadata

| Field | Confidence | Notes |
|---|---|---|
| `title` | 0.90 | From ID3 `TIT2` or M4B `(c)nam` tag |
| `author` | 0.90 | From ID3 `TPE1` or M4B `(c)ART` tag |
| `narrator` | 0.85 | From ID3 `TPE3` or M4B `(c)wrt` tag |
| `album` | 0.90 | Series name often embedded here |
| `year` | 0.90 | From ID3 `TDRC` |
| `genre` | 0.80 | From ID3 `TCON` |
| `track_number` | 0.95 | From ID3 `TRCK` - used for multi-part audiobooks |
| `duration_sec` | 1.0 | Computed from audio stream |
| `container` | 1.0 | `m4b` or `mp3` |
| `audio_bitrate` | 1.0 | From audio stream properties |
| `asin` | 1.0 | From ID3 custom tag `TXXX:ASIN` - used as retail bridge ID |
| `cover_image` | - | From embedded ID3 `APIC` frame |

### Ambiguity resolution for MP3

Classification order for MP3 files:

1. `TXXX:ASIN` tag present -> audiobook
2. Genre tag contains "Audiobook" or "Spoken Word" -> classified as Audiobook
3. `TPE3` (narrator) tag present -> audiobook
4. Duration > 20 minutes -> audiobook candidate
5. AI `MediaTypeAdvisor` - final classification using all available signals

### Providers

1. Apple API - ISBN and title lookup for books/audiobooks; cover art, author/narrator, and series data
2. Wikidata - Stage 2, QID resolution

### Organization template

```
Audiobooks/{Title} ({Qid})/{Title}.m4b
```

---

## Movies

**Processor:** `VideoProcessor` - priority 90

### Supported formats

| Extension | Format | Ambiguity |
|---|---|---|
| `.mkv` | Matroska Video | Ambiguous - may be movie or TV |
| `.mp4` | MPEG-4 Video | Ambiguous - may be movie, TV, or other |
| `.m4v` | iTunes Video | Ambiguous |
| `.webm` | WebM Video | Ambiguous |
| `.avi` | Audio Video Interleave | Ambiguous |

### Extracted metadata

| Field | Confidence | Notes |
|---|---|---|
| `title` | 0.75 | From filename parsing (lower confidence - filenames vary widely) |
| `container` | 1.0 | `mkv`, `mp4`, etc. |
| `video_width` | 1.0 | From video stream |
| `video_height` | 1.0 | From video stream |
| `duration_sec` | 1.0 | From container |
| `video_codec` | 1.0 | e.g., `H.264`, `HEVC`, `AV1` |
| `frame_rate` | 1.0 | From video stream |

### Ambiguity resolution for video files

Classification order:

1. Filename matches `SxxExx` or `1x01` season/episode pattern -> TV
2. Filename contains year in parentheses `(2024)` and no episode markers -> Movie candidate
3. AI `MediaTypeAdvisor` with filename and duration as inputs
4. Promotion held at `NeedsReview` if classification confidence < threshold

### Providers

1. TMDB - title+year search; cover art (poster), description, ratings, cast, TMDB ID bridge
2. Wikidata - Stage 2, QID resolution via TMDB ID bridge

TMDB movie details also supply `belongs_to_collection`. Tuvima stores that as a provider-backed movie-series shelf key, so Watch can show film-series shelves before a Wikidata `series_qid` is available.

### Organization template

```
Movies/{Title} ({Qid})/{Title}.mkv
```

---

## TV Shows

**Processor:** `VideoProcessor` - priority 90

### Supported formats

Same as Movies. Classified as TV via filename pattern matching.

### Extracted metadata

| Field | Confidence | Notes |
|---|---|---|
| `series_title` | 0.70 | Parsed from filename before season/episode marker |
| `season_number` | 0.95 | Parsed from `SxxExx`, `Season xx`, or `xx x xx` patterns |
| `episode_number` | 0.95 | Parsed from filename |
| `episode_end` | 0.55 | Last episode of a multi-episode file (`S01E01E02`, `S01E01-E02`, `S01E01-02`, `1x01-02`). Only unbroken runs of up to 6 episodes; emitted only when the file covers more than one episode |
| `episode_title` | 0.70 | Parsed from filename after episode marker (when present) |
| `container` | 1.0 | |
| `video_width`, `video_height` | 1.0 | |
| `duration_sec` | 1.0 | |
| `video_codec` | 1.0 | |
| `frame_rate` | 1.0 | |

### Filesystem conventions

TV follows the Plex naming convention for maximum compatibility:

```
TV/{Series} ({Qid})/Season {Season}/S{Season}E{Episode} - {Title}.mkv
```

Example:
```
TV/Breaking Bad (Q1079331)/Season 01/S01E01 - Pilot.mkv
```

A file that covers several episodes (`S01E01E02`, `S01E01-E02`, `1x01-02`) uses the `{Episode}` token as `01-e02`. With the template above it is named `S01E01-e02 - Pilot.mkv`; the S and E letters follow whatever template you use. Single-episode files are named exactly as before. The file is never split or re-encoded, and the episodes it covers are recorded separately (see `media_asset_coverage`).

### Providers

1. TMDB - series and episode metadata; poster and backdrop images
2. Wikidata - Stage 2, QID resolution

TV is treated as show-first. The show identity comes from folder/path hints, parsed show/season/episode values, and TMDB/TVDB IDs; episode title remains episode metadata. A show shelf can exist with a provider key before Wikidata relationships are available.

---

## Music

**Processor:** `AudioProcessor` - priority 95

### Supported formats

| Extension | Format | Ambiguity |
|---|---|---|
| `.flac` | Free Lossless Audio Codec | Unambiguous - always music |
| `.ogg` | Ogg Vorbis | Unambiguous - always music |
| `.wav` | Waveform Audio | Unambiguous - always music |
| `.mp3` | MPEG Audio Layer 3 | Ambiguous - see audiobooks |
| `.m4a` | MPEG-4 Audio | Ambiguous - may be music or audiobook |

### Extracted metadata

| Field | Confidence | Notes |
|---|---|---|
| `title` | 0.95 | From ID3 `TIT2` or equivalent |
| `artist` | 0.95 | From ID3 `TPE1` |
| `album_artist` | 0.95 | From ID3 `TPE2` |
| `album` | 0.95 | From ID3 `TALB` |
| `year` | 0.90 | From ID3 `TDRC` |
| `track_number` | 0.95 | From ID3 `TRCK` |
| `disc_number` | 0.95 | From ID3 `TPOS` |
| `genre` | 0.85 | From ID3 `TCON` |
| `duration_sec` | 1.0 | |
| `cover_image` | - | From embedded `APIC` frame |
| `musicbrainz_recording_id` | 1.0 | From `TXXX:MusicBrainz Track Id` - used as bridge ID |
| `musicbrainz_album_id` | 1.0 | From `TXXX:MusicBrainz Album Id` |

### Ambiguity resolution for MP3/M4A

Classification order:

1. MusicBrainz tags present -> music
2. `track_number` present and duration < 15 minutes -> music candidate
3. Genre tag is not "Audiobook" or "Spoken Word" -> music candidate
4. AI `MediaTypeAdvisor` - final arbiter

### Providers

1. MusicBrainz - Stage 1 configured identifier lookup and staged search for recording, release, release-group, artist, and ISRC bridge evidence
2. Apple API - Stage 1 enrichment or fallback identity; an accepted fallback supplies configured normalized hints to one bounded MusicBrainz reconciliation attempt
3. Wikidata - Stage 2, QID resolution only after safe Stage 1 bridge evidence, using corroborated MusicBrainz IDs first and Apple IDs as valid fallback hints

### Organization template

Follows Plex Music naming convention:

```
Music/{Artist}/{Album} ({Qid})/{TrackNumber} - {Title}.flac
```

Example:
```
Music/Pink Floyd/The Dark Side of the Moon (Q183221)/01 - Speak to Me.flac
```

---

## Comics

**Processor:** `ComicProcessor` - priority 85

### Supported formats

| Extension | Format |
|---|---|
| `.cbz` | Comic Book Archive (ZIP) |
| `.cbr` | Comic Book Archive (RAR) |
| `.pdf` | Portable Document Format comic or graphic novel when routed from a Comics library folder |

### Extracted metadata

**From archive structure:**

| Field | Confidence | Notes |
|---|---|---|
| `page_count` | 1.0 | Count of image files in archive |

**From ComicInfo.xml (when present):**

| Field | Confidence | Notes |
|---|---|---|
| `title` | 0.95 | `<Title>` element |
| `series` | 0.90 | `<Series>` element |
| `issue_number` | 0.95 | `<Number>` element |
| `volume` | 0.90 | `<Volume>` element |
| `summary` | 0.90 | `<Summary>` element |
| `year` | 0.90 | `<Year>` element |
| `publisher` | 0.90 | `<Publisher>` element |
| `writer` | 0.90 | `<Writer>` element |
| `penciller` | 0.90 | `<Penciller>` element |
| `colorist` | 0.90 | `<Colorist>` element |
| `genre` | 0.85 | `<Genre>` element |
| `language_iso` | 0.95 | `<LanguageISO>` element |

ComicInfo.xml is the de facto standard metadata format for CBZ/CBR archives. Not all archives include it; files without it fall back to filename parsing.

### Providers

1. Comic Vine - issue search by series+number; publisher, creator, and story arc data
2. Wikidata - Stage 2, QID resolution for notable series

### Organization template

```
Comics/{Title} ({Qid})/{Title}.cbz
```

---

## Ambiguous Format Summary

| Extension | Default Classification | Resolution Method |
|---|---|---|
| `.pdf` | Book unless in a Comics library | Single-type library folder context can route to Comics |
| `.mp3` | Uncertain | Heuristics -> AI `MediaTypeAdvisor` |
| `.mp4` | Uncertain | Filename pattern -> AI `MediaTypeAdvisor` |
| `.m4a` | Uncertain | Heuristics -> AI `MediaTypeAdvisor` |
| `.m4v` | Movie | Filename pattern -> AI confirmation |
| `.mkv` | Movie or TV | Filename pattern -> AI confirmation |
| `.avi` | Movie or TV | Filename pattern -> AI confirmation |
| `.webm` | Movie or TV | Filename pattern -> AI confirmation |

Strong format extensions (`.epub`, `.m4b`, `.flac`, `.ogg`, `.wav`, `.cbz`, `.cbr`) are not changed by unrelated single-type folder hints. PDF and video containers remain media-aware because those formats are common across more than one lane.

## Related

- [How to Add Media to Your Library](../guides/adding-media.md)
- [How to Write a New File Format Processor](../guides/writing-a-processor.md)
- [How File Ingestion Works](../explanation/how-ingestion-works.md)
