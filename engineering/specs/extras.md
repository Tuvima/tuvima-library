# Spec: Extras

> **Status:** Locked · **Version:** 2 · **Last updated:** 2026-10-31 (locked by Shy) · **Related docs:** [Product overview](../product/overview.md) · [TV artwork, cast and matching](tv-artwork-cast-and-matching.md) (D9, Steph Curry Masterclass) · [Videos](videos.md) · [Photo and video filing](photo-video-filing.md) · [Details overlay and edit mode](details-overlay-and-edit-mode.md) (the "Extras" row) · [Universal editor v2](universal-editor-v2.md) · [Mockup](../design/mockups/extras-for-tv-shows.html)

> Written 2026-10-31 after Shy answered the Steph Curry MasterClass question: "given we support documents, pictures and so on, how would that work if the library is for TV? I wouldn't want those things separated into completely different libraries, they should be together."

## Plain-English product walkthrough

**Today.**
- A TV folder can hold more than episodes: a PDF guidebook, a few pictures, a behind-the-scenes clip, a trailer.
- Library only understands episode videos in a TV folder. The other files are either ignored or, if you add the same folder under View, end up in a completely different place (Documents or Photos), far from the show they belong to.

**After this spec.**
1. A file in a matched TV show's folder that is **not an episode** becomes an **Extra of that show**. It never goes to View, never creates a second library and never needs a second folder.
2. The show page gets an **Extras** section under the seasons: a row of cards for the guidebook, the pictures, the clips. It is hidden when there are none.
3. Opening an Extra uses the viewers Library already has: a PDF or text opens in the reader, a picture in the photo viewer, a clip in the video player. Nothing new to learn.
4. Extras are **never counted as episodes**: no effect on "12 episodes", the next-up episode, watched progress, or the show's runtime. A clip you finish does not mark anything watched.
5. A **rule, not a list**: the same rule applies to every show. There is no code for MasterClass, and no title is special-cased.
6. Extras follow the show's rules: who may see it (Kids levels, Vault), where it is stored, delete groups and the Recycle bin, backup and Safer deleting.
7. Corrections happen in the universal editor on the show's page: move a file from Extras to an episode, or from an episode to Extras. Both already exist as "Add anyway" in the match step.

**Representative journeys.**
- *Steph Curry MasterClass.* The folder holds 18 lesson videos and `Workbook.pdf`. The show page shows 18 episodes and an Extras card "Workbook" (PDF). Tapping it opens the reader. The episode count stays 18.
- *A box set with pictures.* A show folder has `Extras/Concept art/*.jpg`. The Extras section shows one card "Concept art, 24 pictures" that opens the photo viewer as a gallery.
- *A behind-the-scenes clip.* `Making of.mkv` sits beside the seasons. It appears in Extras with a play button; watching it changes nothing about your episode progress.
- *A mistake.* A real episode was named badly and landed in Extras. On the show page, Edit, then Match, then "Move to episode" fixes it, and the episode count goes up.

**What stays the same.**
- View (Photos, Personal videos, Documents) is untouched. A folder added to View still works as before; an Extra is a file that lives in a show folder you added to Watch.
- Episode matching, TheTVDB data and artwork do not change.
- Original source media stay protected: Library reads Extras where they are and never moves or changes them.

**Observable acceptance (mapped to packets).**
- A1 (WP1): a `Workbook.pdf` in a matched show's folder is stored as an Extra of that show and not as an episode, a document in View, or a Needs Review item.
- A2 (WP1): the show's episode count and next-up are the same with or without extras in the folder.
- A3 (WP2): the show page lists the PDF, pictures and clip as Extras cards; with none, no Extras section appears.
- A4 (WP3): a PDF opens in the reader, a picture in the photo viewer, a clip in the video player; finishing a clip does not change episode progress.
- A5 (WP4): an Extra can be moved to an episode, and an episode to Extras, in the universal editor.
- A6 (WP5): a Kids profile, Vault rules and delete/Recycle bin behave for Extras as for the show.

## 1. Goal

Keep everything that belongs to a show together. Guidebooks, pictures and behind-the-scenes clips found beside a TV show's episodes appear on that show's page as Extras, instead of drifting into separate libraries or being ignored.

## 2. Scope

**In scope**
- TV shows, movies, books (including audiobooks), albums, comics and Videos courses (D9): non-episode, non-main files inside the item's folder become Extras of that item, matched or not (D10). Walkthrough and packets below are written for TV and apply the same way to the others.
- An Extras section on the show page (desktop, tablet, phone, TV) with cards by kind: document, pictures (grouped by folder), clip.
- Opening Extras in the existing reader, photo viewer and video player.
- Moving a file between Extras and episodes in the universal editor.
- Following the show's visibility, delete and backup rules.
- Public docs and engineering docs.

**Out of scope**
- Downloading extras, or fetching them from a provider (TheTVDB trailers and similar). Only files you already own.
- Searching inside an Extra's text from the main search (follow-up if wanted; View's document search stays in View).
- OCR of scanned Extras beyond what View documents already get (follow-up).
- Editing the Extras' own metadata beyond title and order in v1.

## 3. Decisions made

- **D1. A rule, no per-title code.** One rule decides what is an Extra for every show. Reason: Maintenance. A MasterClass special case would be the first of many.
- **D2. Extras belong to the show, not to a library.** One folder, one library, one place on screen. Reason: Shy's direction; Ease of use.
- **D3. Never counted as episodes.** No effect on episode counts, runtime, next-up, "watched" or the show's progress. Reason: a guidebook must not make a 18-lesson course show "19 episodes".
- **D4. Reuse the viewers we have.** Reader for documents, photo viewer for pictures, video player for clips. Reason: Maintenance and Uniformity.
- **D5. Never Needs Review.** An unclear extra is just kept as an Extra; the Engine is never "unsure" about one. Reason: Needs Review is for identity questions, and 40 extra files would flood it.
- **D6. Follow the show.** Extras inherit the show's access (Kids levels, Vault, shared or private), deletion (delete groups, Recycle bin) and backup behaviour. Reason: Privacy; nothing can leak by being in the "wrong" place.
- **D7. Read in place.** Library never moves, renames or rewrites an Extra. Reason: original media stay protected.
- **D8. Desktop-only editing, as everywhere.** The move between Extras and episodes sits in the universal editor and is not offered on phone or TV.

- **D9 (Q1 = B and Q6 = B, Shy 2026-10-31).** Scope is TV shows, movies, books, albums, comics and Videos courses at once. Reason: one rule, one Extras row everywhere.
- **D10 (Q5 = B, Shy 2026-10-31).** An Extra attaches to whatever Library built from the folder, matched or not, and stays with it if it is matched or re-matched later. Reason: a Videos course is never matched, and homework must still show.
- **D11 (Q2 = A, Q3 = A, Q4 = A, Shy 2026-10-31).** Clips are playable and never counted as progress; pictures are one card per folder opening as a gallery; Extras are not in main search in v1.
- **D12. Artwork and booklets stay artwork.** For albums and comics, cover images, folder art and booklet scans that Library already uses as artwork are not duplicated as Extras; only other files are. A PDF booklet or a lyrics sheet beside an album is an Extra. Reason: no double listing.

## 4. How Library decides what is an Extra

A file is an Extra when all of these are true:
1. It is inside the folder tree of an item Library has built from that folder (a show, movie, book or Videos course), matched or not.
2. It is not claimed by the episode matcher (no season and episode could be read from the name, and it is not a subtitle, artwork or `.nfo` sidecar of an episode).
3. Its type is one Library reads: documents (PDF, text, EPUB), pictures, or video/audio clips.

Folder names such as `Extras`, `Featurettes`, `Behind the Scenes`, `Bonus`, `Trailers`, `Samples` are treated as hints, never as requirements. A clip named like a numbered lesson (`Lesson 07.mp4`) stays an episode.

Other files (executables, archives, `.nfo`, subtitles, thumbs.db, partial downloads) are ignored, as today.

A file that looks like both (a clip with a plausible episode number but the folder is `Extras`) goes with the folder hint. Anything the person disagrees with is fixed in the editor (D8).

## 5. UI behaviour

**Show page, Extras section.** Placed below the last season (and below Cast), headed "Extras" with a small count. A row of cards, newest sorted by kind then name. Documents show a page-one thumbnail and "PDF, 42 pages"; picture folders show a 2x2 mosaic and "24 pictures"; clips show a still and length. Tap or press opens the right viewer. TV remote focus walks the row like any shelf.

| Situation | What the person sees |
|---|---|
| Normal | The Extras row as above |
| Empty | No Extras section at all (no empty state) |
| Loading | Skeleton cards, same height as real ones |
| Error (a file moved or missing) | The card stays with a small warning icon and "File not found"; the show page is unaffected. Missing items list covers it like other media |
| Partly ready (thumbnails still being made) | Plain file-type icon until the thumbnail arrives |
| Kids profile | Shown only if the show is allowed; no extra rating shown |
| Phone | Cards scroll sideways as a shelf; same viewers |

**Editing (desktop).** Edit, Match: each extra row has "Move to episode" (pick season and episode), and each episode row gets "Move to Extras". Both go through Review changes and Save changes as everywhere.

**Settings.** None in v1. No switch for Extras (Fewer controls).

## 6. Data / model changes

A new **Extra** belongs to a show (a TV show group), holds the file path, a kind (document, pictures group, clip), a display title (from the file or folder name), a sort order, and file facts (size, length or page count). It is stored apart from the Work/Edition graph and from View's index (View keeps its own rule that Photos never enter the graph); the show page asks the Engine for "this show's extras". Pre-release: a re-scan of the library creates Extras for existing shows; a reset is acceptable, no migration.

## 7. Work packets

### WP1 — Find and store Extras
- **What it does:** During TV ingestion, after episodes are matched, files that match the rule (section 4) are saved as Extras of the show instead of ignored; episode counts and progress ignore them. Re-scan adds and removes Extras as files appear and disappear.
- **Files likely touched:** to be confirmed (TV ingestion/organise services in `src/MediaEngine.Api`, a new Extra repository in `src/MediaEngine.Storage`, `src/MediaEngine.Domain`, tests).
- **Depends on:** none
- **Size:** Complex
- **Done when:** a test library with episodes plus `Workbook.pdf`, a picture folder and one clip gives 3 Extras and the same episode count as without them.

### WP2 — Engine connection point and Contracts
- **What it does:** A read-only "extras of this show" action returning the cards (kind, title, thumbnail, counts); Contracts types and wire fixtures.
- **Files likely touched:** `src/MediaEngine.Contracts/`, `src/MediaEngine.Api/` endpoints, Contracts tests and fixtures.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a Contracts test and fixture pass; a Kids profile gets an empty list when the show is not allowed.

### WP3 — Extras section and viewers
- **What it does:** The Extras section on the show page and opening each kind in the existing viewers; clip play does not touch episode progress.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/`, Watch components, bUnit tests.
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** bUnit proves the section hides when empty and cards open the right viewer; phone 390 and desktop 1440 screenshots checked.

### WP4 — Move between Extras and episodes
- **What it does:** "Move to episode" and "Move to Extras" in the universal editor match step.
- **Files likely touched:** `src/MediaEngine.Web/Components/MediaEditor/`, Engine edit endpoints, tests.
- **Depends on:** WP1, WP3
- **Size:** Standard
- **Done when:** moving an extra to S01E02 raises the episode count by one and removes the card; moving it back reverses.

### WP5 — Follow the show (access, delete, backup)
- **What it does:** Extras inherit Kids levels, Vault, delete groups, Recycle bin, Safer deleting and backup rules of their show.
- **Files likely touched:** to be confirmed (permission service, delete group code, backup classification), guardrail tests.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** guardrail tests list the new table as backup-classified; deleting a show moves its Extras records with it; a Kids profile never sees a Vault show's Extras.

### WP6 — Docs
- **What it does:** Public docs (TV show page, Library folders) in tuvima-docs and engineering docs; presentation rules updated.
- **Files likely touched:** `docs/product/presentation-rules.md`, tuvima-docs `content/products/library/`, engineering README line.
- **Depends on:** WP3
- **Size:** Mechanical
- **Done when:** docs build passes the link check.

Waves: 1 (WP1) · 2 (WP2, WP5) · 3 (WP3) · 4 (WP4, WP6).

## 8. Local checks

- **L1. Steph Curry MasterClass.** On the real media, ingest the MasterClass folder. Expected: one show with its lessons as episodes and the guidebook as an Extras card; the episode count is the lesson count. Failure: the guidebook counts as an episode, or is missing.
- **L2. Real-world folders.** Run on three real shows that have bonus folders. Expected: sensible Extras, no real episode lost to Extras. Failure: a numbered episode appears under Extras.
- **L3. Viewers.** Open a PDF, a picture folder and a clip from Extras at 1440 and on a phone. Expected: each opens in its usual viewer and returns to the show page.
- **L5. Albums and comics.** Check one album with a booklet PDF and one comic series with a bonus PDF. Expected: each shows an Extras card; cover art is not listed as an Extra. Failure: the cover appears twice, or songs/issues count changes.
- **L4. Progress.** Finish a clip. Expected: next-up episode and watched count unchanged.

## 9. Risks and open questions

- **Risks:** (a) A numbered extra clip mistaken for an episode, or the reverse: mitigated by the editor move (WP4) and the folder hints. (b) A very large number of pictures flooding the row: pictures group by folder into one card. (c) Folder scans get slower with big picture folders: thumbnails are made in the background (Performance).
- **Open questions:** none; Q1 to Q6 are answered (D9 to D12).
