# Closure notes ready to post (issues verified live on 2026-11-06)

Evidence base: https://github.com/Tuvima/tuvima-library/blob/bug-evidence/evidence/<issue>/<file>

- 677: Chips styled; folder list opened from "1 folder" draws on top of the table. Files: 677/ev-677-folderlist-1440.png, 677/after-libraries-1440.png
- 678: Edit window has space under the title; Online is green with a dot. File: 678/ev-678-editwindow-1440.png
- 679: Add library frame is 1100 x 680 on every step. Files: 679/ev-679-add-step1..3-1440.png. (Separate: right edge of step 3 clipped ~20 px, PR in progress.)
- 688: Import folder row is one line (PR 740). Files: 688/after-libraries-1440.png (live), 688/mock-libraries-fixed-1440.png (mock), 688/live-libraries-1440.png (before)
- 697: Page behind the editor stays visible, dimmed. Files: 697/editor-1-details-1440.png, 697/editor-2-artwork-1440.png
- 699: Details close X is a solid bordered button. File: 699/live-show-1440.png (light artwork still a Local check)
- 714: Now Playing shows cover art. Files: 714/live-nowplaying-1440.png, 714/live-minibar-1440.png

## Added after the final rebuild (main at 23d95be, PRs 739-749 merged)
- 702: Menu items measure 16px / weight 500 / 44px rows on the details "..." menu (PR 748). Files: 702/f-menu-1440.png (after), 702/ev-702-tile-menu-1440.png (before, 14px/48px).
- 696 (close if you agree): Libraries page now has Add library top right of the header, then the card, one filter row, then the table, as in the mockup (PRs 744, 740). Files: 696/f-libraries-1440.png vs 696/mock-libraries-fixed-1440.png.
- 705, 706, 707: NOT closed. 706 partly: video Speed moved under Settings (742); audiobook gear menu built (749) but not yet walked live. Keep open until the audiobook check.
- 704: keep open. Account menu, editor rails (739) verified in the top layer; collection rule picker and tag suggestions were moved but not exercised live.
- 711: keep open. Plugin pages are close to the v6 mock; still missing key ending and tested N minutes ago (needs Engine data).
- 703, 713, 709: still reproducing (Home hero shows "14 seasons" and "88 episodes" for a course folder); not built.
- 706 (close if you agree, with 705 left open pending an audiobook full-player look): Video player has no standalone Speed button; Settings holds Audio, Subtitles, Playback speed (slider + Reset to 1x), Styling (PR 742). The audiobook player now has a gear with Playback speed, Sleep timer and Show bookmarks on the bar, with an underline while speed is not 1x (PR 749). Files: 706/after-player-settings-1440.png, 706/after-player-speed-1440.png, 706/f-audiobook-gear-1440.png. Not checked: pause card Rated (707), Aa button on a captioned film, phone widths.

- 696 evidence, side by side: evidence/696/side-by-side-1440.png (approved mockup on the left, the live app on the right, same width). Individual images: evidence/696/mock-libraries-fixed-1440.png and evidence/696/f-libraries-1440.png. What matches: Add library at the top right of the header, then the "In your collection" card, then one filter row (chips, search, status), then the table with the fixed library names. Differences left: the page tabs (Libraries, Activity, Needs Review, Matching, Rules) and a status line with "Check folders for changes" sit above the card, which the mockup does not show.

## Added after PRs 751-756 (checked on a local test install with sample media, 1440 wide)
- 690: The Activity page now lists its history (batches such as "Added 17 audiobooks", with dates and counts) on first open; before, the list stayed empty with a thin loader until something else refreshed the page (fixed in PR 756). File: 690/activity-history-1440.png.
- 703 (partly): A folder of other videos reads "N videos" and "Open folder" on Home and its tiles (PR 754); no Seasons or Episodes wording on those. Still to look at by hand: the folder's own details page and Contents tab wording, and an editor Match tab for it.
- 713: The Home hero for a folder of other videos shows "Open folder" and its length, no Book tag and no Continue Reading (PR 754). Not shown: a single video file in the hero.
- 709: Desktop hero cover size was already larger; the phone hero cover was enlarged (PR 754). Phone look not checked by hand.
- 694: Needs Review rows now show title, kind and one shortened folder line; Last tried date and time stack, and Fix and the menu have a gap (PR 755). A follow-up (PR 757) keeps the folder line inside its column.
- Show editor header (not an issue number): the Show tile is the picture and the word "Show" only, and the Episode dropdown has no blank picture (PR 754).

- 694 evidence: evidence/694/side-by-side-1440.png (approved Needs Review mockup, screen 2, left; live page right, item names and folders replaced with generic text). Matches: filter chips, Why / Best guess / Last tried columns, Fix per row, the folder shown as one short line. Differences left from the mockup: the mockup has a small picture per item, the "Try again for all / Keep all as they are" links at the right of the filter row and a "Use this" shortcut in Best guess; the live page has row tick boxes, buttons for Try again and Keep as they are, a "..." menu beside Fix, and a purple Fix button.
- 713 evidence: evidence/713/side-by-side-1440.png (approved Home hero mockup left; live hero for a folder of videos and for a folder holding one video, right). The live hero no longer shows a Book tag or "Continue Reading" and reads "Open folder" with the length. Not matching the mockup: the mockup puts a small label above the title (for example "Continue Watching") and a Resume button with time left; the live hero for a folder has no label and no Resume. A single video file did not come up in the hero rotation on this install, so that case is not shown.
