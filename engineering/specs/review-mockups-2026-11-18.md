# Spec: Review mock-ups of 2026-11-18 (Tools capacity, video Artwork, Activity list, group tiles, Continue tiles, shared lookup dialog, home network)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-18 · **Related docs:** [Product overview](../product/overview.md) · [Design system](../design/design-system.md) · Mock review page (claude.ai artifact UyPxbfzDcmy5jAjb2wJf7x, "Mock Review Sign-off") · Issues #1101, #1104, #1107, #1108, #1118, #1112, #1113, #1114, #1123, #1131, #1129, #1111, #1137

Locked by Shy on 2026-11-18: "these all look good" and "yes to all" (all 18 recommended answers, including the home network change 161A, 162A, 163A).

## 1. Goal
Fix the items from the 11-17 local review with designs Shy has approved: a capacity panel on Tools, an Artwork tab for videos, a readable Activity batch list, consistent group and Continue tiles, one pick-a-result dialog for subtitles and lyrics, and the Library reachable on the home network by default with protections.

## 2. Scope
**In scope:** the 10 items below, each matching its mock 1:1 (dark only).
**Out of scope:** Outside (internet) access, native-client changes, the Engine listening on the network, light theme.

## 3. Decisions made
- **D1 (160A, #1101).** Settings > Tools gets a pinned "Computer capacity" panel above the tool list: score gauge out of 100 (existing benchmark), graphics hardware row (same as Setup Tools step), three numbers (video streams at once, 4K conversions at once, faster than real time), "Last tested" date and a Run again button. The benchmark row leaves the tool list. Admin only; hidden on the reduced phone Settings.
- **D2 (125A, 126A, #1104).** Video collections, each video file and single episodes get the same Artwork tab (Cover, Logo, Background, plus a strip of frames from the video). The default cover is the Engine's best extracted frame, labelled "From video frame". Choosing applies at once (no Undo, no Review lines). Small editor-spec amendment: Artwork tab on these kinds.
- **D3 (90A, 91A, 92A, #1107, #1108, #1118).** An Activity batch is a list with a small cover, columns Title, Type, Size, Status; headings sort. Chips above show media type with counts and combine with the Watch / Listen / Read filters. A video collection is one row, collapsed by default, expanding to its files. Batch summary line shows items, done and working. Row height matches Working now tiles.
- **D4 (50A, 51A, #1112).** Group tiles: small inset and gap; 1 title = one full cover; 2 titles = top-left and bottom-left; 3 = top-left, bottom-left, top-right; empty squares get a faint dashed outline plus tint; the name sits on a dark gradient so it is legible on any cover; square covers stay square and centered in their quarter (Series, Universes, Artists, people).
- **D5 (52A, 53A, #1113).** A same-media Series keeps the portrait/square tile with a purple outline and tag ("Book series", "Audiobook series", "Series"). A mixed-media group uses a landscape tile with a gold outline, tag "Collection", covers in natural shapes in a row. Universes keep their look.
- **D6 (54A, 55A, #1114).** One Continue tile format: title row (episodes "S1 E1: Title"), second row content rating and year (year only when no rating; books, audiobooks, albums too), percent on the tile top-right beside the progress bar, no stars. With no landscape picture yet, the whole cover shows centered on a soft backdrop and swaps to the landscape picture when it arrives.
- **D7 (140A, #1123, #1131).** Subtitles and lyrics share one dialog shape: search box prefilled (video: title + year; song: title + artist, falling back to album artist), results list with the best match first and selected, "Use this". Normal case auto-uses the best match with no dialog; the dialog opens from "Find another". Free sources only.
- **D8 (161A, 162A, 163A, #1137, standing decision 11-18).** The Dashboard listens on the home network by default (new users too); the Engine stays local and the Dashboard talks to it. On the home network anyone must sign in to do more than view; no localhost admin shortcut; an "Add a password" nudge shows until the admin has one. The first run asks once for a Windows firewall rule for Private (home) networks only, never Public; if declined, Settings explains what to allow. A Settings switch "Reach from other devices" is on by default and can be turned off. Outside access stays Off. Needs the security reviewer before merge.
- **D9 (127A, #1129).** A file named S01E19-20 is one entry "E19-20: Title + Title"; the page and dropdown count episodes (20) and files (19) correctly.
- **D10 (128A, #1111).** Remove the top bar activity count badge; keep the icon.

## 4. UI behaviour
Exactly as drawn on the mock review page. Empty, loading and error states reuse the app's existing states; no toasts. Phone: Activity list scrolls horizontally inside its card, Tools panel stacks, editor Artwork is the existing phone sheet, firewall prompt is Windows-only.

## 5. Data / model changes
Capacity numbers extend the existing capacity result if missing (4K conversions, speed factor). A Contracts type for Activity batch rows if the current one lacks size/type. Network setting stored with other settings. No migrations (pre-release).

## 6. Work packets
- **WP1 Tools capacity panel (D1)**, Standard. Settings Tools tab, capacity contracts/endpoint reuse. Done when: panel shows with Run again; tests pass.
- **WP2 Activity batch list (D3, D10)**, Standard. LibraryActivity components, SystemActivityIndicator badge removal. Done when: sortable list, chips with counts, collapsed collection row, badge gone; bUnit tests.
- **WP3 Group and Continue tiles (D4, D5, D6)**, Standard. MediaTile, ContinueLandscapeTile, MediaTileLabels and CSS. Done when: bUnit tests for 1/2/3/4 layouts, Series vs Collection, Continue rows.
- **WP4 Video Artwork tab (D2)**, Standard. editor.json, Engine tab builder, frame candidates, editor Artwork. Done when: tab appears on video collection, video, episode with frame default.
- **WP5 Shared lookup dialog (D7)**, Standard. SubtitleFindView, PlaybackLyrics alternatives, shared component. Done when: both open the same dialog from Find another with prefilled search.
- **WP6 Double episodes (D9)**, Standard. Details pills, season dropdown counts. Done when: tests for 20 episodes / 19 files.
- **WP7 Home network (D8)**, Complex, depends on nothing but runs last with security review. Launchers, Dashboard URL binding, sign-in rules, no localhost admin shortcut, nudge, firewall prompt, Settings switch. Done when: guardrail and auth tests pass and reviewer finds no must-fix.

Waves: (WP1, WP2) then (WP3, WP4) then (WP5, WP6) then WP7.

## 7. Local checks
- **L1.** Tools panel at 1024/1280/1440/phone with a real GPU; numbers plausible.
- **L2.** Activity batch of mixed media incl. a video collection on real media.
- **L3.** Tiles: 1, 2, 3, 4-cover groups, mixed book/audiobook, Continue for episode, movie, book, album.
- **L4.** Video collection and a video: Artwork tab and frame default on real files.
- **L5.** Find subtitles and Find lyrics with live providers.
- **L6.** Home network: phone on the same Wi-Fi reaches the Dashboard; firewall prompt on Windows; sign-in required to change anything; Public network stays closed.

## 8. Risks and open questions
- **Risks:** network exposure (mitigated by D8 protections and review); tile layout regressions.
- **Open questions:** none.
