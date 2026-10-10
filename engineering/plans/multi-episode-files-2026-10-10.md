# Plan (draft): One file covering several TV episodes (2026-10-10)

Status: **draft. Pick up in its own session.** Split out of `first-run-ingestion-fixes-2026-10-10.md` (former WP13). Needs the PO's answers to §5 before implementation.

## 1. Why
Some TV files cover more than one episode:
- double-length pilots and finales (`S01E01-E02`);
- two-part episodes ripped as one file;
- cartoons that pair two 11-minute episodes per file (often most of a show);
- anime OVAs and arcs;
- occasionally a whole miniseries in one file (Dr. Horrible's Sing-Along Blog).

Today an asset belongs to exactly one Work (asset → edition → work). Such a file therefore counts as a single episode, the other episodes show as missing, and progress and "next episode" go wrong. Plex, Jellyfin, Kodi and Sonarr all support multi-episode files.

Interim behaviour from the first-run fixes plan: Dr. Horrible's whole-series file is attached as Season 0 Episode 1 (Specials).

## 2. Proposed experience
1. A file named `Show - S01E01-E02 - Title.mkv` (also the `S01E01E02` and `S01E01-02` forms) ingests as **two owned episodes** that share one file.
2. The show and season pages count both as owned. Each episode row plays the same file from the start; no chapter seeking in v1.
3. Continue Watching and progress: watching the shared file marks **all covered episodes** watched when it finishes. "Next episode" skips past the covered range.
4. The details page for a covered episode says "Plays with E02 (same file)".
5. Review Queue "Move to TV" (from the first-run fixes) can attach a whole-series file to the full episode range, replacing the interim special.
6. The editor can set or adjust the range ("This file covers episodes 1–3").

## 3. Likely technical shape (to confirm with a scout pass)
- **New data structure:** an `asset_episode_coverage` table linking `asset_id` to `work_id` (episode), with ordinal. This is an idempotent startup migration owned by `SchemaMigrator`, and is documented in `docs/reference/database-schema.md`.
- **Alternative:** one Edition per covered episode, all pointing at the same media asset path. Probably conflicts with the asset uniqueness and hash rules. To evaluate.
- **Domain:** an explicit aggregate method for episode coverage (no public setters).
- **Read services:** TV season/show owned counts, episode lists, the display projection (tiles), and detail composition for TV.
- **Playback:** resolve episode → asset through the coverage table, and write watch state for every covered episode.
- **Ingestion:** filename parser for ranges; the identity pipeline creates/links the episode Works for the range.
- **Wire contracts:** any new fields go through `src/MediaEngine.Contracts/` with snapshot updates.

## 4. Agents
- Scout (Haiku): map every asset→work assumption (owned counts, playback resolution, progress, display projection).
- Opus: decide the data structure and write briefs.
- Implementer (Sonnet, high: data store, playback state, identity pipeline) in 3 pieces: data structure + migration; ingestion/parser + identity; read models + playback + Dashboard.
- Verifier (Haiku); reviewer (Opus, high).
- Fable only with PO approval after two failed attempts.

## 5. Questions for the PO
1. Should finishing a shared file mark all covered episodes watched, or only once each episode's portion has played? (v1 has no chapter data.)
2. In episode lists, show each covered episode as its own row, or one combined row "E01–E02"?
3. Should the editor allow setting a range by hand in v1?
4. Is chapter-aware seeking (jump to E02's start inside the file) wanted later?

## 6. Acceptance criteria (draft)
- A double-episode filename ingests as two owned episodes sharing one asset; the season count is correct.
- Playing E02 opens the shared file; finishing it updates both episodes' watch state per Q1.
- Dr. Horrible can be re-attached from S00E01 to S01E01–E03.
- The migration is idempotent; existing single-episode data is unchanged; 0 warnings, all tests passing.
