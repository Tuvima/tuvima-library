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

Seven media types are supported today. Each type has a processor path, supported file extensions, and configured providers. Ambiguous formats such as PDF, MP3, M4A, MP4, MKV, AVI, and WEBM are resolved through configured library folder context, metadata, filename patterns, heuristics, and Local AI where available.

Provider stages are strict: Stage 1 provider metadata uses active configured providers (MusicBrainz then Apple for music, Apple for books/audiobooks, TMDB for movies/TV, Comic Vine for comics); Stage 2 Wikidata only runs from safe Stage 1 bridge IDs; Stage 3 enrichment adds universe data, TMDB movie/TV artwork, lyrics, subtitles, people, and relationships.

---

## Videos (home videos, clips and courses)

**Library type:** Videos, in Watch. It is for files that are not films or TV shows: phone and camera footage, downloaded
YouTube videos, lectures and courses. When you add a folder, Tuvima asks whether it is **Videos (Watch)** or **Photos and
videos (View)**; a folder of untouched phone footage is suggested for View.

### Supported formats

The same video extensions as Movies and TV (for example `.mp4`, `.mkv`, `.mov`, `.avi`, `.webm`).

### How a Videos folder is read

| Folder shape | Result |
|---|---|
| A loose file | A standalone video |
| A folder of files | A series; the files are its videos |
| Sub-folders inside a series folder | Sections of that series (a course module, a season) |

Order inside a series follows a number in the file name, otherwise natural sort (2 before 10). Reordering by hand is
saved in Tuvima and does not rename files.

### How a video is identified

No outside lookup is needed. In order (set in `config/videos.json`): a YouTube ID in the file name (`[dQw4w9WgXcQ]`) or
the video's `.info.json`, a Kodi `.nfo`, then the cleaned-up file name. The item records which method named it.
Other files beside the video are read too, all optional: `.info.json` (yt-dlp), `.nfo`, a `cover.jpg`/`folder.jpg`/`poster.jpg`
in the folder, and `.srt`/`.vtt` subtitles. Your own edits always win over these.

### Covers

Your own image, then a folder image, then the YouTube thumbnail (if the lookup is on), then a good still frame taken from
the video (never a black one).

### YouTube lookup and privacy

Optional and on by default for a Videos library, and only for files where a YouTube ID is found. It contacts YouTube and
sends **only that 11-character ID**, never your file or its path. Without a key it fills the title, channel and thumbnail;
with your own free YouTube Data API key (kept in the secrets store, never shown again) it also fills the description
and upload date. It fills what is missing and never replaces something you edited. Turn it off per library in
**Settings > Libraries**.

### Ratings and the Explicit mark

Saved videos have no age ratings. Profiles with a content limit (G to R) do not see Videos until an administrator turns on
**OK for limited profiles** for the library; profiles set to Everything always see them. In Editing Mode, a video or a series
can be marked **Explicit**: it is then hidden from every profile with a content limit, whatever the library setting says.

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

Different ebook formats of the same book in the same folder form one Work with one Edition per format, when they share the same title and author or the same Calibre UUID.

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

The shipped template writes the season-and-episode part as `{EpisodeCode?}` (`s01e01`), which is empty unless both numbers are known, and drops the dash after it when empty. An episode with no number (one added to a season anyway, without being matched) is therefore named by its title alone, `Show - Pilot.mkv`, inside its season folder, never `sUnknowneUnknown`.

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

The shipped template writes the number as `{TrackNumber?}` and the dash after an optional token goes with it when it is empty, so a song with no track number (added to an album anyway) is named by its title alone, `Hidden Jam.flac`, inside the album folder, not `Unknown - Hidden Jam.flac`. Audiobook parts already do the same with `{TrackNumber?}`.

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

## View documents (text search)

A file counts as a View document when its extension is `.pdf`, `.doc`, `.docx`, `.xls`, `.xlsx`, `.ppt`, `.pptx`, `.odt`, `.ods`, `.odp`, `.rtf`, `.txt`, `.md`, `.csv`, `.json` or `.xml`. All of them can be listed, filed and downloaded. Their words are read for `.pdf`, `.docx`, `.xlsx`, `.pptx`, `.odt`, `.rtf` and the plain text types; the older `.doc`, `.xls` and `.ppt` formats and the OpenDocument `.ods` and `.odp` formats are listed but not read. The Documents page shows a PDF in a bundled PDF.js viewer and every other format as its extracted words with a Download original action; a PDF's thumbnail is its first page, drawn once in the background.

Documents in a personal View library are searchable by their own text. Plain text files (`.txt`, `.md`, `.csv`, `.json`, `.xml`) are read during the scan (first 256K characters). PDFs (their own text layer), Word `.docx`, Excel `.xlsx`, PowerPoint `.pptx`, OpenDocument `.odt` and `.rtf` files are read by a throttled background job (`view.document_text_read`) after the scan, so folder scans stay fast. Text is capped per document, PDFs are capped by page count and by read time, and password-protected or corrupt files are marked as having no text. Original files are never changed, and document text never appears in Home, global Search or Recommendations. Limits are set under `personal_library_policy` in `config/libraries.json`.

Scanned PDF pages and photos of paper (`.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff`, `.webp`, `.bmp`, `.heic`, `.heif`) are read with text recognition by a second throttled job (`view.document_ocr`). For each PDF page whose own text layer holds fewer than 25 non-whitespace characters, the page is drawn at 300 DPI (longest side capped at 3500 px) and recognised; pages that already have a text layer are never re-read. `text_source` becomes `embedded`, `ocr` or `mixed`. The job waits while anyone is playing media, runs one file at a time, and keeps the previous text if a re-read fails. Originals are never changed and no searchable-PDF copy is written. Settings are in `config/ocr.json`; see [How documents are read](../explanation/how-documents-are-read.md).

---

## Folder artwork

For Books, Audiobooks, and Music, a file with no embedded cover uses a sibling `cover.jpg`, `cover.jpeg`, `cover.png`, `folder.jpg`, `folder.jpeg`, `folder.png`, `front.jpg`, or `front.png` in the same folder. The image must be JPEG or PNG between 1 KB and 25 MB. Tuvima reads it without changing it, embedded art wins when present, and folder art is not used for Comics, Movies, or TV.

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
