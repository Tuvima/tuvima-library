# Spec: One file covering several TV episodes

> **Status:** Built · **Version:** 2 · **Last updated:** 2026-10-17 · **Related docs:** [Product overview](../../product/overview.md) · [First-run ingestion fixes](first-run-ingestion-fixes.md) · [Presentation rules](../../../docs/product/presentation-rules.md) · [Playback architecture](../../../docs/architecture/playback.md)

> Replaces Version 1 (a 2026-10-10 draft). Most of Version 1 is already built; this version records what was built and specifies the one piece still missing (the Dr. Horrible case). Locked by Shy on 2026-10-17.

> Built on 2026-10-17: WP1 (D6, Move to TV covers the whole miniseries, with Specials as the explained fallback) and WP2 (docs). Section 8's "Not built yet" item for D6 is now done; D7 remains a future spec.

## 1. Goal

A video file that holds more than one TV episode (a double-length pilot, two cartoon episodes in one file, a whole miniseries) counts as owned for every episode it covers, plays correctly, and keeps watch progress honest. Finish the last gap: a whole miniseries moved to TV should cover its real episodes, not sit as a one-off special.

## 2. Scope

**In scope**
- Record, as decisions, how combined files already behave.
- Make "Move to TV" attach a whole-miniseries file to all of the show's real episodes when the provider lists them.
- Keep the existing safety rules (limit, same season, Needs Review for unclear cases).

**Out of scope**
- Jumping to an episode's start inside a file (see Q1, a separate future spec).
- Splitting or re-encoding files. Original files are never altered.
- Per-episode progress inside one file, and per-episode skip-intro markers.

## 3. Decisions made

- **D1. Finishing the file marks every covered episode watched.** Without chapter data this is the only honest rule. Marking one covered episode watched or unwatched changes the file, so its siblings follow. Why: Reliability, matches Plex and Jellyfin.
- **D2. Each covered episode keeps its own row** in episode lists, with a quiet "Same file as Episode N" chip. Why: counts, ratings and next-episode logic stay per episode.
- **D3. Shy can set the range by hand** in the editor (Matching tab, "This file covers" picker).
- **D4. The record is the `media_asset_coverage` table.** The file stays attached to its first episode; the other covered episodes are listed against it. The "one Edition per episode" alternative was rejected: it would make one file look like several and break duplicate checks. Why: Maintenance.
- **D5. Safety rules for automatic linking:** a file covers at most 6 episodes, all in one season, in an unbroken run. A gap (E01 and E03), an oversized run or a typo (E01E200) goes to Needs Review instead of guessing. Shy confirmed keeping 6 on 2026-10-17. Why: Reliability; a typo must never mark a season as owned.
- **D6. Whole-miniseries files cover the real episodes.** When Move to TV is used and the provider lists the show's parts in one season, the file is linked to all of them (for Dr. Horrible: S01E01 to E03). One rule for every combined file; no permanent special-case. If the parts cannot be listed clearly, or exceed D5, the existing Season 0 Episode 1 (Specials) placement stays as the fallback and the item says so.
- **D7. Jumping to an episode inside a file is a separate future spec** (read chapter markers when present, else estimate from episode lengths). The data structure already has room for start and end times.
- **D8. Names:** the organiser names a combined file with its episode run (for example `s01e01-e02`).

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | Show and season pages count every covered episode as owned. Each row plays the shared file. A "Same file as Episode N" chip appears under covered episodes (tappable, 44px, wraps on phone; plain text if the other episode is off screen). |
| Playing a covered episode | The shared file opens. It starts at the episode's recorded start time only if there is nothing to resume. |
| Finished | Every covered episode shows watched. Continue Watching lists the file once; Next Up skips the covered range. |
| Move to TV on a miniseries file | The review card still reads "TMDB lists this as the miniseries ... (3 parts). Use it for this film?". After **Move to TV**, the show appears under TV with the file covering all listed episodes. |
| Move to TV, parts unclear | The file is filed as Season 0 Episode 1 (Specials) with a plain message saying why, and the other episodes show as not owned. |
| Empty / Loading / Error | Unchanged from single-episode files. |
| Partly ready | Episode details fill in as enrichment finishes; ownership is correct from the start. |
| Phone / tablet / desktop / TV | Same behaviour. The editor picker is desktop-only, like all editing. |

## 5. Data / model changes

None for D1 to D5 (built). D6 reuses `media_asset_coverage` with its existing sources; no new stored information. Pre-release: a reset and re-ingest is acceptable.

## 6. Work packets

### WP1 — Move to TV links the whole episode range
- **What it does:** when a Movies item is moved to TV as a miniseries and the provider lists its parts in a single season (within D5), place the file on the first episode and record coverage for all parts. Otherwise keep the Season 0 placement and show the reason.
- **Files likely touched:** `src/MediaEngine.Api/Services/Review/ReviewMoveToTvService.cs`, the TV reassignment repository, `src/MediaEngine.Providers/Services/TvEpisodeRangeCoverage.cs`, `src/MediaEngine.Web/Services/Editing/MovieTvSuggestionReader.cs`, matching tests (to be confirmed by Claude Code).
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test moves a file for a 3-part miniseries and the file covers S01E01 to E03 with all three owned; a test with an unclear or over-limit part list still produces the Season 0 placement with a visible reason; existing coverage tests pass.

### WP2 — Docs
- **What it does:** update the product rules, glossary and the first-run fixes spec so none still describes the Season 0 placement as the normal outcome.
- **Files likely touched:** `docs/product/presentation-rules.md`, `docs/reference/glossary.md`, `engineering/specs/first-run-ingestion-fixes.md` (cross-reference only) (to be confirmed by Claude Code).
- **Depends on:** WP1
- **Size:** Mechanical
- **Done when:** a search for "Season 0" in those docs finds only the fallback description.

## 7. Risks and open questions

- **Risks:** a provider listing the wrong number of parts links the wrong range; mitigated by D5 and Needs Review. Someone may later loosen the 6-episode limit; D5 records why it exists.
- **Q1 (not blocking).** Jumping to an episode inside a file: proposed as its own spec, not part of this one.

## 8. Gaps and drift

- **Built and matches Version 1:** filename ranges (`S01E01E02`, `S01E01-E02`, `S01E01-02`, `1x01-02`); all covered episodes owned; shared-file playback; watched state across episodes; editor range picker; Needs Review for unclear cases.
- **Built differently:** table is `media_asset_coverage`, not `asset_episode_coverage` (D4). The "Plays with E02" line on the details page became the "Same file as Episode N" chip in the episode strip. The details page itself was not checked (Claude Code to confirm).
- **Not built yet:** the Dr. Horrible re-attach (D6, WP1) and chapter-aware jumping (D7, future spec).
