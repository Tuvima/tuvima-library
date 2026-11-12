# Spec: Video player follow-ups

> **Status:** Built · **Version:** 1 · **Last updated:** 2026-11-12 (locked by Shy 2026-11-11; built 2026-11-12, awaiting Local checks in [the batch list](../../validation/batch.md)) · **Related docs:** [Unified player v2](unified-player-v2.md) (this spec amends its D5, D6, D9, D10, D12, D13, D14 and D25 where stated) · [Player controls v3](../player-controls-v3.md) · [Global bookmarks](../global-bookmarks.md) · [Design system](../../design/design-system.md) · Mockups: [`../design/mockups/video-player-followups.html`](../../design/mockups/video-player-followups.html) · Issues: #882, #883, #884, #888

## 1. Goal

Fix what the local check of 11 November found in the video player: controls and the paused rating are too small, the movie cover is the wrong shape, bookmark marks are hard to see, the volume slider covers its neighbors, Audio & subtitles rows wrap, the subtitle style tiles are muddled, Find subtitles finds nothing and does not say why, and Playback information is empty.

## 2. Scope

**In scope**
- The video player's control row sizes, the cover in the bar, the rating shown while paused, bookmark marks on the timeline, and the volume slider (#882).
- The Audio & subtitles rows and the Subtitle appearance view (#883).
- Find subtitles: a list of results to pick from, and a plain reason whenever there is none (#884).
- Playback information: filled for video, music and audiobooks, with Copy details (#888).

**Out of scope**
- Bookmark rules (Mine as the default, Global for movies and TV): the thread "Add Global bookmarks for movies and TV" owns them. This spec only changes how a mark is drawn on the timeline.
- The shared dropdown and dialog components (issues #900 and #901, bundle A). The language choice in Find subtitles uses whatever the shared dropdown is at build time.
- New subtitle sources, lyrics, the phone touch layout beyond the sizes listed, TV and car clients.

## 3. Decisions made

- **D1. Bigger control row (amends v2 D6).** Icons 28 px in 48 px buttons (were 24 in 44). Play/pause 40 px in a 56 px button (was 32 in 48). The row is 72 px tall (was 60). All side buttons are equal and aligned; Play stays at the exact center. The same sizes apply to the music and audiobook bar, because all players share one set of size values (Maintenance: one place to change). Tablet uses the same sizes. The phone's collapsed bar keeps 24 in 44; the phone's expanded player uses 28 in 48 and Play 40 in 56. At 1024 wide the title shortens before any button shrinks.
- **D2. Movie cover is portrait (amends v2 D5).** The artwork slot is 64 px tall. A movie shows its poster as a 43 × 64 portrait, with no landscape box and no blurred fill. A TV episode keeps its landscape still (114 × 64); when an episode has only the show's poster, that poster is shown portrait too. Music and audiobooks stay square (64 × 64).
- **D3. Rating while paused (amends v2 D25).** "Rated PG-13" is 28 px bold with a 5 × 40 px purple mark (today it is body-text size with a 4 × 28 mark). Phone: 20 px. Position and fade timing are unchanged.
- **D4. Bookmark marks sit on the timeline (amends v2 D9).** Each mark is a 10 px round bead centered on the line itself, with a thin dark ring so it reads on both the played and unplayed part: purple for the profile's own, white for everyone's (same colors as today). Hover or keyboard focus grows it to 14 px and shows a card with the frame from that moment, the name, the time and, on someone else's, who made it. Click jumps there. A bead that was just added pulses once (skipped under reduced motion). Beads hide while the handle is dragged. Which bookmarks are drawn follows the Bookmarks panel's choice, as today. Shy (2026-11-11): the panel and the dots show only the profile's own bookmarks unless the person chooses Everyone; that default (Mine) is set by the Global bookmarks thread, and this spec must not draw anyone else's dots before that choice is made.
- **D5. Volume is vertical with no panel (amends v2 D10; returns to the direction of v3 D6).** Hover or keyboard focus on the speaker shows a 96 px tall slider directly above it: a 4 px line and a 12 px round handle, with only a faint dark outline for contrast over bright pictures, and no background box. It covers nothing to its right. It stays while the pointer or focus is on it and for 400 ms after. Up and Down change by 5. Click on the speaker mutes and unmutes. Touch keeps the small Volume panel. Same control in the music and audiobook bar.
- **D6. Audio & subtitles rows stay on one line (amends v2 D12).** A long label is shortened with "…" and the full text appears in the app tooltip on hover or focus. The detail line under an audio row follows the same rule. Rows keep their 44 px height. The check stays at the right.
- **D7. Subtitle style tiles (amends v2 D13).** Three tiles of the same size, with the sample "Aa" on the first line and the name on the second (today they run together as "AaDefault"). The Text size row is removed from this view because the Default and Large text tiles already cover it; a Text size choice (Small, Medium, Large) is added to Custom appearance so Small is not lost.
- **D8. Style and Background are independent (amends v2 D13).** A style tile no longer changes the Background choice, and the Background choice no longer unticks the tiles. Default = medium white text; Large text = large white text; High contrast = bold white text, with a solid black box when Background is Box and a heavy black outline when it is Shadow or None. Today every tile forces a box, so choosing None leaves no tile ticked and the tiles look switched off.
- **D9. Find subtitles is a list (amends v2 D12; follows the earlier approved "Find another" design).** Opening the view runs one search and lists up to the provider's results: language, a match tag (Same release, Different release, Hearing impaired), and the release name on one line. "Use this" downloads that one and switches to it at once. The one in use is marked "In use now" with "This one is wrong", which removes it and never offers it again. "Add a subtitle file…" stays in the footer. The single "Find better subtitles" button, which quietly picked one result by itself, is removed. The Engine already offers the list and the choose action (the Lyrics panel uses them); the video player never did.
- **D10. Search language.** The search uses the profile's preferred subtitle language (Settings › Playback), falling back to the app language. A language choice at the top of the view searches another language. From the code, today's search appears to use the title's own language and falls back to English, which would explain an empty result for a foreign-language film; the build confirms this first.
- **D11. A plain reason every time.** Each outcome has its own sentence in the view, written in the Dashboard's wording (not the Engine's internal sentence), and an administrator also gets a link to where it is fixed:

  | Outcome | What the view says |
  |---|---|
  | Nothing found | "No English subtitles were found for this movie." plus "Try another language, or add a subtitle file." |
  | No source switched on | "No subtitle source is switched on." (administrators: link to Plugins) |
  | Source key missing or refused | "SubDL did not accept its key." (administrators: link to Plugins) |
  | Title cannot be identified for a search | "This episode could not be matched for an online search." with what it needs |
  | Daily limit or too many requests | "SubDL has reached its limit for now. Try again later." |
  | Source unreachable | "SubDL could not be reached." with Try again |
  | Library set to never look anything up | "This library never looks anything up online." |
  | Not allowed for this profile | "Your profile cannot search for subtitles." |
  | Still searching after 20 seconds | "Still searching…" stays until a result or a reason arrives; the view never goes blank |
- **D12. No live search just to open the panel.** Today opening Audio & subtitles asks the subtitle source for results only to learn whether a source is switched on, which spends the source's daily allowance on every video. Opening the panel checks the setting without contacting the source; the source is contacted only when Find subtitles opens. (Performance, and Privacy: fewer outside requests.)
- **D13. Playback information content (amends v2 D14; Shy 2026-11-11: fill it).** A two-column list in sections. A line with nothing to say is left out.

  | Section | Lines | Where it comes from |
  |---|---|---|
  | File | Name (file name only, never the folder), Format · size, Overall bitrate | Known today except name and size, which the Engine reads but does not send to the player yet |
  | Video | Picture (label, width × height, codec and profile), Frame rate, Color (SDR, HDR10, HLG) | Known today except Color and profile. Color is already read from the file but not sent. Profile is not read yet |
  | Audio | Track in use (language, codec, channels), Quality (sample rate, bit depth, bitrate) | Known today |
  | Subtitles | Track showing and where it came from (in the file, a file beside it, SubDL, your file) | Known today |
  | How it is playing | Delivery (Playing the file as it is, Converting for this browser, Downloaded copy), Why (when converting), Connection (Home network or Remote, with speed), Buffer (seconds ahead, live), Playing on (browser and system) | Delivery, Why and Connection known today. Buffer and Playing on come from the browser |

  Music and audiobooks show File, Audio and How it is playing. Dropped from the proposal in the issue: a separate video-only bitrate (the Engine reads only the overall bitrate) and Dolby Vision (the Engine cannot tell it from HDR10 today, so it shows as HDR10).
- **D14. Copy details.** A button in the view's footer copies the list as plain text. It says "Copied" in place for two seconds; no toast. Nothing is sent anywhere.
- **D16. Shy's answers (2026-11-11, all as recommended).** Sizes are 28 in 48 with Play 40 in 56 (D1). Bookmark dots are **on by default**, with the "Show bookmarks on the bar" switch kept in the Bookmarks panel; this overrides Player controls v3 D8a (off by default). The Text size row is removed and Small / Medium / Large moves to Custom appearance (D7). Find subtitles is a list to pick from (D9). TV episodes keep the landscape still and movies get the portrait poster (D2).
- **D15. Why the panel was empty is not known yet.** The code builds this list from the playing file's facts and should always show at least Delivery and Connection, and its tests pass, so the empty panel on the real library needs to be reproduced. The build reproduces it first on a small test library and fixes the cause; if it cannot be reproduced in the cloud it becomes a Local check with the Engine log.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | The screens in the mockup, sections 1 to 4. |
| Empty | No bookmarks: no beads. No audio facts: "No audio information for this file" as today. Playback information with no facts at all: "No details are available for this file." |
| Loading | Find subtitles shows "Searching SubDL…" with the language choice usable. Playback information draws at once; Buffer fills in when the browser reports it. |
| Error | Find subtitles shows the D11 sentence and Try again where it helps. "Use this" that fails says "Could not download this one" on that row; the list stays. |
| Partly ready | A bookmark's frame not made yet: the hover card shows the neutral placeholder, then the frame. |

- **Phone:** collapsed bar unchanged; expanded player uses D1's sizes. Find subtitles is one result per row with a full-width "Use this". Playback information is a sheet; Copy details is a full-width button. Volume uses the hardware buttons and the small panel.
- **Tablet:** as desktop, panels as sheets where they are today.
- **TV:** unchanged.
- **Vault titles:** same controls; Find subtitles follows the library's lookup setting.
- **Reduced motion:** no bead pulse and no bead growth animation (it still shows the card).

## 5. Data / model changes

- The playing file's facts sent from the Engine to the player gain: file name, file size, color format, and video profile when the file reports one. Additive; existing clients ignore them.
- Video profile needs one more value read from the file. Files already in the library show it after their next scan; until then the line is left out. No reset needed.
- The subtitle search accepts a language. No new stored data.
- No change to how bookmarks or subtitle looks are stored. A stored look that matches no tile shows no tile ticked, as today.

## 6. Work packets

### WP1 — Control sizes and portrait cover
- **What it does:** D1 and D2.
- **Files likely touched:** `src/MediaEngine.Web/wwwroot/tuvima.tokens.css` (the four `--playback-*` size tokens, row height), `Components/Player/PlaybackShell.razor.css`, `PlaybackIconButton.razor.css`, `PlaybackRelativeSkipButton.razor.css`, `PlaybackToolRow.razor.css`, `PlaybackIdentity.razor` and `.css`, `Services/Playback/PlaybackArtworkUrl.cs`, `PlaybackFullPlayer` for the phone's expanded sizes; bUnit tests for the identity slot; the player size guardrail if one pins 44/24.
- **Depends on:** none
- **Size:** Standard
- **Done when:** computed sizes at 1024, 1280 and 1440 are 48/28 and 56/40, Play's center equals the bar's center, a movie's slot is 43 × 64 with no blurred fill, an episode's is 114 × 64, and nothing in the bar wraps or overlaps at 1024.

### WP2 — Vertical volume
- **What it does:** D5, in the video, music and audiobook bars.
- **Files likely touched:** `Components/Player/PlaybackVolumeControl.razor` and `.css`, `wwwroot/js/playback-volume.js`, the slider it hosts, `tuvima.tokens.css` (`--playback-volume-slider-*`); tests for open, close and keys.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a Playwright check shows the slider's box lies entirely above the speaker and overlaps no other tool, Up/Down change the level by 5, and no background box is drawn.

### WP3 — Bookmark beads on the timeline
- **What it does:** D4. Drawing only; which bookmarks exist and which are shown is untouched.
- **Files likely touched:** `Components/Shared/PlaybackSeekRail.razor` and `.css`, the quick-look card (frame from the existing bookmark frame address), `VideoPlaybackHost.razor` only to flag the newest bookmark for the pulse; tests in `PlaybackSeekRail` suite.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** beads are vertically centered on the rail within 1 px, the hover card shows frame, name and time, the newest bead has the pulse class once, and beads are hidden while dragging.

### WP4 — Paused rating
- **What it does:** D3.
- **Files likely touched:** `Components/Player/VideoRatingCard.razor.css`.
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** the rating's computed size is 28 px on desktop and 20 px at phone width.

### WP5 — Audio & subtitles rows and Subtitle appearance
- **What it does:** D6, D7, D8.
- **Files likely touched:** `Components/Player/AudioSubtitlesChoicesView.razor` and `.css`, `SubtitleAppearanceView.razor` and `.css`, `SubtitleCustomAppearanceView.razor`, `Services/Playback/SubtitleAppearance.cs`, `SubtitleStyleCss` (outline for High contrast), `tests/MediaEngine.Web.Tests/AudioSubtitlesPanelTests.cs` and the appearance tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** no row in either column is taller than 44 px (62 px with a detail line) with long labels, the full label is in the tooltip, the three tiles have equal width and height with two lines each, choosing None leaves the ticked tile ticked, and each tile plus each Background gives the look in D8.

### WP6 — Find subtitles list and plain reasons
- **What it does:** D9 to D12. First step: confirm from the code and a test which language today's search uses and record it in this spec's Gaps and drift.
- **Files likely touched:** `Components/Player/SubtitleFindView.razor` and `.css`, `AudioSubtitlesPanel.razor`, `VideoPlaybackHost.razor` (replace `RefreshSubtitlesAsync` and the on-open probe), `Services/Integration/EngineApiClient.Playback.cs`, `src/MediaEngine.Providers/Workers/TextTrackEnrichmentWorker.Alternatives.cs` (language, a status for every outcome, a no-contact "is a source on" answer), `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs`, `src/MediaEngine.Contracts/Playback/TextTrackResponses.cs` with its contract tests and fixtures, `SharedStrings` resources.
- **Depends on:** WP5 (same panel)
- **Size:** Complex
- **Done when:** with a fake subtitle source, the view lists results and "Use this" switches the playing subtitles without restarting the picture; each row of the D11 table has a test that produces its sentence; opening Audio & subtitles makes no request to the source; a search in another language sends that language.

### WP7 — Playback information
- **What it does:** D13, D14, D15. Reproduce the empty panel first.
- **Files likely touched:** `src/MediaEngine.Contracts/Playback/PlaybackDtos.cs` with contract tests and fixtures (`tools/Update-ContractFixtures.ps1`), `src/MediaEngine.Api/Services/Playback/PlaybackCapabilitiesService.cs`, `src/MediaEngine.Processors/FFmpegService.cs` and `MediaProbeResult` (profile), `src/MediaEngine.Web/Services/Playback/PlaybackInformationRows.cs`, `Components/Player/PlaybackInformationView.razor` and `.css`, `PlaybackSettingsPanel.razor` (footer), a small script for buffer and browser name, `tests/MediaEngine.Web.Tests/PlaybackSettingsAndSpeedTests.cs`.
- **Depends on:** none (different files from WP6; both touch Contracts, so they merge one after the other)
- **Size:** Standard
- **Done when:** playing a generated sample video lists every section with values, a music track lists File, Audio and How it is playing, the folder never appears in the text, Copy details puts the same lines on the clipboard, and the cause of the empty panel is written into Gaps and drift.

### WP8 — Docs, batch list and issues
- **What it does:** Updates the Watch player page and the subtitles guide in `tuvima-docs` (including why the volume is vertical and why Find subtitles is a list), `docs/product/presentation-rules.md`, adds the Local checks to `engineering/validation/batch.md` with the issue numbers, and posts closure notes on #882, #883, #884 and #888.
- **Files likely touched:** as listed.
- **Depends on:** WP1 to WP7
- **Size:** Mechanical
- **Done when:** the docs build passes and each issue has its closure note and `state:built-awaiting-check`.

**Waves:** 1: WP1 and WP5 · 2: WP2 and WP3 (WP4 done inline) · 3: WP6 and WP7 · 4: WP8.

## 7. Local checks

- **L1.** Play a movie at 1024, 1280, 1440 and on a phone. The cover is a portrait poster, the buttons are visibly larger with Play the largest, and nothing overlaps. Failure: a landscape cover, or buttons that wrap.
- **L2.** Pause a rated movie. "Rated PG-13" is easy to read from the sofa.
- **L3.** Add two bookmarks while playing. A bead appears on the line where you were and pulses once; hovering shows the frame, name and time; clicking jumps there.
- **L4.** Hover the speaker. A slim vertical slider appears above it and hides nothing beside it.
- **L5.** Open Audio & subtitles on a file with long track names. Every row is one line; the full name appears on hover.
- **L6.** Subtitle appearance: three equal tiles, two lines each. Choose None for Background; the tiles stay usable and the ticked one stays ticked.
- **L7.** Find subtitles on a movie and on a TV episode with the real SubDL key. A list appears; "Use this" switches subtitles at once. On a title with none, a plain sentence says why. Failure: an empty view or a technical sentence. If a reason appears that looks wrong for that title, note the sentence so the cause can be traced.
- **L8.** Playback information on a 4K HDR file, a file that needs converting, a music track and an audiobook. The lists are filled and Copy details pastes the same text.

## 8. Risks and open questions

- **Risks:** bigger buttons leave less room for the title at 1024 wide (the title shortens first; checked in WP1). A bare volume line can be hard to see over a bright picture (the dark outline is there for that; L4 checks it). The real cause of the empty subtitle search on Shy's library cannot be confirmed in the cloud, because the cloud has no subtitle key and cannot reach the source; D11 makes the reason visible so L7 settles it.
- **Open questions:** none. Shy answered Q1 to Q5 on 2026-11-11 (D16).

## 9. Gaps and drift (found while planning)

- **Built differently:** v2 D12 said Find subtitles would nest the existing "Find another subtitles" list. What was built is a single button that picks one result. D9 restores the list. Shy chose the list (D16).
- **Built differently:** v2 D10 chose a horizontal volume slider over v3's vertical one. The local check prefers vertical; D5 records the change.
- **Not confirmed:** why Playback information showed nothing (D15) and which language the subtitle search used (D10).

## 10. Build notes (added when built, 2026-11-12)

- **D3 rating weight.** The rating is 28 px at weight 600, not 700. The type rules keep 700 for page and hero titles, and the guard test counts every use. The size is what makes it readable.
- **D6 full name.** The full track name appears when you point at a shortened row. It does not appear on keyboard focus, because the shared overflow tooltip only works on hover.
- **D9 footer.** The video player has no subtitle file import, so "Add a subtitle file" is not offered and the "nothing found" sentence does not mention it. The "applies to everyone" line sits under the result list.
- **D9 release tags.** "Same release" is shown when the source's match score is 0.9 or higher. That score is mostly the release name overlap, with a small bonus for format and a small penalty for hearing-impaired files, so it is a strong hint and not a guarantee.
- **D10 confirmed.** Before this build the search used the title's own language, or English when none was stored, never the profile's. A language name the source did not know became "unknown" and found nothing. The search now uses the profile's subtitle language and says so when a language is not supported.
- **D15 findings.** Two real ways to an empty panel were found and fixed: music and audiobooks that play from a direct address never asked for the details, and the video player could open the panel before the details arrived (it now says "Loading details…"). A video whose details request returns nothing is still possible; L8 checks it.
- **D13 video profile.** The profile (for example "Main 10") is read when a file is scanned, so it appears for files scanned after this build.
- **D4 pulse.** The one-time pulse on a new bookmark is on the video timeline only; music and audiobooks show the dot without it.
- **D4 whose dots.** The Global bookmarks work merged first and made Mine the stored default, so a profile that never chose a list sees only its own dots; the Global list's dots appear only after the profile picks Global.
- **D4 existing profiles.** Dots are on for new profiles and for profiles that never saved a playback setting. A profile that saved any playback setting before this build has "off" stored and keeps it until it switches Show on timeline on. No conversion of old settings is made (pre-release rule); a fresh local check starts with dots on.
- **D1 phone.** The phone's full-screen Now playing keeps its existing 64 px Play disc, which is already larger than D1's 56.
- **D10 language list.** The subtitle source's language list was extended so every language the player offers can be searched. The codes for the added languages could not be tried against the live source from the cloud; L7 with a second language covers it.
- **D11 not allowed.** A profile without the right to change playback gets "Your profile cannot search for subtitles", and is not told about a source's key or health.
- **Not seen rendered in the cloud:** the bar at 1024 wide, the volume slider inside the real bar and in fullscreen, and Play's exact centring. L1 and L4 cover them.
- **Left for another thread:** comments in the audio bar that still say bookmark dots are off by default (the Global bookmarks work edits that file).

## Plain English summary

The video player gets larger buttons, a portrait movie cover, a rating you can read when paused, bookmark dots that sit on the timeline, and a slim vertical volume slider that covers nothing. Audio and subtitle rows stay on one line and the subtitle style boxes are tidied. Find subtitles becomes a list you pick from and always says in plain words why nothing was found. Playback information is filled with the file's details and how it is playing, with a Copy button.
