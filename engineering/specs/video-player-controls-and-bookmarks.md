# Video player controls and bookmarks

> **Status:** In progress (Shy, 2026-10-30 06:02Z) · **Version:** 2 · **Last updated:** 2026-10-30 · **Related docs:** [Videos spec](videos.md) (D43, D43a, D46) · [Music player and queue](music-player-and-queue.md) · [Design system](../design/design-system.md) · Mockups: [`../design/mockups/video-player-controls-and-bookmarks.html`](../design/mockups/video-player-controls-and-bookmarks.html) (also https://claude.ai/artifact/DfwqZtJAoQrbHGaZ94x2VS)

## 1. Goal

Make the Watch player's control bar feel like a finished streaming player (play first, episode name centred, same-sized icons, clear hover and "on" states, panels that open on hover and close when the mouse leaves), and make bookmarks fast and trustworthy: one click to add, markers on the rail, a quick list to jump from, and personal bookmarks that always appear for the person who made them.

## 2. Scope

**In**
- The desktop and tablet control bar of the video player (Watch, including episodes and standalone videos; the same bar inside the Vault).
- Bookmarks in the video player: adding, the quick list, rail markers, quick-look card, delete; the fix for personal bookmarks not showing.
- Subtitles and audio chosen from one panel; the audio language list present for every file that has more than one audio track.
- The hover-open / leave-close behaviour of every player panel (Speed, Bookmarks, Subtitles & Audio, Episodes).

**Out**
- Phone layout (the touch player stays as it is; only the bug fixes apply there).
- Audiobook and music players (they share bookmark parts; they get the bug fix and the hover fix, not the new bar).
- Bookmark packs' export and import flow (kept as built, moved behind "More…").
- The book reader.

## 3. Decisions made

- **D1. Order and content of the bar** (from Shy's reference image, 2026-10-30). Left: Play/Pause, back 10 s, forward 10 s, Mute with a slide-out volume. Centre: "From E4 · Episode name" (films: the film title). Right: Next episode, Episodes, Bookmarks, Subtitles & Audio, Speed, Full screen. Up Next, chapters and the queue fold into the Episodes panel (tabs: Episodes · Chapters · Up next); Picture-in-picture and Cast stay in that panel's "…" row, so the bar has six tools (Shy, 2026-10-30: choice 1A).
- **D2. One icon size.** Every glyph is 34 px in a 52 px target, including Play; the resting size equals today's hover size. The circular outline around Play is removed because it made Play look smaller than its neighbours.
- **D3. Hover and "on" states.** Hovering any bar control turns it the accent colour (`--tl-accent-primary` hover shade) with its tooltip. A control whose feature is on (subtitles showing, speed not 1×, muted) keeps a 3 px accent underline so the state is visible at rest.
- **D4. Panels open on hover and close on leave.** A panel opened by hovering closes 300 ms after the mouse leaves both the icon and the panel. Only a click pins a panel; Escape or a click outside closes a pinned one. This is the existing rule; the Speed panel breaks it today (see Root causes).
- **D5. Paused state.** On pause the picture dims slightly and the show title, season/episode and episode name appear centred in the picture; they fade when playback resumes. The bar does not move (Shy, 2026-10-30: choice 2A).
- **D6. Bookmarks: one click to add.** The Bookmarks panel opens with a purple "Add bookmark here · 13:42" row at the top. Clicking it saves a personal bookmark at that time with no note. A note field below it is optional; Enter saves with the note. The position is captured when the panel opens (D43 rule kept). Keyboard: **B** saves a bookmark without opening anything, with a two-second "Bookmark added 13:42" line on the rail instead of a toast; Shift+B opens the list (Shy, 2026-10-30: choice 4A).
- **D7. The panel is the quick list.** Under the add row, that title's bookmarks in time order, earliest first (Shy, 2026-10-30: choice 3A), each with a marker colour, time, note, owner (Mine / Everyone / pack name), a Play action and, for bookmarks the person may change, a Delete action with an inline "Delete?" confirm. The row nearest the current time is highlighted. There are no Add/Saved tabs any more. "More…" at the bottom opens the full panel for the long description, scene end time, and packs (export, import, switch a pack off).
- **D8. Rail markers are bubbles** (Shy, 2026-10-30: lines felt jarring). Bookmarks draw as 12 px round dots sitting on the rail with a thin dark ring: accent for mine, white for Everyone, secondary accent (blue) for pack bookmarks. Chapter ticks stay thin white lines so the two never look alike. A scene (bookmark with an end time) adds a soft accent span. A bubble grows about a quarter on hover and opens the quick-look card (time, note, owner, Play / Play scene, More, Delete).
- **D9. Personal bookmarks are trusted from the Engine.** The Engine already returns only the signed-in profile's own bookmarks plus the shared ones. The Dashboard stops re-filtering that list by its own copy of the profile id and stops rejecting a save because the returned profile id differs from its own copy. Who may delete remains an Engine decision (the Engine returns "not found" for anyone else's). Rationale under Root causes.
- **D10. Subtitles & Audio in one panel.** Two columns: Audio (every audio track in the file, language and layout, audio-description tracks last) and Subtitles (Off, each track, "Find better subtitles…"). Choosing an audio track the browser cannot switch itself asks the Engine for the stream with that track and resumes from the same second. Subtitle size and background stay in Settings › Playback; the panel links there.
- **D11. Share with everyone** stays an administrator-only switch under the note field, off by default (Videos D43, unchanged). Pack rules (D46) unchanged.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Playing, mouse still | Bar hides after 3 s as today; markers hide with it |
| Playing, mouse moves | Bar with the controls of D1; hovered control in accent colour with its name |
| Paused | Bar stays; centred title over the picture (D5) |
| Hover a tool | Its panel after 120 ms; leaves both → closes after 300 ms; click pins |
| Bookmarks panel, no bookmarks | The add row and "No bookmarks yet. Click above, or press B while watching." |
| Bookmarks panel, loading | Add row immediately usable; list area says "Loading…" |
| Bookmark save fails | The add row turns into "Couldn't save · Try again" in place; nothing else moves |
| Subtitles on | Subtitles icon keeps its accent underline |
| File has one audio track | Audio column shows that one track, not selectable |
| Audio switch needs the Engine | "Switching audio…" over the picture for a moment, then playback resumes at the same time |
| Kids / limited profile | Same bar; Share with everyone never shown |
| Vault title | Same bar; packs and export absent (D46) |
| Tablet | Same bar at 44 px targets; panels become sheets as today |
| Phone | Unchanged touch player; bug fixes only |
| TV | Unchanged |

Resolution: designed and checked at 1440 px wide and again at 1280 px (Shy's note). At 1280 the centre title truncates with an ellipsis before any icon shrinks.

## 5. Data / model changes

None to stored data or wire names. The Engine gains one playback action to start a stream with a chosen audio track index (the manifest already lists tracks). Bookmark DTOs unchanged.

## 6. Work packets

1. **WP1 Hover fix for panels** — Standard — Files: `PlaybackPopover.razor`, `PlaybackSpeedControl.razor`, `VideoPlaybackHost.razor` (`_rateMenuOpen`). Done when: hovering Speed then moving away closes it; a click keeps it; the existing popover tests pass and one new test covers "hover-open reported back as Open does not pin".
2. **WP2 Personal bookmarks trusted from the Engine** — Standard — Files: `AudiobookBookmarkActionService.cs` (`FilterAuthorized`, replay, save match), `AudiobookBookmarkDialog.razor` (list filter), `BookmarkPackSection.razor`, tests in `VideoBookmarkTests.cs` and `AudiobookBookmarkActionServiceTests.cs`. Done when: a saved personal bookmark appears in the list and on the rail whatever profile id the Dashboard holds; the Engine's own tests still refuse another profile's personal bookmark; the test "a personal one from them is not listed" is rewritten to assert at the Engine, not the Dashboard.
3. **WP3 Control bar layout and sizes** — Standard — Files: `VideoPlaybackHost.razor`, `PlaybackVideoChrome.razor(.css)`, `PlaybackPrimaryButton.razor.css` (video appearance), `PlaybackUtilityGlyph.razor.css`. Depends on: none. Done when: order per D1, 34/52 px sizes, accent hover, "on" underline; screenshots at 1440 and 1280 published for Shy.
4. **WP4 Paused title** — Mechanical — Files: `VideoPlaybackHost.razor(.css)`. Depends on WP3. Done when: the centred title appears on pause and fades on play.
5. **WP5 Bookmarks quick list and one-click add** — Complex — Files: `VideoBookmarkPanel.razor`, new `VideoBookmarkQuickList.razor`, `AudiobookBookmarkActionService.cs` (save without note), `PlaybackSeekRail.razor(.css)`, `watch-player.js` (B key). Depends on WP2. Done when: one click adds; B adds; list in time order with Play/Delete; "More…" opens the full panel; rail markers per D8.
6. **WP6 Subtitles & Audio panel** — Complex — Files: `VideoPlaybackHost.razor`, new `VideoTracksPanel.razor`, Engine playback endpoint for audio track choice (`PlaybackEndpoints.cs`, streaming service), `app.js` audio helpers. Depends on WP3. Done when: both columns always present; switching audio works in Chrome and Edge on a file with two tracks; subtitles icon shows "on".
7. **WP7 Docs** — Mechanical — `tuvima-docs` Watch player page; `docs/product/presentation-rules.md` player row. Depends on WP5, WP6.

Waves: 1 = WP1 + WP2 (fixes, can merge early). 2 = WP3 + WP4. 3 = WP5 + WP6. 4 = WP7.

## 7. Root causes found (2026-10-30, from the code)

**Speed panel never closes on hover-leave.** The Speed control is the only panel whose open state is also controlled from outside (the host keeps an `_rateMenuOpen` flag so the chrome does not auto-hide while it is open). Sequence: hover opens the panel as "not pinned" → the popover reports "open" to the host → the host sets its flag → the popover sees its `Open` parameter flip to true and re-opens itself as **pinned** → the mouse-leave rule ignores pinned panels, so only a click closes it. Fix: a controlled `Open` that merely echoes the popover's own report must not change the pinned state.

**Personal bookmarks "don't work for me" while shared ones do.** The Engine never fails a personal save: it always files the bookmark under the signed-in session's active profile and returns rows for that profile plus every shared one. The only place a shared and a personal bookmark are treated differently is the Dashboard, which compares each bookmark's profile id with its own idea of the active profile at four points (list filter, replay check, the save-response check, and the pack section). When the two ids differ, personal rows vanish and a personal save is reported as "did not match", while shared rows pass because they skip that check. The cloud cannot see which of the two ids is wrong on SHY-DESKTOP (the Dashboard's comes from the circuit's profile session; the Engine's from the session token), so the fix removes the duplicate check rather than patching one side (Prevent, don't patch: one authority for who owns a bookmark). Local check L3 confirms it on the real library.

**Markers missing from the rail.** Follows from the same bug for personal bookmarks; shared ones should have shown as thin ticks. The ticks are 6×16 px with a transparent 9 px border, drawn only while the bar is visible, so they are easy to miss; D8 makes them larger and gives them colour.

**No audio language option.** The Audio tool only renders when the browser reports every audio track in the file; Chrome and Edge do not expose audio tracks without a flag, so the tool almost never appears. D10 moves the switch to the Engine.

**Hover colour on the right.** The right-hand tools are plain buttons styled by the chrome with a fixed white colour and no hover rule; only the icon-button family has one. D3 gives every bar control the same hover.

## 8. Local checks

| # | Check | Pass when |
|---|---|---|
| L1 | 1440 px and 1280 px, play an episode of a show in the real library | Bar matches mockup frame 1; no icon smaller than its neighbours; centre text truncates at 1280 |
| L2 | Hover Speed, then move away; hover Bookmarks, then move away; click each | Hover-opened panels close; clicked ones stay |
| L3 | As a non-admin profile, add a personal bookmark, close the player, reopen | It is in the list and on the rail; the same as an admin with Share off |
| L4 | Press B three times while watching | Three markers appear; the two-second line shows each time; no toast |
| L5 | A file with two audio tracks (Z:\Development\testing\media\real_media) in Chrome | Audio column lists both; switching resumes at the same second |
| L6 | Turn subtitles on and off | Icon underline follows |
| L7 | Pause | Centred title appears and fades on play |

## 9. Risks and open questions

- Engine-side audio switching needs the stream to restart with a track selection; cost is a short pause (under a second on a direct-play file, longer if it must transcode). Performance risk accepted; Reliability gain is that the option always exists.
- Removing the Dashboard's duplicate profile check moves all ownership checks to the Engine, which already enforces them. Security review (no sign-off needed) confirms delete and share still refuse correctly.
- None open: choices 1 to 4 were decided 1A 2A 3A 4A on 2026-10-30.

## Plain English summary

The player bar will look and behave like the one in your reference: play first, the episode name in the middle, six tools on the right, all icons the same comfortable size, purple on hover, and panels that close when you move away. Bookmarks become one click (or the B key), show as clear coloured markers on the progress bar, and open as a short list you can jump from. Your personal bookmarks were disappearing because the Dashboard double-checked who owns each one against a profile id that did not match the Engine's; the fix is to let the Engine be the only judge. The audio language option was hidden because the browser cannot see the file's audio tracks; switching will go through the Engine instead so the list is always there.
