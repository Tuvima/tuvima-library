# Spec: Film cuts and qualities as Editions of one film

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-25 · **Related docs:** [Product overview](../product/overview.md) · [Details overlay and edit mode (D15, formats of one title are Editions of one Work)](details-overlay-and-edit-mode.md) · [Videos](videos.md) · issue #1362

> Locked by Shy on 2026-11-25 ("go with all recommendations"). Built first on Shy's decision 231A (2026-11-24, "Agree w all") and merged in PR 1380; it fills a gap rather than changing any Locked spec, because no spec said how film cuts group.

## 1. Goal

A film you own in several versions (a Director's Cut and a Theatrical Cut, or a 1080p copy and a 4K copy) is one film in Library, with one Edition per version, each labeled so the details page offers a choice. Today each file becomes its own film, so Search shows the same film several times with nothing to tell them apart.

## 2. Scope

**In scope**
- Films (the Movies library kind) that are separate files of the same film.
- Reading the cut and the picture quality from the file name, and saving them as the Edition's label.
- A Plex-style `{edition-Final Cut}` in the path also names the cut.

**Out of scope**
- TV, Videos, Books and every other kind (Books already join other formats as Editions).
- Merging films that were already ingested before this change (pre-release data may be reset).
- Choosing which Edition plays by default (the existing details page and player rules stay).
- Film Series and Universe grouping (separate work).

## 3. Decisions made

- **D1. One film, many Editions** (Shy, decision 231A, 2026-11-24). Two files of the same cut in different qualities are one film with two Editions labeled by quality. Different cuts are one film with one Edition per cut. Together: "Final Cut · 4K" and "Final Cut · 1080p" are two Editions of one film.
- **D2. What counts as the same film.** Same title (letters and digits, ignoring case, accents and punctuation) and the same release year. When either file has no year, only files in the same folder count. A remake with the same title but another year stays a separate film.
- **D3. Edition label.** The cut (for example Final Cut, Theatrical Cut, Director's Cut, Special Edition, Extended Edition), then the quality (4K, 4K HDR, 1080p, 720p, SD), joined with " · ". Quality comes from the file name first (2160p, 4K, 1080p, HDR and similar words), then from the picture's real size (a widescreen crop counts by its width) when the name says nothing. A film with neither keeps no label; the page then names its Editions "Edition 1", "Edition 2".
- **D4. The cut is not part of the title.** "Blade Runner (1982) - Final Cut" is the title Blade Runner with the year 1982; the cut moves to the Edition label instead of being discarded.
- **D5. The details page does not change.** It already lists a film's Editions under "Your copy" when there is more than one, using the Edition label.

## 4. UI behaviour

- **Search, Watch, Recently Added:** the film appears once.
- **Film details page:** "Your copy" lists each Edition with its label (for example "Director's Cut", "Final Cut · 4K"). Nothing new is drawn.
- **A single file:** looks exactly as before.
- **Phone, tablet, desktop, TV:** no difference; this changes what the Engine groups, not any layout.

## 5. Data / model changes

None to the data structure. The Edition's existing label column now holds the cut and quality for films. No migration (pre-release data may be reset).

## 6. Work packets

### WP1 — Read the cut and quality, group the film (Standard)
- **Does:** the video reader keeps the cut and the quality as claims; the film's title and year no longer carry a trailing cut; ingestion looks for an existing film with the same title and year (or same folder) and attaches the file as another Edition; the Edition is labeled from the cut and quality; two files of one film arriving together cannot both start a film.
- **Files:** `VideoProcessor.cs`, `FilmEditionLabels.cs`, `ReleaseTitleHints.cs`, `FilenameCleaningRules.cs`, `HierarchyResolver.cs`, `MovieEditionSiblingFinder.cs`, `MediaEntityChainFactory.cs`, `IngestionEngine.Pipeline.cs`, `IngestionEngine.ProcessingStage.cs`, `config/filename_cleaning.json` (adds "extended edition").
- **Depends on:** nothing. **Size:** Standard.
- **Done when:** the Storage and Processors tests for three cuts, two qualities, remakes, missing years and unlabeled films pass.

## 7. Local checks

- Real media: a film owned in two cuts and a film owned in two qualities each appear once in Search and Watch, and the details page's "Your copy" lists each version with its label. Failure: a version shown as its own film, or a missing label.
- On a Windows library with real file names (dotted release names, brackets), confirm the cut and quality words read correctly. Listed in `engineering/validation/batch.md`.

## 8. Risks and open questions

- A long Blade Runner style file name with an unusual cut word (not in the list in `config/filename_cleaning.json`) is not recognized and stays a separate film until the word is added to that list.
- A film with a wrong or missing year in one file joins only through the same folder; a later retail match does not re-group already-split films.
- Open for Shy: should an exact duplicate (same cut, same quality) be kept as a second Edition with the same label (what happens now) or flagged for cleanup? Decision (Shy, 2026-11-25, recommended option): keep for now.
