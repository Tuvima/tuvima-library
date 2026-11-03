# Local check, 2026-11-02 (open bugs and review list)

Walked with Shy on SHY-DESKTOP against a fresh instance of main (d58e4e609, after the Settings simplification PRs). Nothing was fixed during the walk. Items are Shy's reports in the order given; numbers are only for this file. Referenced images are in `C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\`. The intro animation Shy supplied is saved at `.tmp/localcheck2/intro-animation.html`.

No batch row was marked Validated. The walk covered first-run setup (first pages), Libraries and Metadata, Home, detail pages, the video player and the Plugins page by eye; every other batch row stays Awaiting.

1. Setup page 1: name field defaults to "Library" but the address shows tuvima.local (mismatch).
2. Setup page 1: "Change" has no outline, doesn't read as a button; its editor has no Cancel.
3. Setup page 2 note "your details stay on your library server" probably belongs on a Welcome page. Wanted: an informative Welcome page (what Tuvima Library is/does, what setup will do) plus an animated intro for setup. Shy attached an animation (copy: .tmp/localcheck2/intro-animation.html, ~980 KB; colours may need updating). Also reuse it as the app-launch splash like Plex.
4. Setup page 3 (Tools): summary line "5 tools detected · 4 ready · 1 needs setup" is plain; needs colour and detail to match Settings pages more closely.
5. Setup Libraries page: bland, icons not great. Shy supplied a target idea image: C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\866e39fd-image.png (need not match exactly, more flavour).
6. Folder picker: very tall, wasted space; target image: C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\c5ea7150-image.png. Also: libraries step comes before metadata providers, so the "no metadata source" warning always shows; rethink step order.
7. Setup order: enable the Vault BEFORE creating libraries, so Vault-only kinds (adult videos/pictures) appear as choices only if the Vault is on.
8. Folder picker still slow; Shy suspects it scans for media before loading. Wants a "dumb" picker (just folders), then a review after Add that checks whether contents match the library kind and warns if not.
9. Library setup/management redesign: five target screenshots in C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\ (5b38cf6b-Movies_Folder_Management-4, 0c7ce11d-...Watch_Listen_Read_View-5, 46902292-...choose_type-2, 82f93551-Tuvima_Movies_library_setup-3, 24a37d21-Choose_your_first_library_area-1). Note colours, detail, add-a-folder, and link-a-folder for View (the real requirement there).
10. Folder warnings: drop "path contains TV/Movies" style warnings (people keep all media under one root). Only flag a clash when that exact folder was already added to another library; show it as an error AFTER selecting. Example: Z:\Movies for Movies is fine, but Z:\ for pictures errors.
11. Correction to 9: ignore the "link" option for View in the screenshots; keep the existing Add library.
12. Setup Metadata providers screen: restyle like target images C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\3fca9de7-image.png and 386a33ea-image.png. Sources needing a key go on top. Tiles must render evenly with an odd count (same size, last one centred).
13. API key entry: one page/dialog where you enter the key(s) (one or many fields per provider), Test immediately, Save only once tested. "Connect Atlas" in the targets is a stand-in for any provider. Targets: C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\31a0d9e8-image.png and 25401a08-image.png
14. Library Activity page: cleaner summary view; replace "..." with a link straight to the item details page; hover on artwork shows a modestly larger preview (to see what was downloaded); bring back a progress bar per just-identified file showing its stage; navbar Activity icon goes straight to Activity (to library summary only when idle). Target: C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\9583755e-image.png
15. Use this layout for the Libraries overview AND likely the setup Libraries step: one Add library button leading into wizard screens; show area, folder count, scanning state etc.; hide the "in your collection" piece in setup (status "pending" there). Target: C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\ee6f727a-image.png
16. Home/tiles (refs: C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\3eb3187b-image.png, 07244dc0-image.png; HBO Max as reference):
 - Home tiles too small (HBO Max size). Remove portrait-to-landscape hover; ALL tiles portrait, no mouseover growth. Hover highlight border too thin: make bolder.
 - Continue watching/reading/listening get LANDSCAPE tiles to separate them from the rest. Books/audiobooks: cover on a landscape background (think through). Music continue: one playlist, or one album, shown as such; "All songs" + shuffle = collage of random album art; never add every album played. Use the pills from earlier screenshots.
 - Hero: a bit less height if backgrounds still work; hero carousel must not list single albums: one "Recently added music" collage entry linking to Recently Added in Music.
 - Read/Watch tiles should grow a bit too; Listen is fine.
 - Wants a Fable review of logic/continuity/uniformity across pages (needs Shy's approval per model routing; this counts as the request).
17. Home hero: book covers far too small, do not fill the hero space. No Watch items appear in the hero rotation at all (needs diagnosis).
18. Tiles/nav (refs b432e593, 36fb536d, db27c5a7 images in the uploads folder): each cover gets a "..." simple context menu; Shy likes the font sizing in the reference. "My List": plus (+) icon instead of heart. Top-bar My List button becomes a bookmark icon, filled gold when selected, click again closes it. Home items: small square "New Episode"/"New" label at top-left instead of the current pill boxes.
18b. TV: no episode art downloads; no episodes seem matched (bug in itself). Editor "still terrible": Edit on a show should open an immediate pop-over editor (not an overlay-on-page/sticky bar) like the older one: navigate show > season > episode within that show only, proper matching; same for music, movies, etc. Needs a Fable review and a search of older commits for the earlier popover editor. NOTE: CLAUDE.md Never #1 and the locked editor spec currently say "no pop-up editor for a single item", so this reverses a Locked decision and needs a new spec version.
19. Stephen Curry MasterClass appears in the library but opening it does nothing; the detail page errors out. (Engine log: .tmp/localcheck2/engine.log may hold the error.)
18c. Shy CONFIRMED (2026-11-02 23:09Z): reverse the "no pop-up editor" decision; a new editor spec version is wanted (popover editor, navigates within the show/album).
20. Look and controls (ref: C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\caaab1b9-image.png):
 - Pills become square (genres etc. too).
 - Home hero: remove like/dislike/edit; keep only Watch and + (My List).
 - Detail page action row: Read/Watch buttons smaller, other icons shrink to match (+, thumbs up, "..."), in the style of the reference with our colours.
 - Remove the Restart button; put "Restart" (from beginning) in the "..." menu. Remove Mark watched/unwatched, Hide from continue, and viewing history entirely. Continue rows get an "X" to remove; starting it again brings it back.
 - Collections admin-only (reverses earlier idea): remove "Add to collection" from menus; the Collections screen is the way in.
 - Rename "Delete from this library" to "Delete" and "Edit" stays "Edit"; "..." menu fonts/layout larger, cleaner, simpler.
 - Part of the Fable review for Home/detail UI.
21. Detail page stack (ref: C:\Users\shaya\.claude\uploads\f832ecd8-a857-4e1f-8482-40c21f54aa17\6603aa35-image.png): rating, season count, Dolby Vision etc. ABOVE the play button; My List/rate/trailer BELOW on their own row (maps to mobile, where a Download button gets its own row). Add to the Fable review.
22. Home: remove the "Featured content" pill.
23. People page: person pictures larger, filling more of the hero. "Works in your library": square pill without the word "Movie"; add a filter when a person has several kinds of works.
24. Collections > People page: poorly built (no search, sort etc.); people icons overwhelming. Reduce people icon sizes a bit there and on the media details page. Shy preferred the earlier square look for people but it must be unified across the app (note: conflicts with #23 "larger pictures" on the person page; clarify at planning).
25. Video player: chapter ticks are lines on the seek bar and bookmarks are circles (12px round dots, PlaybackSeekRail.razor.css, confirmed). Turn chapter ticks OFF by default (optional setting). If the subtitle provider is not enabled, show that state instead of offering a search.
26. Video player (Netflix refs in uploads folder: 9db76e76 speed, 85f6dc5a audio/subtitles, 57d44ff2 season list, 25387825 next episode, 7ff675b9 playing, f9b167d1 paused): play/pause/volume row controls and bottom title text far too small; popups waste space and carry needless description (use all space, no waste, get to the point); REMOVE the B keyboard shortcut for bookmarks (accidental-press risk).
27. Players: volume slider should be vertical (pops up above the speaker) with a square-cornered look, as in b339c5ff-image.png; adopt for all players.
28. Players per media type: a MOVIE should have no "up next" or chapters, only bookmarks. Chapters belong to audiobooks (and music/books where relevant); TV keeps "up next"/episodes. Plan the control icons per media type (movie, TV, audiobook, music, etc.).
29. Player Settings menu (Disney+ refs 7280a6da, 827e6859, ecff5358, 324070e7 in uploads): replace the "CC" button with one Settings (gear) menu -> Subtitles, Audio Options, Subtitle Styling (size, font, shadow, colour, background), each a simple sub-page with back and close.
30. The Netflix menus (audio/subtitles two-column, speed, episodes) may also be used as references for the player; include in the Fable review of the player.
31. Player icons: root cause suspected = big padding on the button overlay vs. the icon, so icons look tiny while the padding stays large (fix padding, not just icon size).
32. Player icon hover: icons simply grow slightly on mouseover and shrink back (Netflix style, ref 8a714194) instead of a highlight background.
33. Alternative hover style: a text label above the icon (Disney+: "PLAY", ref d26a4aed). Undecided; accessibility (ADA/WCAG) angle: a visible text label helps, grow-only hover is not enough alone. Decide in the player review (could do both: grow + label, plus aria-labels/tooltips).
34. BUG: play a movie, exit the player -> back on the detail page; closing the detail page then shows a blank page (close should return to where you came from, e.g. Home/Watch).
35. BUG: landscape Continue tiles show no logo/title, so you cannot tell what the movie/show is (show logo, or title text fallback).
36. BUG/diagnose: navbar Activity indicator still shows running 1h30m later, but the Library Activity page does not show what is running. Page and navbar must agree; page should show the running work (ties to #14). Engine log: .tmp/localcheck2/engine.log.
37. Needs Review page looks redundant now that unmatched items show in the library list; the Unidentified items view is "really bad". Decide at planning: merge Needs Review into the library list/filters and redo the Unidentified view (product decision: Needs Review is a named Library concept).
38. BUG/gap: no way to re-match an item that failed to match (ties to #18b editor and #37 Unidentified view).
39. Libraries: the "Change what this folder is" option is useless; remove it.
40. Plugins page tiles/settings:
 - "Configure" is missing on some plugins (selective): replace with a settings (gear) icon bottom right on EVERY tile, always the remediation action.
 - Tile holds only: name/icon, description, enabled/disabled; "key missing" pill only when needed.
 - Health tick (green/yellow) too small: larger, fixed position (bottom right).
 - Plugin settings pages should be generated from the plugin's own JSON (manifest), not special-cased in code; extend the plugin format where gaps exist. JSON-type plugins can be reviewed/edited in settings, validated before saving.
 - "What it provides" etc.: more structure, uniformity and colour matching the app.
