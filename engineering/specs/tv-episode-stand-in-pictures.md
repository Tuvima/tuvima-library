# Spec: TV episode stand-in pictures

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-03 (locked by Shy) · **Related docs:** [Product overview](../product/overview.md) · [TV artwork, cast and matching](./tv-artwork-cast-and-matching.md) · research: TheTVDB versus TMDB comparison on 17 shows (2026-11-03)

## 1. Goal

Every TV episode looks filled in, even when TheTVDB has no picture for it. A missing episode still is replaced by a clearly marked stand-in made from the show's own artwork, and Library checks again every 90 days for the real one. Why it matters: shows like Shimmer and Shine (115 of 143 episodes have no TheTVDB still) look broken with blanks, but a wrong picture that looks real would be worse.

## 2. Scope

**In scope**
- A stand-in picture for any episode (regular or special) with no TheTVDB still.
- Marking the stand-in so the screen shows it is not the real image, and so the 90-day retry can replace it.
- Showing, in the metadata list, that the episode picture is a stand-in.

**Out of scope**
- Any TMDB use for TV (no fallback; see D1).
- Changing episode numbering, order or titles.
- Stand-ins for movies, music, books or comics.
- Editor picking: a person can still choose their own episode picture; that always wins.

## 3. Decisions made

- **D1. TheTVDB stays the only TV source; no TMDB fallback** (Shy, 2026-11-03). Reason: Maintenance and Reliability. Matching two catalogues cost a multi-step pairing ladder and risked a wrong picture on the wrong episode. Research on 17 shows: TheTVDB is nearly complete for regular episodes; the real gaps are Shimmer and Shine and the Stephen Curry MasterClass. Supersedes the "TMDB fills gaps" idea from earlier the same day. Research kept in project files in case it is revisited.
- **D2. Stand-in look** (Shy, 2026-11-03: "agree on all"): the show's landscape backdrop, slightly dimmed, with the episode number over it. If the show has no backdrop, a Library tile.
- **D3. Specials get the same stand-in** (Shy, 2026-11-03). Reason: most TheTVDB gaps are specials.
- **D4. Retry every 90 days** (Shy, 2026-11-03), using the existing 90-day refresh cycle.
- **D5. The stand-in is data, not a file.** It is recorded on the episode as "stand-in, retry after date" and drawn at display time. It is never saved as the episode's artwork and never written into media files, folders or sidecars. Reason: Reliability (retry can tell it from a real still) and Privacy (nothing changes on the user's disk).
- **D6. A real still always replaces it** (from TheTVDB on retry, or chosen by the person in the editor) with no confirmation.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Episode has a real still | Unchanged. |
| Episode has no still, show has a backdrop | Dimmed show backdrop with the episode number over it, and a small tag "Show image" (info tooltip: "TheTVDB has no picture for this episode yet. Library checks again every 90 days."). |
| Show has no backdrop either | A Library tile with the episode number and the same tag. |
| Special | Same as above, number shows as the special's label (for example S1). |
| Metadata list for the episode | The picture row says "Stand-in from show image" with the next check date; it turns to "TheTVDB" when a real still arrives. |
| Editor (desktop) | The artwork picker lists the stand-in as not selectable; choosing a real picture replaces it. Nothing is saved until Save changes, per the universal editor. |
| Partly ready (artwork still downloading) | Stand-in shows until the real still arrives; no flicker back and forth. |
| Phone / tablet / desktop / TV | Same look at every size; the episode number scales with the tile. |

Square corners and proper capitalisation, per the design system. No toasts.

## 5. Data / model changes

- Each episode records whether its picture is a stand-in and the date of the next check. Pre-release: a reset and re-ingest is acceptable, no migration.
- The existing 90-day refresh already re-fetches TheTVDB pictures; the stand-in flag clears when a real still is stored.
- No new Engine-to-Dashboard fields beyond a "picture is a stand-in" marker on the episode (Contracts tests and fixtures updated with it).

## 6. Work packets

### WP1 — Record the stand-in state
- **What it does:** when episode artwork enrichment finds no TheTVDB still, mark the episode as "stand-in, retry after 90 days" instead of recording a permanent miss; clear the mark when a real still is stored or the person picks one. Includes specials.
- **Files likely touched:** `src/MediaEngine.Providers/Services/ImageEnrichmentService.cs`, storage for the marker (to be confirmed), `config/hydration.json` reuse of the 90-day setting, tests in `tests/MediaEngine.Providers.Tests`.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test with a TheTVDB episode that has no image marks it as a stand-in with a next-check date; a second test shows a later run with an image clears the mark; a test shows a user-chosen picture is never overwritten.

### WP2 — Carry the marker to the Dashboard
- **What it does:** expose "picture is a stand-in" and the next-check date on episode details and tiles.
- **Files likely touched:** `src/MediaEngine.Contracts/` (episode wire type), Engine detail composition (to be confirmed), Contracts tests and fixtures (`pwsh -File tools/Update-ContractFixtures.ps1`).
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** the contract test and refreshed fixture include the marker; an Engine test returns it for a stand-in episode and not for a real still.

### WP3 — Draw the stand-in
- **What it does:** the dimmed backdrop with episode number and the "Show image" tag; Library tile fallback; metadata-list row text; editor artwork picker treats it as not selectable.
- **Files likely touched:** `src/MediaEngine.Web/Services/MediaTiles/MediaTileComposerService.cs`, `src/MediaEngine.Web/Components/Details/DetailPage.razor`, `src/MediaEngine.Web/Components/Watch/VideoContextPanel.razor`, a shared `App*` primitive and component-scoped CSS (all to be confirmed), bUnit tests.
- **Depends on:** WP2
- **Size:** Complex
- **Done when:** bUnit tests show the tag, the episode number and the Library-tile fallback; visual checks at phone and desktop width per `src/MediaEngine.Web/CLAUDE.md` show the stand-in is clearly different from a real still.

### WP4 — Docs
- **What it does:** public docs explain why stand-ins exist, why TheTVDB is the only TV source, and what the 90-day check does (docs explain the why).
- **Files likely touched:** `tuvima-docs` TV artwork pages, `engineering/README.md` index.
- **Depends on:** WP3
- **Size:** Mechanical
- **Done when:** the docs build passes with the new text.

## 7. Local checks

- **L1.** On the real library, open Shimmer and Shine Season 4: every episode shows the dimmed backdrop with its number and a "Show image" tag. Failure: blank tiles, or a stand-in that looks like a real still.
- **L2.** Open the Stephen Curry MasterClass lessons: stand-ins appear (backdrop, or Library tile if none), and nothing is written next to the media files.
- **L3.** Pick a real picture for one stand-in episode in the editor: it replaces the stand-in and stays after the next 90-day check.
- **L4.** Open a special on a show with a TheTVDB gap: it shows a stand-in with the special's label.

## 8. Risks and open questions

- **Risks:** a whole season of identical stand-ins looks repetitive (accepted; the tag and number keep it honest). Retry cost is small: only episodes with a stand-in are rechecked. Obscure shows are probably patchier on TheTVDB than the 17 tested.
- **Open questions:** none. Shy answered placeholder look, specials and TMDB on 2026-11-03.

## Plain English summary

Where TheTVDB has no picture for an episode, Library shows a dimmed picture from the show itself, with the episode number and a small "Show image" tag, so the episode looks filled out but is clearly not the real image. It never touches your files, tries again every 90 days, and a real picture replaces it as soon as one exists. TMDB is not used for TV at all.
