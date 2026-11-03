# Player controls v3

> **Status:** Locked · **Version:** 3 (revises [Video player controls and bookmarks](video-player-controls-and-bookmarks.md) v2 where stated) · **Last updated:** 2026-11-03 · **Related docs:** [Video player controls and bookmarks](video-player-controls-and-bookmarks.md) · [Music player and queue](music-player-and-queue.md) · [Design system](../design/design-system.md) · Mockups: https://claude.ai/artifact/Gvyp7d2gcWM9o5wxd3JBu1 (source `plans/player-controls-redesign/mockup.html` in the planning project)

## 1. Goal

Make the players readable and calm: larger controls and title, slimmer popups, one Settings menu, one volume slider, and only the controls that fit each kind of media. Bookmarks become easy to filter into your own and Global.

## 2. Scope

**In:** the Watch (video) control bar and panels, the volume slider in the video, audiobook and music players, per-media control sets, chapter-tick setting, bookmark list filter and labels, removal of the bookmark keyboard shortcuts, diagnosing private bookmarks that do not save and missing rail markers.
**Out:** phone touch layout (keeps hardware volume), the book reader (its top bar vanishing is a separate bug to investigate), the bookmark import and replace mechanic (own thread "Review global bookmark imports"), detail-page close behaviour (Home/detail redesign).

## 3. Decisions made

- **D1. Sizes.** Control glyphs 38 px in a 56 px target, Play 44 px, bottom title 22 px, at 1440 wide; checked again at 1280, where the title truncates before any icon shrinks. One shared size token set for every playback button. Suspected cause today, to be confirmed in a browser: sizes are set in three places that disagree and only Play was enlarged.
- **D2. Hover (Shy, 2026-11-03: 1C).** Both: the icon grows slightly and a short name appears above it, also on keyboard focus. Under reduced motion the grow is skipped. Every button keeps an accessible name.
- **D3. Slimmer popups.** No descriptions; each popup is as wide as its longest line; two columns where there are two lists (Audio, Subtitles).
- **D4. One Settings menu** with a gear icon replaces "CC": Audio, Subtitles, Styling… (size, font, shadow, colour, background) as simple pages with back and close. Audio is listed for every file; a single track is shown but not selectable.
- **D5. No subtitle source switched on:** the Subtitles page says "No subtitle source is switched on" (administrators also see a link to Plugins) and offers no search.
- **D6. Volume** is a vertical slider with square track and knob opening above the speaker on hover or focus, in video, audiobook and music players; arrow keys change by 5. Phones use hardware buttons.
- **D7. Control sets by media.** Movie: bookmarks only (no Up next, no chapters). TV episode: Next episode and Episodes (season list with Up next). Video or course: movie set, plus a Lessons list when it belongs to a series. Audiobook: previous/next chapter, Chapters, Speed, Sleep timer, Bookmarks. Music: Queue, History, Lyrics, Shuffle, Repeat, no bookmarks.
- **D8. Chapter ticks (Shy: 3A).** Off by default for video, on for audiobooks; one setting under Settings > Playback ("Show chapter marks on the progress bar"), next to the bookmark-marks setting in D8a.
- **D8a. Bookmark markers off by default (Shy, 2026-11-03).** The bookmark bubbles on the progress bar are hidden by default and shown only when a profile turns on "Show bookmarks on the bar" in Settings > Playback (per profile; also reachable from the bookmark panel). Overrides v2 D8, which always drew them. The bookmarks list and the Mine / Global filter work whether or not the marks are shown; with marks on, the dots follow the filter. Wider bookmark mechanics (exporting across profiles, one master Global set) are in the separate bookmark imports thread.
- **D9. Bookmark shortcuts (Shy: 2A).** The B and Shift+B shortcuts are removed; the Bookmarks button is the only way. Overrides v2 D6.
- **D10. Bookmark list, filter and Promote (revised 2026-11-03, Shy; follows the bookmark model v2 in the Global bookmarks spec).** Filter: All · Mine · Global. Administrators on desktop and tablet get a fourth option, **Everyone's**: every profile's personal bookmarks on this title in time order, each with the owner's name (not in the Vault, not on phone or TV). On a row in Everyone's, **Promote** turns it into a Global bookmark, with an optional inline rename first. If other profiles have a bookmark within 3 seconds of that spot, an inline line first says "Replaces 2 bookmarks at this spot (Sam, Alex)" with Confirm, because their copies are deleted and there is no undo; with none nearby there is no extra step. Promote is immediate with a row glow and no toast. Pack names, the blue pack marker, the per-pack switch and Remove pack are gone; Global is white and Mine is the accent colour. The filter choice is remembered per profile and the filter is hidden when a title has only one kind. Overrides v2 D7 and D8 and the earlier D10 text ("All · Mine · Global" only). The administrator-only "Share with everyone" switch stays under the note field, and administrators can edit or delete Global rows from the list. Export and import are no longer in the player's bookmark panel; they become library-level (location decided in the Global bookmarks spec).

## 4. UI behaviour

Empty: a list with no bookmarks shows the add row and "No bookmarks yet." Loading: add row usable at once. Error: save failure shows "Couldn't save · Try again" in place. Reduced motion: no grow. Phone: touch player unchanged except bug fixes. Tablet: 44 px targets, panels as sheets. TV: unchanged. Vault: same controls; no pack features.

## 5. Data / model changes

Remembered bookmark filter and the chapter-ticks setting per profile. No wire renames.

## 6. Work packets

1. **Sizes and hover** — Standard — shared size tokens, title, hover grow and label. Done when 1440 and 1280 screenshots match the mockup.
2. **Popups and Settings menu** — Complex — depends on 1. Slim panels, gear menu, styling pages, "no source" state. Done when each state in the mockup renders.
3. **Volume slider** — Standard — one component for all players.
4. **Control sets, chapter setting, shortcut removal** — Standard — `PlaybackControlCatalog`, Settings > Playback, remove B/Shift+B.
5. **Bookmark filter and Global label** — Standard — depends on 4's bookmark work only for files touched.
6. **Diagnose private bookmarks not saving and missing rail markers** — Standard — reproduce on a small generated library first and report the cause before fixing.
7. **Docs** — Mechanical — Watch and Listen pages in tuvima-docs, presentation rules.

## 7. Local checks

Real episode and film at 1440 and 1280; a file with two audio tracks in Chrome and Edge; an audiobook with chapters; a non-admin profile adding a personal bookmark; the Mine/Global filter with an imported set.

## 8. Risks and open questions

Import and replace behaviour for Global bookmarks is under review in a separate thread and may change D10's wording. The book reader top bar bug needs its own investigation.

## Plain English summary

Buttons and titles get bigger, popups shorter, and "CC" becomes one gear menu. Volume is a vertical slider everywhere. Each media type shows only fitting buttons. The bookmark shortcut goes, and bookmarks can be filtered to Mine or Global.
