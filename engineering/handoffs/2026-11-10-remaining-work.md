Run this handoff end to end per `.claude/rules/end-to-end-runs.md`.

# Handoff: the remaining work (2026-11-10)

> Written 2026-11-10 by the planning thread "Plan the remaining work", approved by Shy the same day ("agree with all", answers 1A to 7A below). Snapshot of `main` at `644d840`; re-check with `git fetch` and `git log` before each thread. The per-thread briefs are for the coordinator to pass to build threads, not prompts for Shy to paste. Item numbers (1 to 31) are from the local check of 2026-11-07; `#n` are GitHub issues.

## 1. Where things stand

| Area | State |
|---|---|
| Look v3, Unified player v2, Editor v5 | Built and merged; awaiting Local checks in `engineering/validation/batch.md` |
| Player: Favorite and Like split | Merged 2026-11-10 (PR 802). The heart adds to a list (Favorites for songs, My List for other titles); Like and Dislike are taste signals only |
| Cloud builds running | None. All three slots are free |
| Local checks list | 549 distinct rows say Awaiting, 3 Validated. Many rows check screens that newer builds replaced |
| Open bug issues | 31. Six are TV and car apps (#782 to #787, backlog). Nine are built, awaiting check. Twelve say `state:planned` although Libraries v3 and Tiles merged. #684, #685, #686 and #708 say `state:in-build` with no build thread running |
| Open proposed changes | tuvima-docs 94 (View docs) and 96 (Achievements guides, origin unknown); tuvima-library 609 (automatic tool update) |

## 2. Shy's decisions (2026-11-10)

1. **Order:** fixes first, then one Local check, then new features.
2. **Local checks list:** rows for screens that newer builds replaced are marked Superseded with the reason. Nothing is deleted.
3. **Issues:** bring them in line with what is built, with closure notes per `.claude/rules/bug-tracking.md`.
4. **First new feature to plan:** the PDF and comic reader. Then the Needs Review redesign, then Profile, Taste and Activity.
5. **Editor v5 open questions, all as recommended:** remove the unused single-file delete (Engine actions and Contracts); give a loose video at a library's root the folder button; refresh details at once after linking an episode or track to Wikidata; follow section 3a where D24's parenthetical disagrees; no extra final review.
6. **Old editor leftovers dropped:** Details overlay WP8 and Universal editor v3 WP5 (Editor v5 replaced those designs).
7. **This handoff** is committed and run end to end.

## 3. Order

At most three cloud builds at once. Builds that touch the same pages never run together. Planning threads and the housekeeping pass use no build slot.

| Round | Slot 1 | Slot 2 | Slot 3 | Alongside |
|---|---|---|---|---|
| 1 | A. View folders | B. Activity | C. Provider key window | H. Housekeeping; plan the PDF and comic reader |
| 2 | D. Metadata and matching | E. Details pages | F. Encrypted plugin keys | Plan the Needs Review redesign |
| 3 | G. Libraries and Add library | I. Home tiles | J. Follow-ups sweep | Plan Profile, Taste and Activity |
| 4 | K. Universe leftovers (plan, then build) | free | free | Shy's one Local check on the trimmed list, and the short local sitting for section 5 |
| 5 | Fixes from the Local check | Fixes from the Local check | PDF and comic reader (once Locked) | |
| 6 onward | Needs Review redesign | Profile, Taste and Activity | Smaller features; TV and car apps last | |

A round's thread starts as soon as a slot is free and its "depends on" is met; rounds are an order, not a gate.

## 4. Threads

Every build thread follows `CLAUDE.md` and `.claude/rules/`, lists `Fixes #n` in its proposed changes, posts closure notes, appends its Local checks to `engineering/validation/batch.md`, finishes with the merge, and reports Complete or Waiting on you. Look v3 owns all shared styling: threads consume its parts and never restyle them. Where a thread has no Locked spec, the items below are Shy's recorded decisions; anything they do not settle is a question for Shy, not a guess.

| Thread | Depends on | Runs beside | Needs Shy |
|---|---|---|---|
| A. View folders | nothing | B, C, D, E, F, I | No |
| B. Activity | nothing | all | No |
| C. Provider key window | nothing | all | No |
| D. Metadata and matching | nothing | all but E on shared details-page reads | Sample media at the Local check |
| E. Details pages | Look v3, Editor v5 (merged) | A, B, C, F | No |
| F. Encrypted plugin keys | nothing | all | Only the gates its spec names |
| G. Libraries and Add library | A merged | I, J | Screenshot for 6a |
| I. Home tiles | E merged | G, J | No |
| J. Follow-ups sweep | A to F merged | G, I | No |
| K. Universe leftovers | Editor v5 (merged) | all | Lock the re-plan |
| H. Housekeeping | nothing | all | No |

**A. View folders.** Build the Locked spec `engineering/specs/view-folders-in-libraries.md` (v4, WP1 to WP5, 3b, 3c; mockup `engineering/design/mockups/view-folders/`). Covers #680. Tidy the stray empty cell in its index row.

**B. Activity.** Items 7a (Working now is one live row), 7b (the card's icon uses the top bar's ring), 7c (a very simple card), 7d (a truthful percent), 8 (one History entry per scan across libraries), 30a ("New" is not shown for a library's first import even when it arrives in several runs), and #708 (ingestion shown as running for hours). Amends `engineering/specs/library-activity.md`; record the changes there as a new version for Shy.

**C. Provider key window.** Item 5: (a) Test no longer flips the window to the empty entry form; (b) one compact window with Test and Remove together and Save as the default; a saved key loads into the tester. Covers #684, #685, #686.

**D. Metadata and matching (Engine).** Item 18 (a TV show page has a description; none is stored today), 22 (a single Series with a Universe still gets the Universe page and button; fix audiobook and show matching to Wikidata, inheriting from the book while keeping narrator and edition), 25 (Match must not say "Nothing called ..." for the show it is already matched to). Free metadata sources only.

**E. Details pages.** Finish `engineering/specs/universal-editor-v4-1-details-page.md` WP9 to WP12 and WP14 (item 16), read together with the archived Editor v5 spec; stop and ask if they disagree. Also 10a (hero "Open" button with a folder icon), 12a (the dropdown names the folder, not "Videos"), 12b (no tick boxes), 12c (stray `{` and `}`), 15 (the "About this" grid at the bottom of the page).

**F. Encrypted plugin keys.** Build the Locked spec `engineering/specs/encrypted-plugin-keys.md`. Backup and restore, which EK2 waited for, is built.

**G. Libraries and Add library.** Items 1 (step 1 icons and text layout; clipped tooltip), 2 ("Added" becomes a green check with spacing), 3a (pills stay put while the folder step scrolls), 3b (the heading names the kind and area), 3c (offer Managed for plain Videos), 6b (the Videos tile follows Watch's color, new icon). 6a ("In your collection") waits for Shy's screenshot. Covers #696, #687, #689, #683, #682, #681; close any that Libraries v3 already fixed, with closure notes.

**I. Home tiles.** Item 14 (the Home video tile: poster size with an inset still, one video color, no Play button or New pill) and 30b ("..." on video and TV tiles; a test that every kind on Home gets a menu). Covers #700, #701, #692, #693. #691 (landscape tiles) is superseded by item 14; say so on the issue.

**J. Follow-ups sweep.** Small items, one thread:
- Look v3: genre text split into separate genres (Engine); timeline Group-by caption; plugin Official pill and Version row; `--tl-font-size-lg` back on the type ladder; an extra-large window step for the shared browser.
- Player v2: "Go to season" (needs a season id from the Engine); the Now playing test covers tracks; remove the old bookmark owner services.
- Editor v5: decisions 5 above; Add files moves into the shared browser frame; `ViewEditHeaderNav` and `EditorHeaderNav` become one; remove `EditorFamilyState` reads only tests use.
- Plugins and Photos: Connection row details from the Engine (#711); gallery scroll-loading; viewer Hide action; enforce a gallery's download-allowed setting (Privacy); Songs list header sort menu and filter.
- Needs a decision before building, so list for Shy instead: per-person gallery scope; video frames in the artwork picker; photo color wash and gallery header picture.

**K. Universe leftovers.** Re-plan what remains of `engineering/handoffs/2026-10-20-universe-remaining.md` and adding or deleting hand-added things (`engineering/specs/universe-contents.md`) against Editor v5, as a Draft spec for Shy to lock. Build only after it is Locked.

**H. Housekeeping (documents and issues only, no app code).**
1. In `engineering/validation/batch.md`, mark rows Superseded where a newer build replaced the screen (Editor v2, v3, v4 by v5; Plugins v3 to v5 by v6; Look pass v1 and v2 by v3; earlier player rows by Player v2; First-run setup v3 by the rework), each with the reason. When unsure, leave the row.
2. Issues: for the twelve `state:planned` issues whose builds merged, post closure notes and set `state:built-awaiting-check`. Check #684 to #686 and #708 for unmerged work and correct their state.
3. `engineering/README.md`: fix stale rows (View section merged in PRs 726 to 733; Look pass v2 superseded by v3; Details overlay WP8 and Editor v3 WP5 dropped). Find out whether `engineering/specs/library-area-pages.md` (Locked 2026-10-27) was covered by later builds and report.
4. Report on tuvima-docs 94 and 96 and tuvima-library 609: merge what is green and belongs to finished work; ask Shy about anything unclear.

**Planning threads (no app code).** PDF and comic reader first (it also unblocks Global bookmarks stage 3), then the Needs Review redesign, then Profile, Taste and Activity. Each produces a Draft spec and mockups for Shy to lock. Later, unplanned: profile preferences layout, QR pairing, Photos and videos picker, Author and Series lanes on Read.

## 5. Waiting on Shy

- Files or screenshots that cannot be worked out in the cloud: missing frames on video episodes (12d), Extras on a course folder (15), the Libraries card (6a), Quick Facts differences (19b), the Artwork picker scroll (20c), the wordmark font (9). One short local sitting finds the causes; fixes then go to the cloud with a failing test.
- The Local check itself, when round 3 is merged and the list is trimmed. It may take two or three sittings by area.
- Earlier open questions: rebuild the `bug-evidence` branch to erase old images; remove names from proposed-change descriptions.

## 6. Parked or backlog

TV artwork and cast WP2, WP3, WP6 and Library setup packet 6 (parked until the Local check). Player lock screen (D27), TV and car fine-tuning (D29) and the six client apps (#782 to #787), after the player passes its Local check. Per-profile document extras; a courses plugin.

## Plain English summary

The three big redesigns are built. This handoff runs what is left in order: three rounds of already-decided fixes, three builds at a time, with a tidy-up of the checklist and bug list beside them. Shy then checks everything once on a shortened list, and new features follow, starting with the PDF and comic reader. Shy is only needed for a few files, the Local check, and locking new plans.
