# Spec: Editor and details page revision (v4.1)

> **Status:** Locked (Shy, 2026-11-06) · **Version:** 1 · **Last updated:** 2026-11-06 · **Amends:** [Universal editor v4](universal-editor-v4.md) (Locked) · **Builds on:** [Look pass v2](look-pass-v2.md) (colors, 44 px controls, one chip and tab style, Material icons, bottom sheets on phones, one tooltip style) · **Related:** [Videos](videos.md), [Libraries](libraries-fixed-names-v3.md), [One tile design](one-tile-design.md) · **Mockups (approved by Shy 2026-11-06):** [header and tabs](https://claude.ai/artifact/RDZ19Fsob8ZA8ndDHAz9Xv), [Match](https://claude.ai/artifact/U1aZqEJqoRmxVUa1Zhg1g9), [details page and phone](https://claude.ai/artifact/2sBVbwd2CMVxqLZ9oVcDKW) (files in `engineering/design/mockups/editor-details-revision/`) · **Planning log:** `plans/editor-overhaul/details-page-items.md`

> **Version note (2026-11-12):** [universal-editor-v6](archive/universal-editor-v6-match-and-aligned-rows.md) (section 9) replaces the "editing is desktop-only" rule wherever this spec states it or hides an edit action on phones: item editing works on desktop, tablet and phone (`EditorDeviceRule.CanEditItems`), the Engine's permission check still decides who may edit, and TVs do not edit. Settings and Vault tools stay desktop-only. The Locked body below is unchanged.

> **Version note (2026-11-10):** parts superseded by [universal-editor-v5](archive/universal-editor-v5-header-navigation-and-supporting-files.md) (section 9): 1a items 2, 3, 5, 7, 9 and 16 and D1, D5, D14, D20 and D21 change: the header (cover, label, home, dropdowns; no kicker), the tabs, Quick facts gaining the file facts, Match with two sides and the current match first (Strong, Possible, Check with reasons; no placement), no Wikidata switch on Review, supporting files edited in their own editor, and the Extras row gaining groups and See all.

Per CLAUDE.md "Never" 10, this revision does not rewrite the body of the Locked v4 spec. It amends it; section 9 lists each v4 line that changes and Shy's recorded override.

## 0. Why

Shy's local check found the editor window "looks terrible and doesn't work": a see-through background (fixed in draft PR 670), Match that says nothing is matched for episodes already matched, a purple window, a crowded header with a pointless cover next to the show, a busy Details tab, and cramped tabs. At the same time the details page needs updates (season picker acts like a switch, episode expand/collapse wastes space, weak Watch button, no pictures for episodes the provider has no still for). The two surfaces share data, buttons and look, so one spec covers both.

## 1. Goal

An editor that is calm, near-black, and tells you plainly what an item is matched to and why. A details page that looks like a streaming service: one obvious Watch button, a real season dropdown, episodes that always have a picture, and extras where you expect them.

## 1a. Plain-English walkthrough

### The editor window
1. **Look.** Near-black like the details page (Look pass v2 tokens), purple only as an accent. No translucent background. Controls are 44 px.
2. **Header.** A static picture of the item and its title. The show is a fixed tile; Season and Episode are dropdowns inside the header (no separate rail row). Links to the provider records sit in the header with the provider's logo, and a Wikidata link. No "..." in the header.
3. **Tabs.** Details, Artwork, Match & Identity, Files, History (plus Contents on a show, season, album or series). Each tab icon has its own color, and the tab row has more padding.
4. **Footer.** Discard and Save changes on the right. **Delete is a red button at the bottom left**, always there when delete is allowed. It asks "Are you sure?" once and then happens at once (unchanged rule).
5. **Details** shows only what a person would edit (metadata, local fields, sorting). Facts that cannot be edited (quality, audio, size, location) leave this tab; they live in **About this** on the details page. Quick Facts beside the form is rethought to a short accent card of the few facts that help the edit (episodes owned, runtime, library mode).
6. **Artwork** is one clean row: role tabs (Poster, Background, Logo, etc.), then one row of actions (Upload, From URL, Choose from Library, Preferred, Remove, Refresh), then a large preview and thumbnails with more room. **Choose from Library** works for every kind, especially Universes (reuse pictures from the Universe's other things). For episodes, videos and films the picker also offers **frames extracted from the video**.
7. **Match & Identity** has two separate parts:
   - **Wikidata identity:** a card showing what the item is aligned to (label, QID, link) with Change. A switch on Review says whether the Wikidata alignment is kept or re-aligned.
   - **Retail match:** a provider dropdown (with logos) and labeled search fields that suit the provider and kind (for TheTVDB TV: Show, Season, Episode, Title; for books: title, author, ISBN). Fields are **filled in automatically from the file name**, with noise stripped (H.264, x265, Atmos, resolution, release-group tags). Anything left out is shown under "Left out of the search" with **Put them back**. Results are rows with a picture, title, details, the provider logo and a **match percentage pill**, and a **"Why 96%"** table (title, year, season and episode, duration, and so on) that opens in place. The first result is only called "Best guess" when it clears the confidence threshold.
8. **A matched episode shows as matched.** Today the editor reports episodes as not matched because it only looks for show-level IDs. After this change an episode with its own episode IDs, or whose file belongs to a matched show, shows as matched with its current match card (provider, logo, ID, link).
9. **Review changes** (existing step) shows Current → Proposed for the match, plus the Wikidata switch. **File moves follow the library's mode** (see D6): in a Managed folder a saved match moves and renames the file automatically and the save bar warns "This file will move when you save"; in a Read-only folder nothing ever moves.

### The details page
10. **Left of the hero** follows the streaming-service layout: the title as a logo (or text), one info line (rating, seasons, quality badge), **one wide solid Watch or Resume button** with a thin progress bar under it, a row of small labeled icons (My List, Like, Edit on desktop, More), a three-line description, genres, and one cast line. The Universe icon and a solid close X sit top right.
11. **Home hero uses the same button style** (it already shares `HeroActionRow`).
12. **Tabs are Overview and Cast.** The Details tab and the Related tab are removed. Facts move into **About this** on Overview. Cast uses the same square (rounded) person tile at one modest size on both the Overview line and the Cast tab.
13. **Seasons** use a real dropdown that opens over the page, lists season name and episode count, and switches the episodes in the same row. On a phone it is a bottom sheet. Season artwork is not shown or downloaded; the show poster is used.
14. **Episodes** are always visible (no expand/collapse anywhere on a details page, including Series rows). Each episode card shows its still and, below, its title, length and three lines of description. If the provider has no still, the card shows **a frame taken from the video itself** instead of the dimmed show backdrop.
15. **Series row** (movies, books, shows): no checkmarks. Owned items look normal; items not in your library are dimmed with a small "Not in your library" label.
16. **Extras** is a row under the episodes (or under the video or film): trailers, featurettes, clips and documents (homework, notes). Per-episode extras are supported. Documents open read-only in the Documents reader. Extras are named in the editor's Contents tab.
17. **Other videos** (a folder of videos such as a course) use video wording: a plain ordered list of videos, sub-folders as named sections, Play or Resume buttons with no "S1 E1", no "Seasons" or "Episodes". They have no Match tab, and the folder's picture is a portrait home-tile with a random frame.
18. **"..." menu** gets Mark as watched. The cover "..." and "+" overlay buttons are included (coordinated with the tile spec). Download stays with the native-app pass.
19. **Phone.** One column. Full-width Resume with progress bar; My List, Like, More (no Edit, no Delete); two-line description with "more"; vertical episode list (still left, text right); Extras scroll sideways; About this in two columns; Season as a bottom sheet.

**What stays the same:** desktop-only editing; nothing saves until Save changes (Review first; Delete is the one exception); provider facts read-only; deep links never open the editor; Engine decides permission; v4's rail-of-dropdowns idea is kept but moved into the header (D1).

**Scope boundaries:** app-wide color change belongs to Look pass v2; tile hover, size and pills belong to the tile spec; menu padding LC-26 belongs to the local-check combined fix; Libraries and Activity items stay in their threads; per-profile copies of document extras are backlog; Download is the native-app pass.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | Show is a static tile; Season and Episode are dropdowns inside the header. No separate rail row, no header "..." (Shy, Q60 to 66). |
| D2 | Window uses Look pass v2 near-black. Purple is accent only. One color per tab icon. |
| D3 | Delete is a red button, bottom left of the footer. Instant after "Are you sure?" |
| D4 | Details tab shows editable things only. Quality, audio, size and location move to About this on the details page. |
| D5 | Match splits into Wikidata identity and Retail match. Retail search uses labeled, auto-filled, noise-stripped fields; results show provider logo, percentage and a "why". |
| D6 | **Override of a Locked rule (deliberate, Shy 2026-11-06):** CLAUDE.md and v4 say a match never moves files unless its own switch is on. New rule: in a **Managed** folder a saved match moves and renames the file automatically, with an amber warning in the save bar; in a **Read-only** folder nothing moves. The per-match switch is removed. |
| D7 | Episode "matched" state also recognizes episode keys (`tmdb_episode_id`, `tvdb_episode_id`, `tvdb_season_id`) and walks a file up to its show. |
| D8 | Episodes without a provider still get a frame from the video: early frames (10 to 20%), dark and flat frames rejected, stored in the derived cache (never beside media) with a "generated" source so a provider still replaces it on the 90-day retry. Frames also appear in the artwork picker for episodes, videos and films. |
| D9 | Season artwork is no longer downloaded or shown; the show poster is used. |
| D10 | Details page tabs: Overview and Cast. Related and Details tabs removed. |
| D11 | No expand/collapse anywhere on a details page. |
| D12 | Series row: no checkmarks; missing items dimmed with "Not in your library". |
| D13 | One wide solid Watch/Resume button; all other actions are small labeled icons. Home hero matches. |
| D14 | Extras row under episodes/videos/films; trailer lives there; per-episode extras; documents read-only. |
| D15 | Other videos use video wording (amends videos.md D4's "sections/episodes"). |
| D16 | Cast tiles: one square rounded tile at one modest size everywhere. |
| D18 | **Amendment (Shy, 2026-11-06, local walk item 134):** Home hero books show a much larger cover. "Other videos" use a frame taken from the video as the full-width background, on the home hero and on their own details page, never a portrait cover shrunk inside the picture. Uses the WP8 frame service. |
| D19 | **Build default (2026-11-06, frame stills):** an episode TheTVDB has no picture for gets one frame from its own video, generated the first time its picture is looked for and kept as a normal picture from the source "generated" (listed in the artwork picker, never written beside the media). The 90-day recheck stays; when a provider still arrives it becomes the preferred picture and the frame stays as a choice. Same frame service as Other videos. |
| D20 | **Build defaults (2026-11-06, match screen):** TV percentage pills compare the file name with each show (name, plus year when both have one) and each episode (season and episode numbers first, title second); "Best guess" appears only at 60% or more; the Why table lists the per-fact scores the Engine already returns; provider logo is the first-letter badge until a plugin supplies an image; the Wikidata switch is "Check the Wikidata link again", on by default (today's behaviour), and off keeps the link exactly as it is. |
| D21 | **Build defaults (2026-11-06, header and Details):** header links carry the provider logo (letter fallback) and the header background is the plain window color (no purple wash); the Details tab lists only the fields you can change, since the header and Quick Facts already carry provider facts (viewers who cannot edit still see them). The family dropdowns stay as one slim row directly above the header for now (moving them inside the header, and the Artwork tab's single action row, are left for the side-by-side pass against the mockups, which needs a live copy). Phone: editing is desktop-only, so the phone layout is the details page, built in earlier waves. |
| D17 | Universe icon (already built) stays top right of the hero; Edit appears only on desktop. |

## 3. Work packets

Cloud-built, at most 2 helper agents at once. Packets 1 to 3 can run first; the others depend as shown. Size in brackets: M = Mechanical, S = Standard, C = Complex.

- **WP1 Window look and header [S]** (PR 670 already holds the blank-background CSS). Look pass v2 tokens, header with static Show tile and Season/Episode dropdowns, per-tab icon colors, tab padding, links with logos plus Wikidata link, remove header "...", Delete button at bottom left of footer. *Depends on:* none.
- **WP2 Details tab and Quick Facts [S].** Editable-only fields, rethought Quick Facts card. *Depends on:* WP1.
- **WP3 Artwork tab [S].** One clean action row, larger preview and thumbnails, Choose from Library for every kind including Universes (trace why the button did nothing on the Spirited Away page and fix), picker shows video frames for episodes, videos and films. Uses the existing `ArtworkWorkspace`, `ArtworkAssetBrowser`, `ArtworkAssetPickerDialog`. *Depends on:* WP1, WP8 for frames.
- **WP4 Matched-state fix [S].** Add episode keys and tvdb_season_id, file leaves walk up to the show (`MediaEditorOwnedChildReadService`, `EditorFamilyState`), regression test with a generated test show. *Depends on:* none.
- **WP5 Retail match UI and scoring [C].** Provider dropdown with logos, labeled fields per provider and kind, auto-fill and noise stripping from the file name, "Left out of the search" and Put them back, percentage pills for every result including TV episodes (currently none), "Why" table from the existing per-field scores, "Best guess" only above the confidence threshold. *Depends on:* WP4.
- **WP6 Wikidata identity card and Review [S].** Separate Wikidata section with Change, Review switch for keeping or re-aligning alignment (today a retail save silently resets automatic alignment). Fix stale context after an in-session re-match. *Depends on:* WP5.
- **WP7 Library-mode file moves [C].** Managed folders move/rename on a saved match with the amber save-bar warning; Read-only never moves; remove the per-match switch; update guardrail and docs. Security-sensitive: Fable review required. *Depends on:* WP6.
- **WP8 Video frame stills [C].** Reuse `VideoStillFrameService` for TV episodes (and films) when a stand-in would be shown; queue a frame pick at identify time or in the background; store as a generated EpisodeStill; respect Kids and Vault rules; skip spoiler-risky final frames. *Depends on:* none.
- **WP9 Details page hero and Watch button [S].** HBO-style left side, one wide Watch/Resume, labeled icon row, three-line description, genres, one cast line, solid close X (about 70% opaque, thin outline, larger target; LC-23), Mark as watched in "...". Home hero matches. *Depends on:* none.
- **WP10 Tabs, About this, Cast, Series row [S].** Remove Details and Related tabs, add About this, one square cast tile, remove checkmarks and dim missing items, remove all `AppCollapsibleRow` expand/collapse (`DetailPrimaryModule.razor`, `DetailSequenceRowRule.cs`, `MusicAlbumSeriesRail.razor`, `SequencePlacementPanel.razor`, tests). *Depends on:* WP9.
- **WP11 Season dropdown and episodes [S].** Real dropdown (LC-22) over the page, bottom sheet on phones, keyboard support; episode cards show three-line descriptions; stop downloading and showing season artwork, verify the season page, editor Contents rows and Artwork tab still look right. *Depends on:* WP10.
- **WP12 Extras row [S].** Extras under episodes/videos/films, per-episode extras, documents open read-only in the Documents reader, naming in Contents. Builds on `ExtrasSection.razor`. *Depends on:* WP10.
- **WP13 Other videos wording [M]/[S].** Video wording across the roughly 20 places that say Show/Season/Episode for a video folder; no Match tab; home-tile folder picture with a random frame; amend `videos.md` with a wording note. *Depends on:* WP10.
- **WP13b Home hero pictures [S] (amendment D18).** Larger book covers in the home hero; Other videos use a video frame as the full-bleed background on the home hero and on their details page (reuses WP8). Also fixes local-check item 138: videos other than movies and TV (Videos, Other videos, YouTube) show on the home hero as books ("Continue Reading", Book tag). Handle the video kinds in `PhoneHeroPresentation.cs` (primary label switch, near line 160) and `CinematicHeroCarousel.razor` (near line 249) with a Video tag and Play or Resume plus time left; an unknown kind must never fall back to Reading, and a guardrail test enforces that. *Depends on:* WP8, WP9, WP13.
- **WP14 Phone layout [S].** One-column layout and bottom sheets as in the phone mockup; no Edit or Delete on phones. *Depends on:* WP9 to WP12.
- **WP15 Docs and cleanup [M].** Update public docs (tuvima-docs), CLAUDE.md "Never" 1 wording on matches and file moves, spec statuses, batch list. *Depends on:* all.
- **WP16 Fable review and visual proof [C].** Fable reviews WP5, WP6, WP7 and WP8 (matching and file moves) and the final diff. Visual proof via standalone harness; cloud cannot run the app, so real screenshots are Local checks. *Depends on:* all.

## 4. Data and model changes

- Wire: match candidates carry `score` and per-field `breakdown` for TV episodes too (Contracts tests and fixtures updated).
- Episode stills: new `generated` source value for the still, stored in the derived cache; no schema change expected beyond the source value (verify at build).
- Remove the per-match "move files" setting from the match request and its Review switch.
- No data-store migrations (pre-release data; resets are fine).

## 5. Local checks (appended to `engineering/validation/batch.md` at build time)

L1 Open Edit on a matched episode: it shows as matched with provider, logo, ID. L2 Search a retail match for a file named with H.264/Atmos tags: terms are stripped and shown under "Left out". L3 Pick a result: percentage and "Why" match the numbers. L4 Managed folder: save a match, file moves and renames, warning shown. L5 Read-only folder: nothing moves. L6 Episode with no provider still shows a video frame; it is replaced by a provider still later. L7 Season dropdown opens over the page and switches episodes; no season posters. L8 Details page on light artwork: the close X is clearly visible. L9 Phone (real device or narrow browser): the layout matches the phone mockup. L10 Course folder: no Seasons, Episodes or S1 E1 anywhere. L11 Extras (existing L1 to L6 stay on the batch list). L12 Artwork Choose from Library on a Universe thing. L13 Home hero (also item 138: "Test Drive (Part 1)" shows a Video tag and Resume, never Book or Continue Reading): a book's cover is large; an Other videos item shows a full-width video frame, and so does its details page.

## 6. Risks and open questions

- Frame extraction runs ffmpeg per episode; on a big library this is background work with a cap. Spoiler or black frames are filtered, not eliminated.
- Removing the file-move switch changes a Locked rule, so every Managed-folder match can move files. Mitigation: the save-bar warning and Review row.
- Dropping Related and Details tabs removes content some people may use; About this keeps the facts.
- No open questions: Shy answered 79 to 89 and 120 to 122.

## 7. Gaps and drift

The Locked v4 body is not rewritten. Lines changed by this revision: v4 section 1a items 2, 3, 4, 7, 8 (header, tabs, Match, Artwork); "a match never moves files unless its own switch is on" (D6); Delete location. videos.md D4 wording (D15). CLAUDE.md "Never" 1 mentions the match switch and the rail; update its wording at WP15.

## Plain English summary

The editor becomes a calm near-black window with a simple header, a Delete button at bottom left, and a Match tab that clearly separates the retail match from Wikidata, shows which provider and how sure we are, and fills in the search for you. The "episodes show as not matched" bug is fixed, and a match in a managed folder now moves the file for you (with a warning) while read-only folders never move anything. The details page gets a streaming-style top with one big Watch button, a real season dropdown, episodes that always have a picture, extras underneath, and a clean phone layout.
