---
title: "How File Ingestion Works"
description: "Follow the path from a raw file on disk to a structured item inside Tuvima Library."
audience: "user"
category: "explanation"
product_area: "ingestion"
tags:
  - "ingestion"
  - "pipeline"
  - "watchers"
status: current
---

# How files enter your library

Learn what happens after you add a file to Tuvima Library. This three-minute overview explains why a file may appear in Operations before you can browse it.

## Follow a new file

The [Engine](../reference/glossary.md#engine) waits for copying to finish, fingerprints the file, and reads its embedded details. It resolves ambiguous types, such as a song versus an audiobook track, before matching.

Provider matching gathers identity clues and artwork. Wikidata can then add structured identity and relationships. An uncertain match may need review rather than an automatic guess.

## Understand when it becomes visible

Browsing needs a real title, a resolved media type, and a settled artwork result. Settled can mean a cover is present or an artwork pass has confirmed that none is available.

Visibility and file organization are separate milestones. Existing/read-only sources remain in place; permitted managed sources follow their organization rules. Personal View media uses a separate local workflow.

## Check progress or act

Open **Operations** from the header activity indicator to follow active work. The screen refreshes automatically; **Scan now** starts an extra folder scan. Use Review Queue only when the item needs your decision.

A completed file count does not mean the whole run has finished. Required identity, enrichment, or organization operations can still be active.

<details>
<summary>Technical details</summary>

When a new file lands in a watched folder, Tuvima Library does not trust it immediately. The Engine waits until the file is stable, reads what it can from the file itself, applies source-policy-aware staging or in-place indexing, and then starts deciding whether it is ready for the Dashboard and the organized library.

---

## The big picture

```
File appears in a watched folder
  -> settle and lock check
  -> fingerprint
  -> scan embedded metadata
  -> classify ambiguous formats
  -> source-policy-aware staging or indexing
  -> Stage 1 Retail
  -> Stage 2 Wikidata
  -> Quick Hydration
  -> Stage 3 enrichment
  -> managed artwork settlement
  -> browse readiness gate
  -> library organisation
```

The key idea is simple: **trust must be earned**.

---

## Step 1: Detection and settling

The Engine notices a new file quickly, but it does not scan it immediately. Large files can take time to finish copying. The system waits for the file to stop changing and confirms it is no longer locked by another process.

That avoids half-read files and noisy filesystem events.

---

## Step 2: Fingerprinting

The Engine computes a SHA-256 hash from the file contents.

That fingerprint lets the system:

- recognise the same file even if it is renamed or moved
- detect duplicates
- attach later rescans to the same underlying asset

The fingerprint is the file's durable identity, not its filename.

---

## Step 3: Scanning

The correct processor opens the file and extracts embedded metadata such as:

- title
- author or creator
- year
- narrator
- series information
- embedded cover art
- file-format-specific tags

These values become metadata claims with confidence scores. A clean EPUB title is more trustworthy than a guessed filename title, and that difference matters later.

---

## Step 4: Classifying ambiguous formats

Some formats are ambiguous. An MP3 could be a song or an audiobook chapter. An MP4 could be a movie or a TV episode.

The Engine combines file signals, folder hints, and AI-assisted classification to resolve those cases. If it still cannot make a safe call, the item goes to review instead of being forced into the wrong media type.

---

## Step 5: Safe staging

Managed intake can stage files before final organization. Existing/read-only sources are indexed in place and are not moved into staging.

Staging is the Engine's safe holding area:

- the original file is preserved while matching and enrichment run
- retries can happen without touching the organised library
- a bad or uncertain match does not immediately rewrite your final folder structure

This staging decision is separate from whether the item is visible in the main browse surfaces.

---

## Step 6: Identity matching

After scan and staging, the Engine starts the two identity stages:

- **Stage 1 Retail** finds practical provider candidates, artwork, people, ratings, descriptions, and bridge IDs
- **Stage 2 Wikidata** resolves canonical identity from those bridge IDs

Retail matching uses these confidence gates:

- `>= 0.90` can be auto-accepted
- `0.65` to `< 0.90` goes to review
- `< 0.65` is treated as too weak

That stricter gate reduces false positives and improves the quality of later Wikidata resolution.

If Retail cannot produce a safe match, Wikidata is not used as a broad text fallback. The item goes to review instead. If Retail succeeds but no QID is found, the item keeps its retail data and can be retried later. Missing QID alone need not create a review item.

---

## Step 7: Artwork settlement

Quick Hydration gets the item visible with core identity and accepted artwork when enough facts are available. Managed artwork is stored under `.data/assets/...` and referenced from the database; media-folder sidecars are optional exports only.

The Engine also tracks whether artwork is:

- present
- still pending
- explicitly missing

That matters because the main browse surfaces do not show an item until its artwork outcome has settled. A file can be safely staged and partly identified without being ready for the main browse surfaces yet.

Stage 3 enrichment continues in the background after the fast path. It expands people, fictional entities, narrative roots, relationships, lyrics/subtitles, extra artwork, and other universe details.

---

## Step 8: Browse visibility

An item becomes visible in the main browse surfaces only after it passes the browse readiness gate:

- non-placeholder title
- resolved media type
- settled artwork outcome

If it fails that gate, it stays visible in Operations and, when a decision is actionable, Review Queue until the missing piece is resolved.

This is why "the system has seen the file" and "the file is in the main browse surfaces" are no longer the same moment.

---

## Step 9: Organisation

Promotion into the organised library on disk is a later decision based on the wider confidence and organisation rules.

So there are two separate milestones:

- the item is ready enough to surface in the main browse surfaces
- the item is ready enough to be promoted into the final organised library structure

That separation makes the product more honest and reduces bad auto-moves.

---

## When things go wrong

The pipeline is designed to fail safely:

- if the file never settles, it is not processed prematurely
- if the processor cannot read it cleanly, the system falls back to weaker signals
- if Retail cannot find a safe match, the item goes to review

---

## Watching Operations

**Settings > Operations > Ingestion** is the single operational surface at `/settings/ingestion`. It answers what is happening now, whether the pipeline is waiting, whether anything needs attention, and what completed in earlier batches. **Needs Review** remains the human decision queue.

It shows real application state from the Engine:

- active scans and ingestion batches, grouped by Discover, Identify, Enrich, and Organize
- registered, provisional, and review lifecycle counts
- current-run outcomes and recent named runs
- Watch, Listen, and Read source folders from `config/libraries.json`
- provider health without exposing secrets
- pipeline counts from durable identity jobs and ingestion logs
- grouped review reasons from pending Review Queue records

File progress and run completion are deliberately separate. The Files checked outcome reports intake volume, while the prominent overall bar combines measurable pipeline stages for the logical run. It stays below completion while a stage is active and shows the current stage's own task count beneath it. The run stays active while required identity, artwork, people, relationship, or organization operations remain outstanding.

While work is active, the Dashboard updates from SignalR `BatchProgress` and `IngestionProgress` events and polls the operations snapshot more frequently. When idle, it polls less often. If a signal is not tracked yet, the page says so instead of inventing a count. The top navigation activity indicator opens Ingestion for authorized system work. The page has no manual status-refresh control because this synchronization is automatic. Its one **Scan now** action starts an extra scan of watched folders; folder monitoring, schedules, and queued processing continue automatically.

Batch history loads the three newest runs first. Search and All, Completed, Needs attention, and Failed quick filters query the server, while **Show older** appends another bounded page. Selecting a run opens the shared searchable, sortable media browser without loading raw technical records. A durable ingestion batch retains the same identity when the Engine restarts and resumes its outstanding work; later watcher debounce windows join the active batch instead of creating duplicate entries, and a scan across multiple configured source folders uses one batch ID.

On a phone, Ingestion keeps current state, active and queued operation counts, attention, and compact batch rows visible. Needs Review resolution is reserved for desktop and tablet, while its unresolved count remains visible beside Operations. All responsive summaries use the same state and counts as desktop.
- if Wikidata finds no QID, the item can still remain usable without forcing a bad identity
- if artwork is still unresolved, the item stays out of the main browse surfaces until that question is settled

</details>

## Next steps

- [Follow work in Operations](../guides/library-settings.md)
- [Correct an item](../guides/editing-items.md)
- [Resolve a review item](../guides/resolving-reviews.md)
- [Read the pipeline architecture](../architecture/ingestion-identity-enrichment-pipeline.md)
