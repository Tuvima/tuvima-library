# Spec: Photo and video filing (the Engine side of Photos and Personal videos)

> **Status:** Locked · **Version:** 2 · **Last updated:** 2026-10-28 (locked by Shy) · **Related docs:** [Product overview](../product/overview.md) · [Library and folder pages](library-pages.md) (D24, D25, D26) · [Simpler library setup](library-setup-groups.md) (D44, D47, D53) · [Batched write-back](batched-writeback.md) · [Safer deleting in View](safer-deleting-in-view.md) · [Vault sections and move](vault-sections-and-move.md) · [Backup and restore](backup-and-restore.md)

> Written 2026-10-28 after Shy chose 1A ("plan photo and video filing") from the follow-ups of the Library and folder pages build. v2 folds in Shy's answers and notes of 2026-10-28 17:33Z (1A, 5A kept; duplicates across the Shared Library; cleanup jobs; staging and rejects held outside the library folder; Tidy this folder). Open questions are in section 8.

## Plain-English product walkthrough

**Today.**
- Photos and Personal videos libraries have their screens (overview, Folders tab, File handling tab), but the Engine behind them only half does the job.
- A photo added from a browser is filed as `Year / 10 - Oct / Oct 28 - 14.22.31 IMG.jpg`. Its own file name is thrown away, and nothing checks whether you already have it.
- Files that arrive any other way (a folder you add, files copied by hand, Add files, a phone backup) are simply indexed where they lie. Nothing tidies them, nothing catches duplicates, and a file that can't be read is skipped without telling anyone.
- The "Needs review" count on a library links to Needs Review but does not narrow it to that library.

**After this spec.**
1. Your library folder holds **only your photos**. Nothing extra is created inside it, so an administrator can back up `E:\Photos` alone. Files on their way in wait in Library's own **Import area** (the existing Import folder, outside every library, with Photos and Personal videos sections), tracked in the data store, and are then filed by the date they were taken: `2026 / 2026-10-28 / IMG_4521.jpg`. The file keeps its own name; a short number is added only if two files clash.
2. The date comes from the photo or video itself (camera date, phone date, video creation time). If there is none, from a date in the file name (`IMG_20240315_142233`, `PXL_20240315_...`), and only then from the file's own date.
3. A RAW and its JPEG, a Live Photo and its short video, and sidecar files (`.xmp`) are filed together under the same name, so they never get separated.
4. An exact copy of something already in that space is **not filed twice**, and the same check runs when a photo is moved or contributed into the Shared Library. The summary says "3 already in your library". Your original stays wherever it was. A **duplicates report** lets an administrator find and clean up exact copies that got in some other way.
5. A file that can't be used is **refused with a reason**, in plain words ("damaged", "not a photo or video type Library reads", "empty", "too big for the space left"). It is moved to Library's **Rejected area** (outside the library folder, tracked, cleaned up automatically after a set time) and shown in the import summary and the library's **Activity** tab, so nothing disappears silently. Stray files that turn up in the Import or Rejected areas without a record are noted too.
6. The library's **Needs review** link now opens Needs Review already narrowed to that library (for every library, not just photos).
7. Linked (As-is) folders are never touched: they are read, never filed, and an unreadable file there is only listed in Activity.
8. **Tidy this folder** files the photos already in a Managed folder into the date layout, in place (renames within the same folder, so no big copy and no second drive). It can run now, which makes the library unavailable until it finishes (with a clear warning first), or on a schedule such as overnight.

**Representative journeys.**
- *Phone backup.* Your phone sends 400 photos overnight. They land in your own space, filed by the date you took them. Eleven were already there from last month's backup, so they are skipped. One is a half-copied file; it is refused and listed in Activity.
- *Copying by hand.* You drop a card full of holiday photos into the Import area's Photos section. Within about a minute they are filed by date into the library and show in the Timeline.
- *Clean-up.* An administrator opens the duplicates report, sees 30 exact copies in the Shared Library with where each lives, and keeps one of each; the others go to Trash, never straight to deletion.
- *Add files to the shared gallery.* An administrator adds 60 photos from the server. 4 are already in the Shared gallery, so the summary says so; the other 56 are copied and filed. The originals on the server are untouched.
- *A fault.* The destination drive is nearly full. Nothing starts, and the message says how much is needed and how much is free.

**What stays the same.**
- Original source media stay protected: Add files and linked folders copy or read, never move or change the originals.
- Each person's own space stays private; the Shared Library keeps who contributed each photo; Vault libraries keep their own rules.
- Show in Photos timeline, galleries, People, Places, Trash and Missing items work as they do now.
- Library backups still never include media (Library pages D26).

**Observable acceptance (mapped to packets).**
- A1 (PF2): a photo dropped in the Import folder is moved to `Year/Year-Month-Day/` by its camera date, with its own name.
- A2 (PF2): a file with no camera date but a date in its name is filed by that date; with neither, by file date.
- A3 (PF2): a RAW with its JPEG and XMP, and a Live Photo with its video, end up in the same folder with the same base name.
- A4 (PF3): the same photo added twice to one space is filed once; the summary counts the skipped one; a different person's space does not influence the answer.
- A5 (PF4): a truncated JPEG and a zero-byte file are refused with the right reason; nothing is moved; Activity lists them.
- A6 (PF5): the Needs review link on any library opens Needs Review showing only that library.
- A7 (PF1): no folder is ever created inside a library folder for importing; the Import and Rejected areas sit outside it and every file in them has a data-store record.
- A9 (PF3): moving a photo into the Shared Library that already holds an exact copy is refused with "Already in the Shared Library"; the duplicates report lists exact copies already present and removing one goes to Trash.
- A10 (PF4): a rejected file lands in the Rejected area with its reason; it leaves automatically after the retention time; a stray file placed there by hand is noted.
- A11 (PF8): Tidy this folder renames files in place, says before starting that the library will be unavailable, and can be scheduled; a stop mid-run resumes without losing or duplicating anything.
- A8 (PF6): every copy checks free space first and refuses cleanly.

## 1. Goal

Make Photos and Personal videos libraries finish the job their new screens promise: put new files where they belong by date, catch exact duplicates, and say plainly when a file can't be used, so a household that drops thousands of phone files in never ends up with a mess or silent gaps.

## 2. Scope

**In scope**
- The Import and Rejected areas (staging outside the library folders, tracked in the data store, auto-cleaned) and the filing step behind them.
- A **duplicates report and cleanup** for administrators, and the duplicate check on moves into the Shared Library.
- **Tidy this folder**: in-place filing of what is already in a Managed folder, now or on a schedule.
- Capture date: reading it, the fallbacks, and the filing layout and names (Library pages D26, applied to the default pattern in the File handling tab).
- Companion files moving together (RAW and JPEG, Live Photo, `.xmp`).
- Exact-duplicate detection by file contents, per space, honouring the existing "Keep one copy / Keep both" setting.
- Rejects: reasons, the import summary, Activity entries, nothing moved.
- Free-space check before every copy or move into a managed View folder (the shared check already exists; this wires it in everywhere).
- The Needs Review `?library=` filter (all libraries).
- Add files and Scan now on the View library pages (they have only Add folder today), because they feed the Import folder.
- One shared filing step that browser upload, phone backup, Add files, drag and drop, and files copied in by hand all use, replacing the browser-upload-only code path.
- Docs, tests and the Local checks.

**Out of scope**
- "Similar" photo detection (resized or edited copies), face recognition, AI tagging.
- Documents filing (the Documents kind keeps its own spec, and its rules are not the date layout).
- The Vault's picture pipeline and Move to Vault (owned by the Vault sections build); this spec only keeps its filing step reusable and safe for it.
- Changing what write-back puts into files (Batched write-back stays as is).
- Anything on the Dashboard beyond the summary text, Activity entries and the filter (screens were built by the Library pages work).

## 3. Decisions made

Carried over (already decided by Shy; the first block of D-numbers cites its source). New proposals are marked **Proposed**; nothing proposed is final until Shy answers section 8.

- **D1. Staging lives outside the library folder** (Shy, 2026-10-28; **replaces the "import subfolder inside the managed folder" part of Library pages D25**, which needs a note in a new Library pages version). A library folder such as `E:\Photos` holds only photos, so an administrator backs up that one folder. Arrivals wait in Library's existing Import area (a Photos and a Personal videos section, outside every library) and rejects in a Rejected area beside it. Both are tracked as rows in the data store (what, where it came from, state, reason, when), never just files on disk. Reason: Reliability and Ease of use.
- **D2. Default layout and names** (Library pages D26): year, then a date folder; original name kept; a short number on a name clash. Date folder style: `2026-10-28`. The pattern is editable in the File handling tab. Capture date from the file's own metadata, then (Proposed, Q1) a date in the file name, then the file date. A file with an unusable date is not refused; it is filed under the date that applies.
- **D3. Whose photo** (Library pages D26): a phone backup goes to that person's own space; a drop or Add files while browsing a space goes to that space; Add files from a library page uses the scope picked at the top.
- **D4. Companion files move and are renamed together** (D26). A group is the main file plus its companions; the group is filed or refused as a whole.
- **D5. Duplicates are exact copies by file contents**, checked as each file reaches the Import folder and before filing (D26). Skipped with "Already in your library" in the summary. Keep both files it under a numbered name. **Scope** (Shy, 2026-10-28): a normal import checks the destination space only (a private space never learns what is in another); a move or contribution **into the Shared Library** checks the Shared Library, because an exact copy may already be there. The Shared Library is visible to the household, so telling the contributor "Already in the Shared Library" reveals nothing private.
- **D6. Rejects are refused at the door with a plain reason, nothing moved** (D26). Reasons: damaged or unreadable, empty, file type Library does not read, a name Library can't store, not enough space, destination offline. A refused file goes to the **Rejected area** (D1), is tracked with its reason, shown in Activity, and removed automatically after a retention time (default 30 days, in config). Administrators can release it back to Import or delete it from the Rejected list. Uploads whose bytes were never complete are discarded and only recorded.
- **D7. Linked (As-is) folders are read-only** (D25). Never filed or renamed. An unreadable file is listed in Activity and left alone.
- **D8. Space is checked before any copy or move** (D26), and long jobs check again and stop cleanly if space runs out.
- **D15. Stray files are noted** (Shy, 2026-10-28). A scan of the Import and Rejected areas records any file that has no data-store row (copied there by hand, left by a crash) as a stray in Activity; strays in Import are adopted and filed like any arrival, strays in Rejected are listed for the administrator.
- **D16. Duplicate cleanup jobs** (Shy, 2026-10-28). A scheduled and run-now job finds exact copies that are already inside a space or the Shared Library (found by hash), and writes a **duplicates report** for the administrator: each group with where every copy lives, who added it, sizes. Actions: keep one (the others go to Trash, per Safer deleting in View, never straight to deletion), or keep all and dismiss. Administrators see the Shared Library's groups and their own space; another person's private space is never reported to them.
- **D17. Tidy this folder** (Shy, 2026-10-28). Files what is already in a Managed folder into the date layout, **in place** (a rename within the same folder, on the same drive, so no big copy and no temporary storage). It can run now, after a warning that the library is unavailable until it finishes, or on a schedule (such as overnight). It is a durable job like the existing folder jobs: restartable, with a ledger. Cross-drive work (merging in a folder from another drive) keeps its existing copy-then-confirm behaviour and is not part of Tidy.
- **D18. The Import area is only for arrivals, and can be anywhere** (Shy, 2026-10-28). Converting a folder to Managed and Tidy this folder work in place and need no copy area. So the Import area is needed only for new files arriving from outside (uploads, phone backup, Add files, hand copies). It is **one global Import location** by default, shared by every media type (Movies, Photos, and so on), with the option to set a different import location for a specific media type or library (the existing "Import destination"). No warning is shown about a different drive: the person chose the location and knows it. Filing from a different drive is simply a copy then confirm.
- **D19. Rejected files are kept 30 days** by default, changeable in config (Shy, 2026-10-28).
- **D9. Filing is a durable, restartable step** (same style as the Import folder filer and folder jobs): each file's move is journaled, a stop mid-move is finished or put back on the next start, and nothing is ever left in two places or none. Reason: Reliability.
- **D10. Changing a photo's date later (in the editor) never moves the file** (Proposed). The file stays where it was filed; the Timeline uses the edited date. Reason: stability for backups and the owner's own tools; Immich behaves the same way once a template has run.
- **D11. Privacy:** the duplicate check uses a hash computed on the server; nothing about a photo leaves the machine. Logs name no photo paths for Vault-protected libraries (only the kind of error), matching the Vault rules. The summary shown to a person only counts or names that person's own files.
- **D12. Needs Review gets a library filter** (the follow-up logged in Library pages). It filters by the library of the item the review row points at. View libraries put nothing into Needs Review in this spec (their problems are rejects, shown in Activity); the filter exists for every library.
- **D13. One filing step for every way in.** Browser upload, phone backup, Add files, drag and drop and by-hand copies all end in the Import folder and the same filer. The existing browser-upload layout (`10 - Oct / Oct 28 - 14.22.31 IMG.jpg`) is retired and no conversion of old filed files is done (pre-release; Library pages D24 rule).
- **D14. Benchmark** (so the choice is explained, not copied): Immich files uploads by date with an editable template (`{year}/{year}-{month}-{day}/{filename}`) and detects duplicates by file checksum, keeping the original name; Plex and Jellyfin only read photos where they lie, offer no filing and no duplicate check; Google Photos hides folders entirely. This spec follows Immich on filing and duplicates and keeps Library's own protections (copy not move, per-person spaces, plain refusal reasons).

## 4. UI behaviour

Screens already exist. This spec changes what appears in them.

| Situation | What the person sees |
|---|---|
| Normal import | Summary after Add files, upload or phone backup: "56 photos added. 4 already in your library." Library page Activity shows the same line with the time. |
| Files copied by hand into Import | No dialog. The Timeline and Photos update within about a minute; Activity shows "Filed 38 photos from Import". |
| Reject | Summary: "2 files could not be read and were not imported." Activity lists each file name (the person's own) with the reason. |
| Empty Import folder | Folders tab shows the Import folder row with "Nothing waiting". |
| Waiting | Import row says "38 waiting" while filing runs, with a quiet spinner. |
| Not enough space | "Not enough space: 12.4 GB needed, 3.1 GB free." Nothing starts. |
| Destination offline | "The library folder is offline, so nothing was copied." |
| As-is (linked) library | No Import row. An unreadable file shows in Activity as "Could not read (left as it is)". |
| Phones | Nothing new. Add files still doesn't appear on phones; phone backup, uploads and Activity read-only text work as before. |
| Needs review link | Opens Needs Review narrowed to that library, with a "Showing: Photos" chip that clears. |
| Vault-protected library | Same behaviour; summaries and Activity show only to profiles with access. Admins see counts, not file names, in logs. |

## 5. Data / model changes

- No folder is created inside a library folder. The Import area's Photos and Personal videos sections are created with the existing Import folder. New data-store rows: one per file in staging or rejected (library, space, original name, state: waiting, filing, filed, rejected, stray; reason code; times), and one per duplicate group found by the cleanup job. A cleanup removes finished rows and rejected files after the retention time.
- A per-file **filing journal** row (source, destination, state), the same idea as the existing Import filing journal, so interrupted filings recover. Reuse the existing journal where it fits.
- Reject records are written to the library's Activity log (existing mechanism), with reason codes; no new table if Activity can carry the reason code and file name.
- Duplicate check: reuse the stored content hash on local assets, indexed per space.
- Needs Review: no new column; the filter joins from the review row's item to its library.
- Pre-release: existing View data may be cleared and re-ingested; no conversion of previously filed files.

## 6. Work packets

### PF1: Import and Rejected areas with tracking
- **What it does:** adds Photos and Personal videos sections to the existing Import folder and a Rejected area, with a data-store row for every file in them, stray-file noting, automatic clean-up, and an "N waiting" line on the library's Folders tab. Nothing is created inside a library folder.
- **Files likely touched:** library folder services and View source indexing (`ViewStorageService`, `ViewSourceIndexingHostedService`), the Folders tab row, tests (to be confirmed)
- **Depends on:** none
- **Size:** Standard
- **Done when:** A7 passes; files placed in the Import area are noticed within about a minute; a hand-placed file with no row is recorded as a stray.

### PF2: The filing step (capture date, layout, companions)
- **What it does:** one filer for View: reads capture date (metadata, then file-name date, then file date), builds the path from the library's pattern, moves the group of main file plus companions together with a short number on a clash, journals each move, recovers after a stop, and replaces the browser-upload-only layout.
- **Files likely touched:** `ViewLibraryService` (upload path and metadata reading), a new filer service beside it, the Import folder filer for the journal pattern, tests (to be confirmed)
- **Depends on:** PF1
- **Size:** Complex
- **Done when:** A1, A2, A3 and A7 pass; a crash between move and index is finished at the next start; the old `MMM dd - HH.mm.ss IMG` naming is gone from code, tests and docs.

### PF3: Duplicates
- **What it does:** exact-duplicate check by file contents at the door, scoped to the destination space (Q2), obeying Keep one copy and Keep both; counts skipped files in the summary.
- **Files likely touched:** the filer, `ILocalAssetRepository` hash lookup, summary contracts, tests (to be confirmed)
- **Depends on:** PF2
- **Size:** Standard
- **Done when:** A4 passes; Keep both files the second copy under a numbered name; a duplicate in another person's space neither blocks nor is revealed.

### PF4: Rejects and the summary
- **What it does:** reason codes and plain sentences, a rejected file goes to the Rejected area (tracked, auto-cleaned, release or delete by an administrator), the import summary text, Activity entries for rejects, and the linked-folder "left as it is" entry.
- **Files likely touched:** the filer, Activity recording (`PersonalMediaActivityRecorder`), summary contracts and fixtures, tests (to be confirmed)
- **Depends on:** PF2
- **Size:** Standard
- **Done when:** A5 and A10 pass; wire fixtures updated; Contracts tests pass.

### PF3b: Duplicate checks on moves into Shared, and the duplicates report
- **What it does:** the Shared Library duplicate check on moves and contributions, the cleanup job (scheduled and run now), the administrator duplicates report and its keep-one action through Trash.
- **Files likely touched:** `ViewSharedContributionService`, the transfer service, a new duplicate scanner and report endpoint, Settings page for the report, Contracts, tests (to be confirmed)
- **Depends on:** PF3
- **Size:** Standard
- **Done when:** A9 passes; a private space's copies never appear in an administrator's report.

### PF5: Needs Review library filter
- **What it does:** `?library=` on Needs Review narrows to that library on the Engine (review rows joined to their item's library) and the page shows the "Showing: <library>" chip; the library page's Needs review link uses it.
- **Files likely touched:** `ReviewEndpoints.cs`, the review read service, the Needs Review page, `LibraryStatusCell`, Contracts and tests (to be confirmed)
- **Depends on:** none
- **Size:** Standard
- **Done when:** A6 passes for a Movies library and a Photos library; a person without access to a library never sees its rows even with the filter removed.

### PF6: Free-space check on every way in, plus Add files and Scan now for View
- **What it does:** uses the existing free-space check on upload, phone backup, drag and drop and conversion into managed View folders; adds Add files and Scan now to the View library pages (the same buttons other libraries have).
- **Files likely touched:** `LibraryAddFilesService`, `FreeSpaceCheck`, `ViewEndpoints.cs`, `ViewKindPage.razor`, tests (to be confirmed)
- **Depends on:** PF2
- **Size:** Standard
- **Done when:** A8 passes for each way in; Add files from a Photos page copies into the Import folder and shows the summary.

### PF8: Tidy this folder
- **What it does:** the in-place filing job (D17) with its warning, run-now and schedule choices, progress and resume.
- **Files likely touched:** `FolderOrganiseJobWorker` and its runners, the folder "..." menu, schedule config, tests (to be confirmed)
- **Depends on:** PF2
- **Size:** Complex
- **Done when:** A11 passes; the job never copies across drives; the library shows as unavailable only while it runs.

### PF7: Docs and batch
- **What it does:** public Libraries and privacy pages, presentation rules, the View personal space guide, the Library pages follow-ups list (marks these items done), and appends the Local checks to `engineering/validation/batch.md`.
- **Depends on:** PF2 to PF6, PF3b, PF8
- **Size:** Mechanical
- **Done when:** a docs search finds the new layout and no description of the old browser-upload naming; the batch list has the checks below.

## 7. Local checks

Run on SHY-DESKTOP with a small generated test library first, then real photos from `Z:\Development\testing\media\real_media` for L4 only.

- **L1.** Create a Managed Photos library on a fresh folder. Confirm nothing but photos ever appears inside it. Copy 20 photos from a phone card into the Import area's Photos section: within about a minute they are in `Year/Year-Month-Day/` with their own names and show in the Timeline.
- **L2.** Add the same 20 again: the summary says all 20 were already in your library and nothing new is filed. Switch the library to Keep both and repeat: they are filed under numbered names.
- **L3.** Include a RAW with a JPEG and an `.xmp`, and an iPhone photo with its Live Photo video: each group lands together with the same base name.
- **L4.** On real media: a folder of photos with no camera date but dates in the file names (e.g. WhatsApp images) files by those dates, not by today.
- **L5.** Make a damaged copy of a JPEG (cut the end off) and a zero-byte file: both are refused with reasons, listed in Activity, and not moved.
- **L6.** Fill a small test drive and try to add more: refused with the needed and free sizes.
- **L7.** Use the Needs review link on a Movies library with some review items: only that library's items show, and the chip clears.
- **L8.** Sign in as a second profile and add the same photo to their own space: it is filed (not "already in your library"), and the first profile's summary never mentions it.
- **L10.** Contribute a photo to the Shared Library that is already there: refused with "Already in the Shared Library". Run the duplicates report on a Shared Library with planted copies: groups are listed; keep one sends the rest to Trash.
- **L11.** Put a stray file into the Rejected area by hand: it is noted in Activity. Let a rejected file pass the retention time (set it to a minute for the test): it is removed and its row cleared.
- **L12.** Run Tidy this folder on a Managed folder of 2,000 photos on one drive: warning first, no copying in the disk activity, library back afterwards; then schedule it for later and check it ran.
- **L9.** Add the same folder as an As-is (linked) library: no Import folder, nothing renamed or moved, an unreadable file only listed in Activity.

## 8. Risks and open questions

Risks: large imports hold the disk busy for a while (job is restartable and low priority); phones that rewrite file dates on copy would mis-date photos without the file-name fallback (D2); a household that hand-edits the Import folder while a filing runs (the journal and the "settled file" wait handle it); filing logic is shared with the Vault, so a Vault change must not break it (Vault build thread reviews).

Open questions: none. Q6 and Q7 were answered 2026-10-28 (see D18 and D1).

## 9. Gaps and drift

- Browser upload files by `Year/MM - Mon/Mon dd - HH.mm.ss IMG.ext`, discarding the file's own name and with no duplicate check; Library pages D26 says year, then date folders, original name kept. Needs Shy's decision (proposed: change code, retire the old naming, D13). Not decided yet.
- Library pages D25 says the import subfolder sits inside the managed folder; Shy's 2026-10-28 note moves it outside (D1). Needs a note or new version of Library pages. Proposed: change spec.
- The `?library=` link exists in the Library status cell but Needs Review ignores it (proposed: change code, PF5). Not decided yet.

## Plain English summary

Photos and Personal videos get a proper "drop zone". Anything that arrives there, from a phone backup, an upload, Add files or you copying files in, is checked, tidied into year and date folders using the date the photo was really taken, and kept together with its companions (RAW plus JPEG, Live Photos). Exact copies are skipped, and anything that can't be used is refused with a plain reason you can see in Activity, so nothing vanishes quietly. Your originals are never moved or changed. The Needs review link on a library also starts narrowing to that library. Tidy this folder and a duplicates report are included, and staging is kept out of your library folder.
