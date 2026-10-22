# Spec: Batched write-back (all files, on a schedule, with ExifTool for photos, videos and PDFs)

> **Status:** Draft · **Version:** 1 · **Last updated:** 2026-10-22 · **Related docs:** [Product overview](../product/overview.md) · [Simpler library setup](library-setup-groups.md) (D48, D55) · [Privacy and local-first](../../docs/explanation/privacy-local-first.md)

> Written 2026-10-22 from Shy's decisions while planning D48 (the profile name written into View files): use ExifTool for photos, videos and PDFs, download it from a copy Tuvima keeps on GitHub, and batch every write into files, not only View's, "so it's only built one way", running every hour by default and configurable. Changes are saved in Library's data store straight away; files catch up on the schedule.

## Plain-English product walkthrough

**Today.**
- When Library learns something about a file (a match, an edit, new artwork), it writes the tags into that file straight away, one file at a time.
- Only library moves and editor saves wait on a to-do list, and that list is worked through every couple of seconds.
- View never writes into files.

**After this spec.**
1. Every change is saved in Library instantly, as today. What you see in Library is always up to date.
2. Each file that needs its tags written gets one line on a to-do list kept in the data store. A later change to the same file replaces the earlier one, so only the final version is ever written.
3. Once an hour (the default, changeable in Settings), Library works through the whole list in one pass. For photos, phone videos and PDFs it starts ExifTool once and gives it every file on the list.
4. A file that is busy, on an unplugged drive or now read-only stays on the list for the next run. A file that can't hold tags is marked "kept in Library only" and isn't retried.
5. Settings > Libraries > Advanced shows how many files are waiting, when the last run was, when the next one is, how many failed, and a **Write now** button for administrators.
6. Restarting the Engine loses nothing; the list carries on at the next run.

**Representative journeys.**
- *Fixing an album.* You correct an album title. Library shows the new title at once, and the music files carry it within the hour.
- *A shared photo.* Profile B adds holiday photos to the shared gallery. The photos are filterable by "B" straight away, and within the hour each Managed photo file carries B's name, including iPhone HEIC files.
- *A busy day of edits.* You re-tag 500 photos and then change your mind. Each photo is written once, with the final tags.

**What stays the same.**
- Read-only folders, As-is folders and original source media are never written to.
- What is written into music, book, comic and video files is unchanged: the same fields, the same backup setting, and the same switches that turn write-back on or off.
- Filing and renaming during ingestion is unchanged.

**Acceptance (mapped to packets).**
- A1 (BW1): no change writes into a file straight away; every write waits for the scheduled run or Write now.
- A2 (BW1): five edits to one file produce one write.
- A3 (BW1): the run interval comes from configuration, 60 minutes by default, and the next run time follows it.
- A4 (BW2): ExifTool is fetched from Tuvima's GitHub copy, its checksum is checked, and one ExifTool run handles a whole batch.
- A5 (BW3): View files get the profile name per D7, and files that can't hold it are counted as "kept in Library only".
- A6 (BW4): the Settings panel shows the waiting count, the last and next run, failures and Write now.

## 1. Goal

Every write into a media file happens one way: changes save in Library at once, and files are updated in a single, efficient batch on a schedule. Editing stays instant, disks and cloud-synced folders see far fewer changes, and Library can write the profile name into photos, phone videos and PDFs through ExifTool.

## 2. Scope

**In scope**
- One durable to-do list for every file write, covering both Library media files and View files, worked through on a schedule.
- Every existing write-back trigger moves onto it: automatic matches, Quick Hydration, Wikidata enrichment, manual edits, editor saves, item preferences, retagging after a settings change, the folder organisers' writes, embedded artwork and library moves.
- The schedule setting, its Settings field, the status panel and Write now.
- ExifTool as a helper program, downloaded like FFmpeg and Tesseract, plus the small task that keeps Tuvima's GitHub copy.
- Writing the profile name into View files (D48 of the library setup spec, which moves here from its WP8c).

**Out of scope**
- New fields written into Watch, Listen or Read files.
- Replacing TagLibSharp for music, audiobooks or MKV.
- Email files.
- Reading text from documents (the library setup spec's WP8d).

## 3. Decisions made

- **D1. One to-do list for every write into a file** (Shy, 2026-10-22: "batching for everything so it's only built one way"). The existing durable file-write list grows to cover every trigger, and View files as well as Library media files. Each file has at most one waiting entry, and a newer change replaces it, so only the final version is written.
- **D2. Runs every hour by default; configurable** (Shy, 2026-10-22). The interval lives in `config/writeback.json` and is changed from Settings > Libraries > Advanced. A run starts when the interval has passed and something is waiting; when the list is empty nothing runs. If the Engine was off when a run was due, the run happens shortly after it starts.
- **D3. Write now.** Administrators can start a run at once from the Settings panel. Nothing else skips the schedule.
- **D4. The data store is the true record.** Library shows every change straight away. A file copied out of Library before the next run may not carry the latest tags yet; that is the accepted cost of batching.
- **D5. ExifTool runs as a helper program, from Tuvima's own GitHub copy** (Shy, 2026-10-22). It is fetched and checked like FFmpeg and Tesseract: the address must be on GitHub and the file must match its checksum. The Windows build is a copy of the official release that Tuvima keeps as a release on its own GitHub, refreshed by a small task run by hand when ExifTool updates. Linux and Docker use the official source on ExifTool's GitHub with Perl installed. It runs on the person's own computer only; nothing is uploaded. Licence: the same terms as Perl (Artistic or GPL). Credit (Shy, 2026-10-22: "always give clear credit in our readme"): "ExifTool by Phil Harvey" with a link in the README's "Built on open-source software" table, a row in the attributions page, and its copyright line and licence in the third-party notices. The downloaded copy keeps ExifTool's own licence and readme files beside it, and Tuvima's GitHub copy is the unmodified official release.
- **D6. One ExifTool start per run.** ExifTool stays open for the whole batch and takes file after file, so thousands of photos cost one program start. If ExifTool stops part-way, the remaining files stay on the list for the next run, and the Engine is not affected.
- **D7. Which View files get the profile name, and with which tool** (replaces the tool choices in the library setup spec's D55).
  - **Written by ExifTool:** JPEG, PNG, TIFF, GIF, WebP, HEIC/HEIF, AVIF and camera RAW (DNG, CR2, CR3, NEF, ARW) photos, MP4, MOV and M4V videos, and PDFs.
  - **Written by Open XML:** Word, Excel and PowerPoint (.docx, .xlsx, .pptx).
  - **Kept in Library only:** MKV, WebM and AVI videos, old Office files, OpenDocument, text formats, signed or encrypted PDFs, and encrypted Office files.
  - The field is Library's own; Author and other existing fields are never touched. PDFsharp is no longer needed. TagLibSharp stays for music, audiobooks and MKV.
- **D8. Library recognises its own writes.** After writing a file, Library records the file's new fingerprint and size, so the file is not treated as new or changed.
- **D9. Safety rules are unchanged and apply to every write.** Library writes only where the folder's handling allows it. A View write goes to a temporary copy, is re-read, and only then replaces the original. On any failure the file is left exactly as it was and the entry is marked failed with the reason; failed entries are retried up to the existing attempt limit.

## 4. UI behaviour

**Settings > Libraries > Advanced** (administrators, desktop), in the existing write-back section:

| Situation | What the person sees |
|---|---|
| Normal | "Write changes into files every [60] minutes". Under it: "12 files waiting · Last run 10:00 · Next run 11:00" and a **Write now** button |
| Nothing waiting | "All files are up to date · Last run 10:00" with Write now disabled |
| Running | "Writing 240 of 1,200 files…" with Write now disabled |
| Some failed | "3 files couldn't be written" with a link to the list of files and reasons |
| ExifTool missing | "Photos, videos and PDFs in View are waiting for ExifTool" and a link to Settings > Tools |
| Write-back turned off | The existing switch is off: the interval and Write now are hidden, and nothing is queued |
| Engine unreachable | The usual Settings error state |

**Settings > Tools** lists ExifTool beside FFmpeg and Tesseract, with its version, status and the usual download and repair buttons.

**View folder rows** show "N files kept in Library only" when some files can't hold the profile name (from the library setup spec, D55).

Phones see the status text only, with no editing or buttons, as with other Settings on phones.

## 5. Data / model changes

- The existing file-write list (one entry per media file) also holds View files and records the kind of write, its outcome and when it was done. Pre-release: the list may be reset, and no data is migrated.
- `config/writeback.json` gains the run interval (minutes, default 60) and the time of the last run is kept with the list.
- `config/tool_downloads.json` gains ExifTool with pinned versions and checksums.

## 6. Work packets

### BW1 — One scheduled to-do list for every write
- **What it does:** D1 to D4, D8, D9.
  - Every write-back caller adds to the durable list instead of writing straight away: enrichment, manual overrides, item preferences and canonical apply, the retag sweep, the folder organisers, artwork embedding and library moves.
  - The list's worker runs batches on the configured interval (and on Write now), claims entries with the existing lease and attempt rules, and keeps one entry per file with the newest generation.
  - The list's shape grows so it can also point at a View item.
  - Adds Engine actions for status and Write now, with their Contracts.
- **Files likely touched:** `src/MediaEngine.Ingestion/Services/WriteBackService.cs`, `src/MediaEngine.Domain/Contracts/IWriteBackService.cs`, `src/MediaEngine.Api/Services/MediaFileWriteIntentDispatcher.cs`, `src/MediaEngine.Storage/MediaFileWriteIntentRepository.cs`, a new upgrade step, `EnrichmentService.cs`, `RetagSweepWorker.cs`, `ItemCanonicalEndpoints.cs`, `MetadataEndpoints.cs`, `FolderConversionOrganiser.cs`, `FolderMergeOrganiser.cs`, `ArtworkWritebackService.cs`, `config/writeback.json`, `WriteBackConfiguration.cs`, `src/MediaEngine.Contracts/`, tests (to be confirmed by a scout)
- **Depends on:** none
- **Size:** Complex; gets its own review (it changes how every file is written)
- **Done when:** tests show:
  - A manual edit, an enrichment and an artwork change each add one entry and write nothing.
  - Five changes to one file give one entry and one write.
  - A run starts only after the interval, or on Write now, and writes every waiting file.
  - An entry interrupted by an Engine stop is written on the next run.
  - A file Library just wrote is not picked up as changed.

### BW2 — ExifTool helper program
- **What it does:** D5, D6.
  - ExifTool entries in `config/tool_downloads.json`: Windows, from Tuvima's GitHub copy; Linux, from ExifTool's GitHub source.
  - Fetch, check and repair, reusing the FFmpeg and Tesseract code; shown in Settings > Tools.
  - Perl added to the Docker image.
  - A batch runner that keeps one ExifTool open for a list of files, writes a given field and reads back a per-file result.
  - A hand-run GitHub task that copies a pinned official ExifTool release into a Tuvima release with its checksum.
  - Licence notices.
- **Files likely touched:** `config/tool_downloads.json`, the tool download services, `tools/` (a download script like `Download-FFmpeg.ps1`), `.github/workflows/` (the copy task), `Dockerfile`, `README.md` (credits table), `docs/reference/attributions.md`, `THIRD-PARTY-NOTICES.md`, Settings > Tools components, tests
- **Depends on:** none (runs beside BW1; different files)
- **Size:** Standard
- **Done when:** tests show:
  - A download with a wrong checksum is refused.
  - A batch of 50 generated JPEGs, PNGs and MP4s is written in one ExifTool start, and each file still opens with its picture or video data unchanged.
  - A corrupt file in the batch fails alone and the rest succeed.
  - ExifTool is credited in the README, the attributions page and the third-party notices, and its licence files sit beside the downloaded copy.

  Tests that need the real ExifTool are skipped when it isn't installed.

### BW3 — The profile name in View files
- **What it does:** D7. A View writer for the to-do list:
  - Sends ExifTool formats to BW2's runner and Office files to Open XML.
  - Marks the rest "kept in Library only" with the folder's count.
  - Queues an entry whenever an item's "added by" profile changes, including moves into the shared library.
  - Follows D9's temporary-copy rule.
- **Files likely touched:** `src/MediaEngine.Api/Services/LocalAssets/` (new writer), `ViewSourceMutationPolicyFactory`, the View indexing and shared-transfer services, tests
- **Depends on:** BW1, BW2, and the library setup spec's WP8a
- **Size:** Complex; gets its own review (it changes files on disk)
- **Done when:** tests show:
  - Each D7 written format carries the field and still opens, with its pixels or video data unchanged.
  - A signed PDF, an encrypted .docx, an MKV and any file in a Read-only folder are unchanged, byte for byte.
  - A write that fails part-way leaves the original unchanged.

### BW4 — Settings: schedule, status and Write now
- **What it does:** section 4's panel in Settings > Libraries > Advanced, ExifTool in Settings > Tools, and the View folder count.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/LibraryAdvancedSettings.razor`, the Tools tab, the Engine API client, tests
- **Depends on:** BW1 (and BW2 for the Tools row)
- **Size:** Standard
- **Done when:** bUnit tests cover each row of the section 4 table, and the interval saves and reloads.

### BW5 — Docs
- **What it does:**
  - The public write-back page explains the schedule and Write now.
  - The privacy page names ExifTool as a local helper program.
  - The tools page covers ExifTool.
- **Files likely touched:** `docs/`
- **Depends on:** BW1 to BW4
- **Size:** Mechanical
- **Done when:** the docs build passes and no page says tags are written straight away.

Waves:
1. BW1 and BW2 side by side.
2. BW3 and BW4.
3. BW5.

## 7. Local checks

- **L1.** Correct an album title and an EPUB title on the real library copy: Library shows them at once. After Write now (or within the hour), the files carry them and are not re-ingested.
- **L2.** On SHY-DESKTOP: Settings > Tools downloads ExifTool from Tuvima's GitHub copy, and it shows as ready.
- **L3.** In a Managed View folder: an iPhone HEIC, a JPEG, a phone MP4 and a PDF get the profile name. Each opens normally in Windows Photos, Films & TV and Acrobat or Edge, and looks unchanged.
- **L4.** Make ten edits, stop the Engine before the run, start it again: the files are written shortly after start.
- **L5.** A View folder inside OneDrive: a day of edits causes one upload per changed file, not one per edit.

## 8. Risks and open questions

- **Risks:**
  - Files lag up to an hour behind Library (accepted, D4).
  - A large first run after a new library is added could take a while. Runs are batched and resumable, and the status panel shows progress.
  - ExifTool is mostly one maintainer's work, with a long, steady record; a missing ExifTool only delays View writes.
  - The Docker image grows by Perl's size.
- **Open questions:**
  - **Q1. Is ExifTool fetched automatically (like FFmpeg) or by a button (like Tesseract)?**
    - Automatic means the profile name just works.
    - The button means nothing is downloaded until someone asks.
    - Recommendation: automatic.
  - **Q2. Do library moves also wait for the batch?**
    - Waiting means moves are built the same way as everything else, but a moved movie stays in its old folder on disk for up to an hour.
    - Moving straight away, with only the tags waiting, keeps folders on disk matching Library.
    - Recommendation: move the file straight away and batch only the tags. Moving is filing, not writing into the file.
