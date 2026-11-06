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
