# Spec: Unified player (video, audiobook and music)

> **Status:** Locked (Shy, 2026-11-04) · **Version:** 1 · **Last updated:** 2026-11-04 · **Related docs:** [Player controls v3](player-controls-v3.md) (this spec proposes v4 changes where stated) · [Video player controls and bookmarks](video-player-controls-and-bookmarks.md) · [Music player and queue](music-player-and-queue.md) · [Global bookmarks](global-bookmarks.md) · [Design system](../design/design-system.md) · [Local check 2026-11-03](../validation/local-check-2026-11-03.md) items 31 to 33 and 48 to 56 · Mockups: [unified-player.html](../design/mockups/unified-player.html)

## 1. Goal

Make video, audiobook and music feel like one player. Today the film player has Play on the left and the audiobook player has it in the middle, and the two look different. After this work, the same bar, the same hover behaviour, the same panels and the same volume control appear everywhere; only the tools on the right change with the media.

## 2. Scope

**In scope:** the shared bar layout; one hover behaviour; the audiobook chapter selector (replacing whole-book tick marks); the audiobook player opening above the detail page; a Netflix-style paused details for every player (rating card on video); the boxed volume slider and the mute-icon bug; Settings, Speed, Subtitles and Bookmarks panels with one clean style and no scroll bars; removing the remaining keyboard-shortcut hints.

**Out of scope:** episodes with no still image (local check item 53) and the empty Videos tab and photos (item 54), which are separate bugs; phone touch layout and TV (unchanged, except the audiobook detail-page fix); the book reader; the music queue, lyrics and Songs list (stay as in the music spec, only wrapped in the shared bar); bookmark mechanics (Global bookmarks spec).

## 3. Decisions made

Proposed, none locked. Items marked **(Q)** wait for Shy's answer in section 8.

- **D1. One bar, three zones (Shy: 1A, Play in the middle).** Title block on the left, a centre cluster, tools on the right, progress rail above with time played and time left. Proposal: Play in the centre for every media type. Reason: Shy flagged Play left for film and centre for audio as confusing; the centre cluster is already how audio and phone work. Overrides the left-aligned Play in Video player controls v2.
- **D2. One centre cluster.** Previous, skip back, Play, skip forward, next. "Previous/next" mean episode for TV, chapter for audiobooks and track for music; for a film they are hidden (no gap). Skip times stay per media (film 10 s, audiobook 15 back and 30 forward, music none).
- **D3. One hover behaviour (Shy, item 48).** Every control, Play included, grows by the same small amount and shows its name above it; no background highlight on any of them. Keyboard focus shows the name and an outline. Reduced motion skips the grow. Keeps v3 D2, and removes the highlight that skip buttons have today.
- **D4. Sizes and fonts stay as in v3 D1** (Shy confirmed icon sizes and title size are right).
- **D5. Audiobook chapter selector (Shy, item 55; overrides v3 D8 for audiobooks).** The progress bar shows the current chapter, labelled "Chapter 4 of 21", with time in the chapter and time left in the book beside it. A Chapters button opens a list of chapters with lengths; the current one is highlighted. Tick marks are for bookmarks only and are never drawn for chapters in audiobooks. The "chapter marks" setting stays for video. **Chapter or whole book (Shy, 2026-11-04: both).** A small switch at the end of the bar, "Chapter | Book", flips the bar between the current chapter (the default) and the whole book; the choice is remembered per profile. The whole-book view draws no tick marks (Shy: they look terrible): the bar is one clean line, the current chapter name and number show above the knob while dragging or hovering, and the Chapters list is the way to jump. Bookmark markers still follow the "Show on timeline" switch in both views. 
- **D6. The player is always on top (Shy, item 56).** Starting an audiobook (and any full-screen player) from a detail page closes or hides that page so it cannot cover or block the player; closing the player returns to the page it came from. This applies to video and audiobook alike. The music bar at the bottom keeps the current behaviour (it never covers the page).
- **D7. Paused details, all three players (Shy, item 49; revised 2026-11-04: no "You're watching" line, and the same mechanic for audiobooks and music).** After a short pause the screen dims and shows left-aligned details, with no lead-in line. Video, TV: show title large, "Season N", episode title with number, description. Film: title, year, length, description. Audiobook: title large, author, narrator, "Chapter 4 of 21 · chapter title", the book's description. Music: song title large, artist, album and year, with the lyrics line if there is one. Video also gets a rating card at the top left: an accent-coloured vertical bar, "RATED TV-MA" in bold, content reasons underneath when we have them (audiobook and music have no rating card). Moving the mouse or pressing Play brings the bar back; on audiobook and music the cover stays visible beside the text. The rating card shows on pause and for the first few seconds of playback (Shy: 3A).
- **D7a. Now Playing page (Shy, 2026-11-04).** Audio has no easy-to-reach "now playing" page today (the player opens only as a pop-out or from the bar). New: clicking the cover art in the bottom player bar opens the Now Playing page for whatever is playing, in the unified layout (cover, details, the shared bar, and the music or audiobook tools such as Queue, Lyrics or Chapters). The title in the bar does the same, and a "Now playing" icon appears in the top navigation bar (on the right, beside search and the profile) while something is playing, with a small animated sound-bars mark so you can see audio is live (Shy, 2026-11-04: an icon in the navbar, to make it easy). On a phone the mini player at the bottom already shows the cover, and tapping it opens the page. Back or Close returns to the page you were on, and playback never stops when you leave. Video already has its own player page, so for video the bar and the navbar icon simply return to it. (Shy, 2026-11-04: yes to cover art, title and the navbar icon.)
- **D8. Volume (Shy, item 50; revises v3 D6).** A dark rounded box opens above the speaker on hover or focus: thick vertical track, accent fill (the app colour, not red), round knob, speaker icon below. Clicking anywhere on the track sets the level. The speaker shows "sound on" by default and "muted" only after the person clicks it or sets volume to zero. Same component in all three players.
- **D9. One panel style (Shy, items 31 to 33 and 52).** Settings, Speed, Subtitles, Chapters and Bookmarks share one shell: rounded dark panel, a title row with back and close, rows with the label left and the current choice right, soft spacing or faint dividers (no hard boxed borders). Each panel is as tall as its content. Settings is wider (about 380 px at 1440). A scroll area appears only for a long list (many bookmarks, chapters or subtitle results), is the only scroll area in the panel, and never scrolls sideways. The Bookmarks panel follows D9a.
- **D9a. Bookmarks panel design (Shy, item 58; overrides the All, Mine, Global filter in v3 D10).** Header with a bookmark icon, "Bookmarks", a count chip and a close button. A "Show on timeline" switch (the same per-profile setting as "Show bookmarks on the bar", now reached from here). A full-width outlined "+ Bookmark at 04:55" button. Tabs "Mine (4)" and "Global (2)" with an accent underline; administrators on desktop and tablet get a third tab, "Everyone's", with Promote as in v3 D10; a tab with no bookmarks is hidden when only one kind exists. A search box. Rows: an accent time chip, the bookmark name, a small "Personal" or "Global" label, edit and delete icons (Global rows are read only for non-administrators; Delete keeps the inline two-click confirm). Footer line "Select a time to jump to that moment." The panel is as tall as its content, so four bookmarks fit with no scrolling; only the list scrolls once it grows past about 80% of the player height, with header, switch, add button, tabs and search fixed. Audiobooks and music-free media use the same panel; audiobook rows show the chapter name beside the time.
- **D10. Settings contents.** Video: Audio, Subtitles, Playback speed (subtitle style is no longer a page in Settings; see D12). Audiobook: Playback speed, Sleep timer, Show bookmarks on the bar. Music: none (its tools are Queue, History, Lyrics, Shuffle, Repeat).
- **D12. Subtitle style has its own menu, and it is dynamic (Shy, 2026-11-04; revises v3 D4).** A separate "Subtitle style" button (an "Aa" icon) sits beside Settings on video, shown whenever the file has subtitles or a subtitle source is on, and the Subtitles page links to it. It opens its own panel in the shared shell, with back and close, laid out after Shy's reference image (local only):
  - A live preview line at the top ("Subtitles will appear like this") that changes as each choice changes; the real picture changes too.
  - **Text:** a three-way size selector (Small, Medium, Large); a row of six font tiles (for example default, serif, rounded, monospace, small caps, bold), each shown in its own font; a "Drop shadow" switch; a row of colour swatches (white, black, blue, teal, green, yellow, red, magenta) with the chosen one ringed; and an opacity slider with stops.
  - **Background:** colour swatches (white, black and the same set) and an opacity slider, so a background can range from none to solid.
  - A "Reset" link at the bottom.
  Settings are remembered per profile, and for a phone or TV for that device. **Every format:** desktop uses mouse and keyboard; tablet and phone use a bottom sheet with 48 px swatches and rows and nothing that depends on hover; TV opens a full-height right-hand panel navigated with the remote alone (up and down between rows, left and right to move across tiles, swatches or slider stops, Back closes), with larger tiles and a clear focus ring. The same choices exist everywhere so a profile's style looks the same on every device.
- **D11. Shortcuts.** The B and Shift+B bookmark shortcuts and any hint text for them are gone (v3 D9, verified in code during the build). Decided (Shy: 4A): keep Space, arrow keys and Escape, because they are basic accessibility; they are never shown as hints on screen. The public docs must say that these keys work and that there are no bookmark shortcuts.

## 4. UI behaviour

**Empty and loading:** the bar shows at once; chapters, tracks and bookmarks fill in as they load. **Error:** a failed chapter or panel load says "Couldn't load · Try again" inside the panel. **Partly ready:** an audiobook with no chapters shows the whole book as one chapter and hides the Chapters button. A title with no rating shows no rating card.
**Desktop:** as the mockups. **Tablet:** 44 px targets; panels become sheets. **Phone:** unchanged layout, but the audiobook player no longer sits under the detail page. **TV:** unchanged.
**Vault:** same player; no Global bookmark features.

## 5. Data / model changes

None to storage. The Dashboard needs the rating and content reasons for the paused screen (if the Engine already exposes the content rating it is reused; reasons only if a provider supplies them, otherwise the line is simply not shown). No wire renames expected; any addition goes through Contracts with fixtures.

## 6. Work packets

1. **Shared bar and hover** — Complex — one bar component used by the film, audiobook and music full players; one hover and size token set. Files: `PlaybackControlStrip`, `PlaybackIconButton`, `PlaybackFullPlayer`, `VideoPlaybackHost`, `PlaybackControlCatalog` (to be confirmed). Done when all three players match the mockup at 1440 and 1280 and every button hovers the same.
2. **Volume box and mute bug** — Standard — depends on 1 for placement. Reproduce why the icon starts muted (likely the first state is read before the player reports its volume), fix, restyle `PlaybackVolumeControl`. Done when the icon shows sound on at start in all three players and the box matches the mockup.
3. **Panel shell and Settings, Speed, Subtitles, Bookmarks** — Complex — depends on 1. Common shell, wider Settings, no stray scroll bars. Done when no panel shows a scroll bar at 1440 and 1280 with a normal-sized list, and each shows one only for a long list.
4. **Audiobook chapters** — Standard — depends on 1. Chapter-scoped rail and the Chapters panel, tick marks removed for audiobooks. Done when an audiobook with 21 chapters picks a chapter in one click and shows no ticks.
5. **Detail page closes on playback** — Standard — independent. Done when starting an audiobook, film or episode from a detail page shows a usable player and Close returns to the page.
6. **Paused details and rating card** — Standard — depends on 1. Done when pausing a TV episode, film, audiobook and song each shows its layout from D7 (rating card on video only).
6a. **Now Playing page** — Standard — depends on 1. New route for audio, opened from the bar's cover art, title and a menu entry. Done when clicking the cover art of a playing audiobook or song opens the page, Back returns, and playback continues.
6b. **Subtitle style menu** — Standard — depends on 1 and 3. Own button and panel, live preview, desktop, tablet, phone and TV variants (TV and the other native clients in `clients/` may need their own packet; confirm during the build). Done when the style can be changed with a mouse, by touch and with a TV remote alone.
7. **Shortcut sweep** — Mechanical — remove leftover hints and handlers for B and Shift+B; check each player. Done when no shortcut hint appears on screen.
8. **Docs** — Mechanical — include a keyboard section (Space, arrows and Escape work; no bookmark shortcuts, and why); Watch and Listen player pages in tuvima-docs with a "Why the player works this way" section from section 9; update presentation rules. Done when each point in section 9 is in the public docs in plain words.

Build waves: 1 and 5, then 2, 3, 4, 6, 6a and 6b (two at a time), then 7 and 8.

## 7. Local checks

Subtitle style on desktop, a tablet, a phone and a TV with the remote only; a TV episode, a film and a two-audio-track file at 1440 and 1280; an audiobook with many chapters started from its detail page; a music track; hover every button in each player; mute and unmute; pause a rated episode and an unrated one; open Settings, Speed, Subtitles and Bookmarks with a short and a long list.

## 8. Risks and open questions

- Rating reasons ("nudity, language, smoking") are rarely supplied by metadata providers; many titles will show only the rating, or nothing.
- The shared bar touches three large components, so it ships first and alone to keep the visual risk small.

**Open questions for Shy:** none. Questions 1 to 5 (Play position, audiobook bar, rating card timing, keyboard keys, Now Playing entry) were answered on 2026-11-04 and are recorded in the decisions above. Ready for Shy to lock.
- Risk: the TV and native apps are built separately from the Dashboard, so the TV subtitle menu may need its own build thread.

## 9. Why it works this way (for the public docs)

1. **Same bar everywhere:** once you know one player you know them all.
2. **Play in the middle:** it is the thing you reach for most, and the middle is where a thumb or cursor expects it.
3. **Chapters, not ticks:** a chapter is a place you jump to, so it gets a list; ticks on the bar are for your own bookmarks. The bar can show a chapter or the whole book, whichever you prefer.
7. **Keyboard:** Space, arrows and Escape work for accessibility, but nothing adds a bookmark by accident.
4. **Paused details:** a pause is a good moment to remind you what you are watching or listening to, and what it is rated.
6. **Now Playing page:** the cover art is the natural thing to click to see what is playing, so it takes you there.
5. **Panels without scroll bars:** a panel sized to its content is quicker to read; scrolling appears only when a list is genuinely long.

## Plain English summary

All three players get the same layout, with Play in the middle and every button behaving the same on hover. Audiobooks get a chapter list instead of ticks across the whole book, and they open above the detail page instead of underneath it. Pausing a show shows what you are watching and its rating, volume becomes a neat box that no longer wrongly shows "muted", and Settings and the other menus are wider, cleaner and free of scroll bars.
