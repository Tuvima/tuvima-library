# Spec: TV artwork, cast and matching

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-10-27 (locked by Shy) · **Related docs:** [Product overview](../product/overview.md) · bug log entries F21, F30, F34, F37, F38, F40, F41, F47 (and F31, F68 as neighbours)

## 1. Goal

When a TV show is matched to TheTVDB, everything that belongs to that show should arrive and land on the right level: the show's own cover, logo in your language, season pictures, a still for every episode, and the cast. Editing a season or episode should find its provider match on its own instead of telling you to "Use Match & Identity first". Separately, file names with a format tag such as "- AVC" must not stop a film from being found, and cast faces must look sharp.

## 2. Scope

**In scope**
- TV show, season and episode artwork: where it is found, which level it is saved against, which one is chosen as the main picture, and which language of logo wins (F37, F38, F47).
- The "No confirmed provider identity" message in the artwork lookup for seasons and episodes (F40).
- Cast on TV shows (F41) and cast face sharpness on details pages (F21).
- Cleaning format and quality tags out of file names before looking a title up (F34).
- A written-down way to diagnose "Steph Curry Masterclass" (F30), which needs Shy's real files.

**Out of scope**
- Home page cover size (F31) and the Home hero look (F68): same family as F21 but separate screens; they are handled in the Home work.
- Redesign of Editing Mode (F49) or the retail match picker (F50).
- Any change to the TheTVDB episode order logic beyond what is needed for the miniseries case (D5).

## 3. Decisions made

D1 to D5 are the investigation's recommendations. D6 to D9 record Shy's answers of 2026-10-27 ("Agree on all" to the recommended options).

- **D1. One shared identity for the whole show.** Seasons and episodes of a TheTVDB-matched show inherit the provider identity from the show when they have none of their own. Reason: Reliability (the show is already confirmed, so the user should never be asked to confirm it again at a lower level).
- **D2. Artwork is chosen, not just stored.** When TheTVDB offers several pictures of one kind, pick the best by your language first and then by quality, instead of whichever was stored last. Reason: Reliability and Presentation.
- **D3. Artwork is sorted into the right level by TheTVDB's own picture-type list**, which says whether a "poster" is a show poster or a season poster, not by the word "poster" alone. Reason: Reliability.
- **D4. One list of format tags**, kept in the settings file the video naming already uses (`config/videos.json`), and used by every place that cleans a file name. Reason: Maintenance (today there are two lists that disagree).
- **D5. Cast and episode matching must not be thrown away when the episode can't be paired.** The show's cast and the show-level identity are kept even when the specific episode cannot be matched to TheTVDB's order; that episode is then listed as needing a look. Reason: Reliability.

- **D6 (Q1 = C).** If only a French logo exists, use it, labelled by language, and let the person pick another in the editor.
- **D7 (Q2 = A).** Main picture: your language first, then highest TheTVDB score, then size.
- **D8 (Q3 = B).** TheTVDB faces under 300 px are kept as a last resort when nothing sharper exists.
- **D9 (Q4 = A).** The Steph Curry Masterclass stays in this spec as Local check L2; split out only if the cause is a separate feature.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal (show matched) | Show page: the show's own poster, a logo in the user's language, cast with sharp faces. Each season shows its own season poster. Each episode shows its still. |
| Season or episode picture lookup in Editing Mode | The provider gallery opens straight away for the show's TheTVDB entry. No "Use Match & Identity first" message when the show is already matched. |
| Genuinely not matched to TheTVDB | The lookup says plainly: "This show is not matched to TheTVDB yet. Open Match and Identity to choose the show." with a button that goes there. |
| Provider has no still for an episode | The episode shows the normal placeholder, and the lookup says "TheTVDB has no picture for this episode." (not a silent blank). |
| Only a French logo exists | The French logo is used only if no English or language-neutral logo exists; the editor labels each logo with its language so the person can choose. |
| Partly ready (artwork still downloading) | Placeholders fill in as pictures arrive; nothing is shown at the wrong level in the meantime. |
| Phone / tablet / desktop / TV | No layout change, except cast faces: on every device the face is drawn from a copy at least as large as the circle at the screen's sharpness. |

## 5. Data / model changes

None to the data structure. Existing libraries are pre-release: a reset and re-ingest is acceptable. A one-off "re-check artwork and cast for TV" action already exists in the editor; the build should make sure it also repairs shows already ingested with the wrong cover or missing stills.

## 6. Work packets

### Findings behind the packets (root causes)

These come from reading the code in `tuvima-library`. Items marked **Confirmed in code** are plain gaps. Items marked **Likely** fit the symptoms but need one real-data check (section 7) before the fix is final.

1. **F40, season/episode lookup message. Confirmed in code.** The lookup (`MetadataEndpoints.ProviderArtworkPicker.cs`, `ResolveProviderArtworkRequestContextAsync`) reads the provider and the TheTVDB id only from the season's or episode's own stored values. It never looks at the show above. A season or episode that has not itself been given a TheTVDB id and provider shows the message even though the show is matched. (A second gate in `MetadataEndpoints.TvdbArtwork.cs` also requires the item's own provider to say "tvdb".)
2. **F37, missing episode stills. Likely, same chain as F40/F41.** Stills are only downloaded for an episode that has its own TheTVDB episode id (`ImageEnrichmentService.EnrichTvdbImagesAsync`). That id is only written when the TheTVDB lookup finds the episode in TheTVDB's default order. If the lookup stops (`tvdb.json` flow: it stops outright when the episode isn't found), no episode id exists, so no still is ever fetched, and the artwork pass records "missing TheTVDB match" and never retries. The Continental (a short miniseries) and Shimmer and Shine (many short, split or re-ordered episodes) are exactly the shows where file numbering and TheTVDB's default order commonly differ.
3. **F41, no cast. Likely, same chain.** Cast is read from the show's TheTVDB record only inside that same episode lookup, after the "episode found" gate (`config/providers/tvdb.json`). So when the episode can't be paired, the cast is silently dropped even though the show itself matched. Cast is stored at show level, so this is a gate in the wrong place, not a missing feature.
4. **F47, a season picture used as the show cover. Likely.** The show's picture list from TheTVDB contains pictures of several levels, and the code classifies them only by the word "poster" in the type name (`ResolveTvdbArtworkType`). TheTVDB uses the same name "Poster" for show posters and season posters, so season posters can be filed as show covers. On top of that, each stored picture is immediately marked as the main one (`ProcessRemoteImageAsync`), so after storing up to three posters the last one stored wins, not the best.
5. **F38, French logo. Confirmed in code.** The TheTVDB path has no language rule at all. The film/TMDB path ranks by your language; the TheTVDB path takes the first three logos in TheTVDB's order and the last one stored becomes the main logo. Settings already hold a metadata language (default English); it is just not used here.
6. **F21, blurry cast faces. Confirmed in code.** The compact cast cards on details pages (`PersonCreditCard.razor`) ask for the smallest face copy (96 px) because the small-card mode is wired to "sm", but the circles are drawn at about 140 to 176 px. On a normal (1x) screen that is a 96 px picture stretched about 1.5 to 1.8 times, so it looks small and soft. The right-sized pictures spec itself lists these cards as needing the 320 px copy. A smaller second issue: TheTVDB faces under 300 px are rejected, so some people fall back to a thumbnail.
7. **F34, "Burnt (2015) - AVC". Confirmed in code.** The name cleaner used for lookups (`ReleaseTitleHints.cs`) has a hard-coded list of tags that does not include AVC (nor HDR, 4K, UHD, AAC, AC3, HDTV, DVDRip and others). The tag list in `config/videos.json` does include AVC, but only the separate video-folder cleaner uses it. Two lists, one incomplete.
8. **F30, Steph Curry Masterclass. Not diagnosable from the code.** Nothing in the code or config mentions it. Possible readings: it is a course or "Videos" item rather than a TV show; the folder or file naming doesn't give a season and episode; or the lookup stopped. See section 7 for exactly what to collect.

### WP1 — Show identity flows down to seasons and episodes
- **What it does:** When a season or episode has no provider identity of its own, the artwork lookup and the artwork refresh use the show's TheTVDB identity and then work out the season or episode id from TheTVDB. If the show really is unmatched, the message becomes the plain wording in section 4 with a button to Match and Identity.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/MetadataEndpoints.ProviderArtworkPicker.cs`, `MetadataEndpoints.TvdbArtwork.cs`, `src/MediaEngine.Providers/Services/ImageEnrichmentService.cs`, matching tests in `tests/MediaEngine.Api.Tests`, and the Dashboard message text.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test with a matched show and a bare season and bare episode returns TheTVDB candidates instead of the "No confirmed provider identity" message; a test with an unmatched show returns the new wording.

### WP2 — Put TheTVDB pictures at the right level and choose the best
- **What it does:** Classify pictures by TheTVDB's picture-type list (show versus season versus episode level) rather than by the word "poster"; choose the main picture by your language, then size and score; do not let storing a picture automatically make it the main one.
- **Files likely touched:** `src/MediaEngine.Providers/Services/ImageEnrichmentService.cs` (`ResolveTvdbArtworkType`, `ProcessRemoteImageAsync`, the TheTVDB branch of `EnrichTvdbImagesAsync`), `MetadataEndpoints.TvdbArtwork.cs` (same classification in the gallery), tests in `tests/MediaEngine.Providers.Tests`.
- **Depends on:** none (can run beside WP1; both touch `ImageEnrichmentService.cs`, so build one after the other if worktrees conflict)
- **Size:** Complex
- **Done when:** a recorded TheTVDB show response with show posters, season posters and logos in two languages ends with the show's own poster as the show cover, season posters on the seasons, and the English logo as the main logo (tests named for each behaviour).

### WP3 — Keep cast and show identity even when an episode can't be paired
- **What it does:** Move cast (and the show-level claims) in the TheTVDB lookup so they are saved once the show is found, before the episode pairing step. An unpaired episode is marked as needing a look, and the artwork pass retries when the episode id arrives later instead of recording a permanent skip.
- **Files likely touched:** `config/providers/tvdb.json` (flow order), `src/MediaEngine.Providers/Services/ImageEnrichmentService.cs` (retry rule), tests for the flow in `tests/MediaEngine.Providers.Tests`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a test with a show whose episode numbering doesn't match still saves the cast at show level; a later episode match triggers the still download without a manual refresh.

### WP4 — Sharper cast faces
- **What it does:** Compact cast cards ask for the copy that matches their real drawn size (small, 320 px), and a Dashboard guardrail checks drawn size against requested copy for credit cards. Optionally keep a small TheTVDB face (under 300 px) as a last resort instead of showing initials.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/PersonCreditCard.razor`, `PersonAvatar.razor`, the right-sized pictures guardrail test in `tests/MediaEngine.Web.Tests`.
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** a bUnit test renders a compact credit card and the face address asks for the 320 px copy (and 1x/2x list at the right sizes).

### WP5 — One list of format tags for file-name cleaning
- **What it does:** The lookup name cleaner reads the same tag list as the video naming settings (including AVC and the other missing tags), so "Burnt (2015) - AVC" searches for "Burnt" (2015).
- **Files likely touched:** `src/MediaEngine.Domain/Services/ReleaseTitleHints.cs`, `config/videos.json`, `src/MediaEngine.Domain/Configuration/VideoNamingConfiguration.cs`, tests in `tests/MediaEngine.Domain.Tests` and the processor tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a table of file names ("Burnt (2015) - AVC", "Movie.2019.1080p.HDR.AAC", "Show (2020) - HDTV") each cleans to the plain title and year.

### WP6 — Final check on the two named shows
- **What it does:** Add the Continental and Shimmer and Shine cases as saved TheTVDB responses (trimmed) and tests, then run the full CI-strength build and tests.
- **Files likely touched:** `tests/MediaEngine.Providers.Tests` fixtures and tests.
- **Depends on:** WP1, WP2, WP3
- **Size:** Standard
- **Done when:** the fixtures show the right show cover, season pictures, episode stills and cast; build and filtered tests pass.

## 7. Local checks

Build order note: WP1, WP4 and WP5 can start now. L1 should be run before WP2 and WP3 are finalised; if it shows a different cause, the build thread stops on that packet and reports rather than guessing.

These need Shy's real files or the live TheTVDB, which the cloud cannot reach.

- **L1. Diagnose before building WP2 and WP3 (live data).** On SHY-DESKTOP, with the Engine on a small test library containing only The Continental and Shimmer and Shine copied from `Z:\Development\testing\media\real_media`: open each show in Editing Mode, then collect (a) the Engine log lines mentioning TheTVDB for those two shows, and (b) the stored values for the show, one season and one episode (provider, TheTVDB show id, season id, episode id). What should be there: the same TheTVDB show id on the show, and a season id and episode id on the lower levels. Failure to report: any of those blank, or the log saying the episode was "not found" in TheTVDB. This confirms findings 2, 3 and 4 before the fix is final.
- **L2. Steph Curry Masterclass (F30).** Copy the Masterclass folder and its files into the small test library, ingest it, and send the Engine log lines for it plus a screenshot of where (if anywhere) it appears: Needs Review, Videos, Movies or TV. Please also send the folder and file names exactly as they are on disk. Failure: the item appears nowhere.
- **L3. After build: look and feel.** Open The Continental and Shimmer and Shine: show cover is the show's own poster; logo is English; each season has its poster; each episode has a still; cast row has sharp faces. Open any film with a "- AVC" style file name and confirm it finds its retail match by default.

## 8. Risks and open questions

- **Risks:** Changing which picture is "main" can change covers people already like; a user's own chosen picture must never be replaced (the existing "user override" rule stays). Changing the order of the TheTVDB lookup touches every TV show, so WP3 needs the broadest test coverage. Findings 2, 3 and 4 are inferred from the code; L1 may show a different cause for one of the shows, in which case that packet is revised before building.
- **Open questions** (each restated with context):
  1. **Q1. Logo language when no English logo exists.** For a show like The Continental, TheTVDB may only have a French logo. Options: **A)** use the French logo, labelled, rather than none; **B)** show the show's title as text instead (clean and always in your language); **C)** use A but let the person pick in the editor. Recommendation: **C** (never blank, never surprising, and you can fix it).
  2. **Q2. Which picture wins when several are good.** Options: **A)** your language first, then highest TheTVDB score, then biggest size; **B)** always the biggest size. Recommendation: **A** (an English poster of fair quality beats a huge foreign-language one).
  3. **Q3. Faces under 300 px.** Options: **A)** keep rejecting them and show initials; **B)** accept them as a last resort when nothing sharper exists. Recommendation: **B** (a soft face is friendlier than initials for a lead actor).
  4. **Q4. Scope of F30.** Options: **A)** keep it as a Local check inside this spec (L2); **B)** split into its own item once the cause is known. Recommendation: **A**, then split only if the cause turns out to be a separate feature (for example Masterclass belonging in Videos, not TV).

Answered by Shy on 2026-10-27: Q1 C, Q2 A, Q3 B, Q4 A. No open questions remain; Shy still has to lock the spec.

Original questions, one line each:
1. Q1: logo when only a French one exists: **A** use French · **B** show title text · **C** use French but pickable (recommended)
2. Q2: picture winner: **A** language, then score, then size (recommended) · **B** biggest size
3. Q3: small TheTVDB faces: **A** reject, show initials · **B** accept as last resort (recommended)
4. Q4: Masterclass: **A** keep in this spec as L2 (recommended) · **B** split now

## 9. Gaps and drift

- **Built and matches the spec:** n/a (new spec).
- **Built differently:** the right-sized pictures spec lists credit-card faces as the 320 px copy; compact cast cards use the 96 px copy (finding 6). Not a new decision, a drift to fix (WP4).
- **In the spec but not built yet:** all work packets above.

## Plain English summary

Most of the problems you saw on The Continental and Shimmer and Shine come from one weak link: the app only trusts a season or episode's own record, not the show it belongs to, and it only saves the cast and episode pictures if it can pair each episode with TheTVDB's order exactly. When it can't, it quietly gives up, which explains the missing cast, missing episode pictures and the unhelpful "Use Match and Identity first" message. Separately, the app picks a TheTVDB picture without checking language or level, so a French logo or a season poster can end up as the main picture. The blurry cast faces are a simple size mix-up from the recent pictures work, and "Burnt - AVC" fails because one list of format tags is missing AVC. I can fix all of that in the cloud, but two items need your real files first: a quick look at the two shows' stored data, and the Steph Curry Masterclass files, because nothing in the code explains that one.
